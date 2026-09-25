namespace HassToast.Agent.Toasts;

/// <summary>
/// Tracks the toasts this agent has raised that can still be updated.
/// <para>
/// Windows requires a strictly increasing sequence number on every progress update and ignores
/// anything not newer than what it already holds, so the counter has to live somewhere. It
/// cannot live in Home Assistant: an automation firing update events has no idea how many the
/// agent has already applied, and a restart of either side would desynchronise it.
/// </para>
/// </summary>
public sealed class ToastRegistry
{
    private readonly Dictionary<(string Group, string Tag), uint> _sequences = [];
    private readonly Lock _gate = new();

    // A tuple rather than a concatenated string. Tags and groups are arbitrary payload
    // values, so ("ci", "build") and ("cibuild", "") would share a key, and forgetting the
    // group "ci" by prefix would sweep up "ci-nightly" with it.
    private static (string, string) Key(string tag, string? group) => (group ?? "", tag);

    /// <summary>Records a freshly raised toast and returns its opening sequence number.</summary>
    public uint Register(string tag, string? group)
    {
        lock (_gate)
        {
            _sequences[Key(tag, group)] = 1;
            return 1;
        }
    }

    /// <summary>
    /// Next sequence number for an update. Returns null when the tag was never raised by this
    /// agent, which usually means it was raised before a restart.
    /// </summary>
    public uint? NextSequence(string tag, string? group)
    {
        lock (_gate)
        {
            var key = Key(tag, group);
            if (!_sequences.TryGetValue(key, out var current)) return null;

            var next = current + 1;
            _sequences[key] = next;
            return next;
        }
    }

    /// <summary>
    /// Adopts a tag the agent does not remember, starting from a high sequence number.
    /// <para>
    /// After a restart the in-memory counter is gone but the notification may still be on screen,
    /// and Windows still holds the old sequence. Resuming from 1 would be silently ignored, so
    /// the count restarts far enough ahead to win.
    /// </para>
    /// </summary>
    public uint Adopt(string tag, string? group)
    {
        lock (_gate)
        {
            const uint restartBase = 1_000_000;
            _sequences[Key(tag, group)] = restartBase;
            return restartBase;
        }
    }

    public void Forget(string tag, string? group)
    {
        lock (_gate) _sequences.Remove(Key(tag, group));
    }

    public void ForgetGroup(string group)
    {
        lock (_gate)
        {
            foreach (var key in _sequences.Keys
                         .Where(k => string.Equals(k.Group, group, StringComparison.Ordinal))
                         .ToList())
            {
                _sequences.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate) _sequences.Clear();
    }

    public int Count
    {
        get { lock (_gate) return _sequences.Count; }
    }
}
