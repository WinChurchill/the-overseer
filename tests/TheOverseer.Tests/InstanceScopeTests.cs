using TheOverseer.App.Desktop;

namespace TheOverseer.Tests;

/// <summary>
/// One instance identity for the mutex and the restore signal. The regression this
/// covers: the mutex used one fixed Local name while the signal hashed the data
/// directory, so an isolated normal-mode run lost the mutex and signalled the wrong
/// scope. Every scope below is unique to the test run so a live HUD is never touched.
/// </summary>
public sealed class InstanceScopeTests
{
    private static string Unique() => Path.Combine(Path.GetTempPath(), "overseer-scope-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SameDirectorySpellingsShareOneScope()
    {
        var directory = Unique();
        var canonical = InstanceScope.Canonical(directory);

        Assert.Equal(canonical, InstanceScope.Canonical(directory + Path.DirectorySeparatorChar));
        Assert.Equal(canonical, InstanceScope.Canonical(directory.ToUpperInvariant()));
        Assert.Equal(canonical, InstanceScope.Canonical("  " + directory + "  "));
        Assert.Equal(canonical, InstanceScope.Canonical(Path.Combine(directory, ".", "sub", "..")));
    }

    [Fact]
    public void DifferentDirectoriesAreDifferentInstances()
    {
        var first = Unique();
        var second = Unique();

        Assert.NotEqual(InstanceScope.Canonical(first), InstanceScope.Canonical(second));
        Assert.NotEqual(InstanceScope.MutexName(first), InstanceScope.MutexName(second));
        Assert.NotEqual(InstanceScope.ShowRequestEventName(first), InstanceScope.ShowRequestEventName(second));
    }

    [Fact]
    public void MutexAndSignalShareTheSameToken()
    {
        var scope = Unique();
        var token = InstanceScope.Token(scope);

        Assert.EndsWith(token, InstanceScope.MutexName(scope), StringComparison.Ordinal);
        Assert.EndsWith(token, InstanceScope.ShowRequestEventName(scope), StringComparison.Ordinal);
        Assert.Equal(InstanceScope.ShowRequestEventName(scope), ShowRequestSignal.EventNameFor(scope));
        Assert.Equal(InstanceScope.MutexName(scope), SingleInstanceGuard.NameFor(scope));
    }

    [Fact]
    public void NamesAreBoundedAndNeverContainThePath()
    {
        var scope = Path.Combine(Path.GetTempPath(), new string('x', 120), "overseer");
        var mutex = InstanceScope.MutexName(scope);
        var signal = InstanceScope.ShowRequestEventName(scope);

        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\'), mutex, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\'), signal, StringComparison.OrdinalIgnoreCase);
        Assert.True(mutex.Length < 128, "kernel object names must stay bounded");
        Assert.True(signal.Length < 128, "kernel object names must stay bounded");
    }

    [Fact]
    public void AnEmptyScopeHasItsOwnStableIdentity()
    {
        Assert.Equal(InstanceScope.Token(null), InstanceScope.Token("   "));
        Assert.Equal(InstanceScope.MutexName(null), InstanceScope.MutexName(null));
        Assert.NotEqual(InstanceScope.MutexName(null), InstanceScope.MutexName(Unique()));
    }

    [Fact]
    public void SameScopeLosesTheSlotAndADifferentScopeOwnsItsOwn()
    {
        var mine = Unique();
        var other = Unique();

        using var guard = SingleInstanceGuard.TryAcquire(mine);
        Assert.True(guard.IsOwner);

        // The same scope is the same instance: a second launch must not own the slot.
        var second = SingleInstanceGuard.TryAcquire(mine + Path.DirectorySeparatorChar);
        Assert.False(second.IsOwner);

        // An isolated scope is independent, which is what lets an isolated test run
        // start while the user's HUD is running.
        using var isolated = SingleInstanceGuard.TryAcquire(other);
        Assert.True(isolated.IsOwner);

        // The slot is released only when the owning instance and every other handle
        // to that scope are gone: a normal restart must be able to take it again.
        second.Dispose();
        guard.Dispose();
        using var reacquired = SingleInstanceGuard.TryAcquire(mine);
        Assert.True(reacquired.IsOwner);
    }
}
