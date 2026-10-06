using System.Text;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.Parsing;
using TheOverseer.Core.Monitoring.Sessions;
using TheOverseer.Core.Monitoring.State;

namespace TheOverseer.Core.Monitoring;

/// <summary>
/// Owns discovery, watching, bounded incremental consumption, identity,
/// optional liveness enrichment and monitoring-health signals. It performs no
/// UI, history or voice work, and it never writes to Codex data.
/// </summary>
public sealed partial class CodexMonitor : IAsyncDisposable, IDisposable
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private MonitorOptions _options;
    private readonly IRuntimeActivitySource _activitySource;
    private readonly IMonitorLog _log;
    private readonly TimeProvider _timeProvider;
    private readonly AgentStateReducer _reducer;
    private readonly AttentionDetector _attention;
    private readonly List<ProfileState> _profileStates = new();
    private readonly Dictionary<string, TrackedSessionFile> _tracked = new(PathComparer);
    private readonly HashSet<string> _internalPaths = new(PathComparer);
    private readonly HashSet<string> _malformedPaths = new(PathComparer);
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly ObservationCounters _counters = new();
    private readonly object _stateLock = new();

    private MonitoringSnapshot _current;
    private Task? _loop;
    private CancellationTokenSource? _loopCancellation;
    private int _nextFileId;
    private long _bytesRead;
    private DateTimeOffset _lastReconcileAt;
    private readonly ProjectResolver? _projects;
    private DateTimeOffset? _lastSuccessfulReadAt;
    private RuntimeActivityAvailability _runtimeAvailability = RuntimeActivityAvailability.NotConfigured;
    private bool _anyLiveAttention;
    private int _disposed;
    private int _reconcileRequested;

    public CodexMonitor(
        MonitorOptions options,
        IReadOnlyList<SessionProfile> profiles,
        IRuntimeActivitySource? activitySource = null,
        IMonitorLog? log = null,
        TimeProvider? timeProvider = null,
        ProjectResolver? projectResolver = null)
    {
        _options = (options ?? MonitorOptions.Default).Clamped();
        _activitySource = activitySource ?? NullRuntimeActivitySource.Instance;
        _log = log ?? NullMonitorLog.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _projects = projectResolver;
        _reducer = new AgentStateReducer(_options, _timeProvider);
        _attention = new AttentionDetector(_options);

        foreach (var profile in profiles)
        {
            _profileStates.Add(new ProfileState(profile, _options));
        }

        _current = MonitoringSnapshot.Empty(_timeProvider.GetUtcNow());
        _runtimeAvailability = activitySource is null ? RuntimeActivityAvailability.NotConfigured : RuntimeActivityAvailability.Unavailable;
    }

    public MonitoringSnapshot Current
    {
        get
        {
            lock (_stateLock)
            {
                return _current;
            }
        }
    }

    public event Action<MonitoringSnapshot>? SnapshotAvailable;

    public int TrackedFileCount => _tracked.Count;

    /// <summary>Apply a composite share before any monitoring work begins.</summary>
    public void ApplyBudget(SourceBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget.BytesPerPass, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget.MaximumSessions, 1);
        if (_loop is not null || _tracked.Count != 0)
            throw new InvalidOperationException("The source budget must be applied before monitoring starts.");
        _options = _options with
        {
            GlobalBytesPerPass = Math.Min(_options.GlobalBytesPerPass, budget.BytesPerPass),
            MaxTrackedFiles = Math.Min(_options.MaxTrackedFiles, budget.MaximumSessions),
        };
        _reducer.SetSessionLimit(budget.MaximumSessions);
    }

    public IReadOnlyList<SessionProfile> Profiles => _profileStates.Select(static state => state.Profile).ToArray();

    /// <summary>Starts the single-owner background loop.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_loop is not null)
        {
            return _loop;
        }

        _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunLoopAsync(_loopCancellation.Token), CancellationToken.None);
        return _loop;
    }

    /// <summary>Runs exactly one cycle on the caller; used by tests and the debug harness.</summary>
    public MonitoringSnapshot Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _cycleGate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return Publish(RunCycle(_timeProvider.GetUtcNow()));
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public async Task<MonitoringSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var snapshot = await Task.Run(() => RunCycle(_timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            return Publish(snapshot);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    /// <summary>Requests a full rediscovery sweep, for example after a wake event.</summary>
    public void RequestReconcile()
    {
        Interlocked.Exchange(ref _reconcileRequested, 1);
    }

    /// <summary>
    /// Synchronous disposal for callers that cannot await. The loop only uses
    /// thread-pool continuations, so blocking here cannot deadlock a UI thread.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_loopCancellation is not null)
        {
            await _loopCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _cycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var state in _profileStates)
            {
                state.Tracker.Dispose();
                state.Enumerator.Dispose();
            }
            _loopCancellation?.Dispose();
        }
        finally { _cycleGate.Release(); }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await Task.Run(Refresh, cancellationToken).ConfigureAwait(false);
                    _ = snapshot;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException) when (_disposed != 0) { break; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A whole-cycle failure must never look like a healthy zero.
                    _log.Write(MonitorLogLevel.Error, "cycle-failure");
                    PublishDegraded(HealthReasons.CycleFailure);
                }

                await WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        var timeout = _options.KnownFilePollInterval;
        ProfileState? primary = null;
        foreach (var state in _profileStates)
        {
            if (state.Tracker.HasPendingHints)
            {
                timeout = _options.DebounceInterval;
            }

            primary ??= state.Tracker.Available ? state : null;
        }

        if (primary is null)
        {
            await Task.Delay(timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            return;
        }

        await primary.Tracker.WaitForChangeAsync(timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private MonitoringSnapshot Publish(MonitoringSnapshot snapshot)
    {
        lock (_stateLock)
        {
            _current = snapshot;
        }

        SnapshotAvailable?.Invoke(snapshot);
        return snapshot;
    }

    private void PublishDegraded(string reason)
    {
        var now = _timeProvider.GetUtcNow();
        var previous = Current;
        Publish(previous with
        {
            GeneratedAt = now,
            Health = new MonitoringHealth(
                MonitoringHealthLevel.Degraded,
                new[] { reason },
                false,
                _lastSuccessfulReadAt,
                now),
            Counts = MonitoringCounts.Unknown,
        });
    }

    private MonitoringSnapshot RunCycle(DateTimeOffset now)
    {
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        if (_log.IsDegraded) reasons.Add(HealthReasons.LogDegraded);
        var coverageComplete = true;
        var transitions = new List<TaskTransition>();
        var malformedThisCycle = false;
        var candidates = new Dictionary<string, SessionFileCandidate>(PathComparer);
        var anyRootAccessible = false;
        var globalBudget = _options.GlobalBytesPerPass;
        foreach (var file in _tracked.Values) file.BytesReservedThisPass = 0;

        if (Interlocked.Exchange(ref _reconcileRequested, 0) != 0 || now - _lastReconcileAt >= _options.ReconcileInterval)
        {
            foreach (var state in _profileStates)
            {
                if (state.Enumerator.SweepCompleted) state.Enumerator.Restart();
                state.Tracker.RequestReconcile();
            }

            _lastReconcileAt = now;
            _internalPaths.Clear();
        }

        foreach (var state in _profileStates)
        {
            CollectCandidates(state, candidates, reasons, ref coverageComplete, ref anyRootAccessible, ref globalBudget, now);
        }

        AdmitCandidates(candidates.Values, reasons, ref coverageComplete, ref globalBudget, now);
        var hydrationDoneThisCycle = false;
        foreach (var file in _tracked.Values
                     .OrderBy(static item => item.LastProcessedAt)
                     .ThenBy(static item => item.FileId)
                     .ToArray())
        {
            if (file.NextAttemptAt > now)
            {
                coverageComplete = false;
                reasons.Add(file.FailureCode ?? HealthReasons.ReadFailure);
                continue;
            }

            if (file.HydrationLimited) { reasons.Add(HealthReasons.HydrationBudget); coverageComplete = false; }
            if (globalBudget <= 0)
            {
                reasons.Add(HealthReasons.HydrationBudget);
                coverageComplete = false;
                continue;
            }

            ProcessFile(file, ref globalBudget, transitions, reasons, ref coverageComplete, ref malformedThisCycle, ref hydrationDoneThisCycle, now);
        }

        if (malformedThisCycle)
        {
            reasons.Add(HealthReasons.FileMalformed);
        }

        var trackedKeys = new HashSet<TaskKey>();
        foreach (var file in _tracked.Values)
        {
            if (file.EvidenceGap) { reasons.Add(HealthReasons.IncompleteCoverage); coverageComplete = false; }
            if (file.FailureCode is null && file.IdentityResolved && file.Hydrated && !file.HydrationLimited && !file.EvidenceGap) trackedKeys.Add(file.Key);
        }

        _reducer.SetTrackedKeys(trackedKeys);

        if (_tracked.Count == 0 || _tracked.Values.All(static file => file.Hydrated))
        {
            _attention.MarkHydrationComplete();
        }

        var tasks = _reducer.Snapshot(now);
        var staleCount = tasks.Count(static task => task.IsStale);
        var provisionalCount = tasks.Count(static task => task.IsProvisional);
        if (staleCount > 0)
        {
            reasons.Add(HealthReasons.StaleTasks);
        }

        if (provisionalCount > 0)
        {
            reasons.Add(HealthReasons.ProvisionalTasks);
            coverageComplete = false;
        }

        if (staleCount > 0)
        {
            coverageComplete = false;
        }

        if (_tracked.Count >= _options.MaxTrackedFiles)
        {
            reasons.Add(HealthReasons.TrackedFileLimit);
        }

        var unavailable = !anyRootAccessible;
        var level = unavailable
            ? MonitoringHealthLevel.Unavailable
            : reasons.Count == 0 && coverageComplete
                ? MonitoringHealthLevel.Healthy
                : MonitoringHealthLevel.Degraded;

        var health = new MonitoringHealth(
            level,
            reasons.ToArray(),
            coverageComplete && level != MonitoringHealthLevel.Unavailable,
            _lastSuccessfulReadAt,
            now);

        var counts = BuildCounts(tasks, level, coverageComplete);
        var attentionEvents = _attention.Detect(tasks, transitions, now, level == MonitoringHealthLevel.Healthy && coverageComplete);
        if (attentionEvents.Count > 0)
        {
            _anyLiveAttention = true;
        }

        var observation = new LiveObservationStats(
            _counters.SessionMeta,
            _counters.TaskStarted,
            _counters.TaskCompleted,
            _counters.TurnAborted,
            _counters.ItemCompleted,
            _counters.ToolCalls,
            _counters.PermissionRequests,
            _counters.InputRequests,
            _counters.Malformed,
            _counters.Oversize,
            _counters.FilesHydrated,
            _tracked.Count,
            _bytesRead);

        return new MonitoringSnapshot(
            now,
            health,
            counts,
            tasks,
            attentionEvents,
            BuildCapabilities(observation, anyRootAccessible),
            observation);
    }


}
