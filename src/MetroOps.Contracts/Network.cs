namespace MetroOps.Contracts;

public static class Network
{
    public static readonly NetworkDefinition Definition = new(
    [
        new("pinecrest", "Pinecrest", 120, 350, "residential", 120, 200, 1, .1, 0, .1),
        new("westhaven", "Westhaven", 300, 390, "residential", 150, 220, .8, .4, 0, .2),
        new("west-junction", "West Junction", 500, 500, "junction", 180, 260, .6, .4, 0, .2),
        new("civic-square", "Civic Square", 670, 500, "government", 180, 260, .1, 1, .1, .1),
        new("central-exchange", "Central Exchange", 850, 500, "business", 280, 360, .2, 1.6, .2, .5),
        new("union-terminal", "Union Terminal", 1030, 500, "terminal", 260, 340, .3, 1.1, .1, .5),
        new("east-junction", "East Junction", 1200, 500, "junction", 180, 260, .7, .4, 0, .2),
        new("riverbank", "Riverbank", 1330, 365, "riverfront", 150, 220, .7, .7, .1, .8),
        new("eastgate", "Eastgate", 1450, 250, "residential", 140, 220, 1.1, .2, 0, .2),
        new("airport", "Airport", 100, 1100, "airport", 220, 300, .1, .8, 0, .3),
        new("aerocity", "Aerocity", 300, 830, "hotel", 130, 200, .3, .8, 0, .3),
        new("stadium", "Stadium", 1330, 700, "stadium", 160, 280, .1, .2, 0, .5),
        new("fairgrounds", "Fairgrounds", 1460, 845, "leisure", 160, 260, .2, .1, 0, 1.2),
        new("northgate", "Northgate", 1120, 100, "residential", 120, 200, 1, .2, 0, .1),
        new("university", "University", 1040, 275, "campus", 180, 260, .6, .5, 1.3, .5),
        new("museum-quarter", "Museum Quarter", 920, 710, "museum", 140, 220, .3, .3, .4, 1.1),
        new("medical-center", "Medical Center", 790, 865, "medical", 180, 260, .3, 1, .3, .1),
        new("southbank", "Southbank", 720, 1080, "residential", 120, 220, 1.1, .2, 0, .3)
    ],
    [
        BuildLine("Blue", "Blue", "#4b8ff5", ["pinecrest", "westhaven", "west-junction", "civic-square", "central-exchange", "union-terminal", "east-junction", "riverbank", "eastgate"], 115, 480),
        BuildLine("Amber", "Amber local", "#f0ac32", ["airport", "aerocity", "west-junction", "civic-square", "central-exchange", "union-terminal", "east-junction", "stadium", "fairgrounds"], 120, 1440),
        BuildLine("Green", "Green", "#46b887", ["northgate", "university", "union-terminal", "museum-quarter", "central-exchange", "medical-center", "southbank"], 135, 600),
        BuildLine("AX", "Airport Express", "#bd88ed", ["airport", "central-exchange", "union-terminal"], 300, 1440)
    ], BuildAccess());

    public static StationDefinition Station(string id) => Definition.Stations.First(s => s.Id == id);
    public static LineDefinition Line(string id) => Definition.Lines.First(l => l.Id == id);
    public static string Group(string line) => line is "Blue" or "Amber" ? "BA" : line;
    public static string Platform(string station, string line, int direction) => $"{station}:{Group(line)}:{(direction > 0 ? "+" : "-")}";
    public static string Block(string line, string from, string to) => $"{Group(line)}:{from}>{to}";
    public static int TravelSeconds(string line, string from, string to) =>
        TravelSeconds(line, from, to, Line(line).TravelSeconds);

    private static int TravelSeconds(string line, string from, string to, int defaultSeconds) => (line, from, to) switch
    {
        ("AX", "airport", "central-exchange") or ("AX", "central-exchange", "airport") => 1080,
        ("AX", _, _) => 105,
        ("Amber", "airport", "aerocity") or ("Amber", "aerocity", "airport") => 540,
        ("Amber", "aerocity", "west-junction") or ("Amber", "west-junction", "aerocity") => 480,
        _ => defaultSeconds
    };
    public static string? Resolve(string text)
    {
        var normalized = NormalizeStationName(text);
        var exact = Definition.Stations.FirstOrDefault(s => s.Id == normalized || s.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Id;
        return normalized switch
        {
            "campus" => "university", "hospital" => "medical-center", "city hall" => "civic-square",
            "museum" => "museum-quarter", "stadium" => "stadium", _ => null
        };
    }

    public static string NormalizeStationName(string text)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (normalized.StartsWith("the ", StringComparison.Ordinal)) normalized = normalized[4..];
        foreach (var suffix in new[] { " metro station", " station" })
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
                return normalized[..^suffix.Length];
        return normalized;
    }

    private static LineDefinition BuildLine(string id, string name, string color, string[] stations, int travelSeconds, int headwaySeconds) =>
        new(id, name, color, stations, travelSeconds,
            stations.Zip(stations.Skip(1), (from, to) => TravelSeconds(id, from, to, travelSeconds)).ToArray(),
            headwaySeconds);

    private static List<AccessEdge> BuildAccess()
    {
        string[] ids = ["pinecrest", "westhaven", "west-junction", "civic-square", "central-exchange", "union-terminal", "east-junction", "riverbank", "eastgate", "airport", "aerocity", "stadium", "fairgrounds", "northgate", "university", "museum-quarter", "medical-center", "southbank"];
        var edges = new List<AccessEdge>();
        foreach (var id in ids)
        {
            edges.Add(new(id, "street", "concourse", 35, 160, true, null));
            edges.Add(new(id, "concourse", "platform", 45, 120, false, $"{id}:escalator"));
            edges.Add(new(id, "concourse", "platform", 65, 24, true, $"{id}:elevator"));
            edges.Add(new(id, "platform", "transfer", 100, 100, false, $"{id}:transfer-stairs"));
            edges.Add(new(id, "platform", "transfer", 130, 24, true, $"{id}:elevator"));
        }
        return edges;
    }
}
