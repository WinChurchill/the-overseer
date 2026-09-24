using System.Globalization;
using TheOverseer.App.Desktop;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.App.Monitoring;

/// <summary>Adapts the existing Codex monitor to the shared adapter contract.</summary>
public sealed class CodexMonitorAdapter : IAgentMonitorAdapter
{
    private readonly IMonitorSource _source;

    public CodexMonitorAdapter(IMonitorSource source, SourceInstance? sourceInstance = null)
    {
        _source = source;
        Source = sourceInstance ?? new SourceInstance(HarnessIds.Codex, "codex", "Codex");
        _current = SourceSnapshot.Empty(Source, DateTimeOffset.UtcNow, "not-started", "codex-monitor");
    }

    private SourceSnapshot _current;

    public SourceInstance Source { get; }

    public SourceSnapshot Current => _current;

    public event Action<SourceSnapshot>? SnapshotAvailable;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _source.SnapshotAvailable += OnSnapshot;
        return _source.StartAsync(cancellationToken);
    }

    public void RequestReconcile() => _source.RequestReconcile();

    private void OnSnapshot(MonitoringSnapshot snapshot)
    {
        _current = new SourceSnapshot(
            Source,
            snapshot.GeneratedAt,
            snapshot.Health.Level,
            snapshot.Health.Reasons,
            snapshot.Health.CoverageComplete,
            snapshot.Tasks,
            snapshot.Transitions,
            snapshot.Capabilities,
            "codex-monitor");
        SnapshotAvailable?.Invoke(_current);
    }

    public ValueTask DisposeAsync()
    {
        _source.SnapshotAvailable -= OnSnapshot;
        return _source.DisposeAsync();
    }
}

/// <summary>
/// One HUD-facing monitor over every enabled source. Each source keeps its own
/// health, freshness and coverage; a failing source can neither erase another
/// source's tasks nor refresh its evidence, and global completion is claimed once,
/// only when every contributing source reports complete coverage.
/// </summary>
public sealed class CompositeMonitorSource : IMonitorSource
{
    private readonly IReadOnlyList<IAgentMonitorAdapter> _adapters;
    private readonly IMonitorLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, SourceSnapshot> _latest = new(StringComparer.Ordinal);
    private MonitoringSnapshot _current;
    private int _started;

    public CompositeMonitorSource(IReadOnlyList<IAgentMonitorAdapter> adapters, IMonitorLog? log = null)
    {
        _adapters = adapters ?? Array.Empty<IAgentMonitorAdapter>();
        _log = log ?? NullMonitorLog.Instance;
        _current = MonitoringSnapshot.Empty(DateTimeOffset.UtcNow);
    }

    public MonitoringSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event Action<MonitoringSnapshot>? SnapshotAvailable;

    public IReadOnlyList<SourceInstance> Sources => _adapters.Select(static adapter => adapter.Source).ToArray();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        foreach (var adapter in _adapters)
        {
            adapter.SnapshotAvailable += OnSourceSnapshot;
        }

        foreach (var adapter in _adapters)
        {
            try
            {
                await adapter.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // A source that cannot start is unavailable, never a silent zero.
                _log.Write(MonitorLogLevel.Warning, "composite-source-start:" + adapter.Source.Describe());
                Push(SourceSnapshot.Empty(adapter.Source, DateTimeOffset.UtcNow, "source-start-failed", adapter.Source.HarnessId));
            }
        }

