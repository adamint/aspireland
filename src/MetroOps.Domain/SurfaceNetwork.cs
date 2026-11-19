namespace MetroOps.Domain;

internal static class SurfaceNetwork
{
    // Fictional bidirectional road links in metres, not distances measured from the rail map.
    private static readonly (string From, string To, int Meters)[] Links =
    [
        ("pinecrest", "westhaven", 1750),
        ("westhaven", "west-junction", 1350),
        ("westhaven", "aerocity", 3100),
        ("west-junction", "civic-square", 1050),
        ("west-junction", "aerocity", 2400),
        ("civic-square", "central-exchange", 900),
        ("civic-square", "medical-center", 1820),
        ("central-exchange", "union-terminal", 1050),
        ("central-exchange", "museum-quarter", 800),
        ("union-terminal", "east-junction", 650),
        ("union-terminal", "university", 850),
        ("union-terminal", "museum-quarter", 930),
        ("east-junction", "riverbank", 950),
        ("east-junction", "university", 1050),
        ("east-junction", "stadium", 1250),
        ("riverbank", "eastgate", 820),
        ("riverbank", "university", 1430),
        ("eastgate", "northgate", 2600),
        ("airport", "aerocity", 3800),
        ("airport", "southbank", 7500),
        ("stadium", "fairgrounds", 1300),
        ("stadium", "museum-quarter", 2200),
        ("fairgrounds", "southbank", 3900),
        ("northgate", "university", 1220),
        ("museum-quarter", "medical-center", 740),
        ("medical-center", "southbank", 1080)
    ];

    public static IEnumerable<(string Station, int Meters)> Nearest(string origin)
    {
        var distances = new Dictionary<string, int>(StringComparer.Ordinal) { [origin] = 0 };
        var queue = new PriorityQueue<string, int>();
        queue.Enqueue(origin, 0);
        while (queue.TryDequeue(out var station, out var meters))
        {
            if (distances[station] != meters) continue;
            foreach (var (from, to, length) in Links)
            {
                var next = from == station ? to : to == station ? from : null;
                if (next is null || distances.GetValueOrDefault(next, int.MaxValue) <= meters + length) continue;
                distances[next] = meters + length;
                queue.Enqueue(next, meters + length);
            }
        }

        return distances.Where(entry => entry.Key != origin)
            .OrderBy(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value));
    }
}
