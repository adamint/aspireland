using System.Data.Common;
using MetroOps.Contracts;

namespace MetroOps.AiHost;

internal sealed class OpenAiSettings
{
    public string? ConnectionName { get; init; }
    public string AzureTenantId { get; init; } = "";
    public string TextModel { get; init; } = "";
    public string TranscriptionModel { get; init; } = "";
    public string SpeechModel { get; init; } = "";
    public string SpeechVoice { get; init; } = "alloy";
    public bool TextEnabled => ConnectionName is not null && TextModel.Length > 0;
    public bool TranscriptionEnabled => ConnectionName is not null && TranscriptionModel.Length > 0;
    public bool SpeechEnabled => ConnectionName is not null && SpeechModel.Length > 0;

    public static OpenAiSettings FromConfiguration(IConfiguration configuration)
    {
        string? connectionName = null;
        string? endpoint = null;
        string Deployment(string name)
        {
            var connection = configuration.GetConnectionString(name);
            if (string.IsNullOrWhiteSpace(connection)) return "";
            var values = new DbConnectionStringBuilder { ConnectionString = connection };
            if (!values.TryGetValue("Endpoint", out var address) ||
                !Uri.TryCreate(address.ToString(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !(uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)) ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                uri.AbsolutePath != "/" ||
                !values.TryGetValue("Deployment", out var deployment) || string.IsNullOrWhiteSpace(deployment.ToString()))
                throw new InvalidOperationException($"The {name} connection must identify an HTTPS Foundry account and deployment.");
            if (endpoint is not null && endpoint != uri.AbsoluteUri)
                throw new InvalidOperationException("The chat, transcription and speech deployments must share one Foundry account.");
            endpoint = uri.AbsoluteUri;
            connectionName ??= name;
            return deployment.ToString()!.Trim();
        }

        var chat = Deployment("chat");
        var transcription = Deployment("transcription");
        var speech = Deployment("speech");
        return new OpenAiSettings
        {
            ConnectionName = connectionName,
            AzureTenantId = configuration["AZURE_TENANT_ID"] ?? "",
            TextModel = chat,
            TranscriptionModel = transcription,
            SpeechModel = speech,
            SpeechVoice = configuration["AI:SpeechVoice"] ?? "alloy"
        };
    }

    public AiCapabilities Capabilities(bool includeSpeech) => new(
        TextEnabled, includeSpeech && TranscriptionEnabled, includeSpeech && SpeechEnabled, "Foundry / OpenAI",
        TextEnabled && (!includeSpeech || TranscriptionEnabled && SpeechEnabled)
            ? null : "One or more Foundry model deployments are not configured.");
}