        Publish();
    }

    public void RequestReconcile()
    {
        foreach (var adapter in _adapters)
        {
            try
            {
                adapter.RequestReconcile();
            }
            catch (InvalidOperationException)
            {
            }
        }

        Publish();
    }

    private void OnSourceSnapshot(SourceSnapshot snapshot)
    {
        Push(snapshot);
        Publish();
    }

    private void Push(SourceSnapshot snapshot)
    {
        lock (_gate)
        {
            _latest[snapshot.Source.SourceInstanceId + "@" + snapshot.Source.HarnessId] = snapshot;
        }
    }

    /// <summary>
    /// Merges every source's latest snapshot. Counts are only complete when all
    /// contributing sources are complete; a source failure produces a partial (or
    /// unknown) count with a source-qualified reason, never a healthy zero.
    /// </summary>
    private void Publish()
    {
        MonitoringSnapshot merged;
        lock (_gate)
        {
            var sources = _latest.Values.ToArray();
            var tasks = new List<AgentTask>();
            var transitions = new List<AttentionEvent>();
            var reasons = new SortedSet<string>(StringComparer.Ordinal);
            var level = MonitoringHealthLevel.Healthy;
            var coverage = true;
            var capabilities = MonitorCapabilities.Initial;
            var observation = LiveObservationStats.Empty;
            var anyReadable = false;

            foreach (var source in sources)
            {
                tasks.AddRange(source.Tasks);
                transitions.AddRange(source.Transitions);
                observation = observation with
                {
                    FilesTracked = observation.FilesTracked + source.Tasks.Count,
                    BytesRead = observation.BytesRead,
                };

                if (source.Health == MonitoringHealthLevel.Healthy)
                {
                    anyReadable = true;
                    capabilities = source.Capabilities;
                }
                else if (source.Health == MonitoringHealthLevel.Degraded)
                {
                    anyReadable = true;
                    if (level == MonitoringHealthLevel.Healthy)
                    {
                        level = MonitoringHealthLevel.Degraded;
                    }
                }
                else if (level != MonitoringHealthLevel.Degraded)
                {
                    level = MonitoringHealthLevel.Unavailable;
                }

                if (!source.CoverageComplete)
                {
                    coverage = false;
                }

                foreach (var reason in source.HealthReasons)
                {
                    reasons.Add(source.Source.HarnessId + ":" + reason);
                }
            }

            if (sources.Length == 0)
            {
                level = MonitoringHealthLevel.Unavailable;
                coverage = false;
                reasons.Add("no-sources");
            }

            var counts = ComputeCounts(tasks, coverage && anyReadable, anyReadable);
            merged = new MonitoringSnapshot(
                DateTimeOffset.UtcNow,
                new MonitoringHealth(level, reasons.ToArray(), coverage, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                counts,
                tasks,
                DedupeTransitions(transitions, coverage),
                capabilities,
                observation);
            _current = merged;
        }

        SnapshotAvailable?.Invoke(merged);
    }

    private static MonitoringCounts ComputeCounts(IReadOnlyList<AgentTask> tasks, bool complete, bool anyReadable)
    {
        if (!anyReadable)
        {
            return MonitoringCounts.Unknown;
        }

        CountValue Count(Func<AgentTask, bool> predicate)
        {
            var value = tasks.Count(task => !task.IsStale && predicate(task));
            return complete ? CountValue.Known(value) : CountValue.Partial(value);
        }

        return new MonitoringCounts(
            Count(static task => task.State == AgentTaskState.Working),
            Count(static task => task.NeedsHuman),
            Count(static task => task.State == AgentTaskState.NeedsApproval),
            Count(static task => task.State == AgentTaskState.NeedsInput),
            Count(static task => task.State == AgentTaskState.Error),
            Count(static task => task.State == AgentTaskState.Done),
            Count(static task => task.State == AgentTaskState.Idle),
            Count(static _ => true));
    }

    /// <summary>
    /// Forwards each transition once, and never claims a global completion while any
    /// source's coverage is incomplete: unknown membership is not all-complete.
    /// </summary>
    private static IReadOnlyList<AttentionEvent> DedupeTransitions(List<AttentionEvent> transitions, bool coverageComplete)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AttentionEvent>(transitions.Count);
        foreach (var transition in transitions)
        {
            if (transition.Kind == AttentionEventKind.AllTasksCompleted && !coverageComplete)
            {
                continue;
            }

            var identity = transition.Kind + ":" + transition.Key + ":" + transition.TurnId + ":" +
                           transition.ObservedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + ":" + transition.Code;
            if (seen.Add(identity))
            {
                result.Add(transition);
            }
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var adapter in _adapters)
        {
            adapter.SnapshotAvailable -= OnSourceSnapshot;
            try
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
