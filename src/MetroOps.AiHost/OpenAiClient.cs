using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using MetroOps.Contracts;
using OpenAI.Audio;
using OpenAI.Chat;
using ChatMessage = OpenAI.Chat.ChatMessage;

namespace MetroOps.AiHost;

internal sealed class OpenAiClient(OpenAiSettings settings, AzureOpenAIClient? client = null)
{
    private const int MaxToolIterations = 6;
    private const int MaxToolCalls = 12;
    private const int MaxAudioResponseBytes = 10 * 1024 * 1024;
    private static readonly ActivitySource ActivitySource = new("MetroOps.AI");
    private AzureOpenAIClient Client => client ?? throw new AiUnavailableException("The Foundry OpenAI client is not configured.");

    public async Task<AssistantResponse> CompleteAsync(
        AssistantRequest request,
        string systemPrompt,
        AssistantToolRegistry tools,
        CancellationToken cancellationToken)
    {
        EnsureAvailable(settings.TextEnabled, "Text assistance");
        using var activity = StartActivity("chat", settings.TextModel);
        var messages = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };
        if (request.History is not null)
            foreach (var message in request.History)
                messages.Add(message.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                    ? new UserChatMessage(message.Content)
                    : new AssistantChatMessage(message.Content));
        messages.Add(new UserChatMessage(request.Message));

        var options = new ChatCompletionOptions
        {
            AllowParallelToolCalls = false,
            MaxOutputTokenCount = 1200,
            StoredOutputEnabled = false
        };
        foreach (var definition in tools.Definitions)
            options.Tools.Add(ChatTool.CreateFunctionTool(definition.Name, definition.Description,
                BinaryData.FromString(definition.Parameters.GetRawText())));
        var names = tools.Definitions.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var capture = new AssistantCapture(request.JourneyContext, AiEndpoints.ResolveViewerTimeZone(request.ViewerTimeZone));
        var iterations = 0;
        var calls = 0;
        while (true)
        {
            var completion = await InvokeAsync(async token =>
                (await Client.GetChatClient(settings.TextModel).CompleteChatAsync(messages, options, token)).Value,
                cancellationToken);
            if (completion.ToolCalls.Count == 0)
            {
                var text = string.Concat(completion.Content.Select(part => part.Text)).Trim();
                if (text.Length == 0)
                    throw new OpenAiProviderException("OpenAI returned an empty assistant response.");
                return capture.BuildResponse(text);
            }

            calls += completion.ToolCalls.Count;
            if (++iterations > MaxToolIterations || calls > MaxToolCalls)
                throw new OpenAiProviderException("The assistant exceeded the tool-call limit.");
            messages.Add(new AssistantChatMessage(completion));
            foreach (var call in completion.ToolCalls)
            {
                if (call.Kind != ChatToolCallKind.Function || !names.Contains(call.FunctionName))
                    throw new OpenAiProviderException("OpenAI returned an unsupported tool call.");
                using var toolActivity = ActivitySource.StartActivity("metroops.ai.tool", ActivityKind.Internal);
                toolActivity?.SetTag("tool.name", call.FunctionName);
                var result = await tools.ExecuteAsync(call.FunctionName, call.FunctionArguments.ToString(), capture, cancellationToken);
                if (capture.Clarification is not null) return capture.BuildResponse("");
                messages.Add(new ToolChatMessage(call.Id, result));
            }
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, string contentType, CancellationToken cancellationToken)
    {
        EnsureAvailable(settings.TranscriptionEnabled, "Transcription");
        using var activity = StartActivity("transcribe", settings.TranscriptionModel);
        var transcription = await InvokeAsync(async token =>
            (await Client.GetAudioClient(settings.TranscriptionModel).TranscribeAudioAsync(
                audio, AudioFileName(contentType),
                new AudioTranscriptionOptions { ResponseFormat = AudioTranscriptionFormat.Simple }, token)).Value,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(transcription.Text))
            throw new OpenAiProviderException("OpenAI returned an empty transcription.");
        return new TranscriptionResult(transcription.Text.Trim(), transcription.Duration?.TotalSeconds);
    }

    public async Task<byte[]> SpeakAsync(string text, CancellationToken cancellationToken)
    {
        EnsureAvailable(settings.SpeechEnabled, "Speech generation");
        using var activity = StartActivity("speech", settings.SpeechModel);
        var audio = await InvokeAsync(async token =>
            (await Client.GetAudioClient(settings.SpeechModel).GenerateSpeechAsync(
                text, new GeneratedSpeechVoice(settings.SpeechVoice),
                new SpeechGenerationOptions { ResponseFormat = GeneratedSpeechFormat.Mp3 }, token)).Value,
            cancellationToken);
        if (audio.ToMemory().Length > MaxAudioResponseBytes)
            throw new OpenAiProviderException("OpenAI returned a response that was too large.");
        if (audio.ToMemory().Length == 0)
            throw new OpenAiProviderException("OpenAI returned empty speech audio.");
        return audio.ToArray();
    }

    private static async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { return await operation(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new OpenAiProviderException("OpenAI timed out."); }
        catch (AuthenticationFailedException)
        { throw new OpenAiProviderException("Azure authentication failed."); }
        catch (ClientResultException exception)
        { throw new OpenAiProviderException($"OpenAI request failed (HTTP {exception.Status})."); }
        catch (HttpRequestException)
        { throw new OpenAiProviderException("OpenAI could not be reached."); }
        catch (JsonException)
        { throw new OpenAiProviderException("OpenAI returned an invalid response."); }
    }

    private static Activity? StartActivity(string operation, string model)
    {
        var activity = ActivitySource.StartActivity($"metroops.ai.{operation}", ActivityKind.Client);
        activity?.SetTag("gen_ai.system", "openai");
        activity?.SetTag("gen_ai.request.model", model);
        return activity;
    }

    private static string AudioFileName(string contentType) => contentType.Split(';', 2)[0].Trim().ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => "recording.mp3",
        "audio/mp4" or "audio/x-m4a" => "recording.m4a",
        "audio/wav" or "audio/x-wav" => "recording.wav",
        "audio/ogg" or "application/ogg" => "recording.ogg",
        "audio/flac" => "recording.flac",
        _ => "recording.webm"
    };

    private static void EnsureAvailable(bool enabled, string feature)
    {
        if (!enabled) throw new AiUnavailableException($"{feature} is unavailable because its Foundry deployment is not configured.");
    }
}

internal sealed record TranscriptionResult(string Text, double? DurationSeconds);
internal sealed class OpenAiProviderException(string message) : Exception(message);
internal sealed class AiUnavailableException(string message) : Exception(message);
