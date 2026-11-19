using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using Azure.Identity;
using MetroOps.Contracts;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Responses;

namespace MetroOps.AiHost;

internal sealed class OpenAiClient(OpenAiSettings settings, OpenAIClient? client = null)
{
    private const int MaxToolIterations = 6;
    private const int MaxToolCalls = 12;
    private const int MaxAudioResponseBytes = 10 * 1024 * 1024;
    private static readonly ActivitySource ActivitySource = new("MetroOps.AI");
    private OpenAIClient Client => client ?? throw new AiUnavailableException("The Foundry OpenAI client is not configured.");

#pragma warning disable OPENAI001 // Responses is experimental in the pinned OpenAI SDK.
    public async Task<AssistantResponse> CompleteAsync(
        AssistantRequest request,
        string systemPrompt,
        AssistantToolRegistry tools,
        CancellationToken cancellationToken,
        DateTimeOffset? serviceTime = null)
    {
        EnsureAvailable(settings.TextEnabled, "Text assistance");
        using var activity = StartActivity("chat", settings.TextModel);
        var options = new CreateResponseOptions
        {
            Model = settings.TextModel,
            Instructions = systemPrompt,
            ParallelToolCallsEnabled = false,
            MaxOutputTokenCount = 1200,
            ReasoningOptions = new() { ReasoningEffortLevel = ResponseReasoningEffortLevel.Minimal },
            StoredOutputEnabled = false
        };
        options.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        if (request.History is not null)
            foreach (var message in request.History)
                options.InputItems.Add(message.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                    ? ResponseItem.CreateUserMessageItem(message.Content)
                    : ResponseItem.CreateAssistantMessageItem(message.Content));
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(request.Message));

        foreach (var definition in tools.Definitions)
            options.Tools.Add(ResponseTool.CreateFunctionTool(definition.Name,
                BinaryData.FromString(definition.Parameters.GetRawText()), strictModeEnabled: false, definition.Description));
        var names = tools.Definitions.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var passenger = tools is PassengerToolRegistry;
        var viewerTimeZone = AiEndpoints.ResolveViewerTimeZone(request.ViewerTimeZone);
        var timing = passenger
            ? AiEndpoints.ResolvePassengerTripTiming(request, serviceTime, viewerTimeZone)
            : null;
        var capture = new AssistantCapture(request.JourneyContext, viewerTimeZone, timing);
        if (passenger && capture.HasSavedOutbound && timing?.ReturnTime is not null)
        {
            await tools.ExecuteAsync("plan_journey", "{}", capture, cancellationToken);
            if (capture.Journey is not null || capture.Clarification is not null)
                return capture.BuildResponse("", passenger: true);
        }
        var iterations = 0;
        var calls = 0;
        while (true)
        {
            var completion = await InvokeAsync(async token =>
                (await Client.GetResponsesClient().CreateResponseAsync(options, token)).Value,
                cancellationToken);
            if (completion.Status != ResponseStatus.Completed)
            {
                var reason = completion.IncompleteStatusDetails?.Reason?.ToString() ?? completion.Status?.ToString() ?? "unknown status";
                throw new OpenAiProviderException($"OpenAI did not complete the assistant response ({reason}).");
            }
            if (completion.OutputItems.Any(item => item is not (MessageResponseItem or ReasoningResponseItem or FunctionCallResponseItem)))
                throw new OpenAiProviderException("OpenAI returned an unsupported tool call.");
            var toolCalls = completion.OutputItems.OfType<FunctionCallResponseItem>().ToArray();
            if (toolCalls.Length == 0)
            {
                var text = completion.GetOutputText().Trim();
                if (text.Length == 0)
                    throw new OpenAiProviderException("OpenAI returned an empty assistant response.");
                return capture.BuildResponse(text, passenger);
            }

            calls += toolCalls.Length;
            if (++iterations > MaxToolIterations || calls > MaxToolCalls)
                throw new OpenAiProviderException("The assistant exceeded the tool-call limit.");
            // Non-stored tool rounds must replay reasoning and call items, not just text.
            foreach (var item in completion.OutputItems) options.InputItems.Add(item);
            foreach (var call in toolCalls)
            {
                if (!names.Contains(call.FunctionName))
                    throw new OpenAiProviderException("OpenAI returned an unsupported tool call.");
                using var toolActivity = ActivitySource.StartActivity("metroops.ai.tool", ActivityKind.Internal);
                toolActivity?.SetTag("tool.name", call.FunctionName);
                var result = await tools.ExecuteAsync(call.FunctionName, call.FunctionArguments.ToString(), capture, cancellationToken);
                if (capture.Clarification is not null) return capture.BuildResponse("", passenger);
                options.InputItems.Add(ResponseItem.CreateFunctionCallOutputItem(call.CallId, result));
            }
        }
    }
#pragma warning restore OPENAI001

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
