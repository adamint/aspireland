using MetroOps.Contracts;
using MetroOps.PublicApi;
using MetroOps.ServiceDefaults;

namespace MetroOps.PassengerApi;

public static class PassengerRoutes
{
    public static void Map(WebApplication app)
    {
        PublicApiRoutes.Map(app);
        app.MapGet("/api/world", (ServiceClients clients, CancellationToken ct) =>
            clients.Get<WorldSnapshot>("information", "/world", ct));
        app.MapGet("/api/capabilities", (HttpContext context, IHttpClientFactory clients) =>
            AiForwarder.Forward(context, clients, "/passenger/capabilities"));
        app.MapPost("/api/assistant", (HttpContext context, IHttpClientFactory clients) =>
            AiForwarder.Forward(context, clients, "/passenger/assistant"));
        app.MapPost("/api/transcribe", (HttpContext context, IHttpClientFactory clients) =>
            AiForwarder.Forward(context, clients, "/passenger/transcribe")).DisableAntiforgery();
        app.MapPost("/api/speech", (HttpContext context, IHttpClientFactory clients) =>
            AiForwarder.Forward(context, clients, "/passenger/speech"));
    }
}
