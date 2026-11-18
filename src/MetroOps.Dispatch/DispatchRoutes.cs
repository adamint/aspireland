using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.ServiceDefaults;
using MetroOps.Simulation.Persistence;

namespace MetroOps.Dispatch;

public static class DispatchRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/stations/{id}/diagnosis", async (string id, HttpContext context, WorldStore store, IConfiguration config, CancellationToken ct) =>
        {
            if (!ServiceKey.Matches(context, config, "AGENT_KEY", "X-Agent-Key")) return Results.Unauthorized();
            if (!Network.Definition.Stations.Any(s => s.Id == id)) return Results.NotFound(new { error = "Unknown station." });
            return Results.Ok(StationDiagnostics.Observe(await store.Read(ct), Network.Station(id), DateTimeOffset.UtcNow));
        });
        app.MapPost("/actions", async (CommandEnvelope request, HttpContext context, WorldStore store, IConfiguration config, CancellationToken ct) =>
        {
            if (!ServiceKey.Matches(context, config, "SERVICE_KEY")) return Results.Unauthorized();
            if (request.Action.Kind is "incident" or "clear-incident")
                return Results.BadRequest(new { error = "This service does not own that action." });
            return Results.Ok(await store.Mutate(w => CommandPolicy.Apply(w, request, false), ct, request));
        });
        app.MapPost("/agent-actions", async (CommandEnvelope request, HttpContext context, WorldStore store, IConfiguration config, CancellationToken ct) =>
        {
            if (!ServiceKey.Matches(context, config, "AGENT_KEY", "X-Agent-Key")) return Results.Unauthorized();
            var command = request with { Actor = "agent" };
            return Results.Ok(await store.Mutate(w => CommandPolicy.Apply(w, command, true), ct, command));
        });
        app.MapPost("/approved-actions", async (CommandEnvelope request, HttpContext context, WorldStore store, IConfiguration config, CancellationToken ct) =>
        {
            if (!ServiceKey.Matches(context, config, "SERVICE_KEY")) return Results.Unauthorized();
            return Results.Ok(await store.Mutate(w =>
            {
                if (w.AuthorityMode == "advise") return new CommandResult(false, "denied", "Switch from advise to approval mode before approving an action.", w.Version);
                return CommandPolicy.Apply(w, request with { Actor = "operator-approved-agent" }, false, approved: true);
            }, ct, request));
        });
    }
}
