using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.ServiceDefaults;

namespace MetroOps.Planner;

public static class PlannerRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/journeys", async (JourneyRequest request, ServiceClients clients, CancellationToken ct) =>
            Results.Ok(Kernel.Plan(await clients.Get<WorldState>("scenario", "/state", ct), request)));
        app.MapPost("/step-free", async (StepFreeRequest request, ServiceClients clients, CancellationToken ct) =>
            Results.Ok(Kernel.CheckStepFree(await clients.Get<WorldState>("scenario", "/state", ct), request, DateTimeOffset.UtcNow)));
    }
}
