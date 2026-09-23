using TheOverseer.Core.Models;

namespace TheOverseer.Core.Voice;

public sealed record VoicePolicyOptions(
    TimeSpan Cooldown,
    TimeSpan AggregationWindow,
    TimeSpan MaximumEventAge,
    TimeSpan MaximumFutureSkew,
    int MaximumDedupeEntries,
    TimeSpan DedupeExpiry,
    int MaximumLatches,
    int MaximumPending,
    int MaximumTasks,
    int MaximumGroupMembers)
{
    public static VoicePolicyOptions Default { get; } = new(
        Cooldown: TimeSpan.FromSeconds(10),
        AggregationWindow: TimeSpan.FromSeconds(2),
        MaximumEventAge: TimeSpan.FromSeconds(30),
        MaximumFutureSkew: TimeSpan.FromSeconds(5),
        MaximumDedupeEntries: 4096,
        DedupeExpiry: TimeSpan.FromMinutes(60),
        MaximumLatches: 1536,
        MaximumPending: 16,
        MaximumTasks: 512,
        MaximumGroupMembers: 32);
}

/// <summary>
/// Semantic voice policy: decides WHICH attention conditions deserve a
/// notification, once per episode, with re-arm, dedupe, cooldown and completion
/// aggregation. It is pure and in-memory: no I/O, no audio, no UI, no timers.
/// </summary>
public sealed class VoiceEventPolicy
{
    private readonly VoicePolicyOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<TaskKey, TaskObservation> _tasks = new();
    private readonly Dictionary<string, Latch> _latches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _dedupe = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastSpoken = new(StringComparer.Ordinal);
    private readonly List<PendingCandidate> _pending = new();
    private readonly List<VoiceCandidate> _completionBuffer = new();
    private readonly HashSet<string> _completionTurns = new(StringComparer.Ordinal);
    private DateTimeOffset _aggregationDeadline = DateTimeOffset.MinValue;
    private VoiceCandidate? _replacementAggregate;
    private bool _baselineEstablished;
    private DateTimeOffset _lastSnapshot = DateTimeOffset.MinValue;
    private MonitoringHealthLevel _lastHealth = MonitoringHealthLevel.Unavailable;
    private CountValue _lastWorking = CountValue.Unknown;
    private bool _lastCoverageComplete;

    public VoiceEventPolicy(VoicePolicyOptions? options = null) => _options = options ?? VoicePolicyOptions.Default;

    public bool BaselineEstablished
    {
        get
        {
            lock (_gate)
            {
                return _baselineEstablished;
            }
        }
    }

