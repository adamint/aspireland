namespace MetroOps.Contracts;

public sealed record FleetTrain(string Id, string Line, string StationId, int RouteIndex, int Direction,
    int Capacity, string Depot, IReadOnlyList<string> CompatibleLines, bool Reserve = false);

public static class Fleet
{
    public static IReadOnlyList<FleetTrain> Trains { get; } = Array.AsReadOnly<FleetTrain>(
    [
        new("blue-1", "Blue", "westhaven", 1, 1, 150, "west-junction", ["Blue"]),
        new("blue-2", "Blue", "central-exchange", 4, 1, 150, "west-junction", ["Blue"]),
        new("blue-3", "Blue", "riverbank", 7, -1, 150, "west-junction", ["Blue"]),
        new("amber-1", "Amber", "airport", 0, 1, 150, "west-junction", ["Amber"]),
        new("amber-2", "Amber", "civic-square", 3, 1, 150, "west-junction", ["Amber"]),
        new("amber-3", "Amber", "stadium", 7, -1, 150, "west-junction", ["Amber"]),
        new("green-1", "Green", "northgate", 0, 1, 150, "southbank", ["Green"]),
        new("green-2", "Green", "museum-quarter", 3, 1, 150, "southbank", ["Green"]),
        new("ax-1", "AX", "central-exchange", 1, 1, 180, "airport", ["AX"]),
        new("ax-2", "AX", "union-terminal", 2, -1, 180, "airport", ["AX"]),
        new("reserve-ba", "", "west-junction", 0, 1, 150, "west-junction", ["Blue", "Amber"], Reserve: true),
        new("reserve-green", "", "southbank", 0, 1, 150, "southbank", ["Green"], Reserve: true)
    ]);
}
