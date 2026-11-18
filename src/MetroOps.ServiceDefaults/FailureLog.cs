namespace MetroOps.ServiceDefaults;

public sealed class FailureLog(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private string? lastFailure;
    private long lastReported;

    public void Report(string key, Action write)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            if (lastFailure == key && clock.GetElapsedTime(lastReported, now) < TimeSpan.FromMinutes(1)) return;
            lastFailure = key;
            lastReported = now;
            write();
        }
    }

    public void Recover(Action write)
    {
        lock (gate)
        {
            if (lastFailure is null) return;
            lastFailure = null;
            write();
        }
    }
}
