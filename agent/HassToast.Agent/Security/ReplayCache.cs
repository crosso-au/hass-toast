namespace HassToast.Agent.Security;

/// <summary>
/// Remembers recently seen nonces so a captured envelope cannot be replayed inside its
/// validity window.
/// <para>
/// Bounded on both axes deliberately. Entries expire once they fall outside the timestamp
/// tolerance, because at that point the timestamp check rejects them anyway and holding them
/// wins nothing. A hard capacity ceiling on top means a flood of forged nonces exhausts an
/// attacker's effort rather than the agent's memory.
/// </para>
/// </summary>
public sealed class ReplayCache
{
    private readonly TimeSpan _retention;
    private readonly int _capacity;
    private readonly Dictionary<string, DateTimeOffset> _seen;
    private readonly Lock _gate = new();

    public ReplayCache(TimeSpan retention, int capacity = 4096)
    {
        _retention = retention;
        _capacity = capacity;
        _seen = new Dictionary<string, DateTimeOffset>(capacity, StringComparer.Ordinal);
    }

    public int Count
    {
        get { lock (_gate) return _seen.Count; }
    }

    /// <summary>
    /// Records the nonce and reports whether it is new. A false return means a replay.
    /// </summary>
    public bool TryRegister(string nonce, DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);

            if (_seen.ContainsKey(nonce)) return false;

            if (_seen.Count >= _capacity)
            {
                // Everything still held is inside the retention window, so there is no
                // harmless entry to drop. Evicting the oldest would let its nonce be replayed;
                // refusing the new one does not. Fail closed.
                return false;
            }

            _seen[nonce] = now;
            return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (_seen.Count == 0) return;

        var cutoff = now - _retention;
        List<string>? expired = null;

        foreach (var (nonce, seenAt) in _seen)
        {
            if (seenAt < cutoff) (expired ??= []).Add(nonce);
        }

        if (expired is null) return;
        foreach (var nonce in expired) _seen.Remove(nonce);
    }
}
