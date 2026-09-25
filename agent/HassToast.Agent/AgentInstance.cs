using System.Diagnostics;

namespace HassToast.Agent;

/// <summary>
/// The two pieces of cross-process state the running agent owns: the claim that makes it the one
/// agent for this session, and the signal an installer or uninstaller uses to ask it to stop.
/// <para>
/// Both names sit in the <c>Local\</c> namespace, which is per-session rather than machine-wide.
/// That is deliberate rather than incidental: a toast can only be raised from a signed-in user's
/// session, so two users signed in at once each want their own agent, and a <c>Global\</c> name
/// would let whoever signed in first lock the other out of notifications entirely.
/// </para>
/// </summary>
public sealed class AgentInstance : IDisposable
{
    /// <summary>Name of the executable, for the process-enumeration liveness check.</summary>
    public const string ProcessName = "HassToast.Agent";

    private const string MutexName = @"Local\HassToast.Agent.Primary";
    private const string QuitEventName = @"Local\HassToast.Agent.Quit";

    private readonly Mutex _mutex;
    private EventWaitHandle? _quitEvent;
    private RegisteredWaitHandle? _quitWait;
    private bool _disposed;

    private AgentInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Claims primary-instance status, or returns null when another agent already holds it.
    /// <para>
    /// A mutex is safe here now, though it was not always. The objection on record was that a
    /// process cold-launched to deliver a toast click would find the mutex taken, exit, and
    /// silently discard the click. Clicks no longer arrive that way: they come through the COM
    /// activator, which routes them to whichever process holds the registered class object. A
    /// cold launch therefore only happens when nothing is running, in which case the mutex is
    /// uncontended and this instance goes on to serve the activation itself.
    /// </para>
    /// </summary>
    public static AgentInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing it — a crash, or a taskkill. Everything
            // the mutex guards is process-local, so there is no half-written state to repair;
            // refusing to start here would strand the user with an agent that never comes back.
            acquired = true;
        }

        if (acquired) return new AgentInstance(mutex);

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Invokes <paramref name="onQuit"/> when another process calls <see cref="RequestQuit"/>.
    /// The callback arrives on a thread-pool thread, so a UI caller must marshal it itself.
    /// </summary>
    public void ListenForQuit(Action onQuit)
    {
        // Manual reset: the requester sets it once and the agent is going away regardless, so
        // there is nothing to be gained by racing to reset it.
        _quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);

        _quitWait = ThreadPool.RegisterWaitForSingleObject(
            _quitEvent, (_, _) => onQuit(), state: null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>
    /// Asks a running agent in this session to shut down, and waits until it has gone.
    /// Returns false if there was nothing to ask, or if it was still there when time ran out.
    /// </summary>
    /// <remarks>
    /// The event only confirms the request was delivered. What the caller actually needs is that
    /// the process is gone, because everything an uninstaller does next depends on it: the exe
    /// cannot be deleted while it is mapped, and the agent re-creates its CLSID registration on
    /// every start, so removing that first would simply be undone.
    /// </remarks>
    public static bool RequestQuit(TimeSpan timeout)
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
        {
            // Nothing is listening. Either no agent is running, or one predating this signal is —
            // so report failure and let the caller fall back rather than assume success.
            return !IsRunning();
        }

        using (quit) quit.Set();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning()) return true;
            Thread.Sleep(100);
        }

        return !IsRunning();
    }

    /// <summary>
    /// Whether an agent process is running, found by enumeration rather than by inspecting the
    /// mutex.
    /// <para>
    /// Deliberately independent of the single-instance mechanism: that is the very thing
    /// <c>--no-single-instance</c> exists to switch off, and a liveness check that goes blind
    /// when the thing it inspects is disabled is worthless. Enumerating is also read-only, where
    /// probing the mutex from a short-lived command risks claiming it from the real agent.
    /// </para>
    /// </summary>
    public static bool IsRunning()
    {
        try
        {
            var self = Environment.ProcessId;
            return Process.GetProcessesByName(ProcessName).Any(p => p.Id != self);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not enumerate processes: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _quitWait?.Unregister(null);
        _quitEvent?.Dispose();

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owning thread, or never acquired. Disposing is enough either way.
        }

        _mutex.Dispose();
    }
}
