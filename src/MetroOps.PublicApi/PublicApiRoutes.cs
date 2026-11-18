using MetroOps.Contracts;
using MetroOps.ServiceDefaults;

namespace MetroOps.PublicApi;

public static class PublicApiRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/stations/{id}/arrivals", (string id, ServiceClients clients, CancellationToken ct) =>
            clients.Get<StationBoard>("information", $"/stations/{Uri.EscapeDataString(id)}/arrivals", ct));
        app.MapPost("/api/journeys", (JourneyRequest request, ServiceClients clients, CancellationToken ct) =>
            clients.Post<JourneyResult>("planner", "/journeys", request, ct));
        app.MapGet("/api/analytics", (ServiceClients clients, CancellationToken ct) =>
            clients.Get<object>("analytics", "/analytics", ct));
    }
}
