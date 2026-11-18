using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.ServiceDefaults;
using MetroOps.Simulation.Persistence;
using OpenTelemetry;

namespace MetroOps.Control;

public static class ControlRoutes
{
    public static bool TraceRequest(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method) ||
        !request.Path.StartsWithSegments("/trains", out var suffix) ||
        suffix.Value.AsSpan().TrimEnd('/').Count('/') != 1;

    public static void Map(WebApplication app)
    {
        app.MapGet("/state", (WorldStore store, CancellationToken ct) => store.Read(ct));
        app.MapGet("/trains/{id}", async (string id, WorldStore store, CancellationToken ct) =>
        {
            // The poll is excluded at request start; also suppress the nested Npgsql span.
            // Exceptions still reach the request error handler after this scope is disposed.
            using var _ = SuppressInstrumentationScope.Begin();
            var view = await store.ReadTrain(id, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        });
        app.MapPost("/register", async (TrainRegistration request, HttpContext context, WorldStore store, IConfiguration config, CancellationToken ct) =>
            !ServiceKey.Matches(context, config, "WORKER_KEY")
                ? Results.Unauthorized()
                : Results.Ok(await store.Mutate(w => Kernel.Register(w, request), ct)));
        app.MapPost("/movement", async (MovementRequest request, HttpContext context, WorldStore store, IConfiguration config, CancellationToken ct) =>
            !ServiceKey.Matches(context, config, "WORKER_KEY")
                ? Results.Unauthorized()
                : Results.Ok(await store.Mutate(w => Kernel.RequestMovement(w, request), ct)));
        app.MapPost("/confirm", async (MovementConfirmation request, HttpContext context, WorldStore store, IConfiguration config, CancellationToken ct) =>
            !ServiceKey.Matches(context, config, "WORKER_KEY")
                ? Results.Unauthorized()
                : Results.Ok(await store.Mutate(w => Kernel.ConfirmMovement(w, request), ct)));
    }
}
