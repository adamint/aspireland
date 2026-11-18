using System.Collections.Concurrent;
using MetroOps.Contracts;

namespace MetroOps.AiHost;

internal sealed class ProposalStore
{
    private const int MaximumEntries = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public ActionProposal Add(ServiceAction action, ScenarioContext world, string explanation)
    {
        Cleanup();
        if (_entries.Count >= MaximumEntries)
        {
            var oldest = _entries.OrderBy(pair => pair.Value.CreatedAt).FirstOrDefault();
            if (!string.IsNullOrEmpty(oldest.Key))
            {
                _entries.TryRemove(oldest.Key, out _);
            }
        }

        var proposal = new ActionProposal(
            Guid.NewGuid().ToString("N"),
            action,
            world.ScenarioId,
            world.Generation,
            world.Version,
            explanation);
        _entries[proposal.Id] = new Entry(proposal, DateTimeOffset.UtcNow);
        return proposal;
    }

    public bool TryGet(string id, out ActionProposal? proposal)
    {
        proposal = null;
        if (!_entries.TryGetValue(id, out var entry))
        {
            return false;
        }
        if (entry.CreatedAt + Lifetime <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(id, out _);
            return false;
        }
        proposal = entry.Proposal;
        return true;
    }

    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - Lifetime;
        foreach (var pair in _entries)
        {
            if (pair.Value.CreatedAt <= cutoff)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record Entry(ActionProposal Proposal, DateTimeOffset CreatedAt);
}
