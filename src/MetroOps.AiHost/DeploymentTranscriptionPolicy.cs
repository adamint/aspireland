using System.ClientModel.Primitives;

namespace MetroOps.AiHost;

internal sealed class DeploymentTranscriptionPolicy(string deployment) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Rewrite(PipelineMessage message)
    {
        if (message.Request.Method == "POST" &&
            message.Request.Uri is { Scheme: "https", AbsolutePath: "/openai/v1/audio/transcriptions" } uri)
            message.Request.Uri = new UriBuilder(uri)
            {
                Path = $"/openai/deployments/{Uri.EscapeDataString(deployment)}/audio/transcriptions",
                Query = "api-version=2025-03-01-preview"
            }.Uri;
    }
}
