using MetroOps.Contracts;

namespace MetroOps.Domain;

public static class CommandPolicy
{
    public static CommandResult Apply(WorldState world, CommandEnvelope command, bool agent, bool approved = false)
    {
        if (command.ScenarioId != world.ScenarioId || command.Generation != world.Generation)
            return Denied(world, "stale", "The scenario changed. Refresh before applying this action.");
        if (command.ExpectedVersion is { } version && version > world.Version)
            return Denied(world, "stale", "The requested state version does not exist.");
        // Model proposals expire quickly in a moving system; ordinary worker ticks do not invalidate every approval.
        if (approved && command.ExpectedVersion is { } proposed && world.Version - proposed > 200)
            return Denied(world, "stale", "This proposal is too old. Compare again against the current checkpoint.");
        if (agent)
        {
            if (world.AuthorityMode != "bounded")
                return Denied(world, "denied", $"Agent execution is disabled in {world.AuthorityMode} mode.");
            if (world.LastAgentActionAt is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(30))
                return Denied(world, "denied", "Bounded autonomy allows one action per 30 wall-clock seconds.");
            var action = command.Action;
            if (action.Kind is not ("hold-train" or "release-train" or "deploy-reserve" or "set-headway"))
                return Denied(world, "denied", "This action is outside the bounded-autonomy allowlist.");
            if (action.Kind == "hold-train" && (action.Amount is null or <= 0 or > 180))
                return Denied(world, "denied", "Agent holds must be between 1 and 180 simulated seconds.");
            if (action.Kind == "set-headway" && (action.Amount is null or < 300 or > 900))
                return Denied(world, "denied", "Agent headways must retain service between 5 and 15 minutes.");
        }
        var result = Kernel.Apply(world, command);
        if (agent && result.Accepted) world.LastAgentActionAt = DateTimeOffset.UtcNow;
        return result;
    }

    private static CommandResult Denied(WorldState world, string status, string message) => new(false, status, message, world.Version);
}
