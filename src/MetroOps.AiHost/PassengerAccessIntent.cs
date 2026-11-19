using System.Text.Json;
using System.Text.RegularExpressions;
using MetroOps.Contracts;

namespace MetroOps.AiHost;

internal sealed record PassengerAccessIntent(bool? Accessible, string? Tool = null, string Arguments = "{}",
    bool UseTripContext = false, string? MissingEvidenceText = null)
{
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string StepFree = @"step[- \u2010\u2011]free";
    private const string AccessTerms = @"\b(?:" + StepFree + @"|accessible|accessibility|wheelchair|no stairs|avoid stairs|without stairs)\b";
    private static readonly Regex Access = new(AccessTerms, MatchOptions);
    private static readonly Regex OptOut = new(
        @"\b(?:stairs (?:are|is) (?:ok|okay|fine)|(?:don['’]t|do not) need (?:a )?(?:" + StepFree +
        @"|accessible)|(?:no|without) (?:an? )?(?:accessibility requirement|" + StepFree + @" requirement))\b", MatchOptions);
    private static readonly Regex Lift = new(@"\b(?:elevators?|lifts?)\b", MatchOptions);
    private static readonly Regex AccessStatus = new(
        @"\b(?:out|outages?|working|broken|closed|operating|available|unavailable|in service|down)\b", MatchOptions);
    private static readonly Regex Trip = new(@"\bfrom\s+([^?!.,\r\n]+?)\s+to\s+([^?!.,\r\n]+)[?!. ]*$", MatchOptions);
    private static readonly Regex TripWithDetails = new(@"\bfrom\s+[^?!\r\n]{1,160}?\s+to\s+\S", MatchOptions);
    private static readonly Regex NearestStation = new(
        @"\b(?:nearest|closest)\s+(?:accessible|" + StepFree + @")\s+station(?:\s+right now)?\s+(?:to|near|from)\s+([^?!.,\r\n]+)[?!. ]*$", MatchOptions);
    private static readonly Regex Correction = new(
        @"^(?:but\s+|what about\s+)?(?:accessibility|" + StepFree + @"|wheelchair[- ]accessible)(?:\s+(?:please|too))?[\s?!.]*$", MatchOptions);
    private static readonly Regex Alternative = new(@"\b(?:nearest|closest|alternativ(?:e|es)|instead|another route)\b", MatchOptions);
    private static readonly Regex Nearest = new(@"\b(?:nearest|closest)\b", MatchOptions);
    private static readonly Regex AlternativeFollowUp = new(
        @"^(?:(?:what|which|is|are|can|any|give|show|find|how|where|try|nearest|closest|alternative|another)\b)", MatchOptions);
    private static readonly Regex TimeCue = new(
        @"\b(?:at|by|after|before|around)\s+\d|\d:\d|\b(?:tomorrow|tonight|today|morning|evening|return|round[- ]trip|later)\b", MatchOptions);
    private static readonly Regex Offer = new(
        @"\b(?:would you like|want (?:me|to)|shall i|can i|i can (?:help|plan|find|check)|tell me (?:the |your )?origin and destination|i(?:['’]ll| will) (?:plan|find|check))[^.?!\r\n]{0,100}" + AccessTerms, MatchOptions);

    public static PassengerAccessIntent Parse(AssistantRequest request)
    {
        var text = request.Message.Trim();
        if (OptOut.IsMatch(text)) return new(false);
        var history = request.History;
        var historyCount = history?.Count ?? 0;
        // The browser includes the current user message in both message and history.
        if (historyCount > 0 && history![historyCount - 1] is { Role: "user", Content: var last } &&
            last == request.Message)
            historyCount--;
        var previous = historyCount > 0 ? history![historyCount - 1] : null;
        var context = request.JourneyContext;
        var continuing = context is not null && previous is { Role: "assistant" } &&
            Network.Resolve(context.Destination) is { } recentDestination &&
            previous.Content.Contains(Network.Station(recentDestination).Name, StringComparison.OrdinalIgnoreCase);
        var stationMatch = NearestStation.Match(text);
        if (stationMatch.Success && Network.Resolve(stationMatch.Groups[1].Value) is { } station)
            return new(true, "check_step_free_access", JsonSerializer.Serialize(new { station }, Wire.Json));

        var explicitAccess = Access.IsMatch(text);
        var namesStation = Network.Definition.Stations.Any(s =>
            Regex.IsMatch(text, $@"\b{Regex.Escape(s.Name)}\b", MatchOptions));
        var trip = Trip.Match(text);
        var followsAccessExchange = previous is { Role: "assistant" } && (trip.Success || TripWithDetails.IsMatch(text)) &&
            (Offer.IsMatch(previous.Content) ||
             historyCount >= 2 && history![historyCount - 2] is { Role: "user", Content: var prior } &&
             !OptOut.IsMatch(prior) && !TripWithDetails.IsMatch(prior) &&
             (Access.IsMatch(prior) || Lift.IsMatch(prior) && AccessStatus.IsMatch(prior)));
        if (trip.Success && (explicitAccess || followsAccessExchange) && !TimeCue.IsMatch(text) &&
            Network.Resolve(trip.Groups[1].Value) is { } origin &&
            Network.Resolve(trip.Groups[2].Value) is { } destination)
        {
            return new(true, "plan_journey", JsonSerializer.Serialize(new { origin, destination }, Wire.Json));
        }

        if (context is not null && Correction.IsMatch(text))
            return new(true, "plan_journey", UseTripContext: true);

        // A short alternative request can reuse the last trip, but a named new trip or station cannot.
        if (continuing && !trip.Success && !TimeCue.IsMatch(text) && Alternative.IsMatch(text) &&
            AlternativeFollowUp.IsMatch(text) && (explicitAccess || context!.Accessible) && !namesStation)
        {
            return new(true, "plan_journey", UseTripContext: true);
        }

        var missingEvidence = Nearest.IsMatch(text) && explicitAccess
            ? !namesStation && !continuing ? "Which station are you starting from?" :
                "I couldn't verify which station is closest by modeled road distance."
            : null;
        return new(explicitAccess || followsAccessExchange ? true : null, MissingEvidenceText: missingEvidence);
    }
}
