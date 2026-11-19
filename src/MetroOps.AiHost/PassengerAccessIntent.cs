using System.Text.Json;
using System.Text.RegularExpressions;
using MetroOps.Contracts;

namespace MetroOps.AiHost;

internal sealed record PassengerAccessIntent(bool? Accessible, string? Tool = null, string Arguments = "{}",
    bool UseTripContext = false, string? MissingEvidenceText = null)
{
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string AccessTerms = @"\b(?:step[- ]free|accessible|accessibility|wheelchair|no stairs|avoid stairs|without stairs)\b";
    private static readonly Regex Access = new(AccessTerms, MatchOptions);
    private static readonly Regex OptOut = new(
        @"\b(?:stairs (?:are|is) (?:ok|okay|fine)|(?:don['’]t|do not) need (?:a )?(?:step[- ]free|accessible)|(?:no|without) (?:an? )?(?:accessibility requirement|step[- ]free requirement))\b", MatchOptions);
    private static readonly Regex Trip = new(@"\bfrom\s+([^?!.,\r\n]+?)\s+to\s+([^?!.,\r\n]+)[?!. ]*$", MatchOptions);
    private static readonly Regex NearestStation = new(
        @"\b(?:nearest|closest)\s+(?:accessible|step[- ]free)\s+station(?:\s+right now)?\s+(?:to|near|from)\s+([^?!.,\r\n]+)[?!. ]*$", MatchOptions);
    private static readonly Regex Correction = new(
        @"^(?:but\s+|what about\s+)?(?:accessibility|step[- ]free|wheelchair[- ]accessible)(?:\s+(?:please|too))?[\s?!.]*$", MatchOptions);
    private static readonly Regex Alternative = new(@"\b(?:nearest|closest|alternativ(?:e|es)|instead|another route)\b", MatchOptions);
    private static readonly Regex Nearest = new(@"\b(?:nearest|closest)\b", MatchOptions);
    private static readonly Regex AlternativeFollowUp = new(
        @"^(?:(?:what|which|is|are|can|any|give|show|find|how|where|try|nearest|closest|alternative|another)\b)", MatchOptions);
    private static readonly Regex TimeCue = new(
        @"\b(?:at|by|after|before|around)\s+\d|\d:\d|\b(?:tomorrow|tonight|today|morning|evening|return|round[- ]trip|later)\b", MatchOptions);
    private static readonly Regex Offer = new(
        @"(?:would you like|want (?:me|to)|shall i|can i|i can (?:help|plan|find|check))[^.?!\r\n]{0,100}" + AccessTerms, MatchOptions);

    public static PassengerAccessIntent Parse(AssistantRequest request)
    {
        var text = request.Message.Trim();
        if (OptOut.IsMatch(text)) return new(false);
        var previous = request.History?.LastOrDefault();
        var context = request.JourneyContext;
        var continuing = context is not null && previous is { Role: "assistant" } &&
            Network.Resolve(context.Destination) is { } recentDestination &&
            previous.Content.Contains(Network.Station(recentDestination).Name, StringComparison.OrdinalIgnoreCase);
        var stationMatch = NearestStation.Match(text);
        if (stationMatch.Success && Network.Resolve(stationMatch.Groups[1].Value) is { } station)
            return continuing && station == Network.Resolve(context!.Origin)
                ? new(true, "plan_journey", UseTripContext: true)
                : new(true, "check_step_free_access", JsonSerializer.Serialize(new { station }, Wire.Json));

        var explicitAccess = Access.IsMatch(text);
        var namesStation = Network.Definition.Stations.Any(s =>
            Regex.IsMatch(text, $@"\b{Regex.Escape(s.Name)}\b", MatchOptions));
        var trip = Trip.Match(text);
        var acceptedOffer = trip.Success && previous is { Role: "assistant" } && Offer.IsMatch(previous.Content);
        if (trip.Success && (explicitAccess || acceptedOffer) && !TimeCue.IsMatch(text) &&
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
        return new(explicitAccess || acceptedOffer ? true : null, MissingEvidenceText: missingEvidence);
    }
}
