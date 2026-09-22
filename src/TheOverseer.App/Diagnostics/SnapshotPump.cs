using TheOverseer.Core.Models;

namespace TheOverseer.App.Diagnostics;

/// <summary>Abstracts the UI thread so delivery can be tested without a window.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    public WpfUiDispatcher(System.Windows.Threading.Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Post(Action action) => _dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
}

/// <summary>
/// Coalesces producer snapshots into at most one queued UI delivery holding the
/// newest snapshot. The producer callback stays allocation-light and never
/// blocks, waits on the UI, or disposes anything.
/// </summary>
public sealed class SnapshotPump
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<MonitoringSnapshot> _apply;
    private readonly object _gate = new();
    private MonitoringSnapshot? _pending;
    private int _posted;
    private bool _stopped;
    private long _received;
    private long _applied;

    public SnapshotPump(IUiDispatcher dispatcher, Action<MonitoringSnapshot> apply)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public long ReceivedCount => Interlocked.Read(ref _received);

    public long AppliedCount => Interlocked.Read(ref _applied);

    /// <summary>Producer callback: store the newest snapshot and queue one delivery.</summary>
    public void OnSnapshot(MonitoringSnapshot snapshot)
    {
        Interlocked.Increment(ref _received);
        lock (_gate)
        {
            if (_stopped) return;
            _pending = snapshot;
            if (Interlocked.Exchange(ref _posted, 1) == 1)
            {
                return;
            }
        }

        _dispatcher.Post(Drain);
    }

    /// <summary>Applies the newest pending snapshot once. UI thread only.</summary>
    public void Drain()
    {
        while (true)
        {
            MonitoringSnapshot? snapshot;
            lock (_gate)
            {
                if (_stopped) return;
                snapshot = _pending;
                _pending = null;
                Interlocked.Exchange(ref _posted, 0);
            }

            if (snapshot is null)
            {
                return;
            }

            _apply(snapshot);
            Interlocked.Increment(ref _applied);

            lock (_gate)
            {
                if (_pending is null)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _posted, 1) == 1)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Cancels any queued delivery; used during shutdown.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _pending = null;
            Interlocked.Exchange(ref _posted, 1);
        }
    }
}