    public int LatchCount
    {
        get
        {
            lock (_gate)
            {
                return _latches.Count;
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Safe bounded diagnostic summary: counts only, no task content.</summary>
    public string Describe() =>
        "baseline=" + (_baselineEstablished ? "yes" : "no") +
        " latches=" + _latches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        " deferred=" + _pending.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        " dedupe=" + _dedupe.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        " spoken=" + _lastSpoken.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public void Reset()
    {
        lock (_gate)
        {
            _lastSnapshot = DateTimeOffset.MinValue;
            _lastHealth = MonitoringHealthLevel.Unavailable;
            _tasks.Clear();
            _latches.Clear();
            _dedupe.Clear();
            _lastSpoken.Clear();
            _pending.Clear();
            _completionBuffer.Clear();
            _completionTurns.Clear();
            _replacementAggregate = null;
            _baselineEstablished = false;
            _aggregationDeadline = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Observes one snapshot and returns candidates that are eligible right now.
    /// Called from the monitor callback: bounded work, no I/O, no awaiting.
    /// </summary>
    public IReadOnlyList<VoiceCandidate> Observe(MonitoringSnapshot snapshot)
    {
        if (snapshot is null)
        {
            return Array.Empty<VoiceCandidate>();
        }

        var now = snapshot.GeneratedAt;
        lock (_gate)
        {
            if (now < _lastSnapshot) return Array.Empty<VoiceCandidate>();
            _lastSnapshot = now;
            PruneDedupe(now);
            UpdateTasks(snapshot);
            _lastHealth = snapshot.Health.Level;
            _lastWorking = snapshot.Counts.Working;
            _lastCoverageComplete = snapshot.Health.CoverageComplete;

            var raised = new List<VoiceCandidate>(4);
            foreach (var attention in snapshot.Transitions)
            {
                if (!IsFresh(attention.ObservedAt, now))
                {
                    continue;
                }

                switch (attention.Kind)
                {
                    case AttentionEventKind.TaskCompleted:
                        BufferCompletion(attention, now);
                        break;
                    case AttentionEventKind.AllTasksCompleted:
                        BufferAggregate(attention, now);
                        break;
                    default:
                        var kind = Map(attention.Kind);
                        var candidate = FromEvent(attention, kind, now);
                        if (MarkDedupe(candidate.Identity, now) && ValidateCondition(candidate, now) &&
                            (!_latches.TryGetValue(LatchKey(kind, candidate.Key), out var active) ||
                             !active.Active || active.TurnId != candidate.TurnId))
                        {
                            SetLatch(kind, candidate.Key, candidate.TurnId, spoken: true, candidate.ObservedAt);
                            raised.Add(candidate);
                        }

                        break;
                }
            }

            if (!_baselineEstablished)
            {
                _baselineEstablished = true;
                SeedBaseline(now);
            }

            foreach (var observation in _tasks)
            {
                CheckLatch(VoiceEventKind.PermissionRequired, observation.Key, observation.Value, observation.Value.State == AgentTaskState.NeedsApproval, now, raised);
                // A pending approval already announces itself: input only raises
                // its own cue for an explicit blocking input state or a
                // non-blocking needs-human request, never as a second cue for
                // the same approval. It stays latched while NeedsHuman holds.
                CheckLatch(
                    VoiceEventKind.InputRequired,
                    observation.Key,
                    observation.Value,
                    observation.Value.State == AgentTaskState.NeedsInput ||
                    (observation.Value.NeedsHuman && observation.Value.State != AgentTaskState.NeedsApproval),
                    now,
                    raised,
                    held: observation.Value.State == AgentTaskState.NeedsInput || observation.Value.NeedsHuman);
                CheckLatch(VoiceEventKind.TaskError, observation.Key, observation.Value, observation.Value.State == AgentTaskState.Error, now, raised);
            }

            return ApplyCooldown(raised, now);
        }
    }

    /// <summary>
    /// Timer-driven step: flushes completion aggregation and promotes pending
    /// candidates whose cooldown has expired. Never called from the producer.
    /// </summary>
    public IReadOnlyList<VoiceCandidate> Pump(DateTimeOffset now)
    {
        lock (_gate)
        {
            var ready = new List<VoiceCandidate>(2);

            if (now >= _aggregationDeadline && (_completionBuffer.Count > 0 || _replacementAggregate is not null))
            {
                FlushAggregation(now, ready);
            }

            for (var index = _pending.Count - 1; index >= 0; index--)
            {
                var pending = _pending[index];
                if (!IsRelevant(pending.Candidate, now))
                {
                    _pending.RemoveAt(index);
                    continue;
                }

                if (IsEligible(pending.Candidate, now))
                {
                    _pending.RemoveAt(index);
                    MarkSpoken(pending.Candidate, now);
                    ready.Add(pending.Candidate);
                }
            }

            PruneDedupe(now);
            return ready;
        }
    }

    private void UpdateTasks(MonitoringSnapshot snapshot)
    {
        _tasks.Clear();
        var count = 0;
        foreach (var task in snapshot.Tasks)
        {
            if (count++ >= _options.MaximumTasks)
            {
                break;
            }

            _tasks[task.Key] = new TaskObservation(
                task.State,
                task.NeedsHuman,
                task.IsStale,
                task.IsProvisional,
                task.CancellationObserved,
                task.TerminalFailureObserved,
                task.TurnId,
                task.Number);
        }
    }

    private void CheckLatch(
        VoiceEventKind kind,
        TaskKey key,
        TaskObservation task,
        bool present,
        DateTimeOffset now,
        List<VoiceCandidate> raised,
        bool? held = null)
    {
        var latchKey = LatchKey(kind, key);
        _latches.TryGetValue(latchKey, out var latch);
        if (task.IsStale || task.IsProvisional || _lastHealth == MonitoringHealthLevel.Unavailable) return;
        if (!(held ?? present))
        {
            if (latch is not null) latch.Active = false;
            return;
        }

        // State is only a baseline/clearance signal. A fresh AttentionEvent is
        // the sole initiator, including for tasks discovered after startup.
        if (latch is null || latch.TurnId != task.TurnId || !latch.Active)
            SetLatch(kind, key, task.TurnId, spoken: false, now);
    }

    /// <summary>Discard work while retaining silent episode and replay guards.</summary>
    public void DiscardPending()
    {
        lock (_gate)
        {
            _pending.Clear();
            _completionBuffer.Clear();
            _replacementAggregate = null;
            _aggregationDeadline = DateTimeOffset.MinValue;
        }
    }

    public bool IsRelevant(VoiceCandidate candidate, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!IsFresh(candidate.ObservedAt, now) || now - _lastSnapshot > _options.MaximumEventAge ||
                _lastHealth == MonitoringHealthLevel.Unavailable) return false;
            if (candidate.Kind == VoiceEventKind.AllTasksCompleted)
                return ValidateAggregate(candidate, now);
            if (candidate.Kind == VoiceEventKind.TaskCompleted)
                return candidate.IsAggregate
                    ? candidate.Members.Count > 0 && candidate.Members.All(member => ValidateCompletion(member, now))
                    : ValidateCompletion(candidate, now);
            return ValidateCondition(candidate, now) &&
                _latches.TryGetValue(LatchKey(candidate.Kind, candidate.Key), out var latch) &&
                latch.Active && latch.TurnId == candidate.TurnId && latch.ObservedAt == candidate.ObservedAt;
        }
    }

    private bool ValidateCondition(VoiceCandidate candidate, DateTimeOffset now)
    {
        if (!IsFresh(candidate.ObservedAt, now) || _lastHealth == MonitoringHealthLevel.Unavailable ||
            !_tasks.TryGetValue(candidate.Key, out var task) || task.IsStale || task.IsProvisional ||
            task.TurnId != candidate.TurnId) return false;
        return candidate.Kind switch
        {
            VoiceEventKind.PermissionRequired => task.State == AgentTaskState.NeedsApproval,
            VoiceEventKind.InputRequired => task.State == AgentTaskState.NeedsInput ||
                (task.NeedsHuman && task.State != AgentTaskState.NeedsApproval),
            VoiceEventKind.TaskError => task.State == AgentTaskState.Error && !task.Cancelled,
            _ => false,
        };
    }

    private void SeedBaseline(DateTimeOffset now)
    {
        foreach (var observation in _tasks)
        {
            var task = observation.Value;
            if (task.State == AgentTaskState.NeedsApproval)
            {
                EnsureLatch(VoiceEventKind.PermissionRequired, observation.Key, task);
            }

            if (task.State == AgentTaskState.NeedsInput || task.NeedsHuman)
            {
                EnsureLatch(VoiceEventKind.InputRequired, observation.Key, task);
            }

            if (task.State == AgentTaskState.Error)
            {
                EnsureLatch(VoiceEventKind.TaskError, observation.Key, task);
            }
        }

        _ = now;
    }

    private void EnsureLatch(VoiceEventKind kind, TaskKey key, TaskObservation task)
    {
        var latchKey = LatchKey(kind, key);
        if (!_latches.ContainsKey(latchKey))
        {
            _latches[latchKey] = new Latch { Active = true, TurnId = task.TurnId, Spoken = false };
            TrimLatches();
        }
    }

    private void BufferCompletion(AttentionEvent attention, DateTimeOffset now)
    {
        var identity = VoiceEventKind.TaskCompleted + ":" + attention.Key + ":" + attention.TurnId + ":" + attention.ObservedAt.ToUnixTimeMilliseconds();
        if (!MarkDedupe(identity, now) || !MarkCompletionTurn(attention.Key, attention.TurnId))
        {
            return;
        }

        if (_completionBuffer.Count >= _options.MaximumGroupMembers)
        {
            _completionBuffer.RemoveAt(0);
        }

        _completionBuffer.Add(FromEvent(attention, VoiceEventKind.TaskCompleted, now));
        if (_completionBuffer.Count == 1 || _aggregationDeadline < now)
        {
            _aggregationDeadline = now + _options.AggregationWindow;
        }
    }

    private void BufferAggregate(AttentionEvent attention, DateTimeOffset now)
    {
        var candidate = FromEvent(attention, VoiceEventKind.AllTasksCompleted, now) with
        {
            Members = _tasks.Where(pair => pair.Value.State == AgentTaskState.Done).Select(pair =>
                new VoiceCandidate(VoiceEventKind.TaskCompleted, pair.Key, pair.Value.Number, "Task", pair.Value.TurnId, attention.ObservedAt, "complete", false, 1, "")).ToArray(),
        };
        if (!MarkDedupe(candidate.Identity, now))
        {
            return;
        }

        _replacementAggregate = candidate;
        _aggregationDeadline = now + _options.AggregationWindow;
    }

    private void FlushAggregation(DateTimeOffset now, List<VoiceCandidate> ready)
    {
        var replacement = _replacementAggregate;
        _replacementAggregate = null;
        var buffer = _completionBuffer.ToArray();
        _completionBuffer.Clear();

        if (replacement is not null)
        {
            // A valid all-complete notice supersedes the individual completions
            // instead of being announced alongside them.
            if (ValidateAggregate(replacement, now))
            {
                MarkSpoken(replacement, now);
                ready.Add(replacement);
                return;
            }
        }

        var valid = new List<VoiceCandidate>(buffer.Length);
        foreach (var candidate in buffer)
        {
            if (ValidateCompletion(candidate, now))
            {
                valid.Add(candidate);
            }
        }

        if (valid.Count == 0)
        {
            return;
        }

        if (valid.Count == 1)
        {
            MarkSpoken(valid[0], now);
            ready.Add(valid[0]);
            return;
        }

        var group = valid[0] with
        {
            IsAggregate = true,
            TaskCount = valid.Count,
            Alias = valid.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " tasks",
            Code = "task_complete_group",
            EpisodeId = "group:" + valid[0].ObservedAt.ToUnixTimeMilliseconds(),
            Members = valid.ToArray(),
            Key = default,
            Number = 0,
            TurnId = string.Empty,
        };
        MarkSpoken(group, now);
        ready.Add(group);
    }

    private bool ValidateCompletion(VoiceCandidate candidate, DateTimeOffset now)
    {
        if (now - candidate.ObservedAt > _options.MaximumEventAge)
        {
            return false;
        }

        if (!_tasks.TryGetValue(candidate.Key, out var task))
        {
            // Disappearance is not completion evidence.
            return false;
        }

        return task.State == AgentTaskState.Done && !task.IsStale && !task.IsProvisional &&
            !task.Cancelled && !task.Failed && task.TurnId == candidate.TurnId;
    }

    private bool ValidateAggregate(VoiceCandidate candidate, DateTimeOffset now)
    {
        if (now - candidate.ObservedAt > _options.MaximumEventAge || _lastHealth == MonitoringHealthLevel.Unavailable)
        {
            return false;
        }

        if (!_lastCoverageComplete || candidate.Members.Count == 0 ||
            candidate.Members.Any(member => !ValidateCompletion(member, now)) || !_lastWorking.IsKnown || !_lastWorking.IsComplete || _lastWorking.Value != 0)
        {
            return false;
        }

        var done = false;
        foreach (var observation in _tasks)
        {
            var task = observation.Value;
            if (task.State is AgentTaskState.Working or AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput)
            {
                return false;
            }

            if (task.State == AgentTaskState.Error || task.Cancelled || task.Failed || task.NeedsHuman || task.IsStale || task.IsProvisional)
            {
                return false;
            }

            if (task.State == AgentTaskState.Done)
            {
                if (task.IsStale || task.IsProvisional)
                {
                    return false;
                }

                done = true;
            }
        }

        _ = _lastCoverageComplete;
        return done;
    }

    private bool MarkCompletionTurn(TaskKey key, string turnId)
    {
        var identity = "turn:" + key + ":" + turnId;
        if (_completionTurns.Contains(identity))
        {
            return false;
        }

        if (_completionTurns.Count >= _options.MaximumDedupeEntries)
        {
            _completionTurns.Remove(_completionTurns.First());
        }

        _completionTurns.Add(identity);
        return true;
    }

    private IReadOnlyList<VoiceCandidate> ApplyCooldown(List<VoiceCandidate> raised, DateTimeOffset now)
    {
        if (raised.Count == 0)
        {
            return Array.Empty<VoiceCandidate>();
        }

        var ready = new List<VoiceCandidate>(raised.Count);
        foreach (var candidate in raised)
        {
            if (IsEligible(candidate, now))
            {
                MarkSpoken(candidate, now);
                ready.Add(candidate);
                continue;
            }

            if (_pending.Count >= _options.MaximumPending)
            {
                _pending.RemoveAt(0);
            }

            _pending.Add(new PendingCandidate(candidate, now));
        }

        return ready;
    }

    private bool IsEligible(VoiceCandidate candidate, DateTimeOffset now)
    {
        if (candidate.IsAggregate)
        {
            return true;
        }

        var key = SpokenKey(candidate.Kind, candidate.Key);
        return !_lastSpoken.TryGetValue(key, out var last) || now - last >= _options.Cooldown;
    }

    private void MarkSpoken(VoiceCandidate candidate, DateTimeOffset now)
    {
        if (candidate.IsAggregate)
        {
            return;
        }

        _lastSpoken[SpokenKey(candidate.Kind, candidate.Key)] = now;
        while (_lastSpoken.Count > _options.MaximumLatches * 2)
        {
            foreach (var key in _lastSpoken.Keys)
            {
                _lastSpoken.Remove(key);
                break;
            }
        }
    }

    private bool MarkDedupe(string identity, DateTimeOffset now)
    {
        if (_dedupe.ContainsKey(identity))
        {
            return false;
        }

        if (_dedupe.Count >= _options.MaximumDedupeEntries)
        {
            PruneDedupe(now, force: true);
        }

        _dedupe[identity] = now;
        return true;
    }

    private void PruneDedupe(DateTimeOffset now, bool force = false)
    {
        if (!force && _dedupe.Count < _options.MaximumDedupeEntries / 2)
        {
            return;
        }

        List<string>? expired = null;
        foreach (var pair in _dedupe)
        {
            if (now - pair.Value > _options.DedupeExpiry)
            {
                (expired ??= new List<string>()).Add(pair.Key);
            }
        }

        if (expired is not null)
        {
            foreach (var key in expired)
            {
                _dedupe.Remove(key);
            }
        }

        if (_dedupe.Count >= _options.MaximumDedupeEntries)
        {
            _dedupe.Remove(_dedupe.MinBy(pair => pair.Value).Key);
        }
    }

    private void TrimLatches()
    {
        while (_latches.Count > _options.MaximumLatches)
        {
            foreach (var key in _latches.Keys)
            {
                _latches.Remove(key);
                break;
            }
        }
    }

    private bool IsFresh(DateTimeOffset observedAt, DateTimeOffset now)
    {
        if (observedAt - now > _options.MaximumFutureSkew)
        {
            return false;
        }

        return now - observedAt <= _options.MaximumEventAge;
    }

    private static VoiceEventKind Map(AttentionEventKind kind) => kind switch
    {
        AttentionEventKind.PermissionRequired => VoiceEventKind.PermissionRequired,
        AttentionEventKind.InputRequired => VoiceEventKind.InputRequired,
        AttentionEventKind.TaskError => VoiceEventKind.TaskError,
        AttentionEventKind.TaskCompleted => VoiceEventKind.TaskCompleted,
        _ => VoiceEventKind.AllTasksCompleted,
    };

    private static VoiceCandidate FromEvent(AttentionEvent attention, VoiceEventKind kind, DateTimeOffset now)
    {
        var aggregate = kind == VoiceEventKind.AllTasksCompleted;
        var alias = aggregate ? "All tasks" : DefaultAlias(attention.Number);
        return new VoiceCandidate(
            kind,
            attention.Key,
            attention.Number,
            alias,
            attention.TurnId,
            attention.ObservedAt,
            attention.Code,
            aggregate,
            aggregate ? 0 : 1,
            aggregate ? "episode:" + attention.ObservedAt.ToUnixTimeMilliseconds() : string.Empty);
    }

    private static string DefaultAlias(int number) =>
        number > 0 ? "Task " + number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "Task";

    private static string LatchKey(VoiceEventKind kind, TaskKey key) => kind + ":" + key;

    private static string SpokenKey(VoiceEventKind kind, TaskKey key) => kind + ":" + key;

    private void SetLatch(VoiceEventKind kind, TaskKey key, string turnId, bool spoken, DateTimeOffset now)
    {
        var latchKey = LatchKey(kind, key);
        if (!_latches.TryGetValue(latchKey, out var latch))
        {
            latch = new Latch();
            _latches[latchKey] = latch;
            TrimLatches();
        }

        latch.Active = true;
        latch.TurnId = turnId;
        latch.Spoken = latch.Spoken || spoken;
        latch.ObservedAt = now;
    }

    private sealed class Latch
    {
        public bool Active;
        public DateTimeOffset ObservedAt;
        public bool Spoken;
        public string TurnId = string.Empty;
    }

    private sealed record PendingCandidate(VoiceCandidate Candidate, DateTimeOffset EnqueuedAt);

    private sealed record TaskObservation(
        AgentTaskState State,
        bool NeedsHuman,
        bool IsStale,
        bool IsProvisional,
        bool Cancelled,
        bool Failed,
        string TurnId,
        int Number);
}
