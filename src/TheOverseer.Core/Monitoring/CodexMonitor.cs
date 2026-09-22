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
public sealed class CodexMonitor : IAsyncDisposable, IDisposable
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly MonitorOptions _options;
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
    private DateTimeOffset? _lastSuccessfulReadAt;
    private RuntimeActivityAvailability _runtimeAvailability = RuntimeActivityAvailability.NotConfigured;
    private bool _anyLiveAttention;
    private int _disposed;

    public CodexMonitor(
        MonitorOptions options,
        IReadOnlyList<SessionProfile> profiles,
        IRuntimeActivitySource? activitySource = null,
        IMonitorLog? log = null,
        TimeProvider? timeProvider = null)
    {
        _options = (options ?? MonitorOptions.Default).Clamped();
        _activitySource = activitySource ?? NullRuntimeActivitySource.Instance;
        _log = log ?? NullMonitorLog.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        foreach (var state in _profileStates)
        {
            state.Enumerator.Restart();
        }

        _lastReconcileAt = DateTimeOffset.MinValue;
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

        foreach (var state in _profileStates)
        {
            state.Tracker.Dispose();
        }

        _loopCancellation?.Dispose();
        _cycleGate.Dispose();
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
        var coverageComplete = true;
        var transitions = new List<TaskTransition>();
        var malformedThisCycle = false;
        var candidates = new Dictionary<string, SessionFileCandidate>(PathComparer);
        var anyRootAccessible = false;

        if (now - _lastReconcileAt >= _options.ReconcileInterval)
        {
            foreach (var state in _profileStates)
            {
                state.Enumerator.Restart();
                state.Tracker.RequestReconcile();
            }

            _lastReconcileAt = now;
            _internalPaths.Clear();
        }

        foreach (var state in _profileStates)
        {
            CollectCandidates(state, candidates, reasons, ref coverageComplete, ref anyRootAccessible, now);
        }

        AdmitCandidates(candidates.Values, reasons, ref coverageComplete, now);

        var globalBudget = _options.GlobalBytesPerPass;
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

            if (globalBudget <= 0 && hydrationDoneThisCycle)
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
            trackedKeys.Add(file.Key);
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
        var attentionEvents = _attention.Detect(tasks, transitions, now);
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

    private void CollectCandidates(
        ProfileState state,
        Dictionary<string, SessionFileCandidate> candidates,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        ref bool anyRootAccessible,
        DateTimeOffset now)
    {
        var profile = state.Profile;
        if (!Directory.Exists(profile.SessionsRoot))
        {
            if (Directory.Exists(profile.RootPath))
            {
                reasons.Add(HealthReasons.RootMissing);
            }
            else
            {
                reasons.Add(HealthReasons.RootMissing);
            }

            coverageComplete = false;
            return;
        }

        anyRootAccessible = true;

        if (!state.Tracker.Available)
        {
            reasons.Add(HealthReasons.WatcherFailed);
            state.Tracker.RequestReconcile();
        }

        if (state.Tracker.ConsumeOverflow())
        {
            reasons.Add(HealthReasons.WatcherOverflow);
        }

        if (state.Tracker.ConsumeStructuralChange())
        {
            state.Enumerator.Restart();
        }

        var hints = state.Tracker.DrainHints();
        var hintBudget = 0;
        foreach (var hint in hints)
        {
            if (hintBudget++ >= _options.MaxWatcherHintPaths)
            {
                reasons.Add(HealthReasons.WatcherOverflow);
                break;
            }

            if (!hint.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                !IsUnderRoot(profile, hint))
            {
                continue;
            }

            var candidate = SessionFileProbe.TryStat(hint, profile.Id, "watcher-hint");
            if (candidate is not null)
            {
                candidates[candidate.FullName] = candidate;
            }
        }

        var slice = state.Enumerator.NextSlice();
        if (slice.RootInaccessible)
        {
            reasons.Add(HealthReasons.RootInaccessible);
            coverageComplete = false;
        }

        foreach (var candidate in slice.Candidates)
        {
            candidates.TryAdd(candidate.FullName, candidate);
        }

        if (!slice.SweepCompleted)
        {
            reasons.Add(HealthReasons.DiscoveryBacklog);
            coverageComplete = false;
        }

        var priorityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in _tracked.Values)
        {
            if (string.Equals(file.ProfileId, profile.Id, StringComparison.Ordinal))
            {
                priorityIds.Add(file.ConversationId);
            }
        }

        if (state.Titles.Refresh(profile.SessionIndexPath, priorityIds))
        {
            foreach (var file in _tracked.Values)
            {
                if (!string.Equals(file.ProfileId, profile.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                var title = state.Titles.GetTitle(file.ConversationId);
                if (title.Length > 0)
                {
                    _reducer.SetLabel(file.Key, title, file.Surface);
                }
            }
        }

        if (state.Titles.Status.Availability == TitleIndexAvailability.Failed)
        {
            reasons.Add(HealthReasons.TitleSourceUnavailable);
        }

        var runtime = _activitySource.Read(profile, now - _options.StaleLease, _options.MaxTrackedFiles);
        state.RuntimeAvailability = runtime.Availability;
        if (_profileStates.IndexOf(state) == 0)
        {
            _runtimeAvailability = runtime.Availability;
        }

        if (runtime.Availability == RuntimeActivityAvailability.Available)
        {
            foreach (var hint in runtime.Hints)
            {
                if (string.IsNullOrWhiteSpace(hint.RolloutPath) || !IsUnderRoot(profile, hint.RolloutPath))
                {
                    continue;
                }

                var candidate = SessionFileProbe.TryStat(hint.RolloutPath, profile.Id, "runtime-hint");
                if (candidate is not null)
                {
                    candidates.TryAdd(candidate.FullName, candidate);
                }
            }
        }
    }

    private void AdmitCandidates(
        IEnumerable<SessionFileCandidate> candidates,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        DateTimeOffset now)
    {
        foreach (var candidate in candidates.OrderByDescending(static item => item.LastWriteUtc))
        {
            if (_tracked.ContainsKey(candidate.FullName) || _internalPaths.Contains(candidate.FullName))
            {
                continue;
            }

            var state = FindState(candidate.ProfileId);
            if (state is null || !IsUnderRoot(state.Profile, candidate.FullName))
            {
                continue;
            }

            if (_tracked.Count >= _options.MaxTrackedFiles)
            {
                var evictable = _tracked.Values
                    .Where(file => now - file.LastWriteUtc > _options.StaleLease)
                    .OrderBy(static file => file.LastWriteUtc)
                    .FirstOrDefault();
                if (evictable is null || evictable.LastWriteUtc >= candidate.LastWriteUtc)
                {
                    reasons.Add(HealthReasons.TrackedFileLimit);
                    coverageComplete = false;
                    continue;
                }

                _tracked.Remove(evictable.Path);
                reasons.Add(HealthReasons.TrackedFileLimit);
                coverageComplete = false;
            }

            if (SessionFileProbe.IsReadBlocked(candidate.FullName))
            {
                reasons.Add(HealthReasons.FileLocked);
                coverageComplete = false;
                continue;
            }

            var identity = SessionIdentityReader.Read(candidate.FullName, _options.MaxIdentityHeaderBytes);
            if (identity.Found && identity.IsExplicitInternal)
            {
                if (_internalPaths.Count < 512)
                {
                    _internalPaths.Add(candidate.FullName);
                }

                continue;
            }

            var conversationId = identity.ConversationId;
            var provisional = false;
            if (conversationId.Length == 0)
            {
                conversationId = SessionIdentityReader.TryDeriveConversationIdFromFileName(candidate.FullName);
                provisional = conversationId.Length > 0;
            }

            if (conversationId.Length == 0)
            {
                reasons.Add(HealthReasons.ReadFailure);
                coverageComplete = false;
                continue;
            }

            var file = new TrackedSessionFile
            {
                FileId = ++_nextFileId,
                ProfileId = state.Profile.Id,
                Path = candidate.FullName,
                ConversationId = conversationId,
                Surface = identity.Surface,
                WorkspaceLeaf = identity.WorkspaceLeaf,
                IdentityResolved = identity.Found,
                IdentityProvisional = provisional,
                Length = candidate.Length,
                LastWriteUtc = candidate.LastWriteUtc,
                AdmittedAt = now,
                LastProcessedAt = DateTimeOffset.MinValue,
            };
            file.Signature = ComputeSignature(candidate.FullName);
            file.LastSignatureCheckAt = now;
            _tracked[candidate.FullName] = file;

            var task = _reducer.RegisterConversation(
                state.Profile.Id,
                conversationId,
                identity.Surface,
                identity.WorkspaceLeaf,
                identity.Found,
                now);

            var title = state.Titles.GetTitle(conversationId);
            if (title.Length > 0)
            {
                _reducer.SetLabel(task.Key, title, identity.Surface);
            }
        }
    }

    private void ProcessFile(
        TrackedSessionFile file,
        ref int globalBudget,
        List<TaskTransition> transitions,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        ref bool malformedThisCycle,
        ref bool hydrationDoneThisCycle,
        DateTimeOffset now)
    {
        file.LastProcessedAt = now;
        try
        {
            if (!file.Hydrated)
            {
                HydrateFile(file, ref globalBudget, transitions, reasons, ref coverageComplete, ref malformedThisCycle, ref hydrationDoneThisCycle, now);
                return;
            }

            ReadAppended(file, ref globalBudget, transitions, reasons, ref coverageComplete, ref malformedThisCycle, now);
        }
        catch (FileNotFoundException)
        {
            HandleFailure(file, HealthReasons.FileMissing, reasons, ref coverageComplete, now);
        }
        catch (DirectoryNotFoundException)
        {
            HandleFailure(file, HealthReasons.FileMissing, reasons, ref coverageComplete, now);
        }
        catch (IOException)
        {
            HandleFailure(file, HealthReasons.FileLocked, reasons, ref coverageComplete, now);
        }
        catch (UnauthorizedAccessException)
        {
            HandleFailure(file, HealthReasons.ReadFailure, reasons, ref coverageComplete, now);
        }
    }

    private void HydrateFile(
        TrackedSessionFile file,
        ref int globalBudget,
        List<TaskTransition> transitions,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        ref bool malformedThisCycle,
        ref bool hydrationDoneThisCycle,
        DateTimeOffset now)
    {
        var info = new FileInfo(file.Path);
        info.Refresh();
        if (!info.Exists)
        {
            HandleFailure(file, HealthReasons.FileMissing, reasons, ref coverageComplete, now);
            return;
        }

        var length = info.Length;
        var wanted = Math.Min(length, _options.HydrationMaxScanBytes);

        // Fair sharing: while other files still need hydration this cycle, use
        // at most half of what is left so one large rollout cannot starve the
        // rest of the backlog.
        var waiting = _tracked.Values.Count(static item => !item.Hydrated) > 1;
        var share = waiting ? Math.Max(_options.HydrationInitialWindowBytes, globalBudget / 2) : Math.Max(_options.HydrationInitialWindowBytes, globalBudget);
        var window = Math.Min(wanted, share);
        var limited = window < wanted;
        var tail = length <= 0
            ? new TailReadResult(Array.Empty<string>(), 0, false, false, 0)
            : TailWindowReader.ReadTailLines(file.Path, Math.Max(1, window), _options.MaxHydrationRecords, _options.MaxPendingRecordBytes);

        globalBudget -= tail.BytesRead;
        _bytesRead += tail.BytesRead;
        file.BytesRead += tail.BytesRead;
        hydrationDoneThisCycle = true;

        if (tail.OversizeDiscarded)
        {
            reasons.Add(HealthReasons.RecordOversize);
            _counters.Oversize++;
        }

        var sawLifecycle = false;
        var order = 0;
        foreach (var line in tail.Lines)
        {
            order++;
            var context = new ParseContext(
                file.ProfileId,
                file.ConversationId,
                file.FileId,
                order,
                order,
                file.Reader.Generation,
                true,
                now,
                _options.MaxRecordChars);
            var result = CodexEventParser.ParseLine(line, in context);
            ApplyParseResult(file, result, transitions, ref sawLifecycle, ref malformedThisCycle);
        }

        file.SourceOrder += order;
        file.Reader.SeekTo(length);
        file.Hydrated = true;
        file.HydrationLimited = limited && !sawLifecycle;
        file.Length = length;
        file.LastWriteUtc = info.LastWriteTimeUtc;
        file.ReadBlocked = false;
        file.ConsecutiveFailures = 0;
        file.FailureCode = null;
        _counters.FilesHydrated++;
        _lastSuccessfulReadAt = now;

        if (file.HydrationLimited)
        {
            // Deeper history exists but was not read; the current state may be
            // unanchored, so coverage is reported as incomplete instead of
            // guessing a state.
            reasons.Add(HealthReasons.HydrationBudget);
            coverageComplete = false;
        }
    }

    private void ReadAppended(
        TrackedSessionFile file,
        ref int globalBudget,
        List<TaskTransition> transitions,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        ref bool malformedThisCycle,
        DateTimeOffset now)
    {
        var info = new FileInfo(file.Path);
        info.Refresh();
        if (!info.Exists)
        {
            HandleFailure(file, HealthReasons.FileMissing, reasons, ref coverageComplete, now);
            return;
        }

        if (info.Length < file.Reader.Offset)
        {
            reasons.Add(HealthReasons.FileTruncated);
            coverageComplete = false;
            ResetForRehydrate(file);
            return;
        }

        if (now - file.LastSignatureCheckAt >= _options.ReconcileInterval)
        {
            file.LastSignatureCheckAt = now;
            var signature = ComputeSignature(file.Path);
            if (file.Signature != 0 && signature != 0 && signature != file.Signature)
            {
                reasons.Add(HealthReasons.FileReplaced);
                coverageComplete = false;
                ResetForRehydrate(file);
                return;
            }

            file.Signature = signature;
        }

        var allowance = Math.Min(_options.PerFileBytesPerPass, Math.Max(0, globalBudget));
        if (allowance < 4096)
        {
            reasons.Add(HealthReasons.HydrationBudget);
            coverageComplete = false;
            return;
        }

        var read = file.Reader.ReadAppended(file.Path, allowance);
        globalBudget -= read.BytesRead;
        _bytesRead += read.BytesRead;
        file.BytesRead += read.BytesRead;

        if (read.Truncated)
        {
            reasons.Add(HealthReasons.FileTruncated);
            coverageComplete = false;
            return;
        }

        if (read.OversizeDiscarded)
        {
            reasons.Add(HealthReasons.RecordOversize);
            _counters.Oversize++;
        }

        var sawLifecycle = false;
        var order = file.SourceOrder;
        foreach (var line in read.Lines)
        {
            order++;
            var context = new ParseContext(
                file.ProfileId,
                file.ConversationId,
                file.FileId,
                order,
                order,
                file.Reader.Generation,
                false,
                now,
                _options.MaxRecordChars);
            var result = CodexEventParser.ParseLine(line, in context);
            ApplyParseResult(file, result, transitions, ref sawLifecycle, ref malformedThisCycle);
        }

        file.SourceOrder = order;
        file.Length = info.Length;
        file.LastWriteUtc = info.LastWriteTimeUtc;
        file.ConsecutiveFailures = 0;
        file.FailureCode = null;
        file.ReadBlocked = false;
        _lastSuccessfulReadAt = now;
        // A rollout append is recency evidence only, and only when it is not
        // ahead of the monitor clock: a future timestamp is unusable evidence
        // and never refreshes freshness.
        var writtenAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (writtenAt <= now)
        {
            _reducer.NoteSourceActivity(file.Key, writtenAt);
        }

        if (read.BudgetExhausted)
        {
            reasons.Add(HealthReasons.HydrationBudget);
            coverageComplete = false;
        }
    }

    private void ApplyParseResult(
        TrackedSessionFile file,
        ParseResult result,
        List<TaskTransition> transitions,
        ref bool sawLifecycle,
        ref bool malformedThisCycle)
    {
        switch (result.Outcome)
        {
            case ParseOutcome.Malformed:
                _counters.Malformed++;
                malformedThisCycle = true;
                if (_malformedPaths.Count < 256)
                {
                    _malformedPaths.Add(file.Path);
                }

                return;
            case ParseOutcome.Oversize:
                _counters.Oversize++;
                return;
            case ParseOutcome.UnknownSchema:
                _counters.UnknownSchema++;
                return;
            case ParseOutcome.Ignored:
                return;
        }

        if (result.Event is not { } codexEvent)
        {
            return;
        }

        switch (codexEvent.Kind)
        {
            case RawCodexEventKind.SessionIdentity:
                _counters.SessionMeta++;
                break;
            case RawCodexEventKind.TaskStarted:
                _counters.TaskStarted++;
                sawLifecycle = true;
                break;
            case RawCodexEventKind.TaskCompleted:
                _counters.TaskCompleted++;
                sawLifecycle = true;
                break;
            case RawCodexEventKind.TurnAborted:
                _counters.TurnAborted++;
                sawLifecycle = true;
                break;
            case RawCodexEventKind.TaskFailed:
                _counters.TaskFailed++;
                sawLifecycle = true;
                break;
            case RawCodexEventKind.ItemActivity:
                _counters.ItemCompleted++;
                break;
            case RawCodexEventKind.ToolCallStarted:
            case RawCodexEventKind.ToolCallCompleted:
                _counters.ToolCalls++;
                break;
            case RawCodexEventKind.PermissionRequested:
                _counters.PermissionRequests++;
                break;
            case RawCodexEventKind.InputRequested:
                _counters.InputRequests++;
                break;
        }

        var outcome = _reducer.Apply(in codexEvent);
        if (outcome.Transition is { } transition)
        {
            transitions.Add(transition);
        }
    }

    private static void ResetForRehydrate(TrackedSessionFile file)
    {
        file.Reader.Reset();
        file.Hydrated = false;
        file.HydrationLimited = false;
        file.SourceOrder = 0;
        file.Signature = 0;
    }

    private void HandleFailure(
        TrackedSessionFile file,
        string reasonCode,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        DateTimeOffset now)
    {
        file.ConsecutiveFailures++;
        file.FailureCode = reasonCode;
        var backoff = TimeSpan.FromTicks(Math.Min(
            _options.FailureBackoffMax.Ticks,
            _options.FailureBackoffInitial.Ticks * (1L << Math.Min(file.ConsecutiveFailures - 1, 16))));
        file.NextAttemptAt = now + backoff;
        file.ReadBlocked = reasonCode == HealthReasons.FileLocked;
        reasons.Add(reasonCode);
        coverageComplete = false;
    }

    private MonitoringCounts BuildCounts(IReadOnlyList<AgentTask> tasks, MonitoringHealthLevel level, bool coverageComplete)
    {
        if (level == MonitoringHealthLevel.Unavailable)
        {
            return MonitoringCounts.Unknown;
        }

        var authoritative = tasks.Where(static task => !task.IsStale && !task.IsProvisional).ToArray();
        var partial = !coverageComplete ||
                      tasks.Any(static task => task.IsStale || task.IsProvisional);

        CountValue Count(Func<AgentTask, bool> predicate) =>
            partial ? CountValue.Partial(authoritative.Count(predicate)) : CountValue.Known(authoritative.Count(predicate));

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

    private MonitorCapabilities BuildCapabilities(LiveObservationStats observation, bool anyRootAccessible)
    {
        var lifecycle = observation.AnyLifecycleObserved
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "rollout-lifecycle", "task_started/task_complete/turn_aborted observed live")
            : new CapabilityStatus(CapabilitySupport.Supported, "rollout-lifecycle", "implemented and contract-tested; no lifecycle record read in this run");

        var activity = observation.ItemCompletedRecords + observation.ToolCallRecords > 0
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "structured-activity", "item_completed/tool-call records observed live")
            : new CapabilityStatus(CapabilitySupport.Supported, "structured-activity", "implemented and contract-tested; not observed live");

        var permission = observation.PermissionRequestRecords > 0
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "permission-requests", "structured approval request observed live")
            : new CapabilityStatus(CapabilitySupport.Unsupported, "permission-requests", "Codex keeps approval events transient; no durable record exists to observe");

        var input = observation.InputRequestRecords > 0
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "input-requests", "structured input request observed live")
            : new CapabilityStatus(CapabilitySupport.Unsupported, "input-requests", "Codex keeps request_user_input transient; no durable record exists to observe");

        var failure = observation.TaskStartedRecords >= 0 && observation.TurnAbortedRecords > 0
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "task-failure", "abort records observed; explicit failure evidence still requires a failure-coded record")
            : new CapabilityStatus(CapabilitySupport.Unsupported, "task-failure", "no explicit task-level failure record observed in this Codex version");

        var runtime = _runtimeAvailability switch
        {
            RuntimeActivityAvailability.Available => new CapabilityStatus(CapabilitySupport.LiveObserved, "runtime-activity-source", "optional read-only state store read succeeded"),
            RuntimeActivityAvailability.Failed => new CapabilityStatus(CapabilitySupport.Unavailable, "runtime-activity-source", "optional source present but unreadable"),
            RuntimeActivityAvailability.NotConfigured => new CapabilityStatus(CapabilitySupport.Unsupported, "runtime-activity-source", "not configured on this platform"),
            _ => new CapabilityStatus(CapabilitySupport.Unavailable, "runtime-activity-source", "optional source not read (headless or unavailable)"),
        };

        var titlesAvailable = _profileStates.Any(static state => state.Titles.Status.Availability == TitleIndexAvailability.Available);
        var titles = titlesAvailable
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "session-titles", "official session index read")
            : new CapabilityStatus(CapabilitySupport.Unavailable, "session-titles", "official session index missing or unreadable");

        var attention = _anyLiveAttention
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "attention-live", "a live attention transition was observed")
            : new CapabilityStatus(CapabilitySupport.NotExercised, "attention-live", "no live attention signal available to observe; contracts covered by synthetic tests");

        if (!anyRootAccessible)
        {
            lifecycle = new CapabilityStatus(CapabilitySupport.Unavailable, "rollout-lifecycle", "no readable session root in this run");
        }

        return new MonitorCapabilities(lifecycle, activity, permission, input, failure, runtime, titles, attention);
    }

    private ProfileState? FindState(string profileId)
    {
        foreach (var state in _profileStates)
        {
            if (string.Equals(state.Profile.Id, profileId, StringComparison.Ordinal))
            {
                return state;
            }
        }

        return null;
    }

    private static bool IsUnderRoot(SessionProfile profile, string path)
    {
        try
        {
            var root = Path.GetFullPath(profile.RootPath);
            var full = Path.GetFullPath(path);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
            {
                root += Path.DirectorySeparatorChar;
            }

            return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static ulong ComputeSignature(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);
            Span<byte> buffer = stackalloc byte[4096];
            var read = stream.Read(buffer);
            const ulong offset = 14695981039346656037;
            const ulong prime = 1099511628211;
            var hash = offset;
            foreach (var value in buffer[..read])
            {
                hash ^= value;
                hash *= prime;
            }

            return hash == 0 ? 1 : hash;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private sealed class ProfileState
    {
        public ProfileState(SessionProfile profile, MonitorOptions options)
        {
            Profile = profile;
            Enumerator = new BoundedSessionEnumerator(profile, options);
            Tracker = new SessionChangeTracker(profile.SessionsRoot, options.MaxWatcherHintPaths);
            Titles = new SessionTitleIndex(options.MaxTitleCacheEntries, options.TitleReadBytesPerPass);
        }

        public SessionProfile Profile { get; }

        public BoundedSessionEnumerator Enumerator { get; }

        public SessionChangeTracker Tracker { get; }

        public SessionTitleIndex Titles { get; }

        public RuntimeActivityAvailability RuntimeAvailability { get; set; } = RuntimeActivityAvailability.Unavailable;
    }

    private sealed class ObservationCounters
    {
        public int SessionMeta;
        public int TaskStarted;
        public int TaskCompleted;
        public int TurnAborted;
        public int TaskFailed;
        public int ItemCompleted;
        public int ToolCalls;
        public int PermissionRequests;
        public int InputRequests;
        public int Malformed;
        public int Oversize;
        public int UnknownSchema;
        public int FilesHydrated;
    }
}
