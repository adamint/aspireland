using System.Text.Json;
using System.Xml.Linq;

namespace MetroOps.Runtime.Tests;

public sealed class ServiceBoundaryTests
{
    [Fact]
    public void EachServiceOwnsAnExecutableAndReadOnlyHostsHaveNoInfrastructureOrModelClients()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MetroOps.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var projects = new[] { "Scenario", "Control", "Dispatch", "Incidents", "Planner", "Information", "OperationsApi", "PassengerApi" };
        var solution = XDocument.Load(Path.Combine(root.FullName, "MetroOps.slnx"))
            .Descendants("Project").Select(p => p.Attribute("Path")!.Value).ToHashSet();
        foreach (var service in projects)
        {
            var directory = Path.Combine(root.FullName, "src", $"MetroOps.{service}");
            var project = $"src/MetroOps.{service}/MetroOps.{service}.csproj";
            Assert.True(File.Exists(Path.Combine(root.FullName, project)), $"Missing concrete service project: {project}");
            Assert.Contains(project, solution);
            Assert.True(File.Exists(Path.Combine(directory, "Program.cs")));
        }
        Assert.False(File.Exists(Path.Combine(root.FullName, "src/MetroOps.ServiceHost/MetroOps.ServiceHost.csproj")));
        foreach (var service in new[] { "Planner", "PassengerApi", "OperationsApi" })
        {
            using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "src", $"MetroOps.{service}", "obj/project.assets.json")));
            var dependencies = assets.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name).ToArray();
            Assert.DoesNotContain(dependencies, name =>
                name.StartsWith("Npgsql/", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("RabbitMQ.Client/", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("StackExchange.Redis/", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("OpenAI/", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Azure.AI.OpenAI/", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("MetroOps.Simulation.Persistence/", StringComparison.OrdinalIgnoreCase));
        }
    }
}
