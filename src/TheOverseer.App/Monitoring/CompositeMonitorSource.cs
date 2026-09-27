using System.Globalization;
using TheOverseer.App.Desktop;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.App.Monitoring;

/// <summary>Adapts the existing Codex monitor to the shared adapter contract.</summary>
public sealed class CodexMonitorAdapter : IAgentMonitorAdapter, IBudgetedMonitorAdapter
{
    private readonly IMonitorSource _source;
    private int _disposed;

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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _source.SnapshotAvailable += OnSnapshot;
        await _source.StartAsync(cancellationToken).ConfigureAwait(false);
        _ = _source.Completion.ContinueWith(task =>
        {
            _ = task.Exception;
            if (_disposed != 0 || cancellationToken.IsCancellationRequested) return;
            _current = _current with
            {
                ObservedAt = DateTimeOffset.UtcNow,
                Health = MonitoringHealthLevel.Unavailable,
                CoverageComplete = false,
                HealthReasons = new[] { task.IsFaulted ? "source-faulted" : "source-stopped" },
                Tasks = _current.Tasks.Select(item => item with { IsStale = true }).ToArray(),
                Transitions = Array.Empty<AttentionEvent>(),
            };
            SnapshotAvailable?.Invoke(_current);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void RequestReconcile() => _source.RequestReconcile();

    public void ApplyBudget(SourceBudget budget)
    {
        if (_source is IBudgetedMonitorAdapter budgeted) budgeted.ApplyBudget(budget);
    }

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
            "codex-monitor",
            snapshot.Health.LastSuccessfulReadAt ?? snapshot.GeneratedAt);
        SnapshotAvailable?.Invoke(_current);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
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
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, SourceSnapshot> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _receivedAt = new(StringComparer.Ordinal);
    private readonly HashSet<AttentionEvent> _seenTransitions = new();
    private readonly Queue<AttentionEvent> _transitionOrder = new();
    private readonly List<AttentionEvent> _pendingTransitions = new();
    private bool _allCompleteArmed;
    private MonitoringSnapshot _current;
    private int _started;
    private int _disposed;
    private ITimer? _watchdog;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _cancellation;
    public Task Completion => _completion.Task;
    private static readonly TimeSpan SourceSilenceLimit = TimeSpan.FromMinutes(2);

    public CompositeMonitorSource(
        IReadOnlyList<IAgentMonitorAdapter> adapters,
        IMonitorLog? log = null,
        TimeProvider? timeProvider = null,
        SourceBudget? aggregateBudget = null,
        bool discoveryIncomplete = false)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var enabled = adapters.Where(adapter => adapter.Source.IsEnabled).ToArray();
        SourcesTruncated = discoveryIncomplete;
        // The caller passes the AGGREGATE allowance; the composite divides it. Adding
        // a source therefore never multiplies the work the machine performs per pass.
        var aggregate = aggregateBudget ?? SourceBudget.Default;
        Budget = SourceBudget.Share(aggregate.BytesPerPass, aggregate.MaximumSessions, enabled.Length);
        if (enabled.Length > SourceBudget.MaximumSourceInstances)
        {
            // A machine with more spools than the supported source limit is bounded
            // rather than monitored more heavily than the budget allows.
            Array.Resize(ref enabled, SourceBudget.MaximumSourceInstances);
            SourcesTruncated = true;
        }

        _adapters = enabled;
        _log = log ?? NullMonitorLog.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _current = MonitoringSnapshot.Empty(_timeProvider.GetUtcNow());
        foreach (var adapter in _adapters)
        {
            if (adapter is IBudgetedMonitorAdapter budgeted)
            {
                // Applied before StartAsync so the reader cannot start unbudgeted.
                budgeted.ApplyBudget(Budget);
            }

            _latest[SourceKey(adapter.Source)] = SourceSnapshot.Empty(
                adapter.Source, DateTimeOffset.MinValue, "not-started", adapter.Source.HarnessId);
        }
    }

