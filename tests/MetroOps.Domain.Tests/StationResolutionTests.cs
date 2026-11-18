using MetroOps.Contracts;
using Xunit;

namespace MetroOps.Domain.Tests;

public sealed class StationResolutionTests
{
    [Theory]
    [InlineData("Airport", "airport")]
    [InlineData(" the airport ", "airport")]
    [InlineData("airport station", "airport")]
    [InlineData("Airport metro station", "airport")]
    [InlineData(" THE   AIRPORT  METRO   STATION ", "airport")]
    [InlineData("Union Terminal station", "union-terminal")]
    [InlineData("medical-center", "medical-center")]
    public void OrdinaryStationWrappersPreserveCuratedResolution(string text, string expected) =>
        Assert.Equal(expected, Network.Resolve(text));

    [Theory]
    [InlineData("gate")]
    [InlineData("Airport Terminal 1")]
    [InlineData("Airport Terminal 2 station")]
    [InlineData("airport station annex")]
    [InlineData("station")]
    public void PartialOrUnknownStationsAreNotSilentlyMatched(string text) =>
        Assert.Null(Network.Resolve(text));
}
