using System.IO;

namespace TheOverseer.App.Desktop;

/// <summary>
/// Minimal per-user single-instance guard. A second normal launch exits quietly;
/// the first instance keeps its monitor and tray. Debug and fixture modes bypass
/// the guard so diagnostics never fight the running HUD.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "Local\\OVERSEER.Hud.SingleInstance";

    private Mutex? _mutex;
    private bool _owned;

    private SingleInstanceGuard(Mutex? mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    public bool IsOwner => _owned;

    public static SingleInstanceGuard TryAcquire()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
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
