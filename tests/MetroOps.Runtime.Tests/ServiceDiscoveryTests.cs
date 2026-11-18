using System.Net;
using System.Text;
using System.Text.Json;
using MetroOps.AiHost;
using MetroOps.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MetroOps.Runtime.Tests;

public sealed class ServiceDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DomainAndAiToolsUseTheReferencedEndpoint(bool tool)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["services:scenario:http:0"] = "http://127.0.0.1:43210"
        });
        builder.Services.AddServiceDiscovery();
        var handler = new Handler();
        builder.Services.ConfigureHttpClientDefaults(http =>
            http.AddServiceDiscovery().ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.Services.AddHttpClient("domain");
        builder.Services.AddSingleton<ServiceClients>();
        builder.Services.AddHttpClient<ServiceJsonClient>();
        using var host = builder.Build();

        var response = tool
            ? await host.Services.GetRequiredService<ServiceJsonClient>().GetAsync<JsonElement>(
                "scenario", "context", "Scenario context", CancellationToken.None)
            : await host.Services.GetRequiredService<ServiceClients>().Get<JsonElement>(
                "scenario", "/context", CancellationToken.None);

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("http://127.0.0.1:43210/context", handler.Url);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Url { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json") });
        }
    }
}
