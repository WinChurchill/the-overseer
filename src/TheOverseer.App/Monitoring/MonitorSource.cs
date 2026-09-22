using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.App;

/// <summary>Injectable monitor boundary so the HUD lifecycle can be tested.</summary>
public interface IMonitorSource : IAsyncDisposable
{
    MonitoringSnapshot Current { get; }

    event Action<MonitoringSnapshot>? SnapshotAvailable;

    Task StartAsync(CancellationToken cancellationToken);

    void RequestReconcile();
}

/// <summary>Wraps the reviewed CodexMonitor without changing its semantics.</summary>
public sealed class CodexMonitorSource : IMonitorSource
{
    private readonly CodexMonitor _monitor;

    public CodexMonitorSource(CodexMonitor monitor)
    {
        _monitor = monitor;
        _monitor.SnapshotAvailable += OnSnapshot;
    }

    public event Action<MonitoringSnapshot>? SnapshotAvailable;

    public MonitoringSnapshot Current => _monitor.Current;

    public Task StartAsync(CancellationToken cancellationToken) => _monitor.StartAsync(cancellationToken);

    public void RequestReconcile() => _monitor.RequestReconcile();

    public async ValueTask DisposeAsync()
    {
        _monitor.SnapshotAvailable -= OnSnapshot;
        await _monitor.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSnapshot(MonitoringSnapshot snapshot) => SnapshotAvailable?.Invoke(snapshot);
}
