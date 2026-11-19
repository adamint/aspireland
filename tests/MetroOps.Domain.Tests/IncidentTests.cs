using MetroOps.Contracts;
using MetroOps.Domain;
using Xunit;

namespace MetroOps.Domain.Tests;

public sealed class IncidentTests
{
    [Fact]
    public void RandomServiceAlertOpensOneAuditedEligibleIncident()
    {
        var world = Kernel.Create(20261118);
        world.Incidents.Add(new()
        {
            Id = "existing",
            Kind = "elevator-outage",
            Target = "central-exchange",
            Description = "Existing outage.",
            StartedAt = world.SimTime,
            EndsAt = world.SimTime.AddHours(3)
        });
        var beforeHistory = world.History.Count;

        var command = Command(world, "random-alert", "random-incident");
        var result = Kernel.Apply(world, command);
        var retry = Kernel.Apply(world, command);

        Assert.True(result.Accepted, result.Message);
        Assert.Equal(result, retry);
        Assert.Equal("accepted", result.Status);
        var incident = Assert.Single(world.Incidents, i => i.Id == result.EntityId);
        Assert.NotEqual(("elevator-outage", "central-exchange"), (incident.Kind, incident.Target));
        Assert.Contains(Network.Station(incident.Target).Name, result.Message);
        Assert.Contains("estimated restoration in ", result.Message);
        Assert.Single(world.History.Skip(beforeHistory), e => e.Kind == "incident-opened");
        Assert.Single(world.History.Skip(beforeHistory), e => e.Kind == "command-accepted");
    }

    [Fact]
    public void RandomElevatorAlertUsesAnHoursLongRestorationEstimate()
    {
        var world = Kernel.Create(20261118);
        Incident? elevator = null;

        for (var index = 0; index < 200 && elevator is null; index++)
        {
            var result = Kernel.Apply(world, Command(world, $"random-alert-{index}", "random-incident"));
            Assert.True(result.Accepted, result.Message);
            var incident = world.Incidents.Single(i => i.Id == result.EntityId);
            if (incident.Kind == "elevator-outage")
            {
                elevator = incident;
            }
            else
            {
                Assert.True(Kernel.Apply(world, Command(world, $"clear-{index}", "clear-incident", incident.Id)).Accepted);
            }
        }

        Assert.NotNull(elevator);
        Assert.InRange(elevator.EndsAt!.Value - elevator.StartedAt, TimeSpan.FromHours(2), TimeSpan.FromHours(24));
    }

    [Fact]
    public void RandomAlertsKeepTheActiveLiftCap()
    {
        var world = Kernel.Create();
        Assert.True(Kernel.Apply(world, Command(world, "existing", "incident",
            "central-exchange", "elevator-outage", 10_800)).Accepted);
        for (var index = 0; index < 30; index++)
        {
            var result = Kernel.Apply(world, Command(world, $"random-{index}", "random-incident"));
            Assert.True(result.Accepted, result.Message);
            var incident = world.Incidents.Single(i => i.Id == result.EntityId);
            Assert.NotEqual("elevator-outage", incident.Kind);
            Assert.True(Kernel.Apply(world, Command(world, $"clear-{index}", "clear-incident", incident.Id)).Accepted);
        }
    }

    [Fact]
    public void StaleRandomAlertDoesNotSelectOrOpenAnIncident()
    {
        var world = Kernel.Create();
        var command = Command(world, "stale-random-alert", "random-incident") with { Generation = world.Generation - 1 };

        var result = Kernel.Apply(world, command);

        Assert.False(result.Accepted);
        Assert.Equal("rejected", result.Status);
        Assert.Equal("Stale generation.", result.Message);
        Assert.Empty(world.Incidents);
        Assert.DoesNotContain(world.History, e => e.Kind == "incident-opened");
    }

    [Fact]
    public void RiderProjectionIncludesEstimatedRestoration()
    {
        var world = Kernel.Create();
        var result = Kernel.Apply(world, Command(world, "elevator-alert", "incident",
            "central-exchange", "elevator-outage", 10_800));
        var incident = world.Incidents.Single(i => i.Id == result.EntityId);

        var riderIncident = Assert.Single(Kernel.ProjectRiders(world).Incidents);

        Assert.Equal(incident.EndsAt, riderIncident.EndsAt);
        Assert.True(Kernel.Apply(world, Command(world, "clear-elevator-alert", "clear-incident", incident.Id)).Accepted);
        Assert.Empty(Kernel.ProjectRiders(world).Incidents);
    }

    [Fact]
    public void StochasticElevatorOutagesAreRarerAndLongerThanBriefCirculationIssues()
    {
        var elevators = new List<Incident>();
        var circulation = new List<Incident>();
        foreach (var seed in new long[] { 11, 42, 123, 456, 789, 2026, 20261117, 87654 })
        {
            var world = Kernel.Create(seed);
            Kernel.Tick(world, [], 14_400);
            var worldElevators = world.Incidents.Where(i => i.Source == "stochastic" && i.Kind == "elevator-outage").ToArray();
            elevators.AddRange(worldElevators);
            circulation.AddRange(world.Incidents.Where(i => i.Source == "stochastic" && i.Kind == "circulation"));
            var orderedElevators = worldElevators.OrderBy(i => i.StartedAt).ToArray();
            Assert.DoesNotContain(orderedElevators.Zip(orderedElevators.Skip(1)),
                pair => pair.First.EndsAt > pair.Second.StartedAt);
        }

        Assert.NotEmpty(elevators);
        Assert.True(circulation.Count > elevators.Count * 2,
            $"Expected brief circulation issues to remain more frequent, but saw {circulation.Count} circulation and {elevators.Count} elevator incidents.");
        Assert.All(circulation, incident =>
            Assert.InRange(incident.EndsAt!.Value - incident.StartedAt, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(4)));
        Assert.All(elevators, incident =>
            Assert.InRange(incident.EndsAt!.Value - incident.StartedAt, TimeSpan.FromHours(2), TimeSpan.FromHours(24)));
    }

    private static CommandEnvelope Command(
        WorldState world,
        string id,
        string kind,
        string? target = null,
        string? value = null,
        double? amount = null) =>
        new(id, world.ScenarioId, world.Generation, world.Version, "operator", "Test service alert",
            new(kind, target, value, amount));
}
