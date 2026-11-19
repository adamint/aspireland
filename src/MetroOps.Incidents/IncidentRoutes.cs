using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.ServiceDefaults;
using MetroOps.Simulation.Persistence;

namespace MetroOps.Incidents;

public static class IncidentRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/actions", async (CommandEnvelope request, HttpContext context, WorldStore store, IConfiguration config, CancellationToken ct) =>
        {
            if (!ServiceKey.Matches(context, config, "SERVICE_KEY")) return Results.Unauthorized();
            if (request.Action.Kind is not ("incident" or "random-incident" or "clear-incident"))
                return Results.BadRequest(new { error = "This service does not own that action." });
            return Results.Ok(await store.Mutate(w => CommandPolicy.Apply(w, request, false), ct, request));
        });
    }
}
