using MetroOps.Contracts;
using MetroOps.Domain;

namespace MetroOps.Information;

public static class InformationRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/world", (InformationProjection projection) =>
        {
            if (projection.Latest is not { } world)
                return Results.Json(new { error = "Passenger information is awaiting its first consistent projection." }, statusCode: 503);
            var stale = DateTimeOffset.UtcNow - world.UpdatedAt > TimeSpan.FromSeconds(10);
            return Results.Ok(new WorldSnapshot(world, Network.Definition, stale, stale ? "Live projections are stale. Predictions are not current." : null));
        });
        app.MapGet("/stations/{id}/arrivals", (string id, InformationProjection projection) =>
        {
            if (projection.Latest is not { } world)
                return Results.Json(new { error = "Arrival predictions are unavailable." }, statusCode: 503);
            if (!Network.Definition.Stations.Any(s => s.Id == id)) return Results.NotFound(new { error = "Unknown station." });
            var board = Kernel.Board(world, id);
            return Results.Ok(DateTimeOffset.UtcNow - world.UpdatedAt > TimeSpan.FromSeconds(10)
                ? board with { Stale = true, Arrivals = board.Arrivals.Select(a => a with { Seconds = null, Status = "stale", PredictionKind = "stale" }).ToList() }
                : board);
        });
    }
}
