using Xunit;

namespace HassToast.Agent.Tests;

/// <summary>
/// Single-instancing and the quit signal.
/// <para>
/// Worth pinning because the mutex replaced <c>AppInstance</c> on an argument about behaviour,
/// not about tidiness: the objection to a mutex was that a process cold-launched to deliver a
/// toast click would find it taken, exit, and lose the click. That is only sound while clicks
/// travel through the single-instance mechanism, and they no longer do — so what has to hold is
/// that acquiring is uncontended whenever nothing else is running.
/// </para>
/// </summary>
public sealed class AgentInstanceTests
{
    [Fact]
    public void A_second_instance_cannot_claim_the_mutex()
    {
        using var first = AgentInstance.TryAcquire();
        Assert.NotNull(first);

        // The contended claim has to come from another thread. A Win32 mutex is recursive for the
        // thread that owns it, so a second TryAcquire here would re-enter the claim this test
        // already holds and hand back an instance — proving nothing about a second agent. Ownership
        // is per-thread, so a foreign thread sees what a foreign process sees.
        AgentInstance? second = null;
        var thread = new Thread(() => second = AgentInstance.TryAcquire());
        thread.Start();
        thread.Join();

        using (second) Assert.Null(second);
    }

    [Fact]
    public void The_claim_is_released_on_dispose()
    {
        using (var first = AgentInstance.TryAcquire())
        {
            Assert.NotNull(first);
        }

        // The cold-launch case: nothing is running, so the process Windows started to deliver a
        // click acquires without contention and goes on to serve the activation itself.
        using var next = AgentInstance.TryAcquire();
        Assert.NotNull(next);
    }

    [Fact]
    public void An_abandoned_mutex_does_not_lock_the_agent_out()
    {
        var thread = new Thread(() =>
        {
            // Acquired and never released — what a crash or a taskkill leaves behind.
            _ = AgentInstance.TryAcquire();
        });

        thread.Start();
        thread.Join();

        using var recovered = AgentInstance.TryAcquire();
        Assert.NotNull(recovered);
    }

    [Fact]
    public void Requesting_a_quit_reaches_a_listening_instance()
    {
        using var instance = AgentInstance.TryAcquire();
        Assert.NotNull(instance);

        using var asked = new ManualResetEventSlim();
        instance.ListenForQuit(asked.Set);

        Assert.True(AgentInstance.RequestQuit(TimeSpan.FromSeconds(5)));
        Assert.True(asked.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Requesting_a_quit_with_nothing_listening_is_not_an_error()
    {
        // No agent process exists under the test host, so this is the "nothing to ask" path:
        // it must report the absence rather than block for the full timeout.
        var started = DateTime.UtcNow;
        var result = AgentInstance.RequestQuit(TimeSpan.FromSeconds(10));

        Assert.True(result);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
