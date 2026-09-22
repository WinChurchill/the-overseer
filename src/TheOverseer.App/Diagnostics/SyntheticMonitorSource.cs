using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Deterministic in-process monitor for HUD fixtures. It never reads rollouts,
/// never touches the filesystem and never registers startup; it only replays
/// normalized snapshots so the window can be exercised and screenshotted.
/// </summary>
public sealed class SyntheticMonitorSource : IMonitorSource
{
    private readonly IReadOnlyList<MonitoringSnapshot> _scenarios;
    private readonly TimeProvider _timeProvider;
    private int _index;

    public SyntheticMonitorSource(IReadOnlyList<MonitoringSnapshot> scenarios, TimeProvider? timeProvider = null)
    {
        _scenarios = scenarios;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<MonitoringSnapshot>? SnapshotAvailable;

    public MonitoringSnapshot Current { get; private set; } = MonitoringSnapshot.Empty(DateTimeOffset.UnixEpoch);

    public IReadOnlyList<MonitoringSnapshot> Scenarios => _scenarios;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Push(0);
        return Task.Delay(Timeout.Infinite, cancellationToken);
    }

    /// <summary>Advances to the next scenario; used by the fixture runner.</summary>
    public void Advance()
    {
        Push(_index + 1);
    }

    public void Push(int index)
    {
        if (_scenarios.Count == 0)
        {
            return;
        }

        _index = Math.Clamp(index, 0, _scenarios.Count - 1);
        Current = _scenarios[_index];
        SnapshotAvailable?.Invoke(Current);
    }

    public void RequestReconcile()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
