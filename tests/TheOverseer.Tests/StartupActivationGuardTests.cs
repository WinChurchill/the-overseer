using TheOverseer.App.Desktop;

namespace TheOverseer.Tests;

public sealed class StartupActivationGuardTests
{
    private const long NoActivate = 0x08000000;

    [Fact]
    public void TimeoutRestoresOnlyOwnedBitEvenWithoutPriorForeground()
    {
        long style = 0x100;
        var guard = new StartupActivationGuard(() => style, value => style = value, () => { });
        Assert.Equal(0x100 | NoActivate, style);
        style |= 0x40000; // A legitimate style change after startup must survive.
        for (var tick = 0; tick < 20; tick++) guard.Tick();
        Assert.False(guard.IsActive);
        Assert.Equal(0x40100, style);
    }

    [Fact]
    public void StopBeforeExplicitActivationPreventsAnyLaterFocusRestoration()
    {
        long style = 0;
        var restores = 0;
        var guard = new StartupActivationGuard(() => style, value => style = value, () => restores++);
        guard.Tick();
        Assert.Equal(1, restores);
        guard.Stop(); // Same synchronous cancellation used by explicit show and input.
        Assert.Equal(0, style & NoActivate);
        for (var tick = 0; tick < 30; tick++) guard.Tick();
        guard.Stop();
        Assert.Equal(1, restores);
    }

    [Fact]
    public void EarlyShutdownAndRepeatedCleanupPreservePreexistingNoActivate()
    {
        long style = NoActivate | 0x100;
        var writes = 0;
        var guard = new StartupActivationGuard(() => style, value => { style = value; writes++; },
            () => throw new InvalidOperationException("cleanup must not restore focus"));
        guard.Stop();
        guard.Stop();
        guard.Tick();
        Assert.Equal(0, writes);
        Assert.Equal(NoActivate | 0x100, style);
    }
}
