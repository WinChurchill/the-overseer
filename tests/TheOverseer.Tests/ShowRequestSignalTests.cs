using TheOverseer.App.Desktop;

namespace TheOverseer.Tests;

/// <summary>
/// The second-launch restore path is a real named-event round trip: the tests use
/// the production type (not a stub) so the signal, the waiter thread and disposal
/// are all exercised. The App wiring that calls TrySignal before exiting is covered
/// by the packaged second-instance lifecycle check.
/// </summary>
public sealed class ShowRequestSignalTests
{
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task RepeatedSignalsReachTheListeningInstance()
    {
        var delivered = 0;
        // A unique scope isolates this test from any HUD that happens to be running.
        var scope = "test-" + Guid.NewGuid().ToString("N");
        using var listener = ShowRequestSignal.TryListen(() => Interlocked.Increment(ref delivered), scope);
        Assert.NotNull(listener);

        Assert.True(ShowRequestSignal.TrySignal(scope));
        Assert.True(await WaitForAsync(() => Volatile.Read(ref delivered) >= 1), "first show request was not delivered");

        // Auto-reset semantics: a later launch must be able to restore again.
        Assert.True(ShowRequestSignal.TrySignal(scope));
        Assert.True(await WaitForAsync(() => Volatile.Read(ref delivered) >= 2), "second show request was not delivered");
    }

    [Fact]
    public async Task ADisposedListenerStopsDeliveringAndSignallingStaysSafe()
    {
        var delivered = 0;
        var scope = "test-" + Guid.NewGuid().ToString("N");
        var listener = ShowRequestSignal.TryListen(() => Interlocked.Increment(ref delivered), scope);
        Assert.NotNull(listener);
        listener!.Dispose();
        listener.Dispose();

        // Never throws, whether or not a stale kernel object is still visible.
        _ = ShowRequestSignal.TrySignal(scope);
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref delivered));
    }
}
