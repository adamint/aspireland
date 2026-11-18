using System.Net.Http.Headers;

namespace MetroOps.PublicApi;

public static class AiForwarder
{
    public static void AddAiForwarding(this IServiceCollection services) =>
        services.AddHttpClient("ai", client => client.Timeout = Timeout.InfiniteTimeSpan);

    public static async Task Forward(HttpContext context, IHttpClientFactory clients, string path, string? credential = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), $"https+http://ai-operations{path}");
        if (context.Request.Method == HttpMethods.Post)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } contentType)
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        if (credential is not null) request.Headers.Add("X-Operator-Key", credential);
        using var response = await clients.CreateClient("ai").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
        context.Response.Headers.CacheControl = "no-store";
        foreach (var header in new[] { "X-AI-Generated-Voice", "X-AI-Voice" })
            if (response.Headers.TryGetValues(header, out var values)) context.Response.Headers[header] = values.ToArray();
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
