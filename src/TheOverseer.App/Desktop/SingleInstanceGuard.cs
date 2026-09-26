using System.IO;

namespace TheOverseer.App.Desktop;

/// <summary>
/// Minimal per-user single-instance guard, scoped to one canonical instance
/// identity (Windows user plus data directory). A second launch of the same scope
/// exits quietly; the first instance keeps its monitor and tray. An isolated run
/// with its own data directory therefore owns its own slot instead of stealing the
/// user's. Debug and fixture modes bypass the guard so diagnostics never fight the
/// running HUD.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private Mutex? _mutex;
    private bool _owned;

    private SingleInstanceGuard(Mutex? mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    public bool IsOwner => _owned;

    /// <summary>Kernel object name for diagnostics and tests (never a path).</summary>
    public static string NameFor(string? scope) => InstanceScope.MutexName(scope);

    public static SingleInstanceGuard TryAcquire(string? scope = null)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, InstanceScope.MutexName(scope), out var created);
            return new SingleInstanceGuard(mutex, created);
        }
        catch (UnauthorizedAccessException)
        {
            return new SingleInstanceGuard(null, false);
        }
        catch (IOException)
        {
            return new SingleInstanceGuard(null, false);
        }
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            if (_owned)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
        _mutex = null;
        _owned = false;
    }
}
