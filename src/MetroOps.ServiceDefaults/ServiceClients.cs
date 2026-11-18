using System.Net;
using System.Net.Http.Json;
using MetroOps.Contracts;

namespace MetroOps.ServiceDefaults;

public sealed class UpstreamException(string message, HttpStatusCode status = HttpStatusCode.ServiceUnavailable) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class ServiceClients(IHttpClientFactory factory)
{
    public async Task<T> Get<T>(string service, string path, CancellationToken ct, string? key = null, string header = "X-Service-Key")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(service, path));
        if (key is not null) request.Headers.Add(header, key);
        return await Send<T>(request, ct);
    }

    public async Task<T> Post<T>(string service, string path, object body, CancellationToken ct, string? key = null, string header = "X-Service-Key")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(service, path));
        request.Content = JsonContent.Create(body, options: Wire.Json);
        if (key is not null) request.Headers.Add(header, key);
        return await Send<T>(request, ct);
    }

    private static string Url(string service, string path) => $"https+http://{service}/{path.TrimStart('/')}";

    private async Task<T> Send<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await factory.CreateClient("domain").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new UpstreamException($"{request.RequestUri?.AbsolutePath} is unavailable ({(int)response.StatusCode}).", response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(Wire.Json, ct)
            ?? throw new UpstreamException("An upstream service returned an empty response.");
    }
}
