using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MetroOps.Simulation.Persistence;

public static class PersistenceExtensions
{
    public static void AddSimulationPersistence(this WebApplicationBuilder builder)
    {
        builder.AddNpgsqlDataSource("metro");
        builder.Services.AddSingleton<WorldStore>();
    }
}
