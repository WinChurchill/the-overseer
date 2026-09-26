namespace TheOverseer.App.Desktop;

/// <summary>
/// Per-user "show the running HUD" signal. A second normal launch exits quietly,
/// but first asks the running instance to restore its window, so the shortcut and
/// the tray both return the same window and session. Bounded and local-only: one
/// named event, one waiter thread, no IPC payload.
/// </summary>
public sealed class ShowRequestSignal : IDisposable
{
    private const int WaitMilliseconds = 500;

    private readonly EventWaitHandle? _handle;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Thread? _waiter;
    private bool _disposed;

    private ShowRequestSignal(EventWaitHandle? handle, Action? onRequest)
    {
        _handle = handle;
        if (handle is null || onRequest is null)
        {
            return;
        }

        _waiter = new Thread(() => Wait(onRequest))
        {
            IsBackground = true,
            Name = "OVERSEER.ShowRequest",
        };
        _waiter.Start();
    }

    /// <summary>
    /// Stable event name for one instance scope (Windows user plus canonical data
    /// directory). It is derived from the same canonical identity as the
    /// single-instance mutex, so the instance that owns the slot is exactly the one
    /// that receives the restore request. Isolated runs and tests can therefore
    /// never signal the user's real HUD, and two independent instances cannot steal
    /// each other's requests.
    /// </summary>
    public static string EventNameFor(string? scope) => InstanceScope.ShowRequestEventName(scope);

    /// <summary>Running instance: listen for show requests. Null when unavailable.</summary>
    public static ShowRequestSignal? TryListen(Action onRequest, string? scope = null)
    {
        try
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventNameFor(scope));
            return new ShowRequestSignal(handle, onRequest);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Second instance: ask the running HUD to restore. Never throws.</summary>
    public static bool TrySignal(string? scope = null)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventNameFor(scope), out var handle))
            {
                using (handle)
                {
                    return handle.Set();
                }
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void Wait(Action onRequest)
    {
        while (!_lifetime.IsCancellationRequested)
        {
            var signaled = false;
            try
            {
                signaled = _handle!.WaitOne(WaitMilliseconds);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!signaled || _lifetime.IsCancellationRequested)
            {
                continue;
            }

            try
            {
                onRequest();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _handle?.Dispose();
        _lifetime.Dispose();
    }
}