    /// <summary>Equal share of the aggregate budget every adapter received.</summary>
    public SourceBudget Budget { get; }

    /// <summary>True when more sources were discovered than the aggregate limit allows.</summary>
    public bool SourcesTruncated { get; }

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

    /// <summary>Latest immutable snapshot per source, for diagnostics and tests.</summary>
    public IReadOnlyList<SourceSnapshot> SourceSnapshots
    {
        get
        {
            lock (_gate)
            {
                return _latest.Values.ToArray();
            }
        }
    }

    public event Action<MonitoringSnapshot>? SnapshotAvailable;

    public IReadOnlyList<SourceInstance> Sources => _adapters.Select(static adapter => adapter.Source).ToArray();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _cancellation = cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));

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
                Push(SourceSnapshot.Empty(adapter.Source, _timeProvider.GetUtcNow(), "source-start-failed", adapter.Source.HarnessId));
            }
        }

        Publish();
        _watchdog = _timeProvider.CreateTimer(_ =>
        {
            try { Publish(); }
            catch (Exception) { _log.Write(MonitorLogLevel.Error, "composite-watchdog-publication-failed"); }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
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
        lock (_gate)
        {
            Push(snapshot);
            Publish();
        }
    }

    private static string SourceKey(SourceInstance source) => source.SourceInstanceId + "@" + source.HarnessId;

    private void Push(SourceSnapshot snapshot)
    {
        lock (_gate)
        {
            var key = SourceKey(snapshot.Source);
            if (!_latest.TryGetValue(key, out var previous) || snapshot.ObservedAt < previous.ObservedAt)
                return;
            // Only this source's entry is replaced: a fresh publication from one
            // harness can never advance another harness's evidence time.
            _latest[key] = snapshot with { Transitions = Array.Empty<AttentionEvent>() };
            _receivedAt[key] = _timeProvider.GetTimestamp();
            foreach (var transition in snapshot.Transitions)
            {
                // Source-local all-complete is never a global completion edge.
                if (transition.Kind == AttentionEventKind.AllTasksCompleted || !_seenTransitions.Add(transition)) continue;
                _pendingTransitions.Add(transition);
                _transitionOrder.Enqueue(transition);
                while (_transitionOrder.Count > 8192) _seenTransitions.Remove(_transitionOrder.Dequeue());
            }
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
            if (_disposed != 0) return;
            var sources = _latest.Values.ToArray();
            var tasks = new List<AgentTask>();
            var transitions = new List<AttentionEvent>(_pendingTransitions);
            _pendingTransitions.Clear();
            var reasons = new SortedSet<string>(StringComparer.Ordinal);
            var level = MonitoringHealthLevel.Healthy;
            var coverage = true;
            var readable = new List<(SourceInstance Source, MonitorCapabilities Capabilities)>();
            var observation = LiveObservationStats.Empty;
            var anyReadable = false;
            var perSource = new List<SourceHealth>(sources.Length);
            DateTimeOffset? lastEvidence = null;
            var now = _timeProvider.GetUtcNow();

            foreach (var stored in sources)
            {
                var source = stored;
                if (_receivedAt.TryGetValue(SourceKey(stored.Source), out var received) &&
                    _timeProvider.GetElapsedTime(received) > SourceSilenceLimit)
                {
                    source = stored with
                    {
                        Health = MonitoringHealthLevel.Unavailable,
                        CoverageComplete = false,
                        HealthReasons = stored.HealthReasons.Append("source-silent").ToArray(),
                        Tasks = stored.Tasks.Select(task => task with { IsStale = true }).ToArray(),
                    };
                }
                tasks.AddRange(source.Tasks);
                observation = observation with
                {
                    FilesTracked = observation.FilesTracked + source.Tasks.Count,
                    BytesRead = observation.BytesRead,
                };

                var sourceReadable = source.Health != MonitoringHealthLevel.Unavailable;
                if (sourceReadable)
                {
                    anyReadable = true;
                    readable.Add((source.Source, source.Capabilities));
                    var evidence = source.EffectiveEvidenceAt;
                    if (lastEvidence is null || evidence > lastEvidence) lastEvidence = evidence;
                }

                switch (source.Health)
                {
                    case MonitoringHealthLevel.Healthy:
                        break;
                    case MonitoringHealthLevel.Degraded:
                        if (level == MonitoringHealthLevel.Healthy)
                        {
                            level = MonitoringHealthLevel.Degraded;
                        }

                        break;
                    default:
                        if (level != MonitoringHealthLevel.Degraded)
                        {
                            level = MonitoringHealthLevel.Unavailable;
                        }

                        break;
                }

                if (!source.CoverageComplete || source.Health == MonitoringHealthLevel.Unavailable)
                {
                    coverage = false;
                }

                foreach (var reason in source.HealthReasons)
                {
                    reasons.Add(source.Source.HarnessId + ":" + reason);
                }

                // Typed per-source health for consumers that need source-specific detail.
                // The evidence time is this source's own, so a fresh peer can never
                // renew a stalled source's displayed freshness.
                perSource.Add(SourceHealth.From(source, now));
            }

            // Deterministic order: harness, then instance identity.
            perSource.Sort(static (left, right) =>
            {
                var byHarness = string.CompareOrdinal(left.HarnessId, right.HarnessId);
                return byHarness != 0 ? byHarness : string.CompareOrdinal(left.SourceInstanceId, right.SourceInstanceId);
            });

            if (SourcesTruncated)
            {
                reasons.Add("sources-truncated:" + SourceBudget.MaximumSourceInstances.ToString(CultureInfo.InvariantCulture));
                coverage = false;
                if (level == MonitoringHealthLevel.Healthy) level = MonitoringHealthLevel.Degraded;
            }

            // Readable work always leaves the source set at worst degraded: a
            // failed peer reduces visibility but never hides healthy tasks.
            if (anyReadable && level == MonitoringHealthLevel.Unavailable)
            {
                level = MonitoringHealthLevel.Degraded;
            }

            if (sources.Length == 0)
            {
                level = MonitoringHealthLevel.Unavailable;
                coverage = false;
                reasons.Add("no-sources");
            }

            var capabilities = MonitorCapabilities.Aggregate(readable);
            var counts = ComputeCounts(tasks, coverage && anyReadable, anyReadable);
            if (tasks.Any(task => !task.IsStale && task.State == AgentTaskState.Working)) _allCompleteArmed = true;
            if (_allCompleteArmed && coverage && anyReadable && tasks.Count > 0 &&
                tasks.All(task => !task.IsStale && !task.IsProvisional && task.State == AgentTaskState.Done) &&
                transitions.Any(transition => transition.Kind == AttentionEventKind.TaskCompleted))
            {
                transitions.Add(new AttentionEvent(AttentionEventKind.AllTasksCompleted, default, 0,
                    string.Empty, AgentTaskState.Done, AgentActionKind.Completed, string.Empty,
                    now, "all-tasks-completed"));
                _allCompleteArmed = false;
            }

            // LastSuccessfulReadAt is the newest EVIDENCE among readable sources, not
            // the wall clock: a peer that keeps publishing must not make a stalled
            // source look freshly read.
            merged = new MonitoringSnapshot(
                now,
                new MonitoringHealth(level, reasons.ToArray(), coverage, lastEvidence, now),
                counts,
                tasks,
                DedupeTransitions(transitions, coverage),
                capabilities,
                observation)
            {
                Sources = perSource.Count == 0 ? Array.Empty<SourceHealth>() : perSource.ToArray(),
            };
            _current = merged;
            // Deliver in the same serialized order as source replacement. Subscribers
            // enqueue UI work; they must not block waiting for another publication.
            SnapshotAvailable?.Invoke(merged);
        }
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Dispose();
        _completion.TrySetResult();
        if (_watchdog is not null) await _watchdog.DisposeAsync().ConfigureAwait(false);
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
