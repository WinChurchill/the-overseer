using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.State;

public sealed record ReduceOutcome(bool Applied, string RejectCode, TaskTransition? Transition)
{
    public static ReduceOutcome Rejected(string code) => new(false, code, null);

    public static ReduceOutcome Accepted { get; } = new(true, "accepted", null);
}

/// <summary>
/// Owns canonical task state and transitions. The reducer is deterministic and
/// single-owner: the monitor feeds it normalized events from one background
/// loop, and it never performs I/O or reads recorded text.
/// </summary>
public sealed class AgentStateReducer
{
    private readonly MonitorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TaskNumberPool _numbers;
    private readonly CurrentActionClassifier _classifier;
    private readonly Dictionary<TaskKey, MutableTask> _tasks = new();
    private readonly LinkedList<TaskKey> _usage = new();
    private readonly Dictionary<TaskKey, LinkedListNode<TaskKey>> _usageIndex = new();
    private readonly HashSet<TaskKey> _tracked = new();

    public AgentStateReducer(MonitorOptions options, TimeProvider? timeProvider = null, CurrentActionClassifier? classifier = null)
    {
        _options = options.Clamped();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _numbers = new TaskNumberPool(_options.MaxReleasedNumbers, _timeProvider);
        _classifier = classifier ?? CurrentActionClassifier.Instance;
    }

    public int ConversationCount => _tasks.Count;

    public AgentTask? TryGetTask(TaskKey key) =>
        _tasks.TryGetValue(key, out var task) ? ToImmutable(task) : null;

    /// <summary>
    /// Registers a conversation and allocates its stable number. Re-registering
    /// the same conversation (for example after a rollout rollover) keeps the
    /// number and merges evidence.
    /// </summary>
    public AgentTask RegisterConversation(
        string profileId,
        string conversationId,
        ClientSurface surface,
        string label,
        bool identityResolved,
        DateTimeOffset now)
    {
        var key = new TaskKey(profileId, conversationId);
        var task = GetOrCreate(key, now);
        if (surface != ClientSurface.Unknown && task.Surface == ClientSurface.Unknown)
        {
            task.Surface = surface;
        }

        if (label.Length > 0 && task.Label.Length == 0)
        {
            task.Label = label;
        }

        if (identityResolved)
        {
            task.IdentityResolved = true;
        }

        Touch(key);
        return ToImmutable(task);
    }

    public void SetLabel(TaskKey key, string label, ClientSurface surface)
    {
        if (!_tasks.TryGetValue(key, out var task))
        {
            return;
        }

        if (label.Length > 0)
        {
            task.Label = label;
        }

        if (surface != ClientSurface.Unknown)
        {
            task.Surface = surface;
        }
    }

    /// <summary>Marks which conversations still have an actively tracked rollout file.</summary>
    public void SetTrackedKeys(IReadOnlyCollection<TaskKey> trackedKeys)
    {
        _tracked.Clear();
        foreach (var key in trackedKeys)
        {
            _tracked.Add(key);
        }
    }

    public ReduceOutcome Apply(in RawCodexEvent codexEvent)
    {
        if (codexEvent.ConversationId.Length == 0)
        {
            return ReduceOutcome.Rejected("no-conversation");
        }

        var task = GetOrCreate(codexEvent.Key, codexEvent.ObservedAt);
        Touch(codexEvent.Key);
        if (codexEvent.SourceFileId > 0 && task.SourceFiles.Count < 8)
        {
            task.SourceFiles.Add(codexEvent.SourceFileId);
        }

        if (!Accepts(task, in codexEvent, out var rejectCode))
        {
            return ReduceOutcome.Rejected(rejectCode);
        }

        var from = task.State;
        var fromAttention = task.AttentionCode;
        var transitionKind = ApplyMutation(task, in codexEvent);

        var observedAt = codexEvent.EventTime ?? (codexEvent.IsHydration ? DateTimeOffset.MinValue : codexEvent.ObservedAt);
        if (codexEvent.Kind is not (RawCodexEventKind.SessionIdentity or RawCodexEventKind.TurnContext or
            RawCodexEventKind.TokenUsage or RawCodexEventKind.HistoryCompacted) &&
            observedAt <= codexEvent.ObservedAt && observedAt > task.LastActivityAt)
        {
            task.LastActivityAt = observedAt;
        }

        task.Action = UpdateAction(task);

        if (transitionKind == TaskTransitionKind.StateChanged && from != task.State)
        {
            return new ReduceOutcome(true, "state-changed", new TaskTransition(
                TaskTransitionKind.StateChanged,
                task.Key,
                task.Number,
                from,
                task.State,
                task.StateCauseCode,
                task.TurnId,
                codexEvent.IsHydration,
                codexEvent.ObservedAt));
        }

        if (transitionKind == TaskTransitionKind.AttentionRaised ||
            transitionKind == TaskTransitionKind.AttentionCleared ||
            transitionKind == TaskTransitionKind.NonBlockingInputRaised ||
            transitionKind == TaskTransitionKind.NonBlockingInputResolved)
        {
            if (fromAttention != task.AttentionCode || from != task.State)
            {
                return new ReduceOutcome(true, "attention", new TaskTransition(
                    transitionKind,
                    task.Key,
                    task.Number,
                    from,
                    task.State,
                    task.StateCauseCode,
                    task.TurnId,
                    codexEvent.IsHydration,
                    codexEvent.ObservedAt));
            }
        }

        return ReduceOutcome.Accepted;
    }

    /// <summary>
    /// Projects immutable rows. Staleness is computed here: a task whose lease
    /// expired, or whose rollout file is no longer tracked, keeps its last known
    /// state but is flagged instead of being silently reused as current truth.
    /// </summary>
    public IReadOnlyList<AgentTask> Snapshot(DateTimeOffset now)
    {
        var rows = new List<AgentTask>(_tasks.Count);
        foreach (var task in _tasks.Values)
        {
            task.IsStale = ComputeStale(task, now);
            task.Action = UpdateAction(task);
            rows.Add(ToImmutable(task));
        }

        rows.Sort(static (left, right) => left.Number.CompareTo(right.Number));
        return rows;
    }

    /// <summary>Updates the activity floor from a tracked file append time (freshness only).</summary>
    public void NoteSourceActivity(TaskKey key, DateTimeOffset lastWriteUtc)
    {
        if (!_tasks.TryGetValue(key, out var task))
        {
            return;
        }

        if (task.State is AgentTaskState.Working or AgentTaskState.NeedsInput or AgentTaskState.NeedsApproval &&
            lastWriteUtc > task.LastActivityAt)
        {
            task.LastActivityAt = lastWriteUtc;
        }
    }

    public void Forget(TaskKey key)
    {
        if (_tasks.Remove(key, out var task))
        {
            _numbers.Release(task.Number, _options.NumberReleaseCooldown);
        }

        if (_usageIndex.Remove(key, out var node))
        {
            _usage.Remove(node);
        }

        _tracked.Remove(key);
    }

    public void ResetEvidence(TaskKey key)
    {
        if (!_tasks.TryGetValue(key, out var old)) return;
        _tasks[key] = new MutableTask
        {
            Key = key, Number = old.Number, ProfileId = old.ProfileId, ConversationId = old.ConversationId,
            Label = old.Label, Surface = old.Surface, IdentityResolved = old.IdentityResolved,
            FirstSeenAt = old.FirstSeenAt, StateSince = _timeProvider.GetUtcNow(),
            State = AgentTaskState.Idle, TurnLimit = _options.MaxTurnHistoryPerTask,
            RequestLimit = _options.MaxCorrelatedRequestsPerTask, SeenMarkerLimit = _options.MaxSeenRecordMarkers,
        };
    }

    private bool ComputeStale(MutableTask task, DateTimeOffset now)
    {
        if (!_tracked.Contains(task.Key)) return true;
        if (task.State is not (AgentTaskState.Working or AgentTaskState.NeedsInput or AgentTaskState.NeedsApproval))
        {
            return false;
        }

        if (!_tracked.Contains(task.Key))
        {
            return true;
        }

        return now - task.LastActivityAt > _options.StaleLease;
    }

    private MutableTask GetOrCreate(TaskKey key, DateTimeOffset now)
    {
        if (_tasks.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var task = new MutableTask
        {
            Key = key,
            Number = _numbers.Acquire(),
            ProfileId = key.ProfileId,
            ConversationId = key.ConversationId,
            FirstSeenAt = now,
            LastActivityAt = DateTimeOffset.MinValue,
            StateSince = now,
            State = AgentTaskState.Idle,
            Evidence = EvidenceKind.None,
            Action = AgentAction.Idle,
            TurnLimit = _options.MaxTurnHistoryPerTask,
            RequestLimit = _options.MaxCorrelatedRequestsPerTask,
            SeenMarkerLimit = _options.MaxSeenRecordMarkers,
        };
        _tasks[key] = task;
        _usageIndex[key] = _usage.AddLast(key);
        EvictExcess();
        return task;
    }

    private void EvictExcess()
    {
        while (_tasks.Count > _options.MaxTrackedConversations && _usage.First is { } oldest)
        {
            var key = oldest.Value;
            _usage.RemoveFirst();
            _usageIndex.Remove(key);
            Forget(key);
        }
    }

    private void Touch(TaskKey key)
    {
        if (_usageIndex.TryGetValue(key, out var node) && node.List is not null)
        {
            _usage.Remove(node);
            _usage.AddLast(node);
        }
    }

    private bool Accepts(MutableTask task, in RawCodexEvent codexEvent, out string rejectCode)
    {
        rejectCode = "accepted";
        if (codexEvent.SourceOrder > 0)
        {
            var marker = codexEvent.SourceFileId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                         codexEvent.SourceGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                         codexEvent.SourceOrder.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (task.SeenMarkers.Contains(marker))
            {
                rejectCode = "duplicate-record";
                return false;
            }

            RememberMarker(task, marker);
        }

        if (codexEvent.TurnId.Length == 0)
        {
            if (task.Turns.TryGetValue(task.TurnId, out var terminal) && terminal.Terminal &&
                codexEvent.Kind is not (RawCodexEventKind.SessionIdentity or RawCodexEventKind.TokenUsage or RawCodexEventKind.HistoryCompacted))
            {
                rejectCode = "uncorrelated-terminal-evidence";
                return false;
            }
            return true;
        }

        if (task.TurnId.Length == 0)
        {
            AdoptTurn(task, codexEvent.TurnId, codexEvent.EventTime ?? codexEvent.ObservedAt);
            return true;
        }

        if (string.Equals(codexEvent.TurnId, task.TurnId, StringComparison.Ordinal))
        {
            if (task.Turns.TryGetValue(task.TurnId, out var current) && current.Terminal &&
                codexEvent.Kind is not (RawCodexEventKind.PermissionResolved or RawCodexEventKind.InputResolved))
            {
                rejectCode = "terminal-turn-evidence";
                return false;
            }

            return true;
        }

        var eventAt = codexEvent.EventTime ?? codexEvent.ObservedAt;
        if (task.Turns.TryGetValue(codexEvent.TurnId, out var known))
        {
            if (known.Terminal || known.LastActivityAt <= task.TurnStartedAt)
            {
                rejectCode = "old-turn";
                return false;
            }

            AdoptTurn(task, codexEvent.TurnId, eventAt);
            return true;
        }

        if (eventAt < task.TurnLastActivityAt)
        {
            rejectCode = "old-turn";
            return false;
        }

        AdoptTurn(task, codexEvent.TurnId, eventAt);
        return true;
    }

    private static void RememberMarker(MutableTask task, string marker)
    {
        if (task.SeenMarkers.Add(marker))
        {
            task.SeenOrder.Enqueue(marker);
            while (task.SeenOrder.Count > task.SeenMarkerLimit)
            {
                var oldest = task.SeenOrder.Dequeue();
                task.SeenMarkers.Remove(oldest);
            }
        }
    }

    private static void AdoptTurn(MutableTask task, string turnId, DateTimeOffset at)
    {
        var isNewCurrentTurn = !string.Equals(task.TurnId, turnId, StringComparison.Ordinal);
        if (!task.Turns.TryGetValue(turnId, out var record))
        {
            record = new TurnRecord { TurnId = turnId, Order = task.TurnOrder++ };
            task.Turns[turnId] = record;
            task.TurnSequence.Enqueue(turnId);
            var attempts = task.TurnSequence.Count;
            while (task.Turns.Count > task.TurnLimit && task.TurnSequence.Count > 0 && attempts-- > 0)
            {
                var oldest = task.TurnSequence.Dequeue();
                if (string.Equals(oldest, task.TurnId, StringComparison.Ordinal) ||
                    string.Equals(oldest, turnId, StringComparison.Ordinal))
                {
                    task.TurnSequence.Enqueue(oldest);
                    continue;
                }

                task.Turns.Remove(oldest);
            }
        }

        if (at > record.LastActivityAt)
        {
            record.LastActivityAt = at;
        }

        if (isNewCurrentTurn)
        {
            ClearRequests(task, string.Empty);
            task.ActiveTools.Clear();
            task.ToolOrder.Clear();
            task.LastActivity = null;
            task.AttentionCode = null;
            task.CancellationObserved = false;
            task.FailureCode = null;
            task.TurnId = turnId;
            task.TurnStartedAt = record.LastActivityAt;
            task.TurnLastActivityAt = record.LastActivityAt;
            task.HasActivityInCurrentTurn = false;
        }
        else if (record.LastActivityAt > task.TurnLastActivityAt)
        {
            task.TurnLastActivityAt = record.LastActivityAt;
        }
    }

    private TaskTransitionKind ApplyMutation(MutableTask task, in RawCodexEvent codexEvent)
    {
        if (codexEvent.TurnId.Length > 0)
        {
            if (task.Turns.TryGetValue(codexEvent.TurnId, out var record))
            {
                var at = codexEvent.EventTime ?? codexEvent.ObservedAt;
                if (at > record.LastActivityAt)
                {
                    record.LastActivityAt = at;
                }

                if (string.Equals(task.TurnId, codexEvent.TurnId, StringComparison.Ordinal) && at > task.TurnLastActivityAt)
                {
                    task.TurnLastActivityAt = at;
                }
            }
        }

        switch (codexEvent.Kind)
        {
            case RawCodexEventKind.SessionIdentity:
                task.IdentityResolved = true;
                return TaskTransitionKind.None;

            case RawCodexEventKind.TurnContext:
            case RawCodexEventKind.TokenUsage:
            case RawCodexEventKind.HistoryCompacted:
                return TaskTransitionKind.None;

            case RawCodexEventKind.TaskStarted:
                task.HasExplicitLifecycle = true;
                task.StartedAt = codexEvent.EventTime ?? codexEvent.ObservedAt;
                task.CompletedAt = null;
                task.CancellationObserved = false;
                task.FailureCode = null;
                MarkTurn(task, codexEvent.TurnId, terminal: false, outcome: null, codexEvent.EventTime ?? codexEvent.ObservedAt);
                SetState(task, AgentTaskState.Working, EvidenceKind.Explicit, "task-started");
                RecomputeAttentionState(task);
                return TaskTransitionKind.StateChanged;

            case RawCodexEventKind.TaskCompleted:
                task.CompletedAt = codexEvent.EventTime ?? codexEvent.ObservedAt;
                task.CancellationObserved = false;
                task.FailureCode = null;
                ClearRequests(task, codexEvent.TurnId);
                MarkTurn(task, codexEvent.TurnId, terminal: true, outcome: TurnOutcome.Completed, codexEvent.EventTime ?? codexEvent.ObservedAt);
                SetState(task, AgentTaskState.Done, EvidenceKind.Explicit, "task-complete");
                return TaskTransitionKind.StateChanged;

            case RawCodexEventKind.TurnAborted:
                task.CompletedAt = codexEvent.EventTime ?? codexEvent.ObservedAt;
                ClearRequests(task, codexEvent.TurnId);
                if (codexEvent.ReasonCode == "failed")
                {
                    task.FailureCode = "aborted-failure";
                    MarkTurn(task, codexEvent.TurnId, terminal: true, outcome: TurnOutcome.Failed, codexEvent.EventTime ?? codexEvent.ObservedAt);
                    SetState(task, AgentTaskState.Error, EvidenceKind.Explicit, "turn-aborted-failed");
                }
                else
                {
                    task.CancellationObserved = true;
                    MarkTurn(task, codexEvent.TurnId, terminal: true, outcome: TurnOutcome.Aborted, codexEvent.EventTime ?? codexEvent.ObservedAt);
                    SetState(task, AgentTaskState.Idle, EvidenceKind.Explicit, "turn-aborted");
                }

                return TaskTransitionKind.StateChanged;

            case RawCodexEventKind.TaskFailed:
                task.FailureCode = codexEvent.ReasonCode ?? "failure";
                task.CompletedAt = codexEvent.EventTime ?? codexEvent.ObservedAt;
                ClearRequests(task, codexEvent.TurnId);
                MarkTurn(task, codexEvent.TurnId, terminal: true, outcome: TurnOutcome.Failed, codexEvent.EventTime ?? codexEvent.ObservedAt);
                SetState(task, AgentTaskState.Error, EvidenceKind.Explicit, "task-failed");
                return TaskTransitionKind.StateChanged;

            case RawCodexEventKind.PermissionRequested:
                AddRequest(task, codexEvent, isPermission: true, isBlocking: true);
                task.AttentionCode = "permission-requested";
                SetState(task, AgentTaskState.NeedsApproval, EvidenceKind.Explicit, "permission-requested");
                return TaskTransitionKind.StateChanged;

            case RawCodexEventKind.PermissionResolved:
                if (!RemoveRequest(task, codexEvent))
                {
                    return TaskTransitionKind.None;
                }

                RecomputeAttentionState(task);
                return TaskTransitionKind.AttentionCleared;

            case RawCodexEventKind.InputRequested:
                var blocking = codexEvent.IsBlocking ?? true;
                AddRequest(task, codexEvent, isPermission: false, isBlocking: blocking);
                if (blocking)
                {
                    RecomputeAttentionState(task);
                    return TaskTransitionKind.StateChanged;
                }

                RecomputeAttentionState(task);
                return TaskTransitionKind.NonBlockingInputRaised;

            case RawCodexEventKind.InputResolved:
                var wasNonBlocking = task.NonBlockingInputPending;
                if (!RemoveRequest(task, codexEvent))
                {
                    return TaskTransitionKind.None;
                }

                RecomputeAttentionState(task);
                return wasNonBlocking ? TaskTransitionKind.NonBlockingInputResolved : TaskTransitionKind.AttentionCleared;

            case RawCodexEventKind.ToolCallStarted:
                AddTool(task, codexEvent);
                RecordActivity(task, codexEvent);
                return PromoteToWorking(task, codexEvent);

            case RawCodexEventKind.ToolCallCompleted:
                CompleteTool(task, codexEvent);
                RecordActivity(task, codexEvent);
                return PromoteToWorking(task, codexEvent);

            case RawCodexEventKind.ItemActivity:
                RecordActivity(task, codexEvent);
                return PromoteToWorking(task, codexEvent);

            default:
                return TaskTransitionKind.None;
        }
    }

    private TaskTransitionKind PromoteToWorking(MutableTask task, in RawCodexEvent codexEvent)
    {
        var hasWork = codexEvent.Action is not null || codexEvent.Tool is not (ToolCallCategory.None or ToolCallCategory.Message);
        if (!hasWork)
        {
            return TaskTransitionKind.None;
        }

        if (task.State is AgentTaskState.Idle or AgentTaskState.Done or AgentTaskState.Error)
        {
            // Structured work in the current turn. It is not an explicit
            // lifecycle start, so the evidence kind stays Structured.
            task.Evidence = EvidenceKind.Structured;
            SetState(task, AgentTaskState.Working, EvidenceKind.Structured, "structured-activity");
            return TaskTransitionKind.StateChanged;
        }

        return TaskTransitionKind.None;
    }

    private void SetState(MutableTask task, AgentTaskState state, EvidenceKind evidence, string causeCode)
    {
        if (task.State != state)
        {
            task.State = state;
            task.StateSince = _timeProvider.GetUtcNow();
            task.StateCauseCode = causeCode;
            task.Evidence = evidence;
            if (state == AgentTaskState.Working)
            {
                if (!task.NonBlockingInputPending)
                {
                    task.AttentionCode = null;
                }
            }
            else if (state != AgentTaskState.NeedsApproval && state != AgentTaskState.NeedsInput)
            {
                task.AttentionCode = null;
            }

            return;
        }

        if (evidence > task.Evidence)
        {
            task.Evidence = evidence;
        }

        task.StateCauseCode = causeCode;
    }

    private void MarkTurn(MutableTask task, string turnId, bool terminal, TurnOutcome? outcome, DateTimeOffset at)
    {
        if (turnId.Length == 0)
        {
            return;
        }

        if (!task.Turns.ContainsKey(turnId))
        {
            AdoptTurn(task, turnId, at);
        }

        if (task.Turns.TryGetValue(turnId, out var record))
        {
            record.Terminal = terminal;
            record.Outcome = outcome;
        }
    }

    private void AddRequest(MutableTask task, in RawCodexEvent codexEvent, bool isPermission, bool isBlocking)
    {
        var requestId = codexEvent.RequestId ?? codexEvent.CallId ?? "request-" + codexEvent.SourceOrder.ToString(System.Globalization.CultureInfo.InvariantCulture);
        task.Requests[requestId] = new RequestRecord
        {
            RequestId = requestId,
            IsPermission = isPermission,
            IsBlocking = isBlocking,
            TurnId = codexEvent.TurnId,
            Since = codexEvent.EventTime ?? codexEvent.ObservedAt,
        };
        while (task.Requests.Count > task.RequestLimit)
        {
            var oldest = task.Requests.Values.MinBy(static request => request.Since)!.RequestId;
            if (task.Requests.Remove(oldest) && task.Requests.Count == 0)
            {
                task.NonBlockingInputPending = false;
            }
        }
    }

    private static bool RemoveRequest(MutableTask task, in RawCodexEvent codexEvent)
    {
        if (codexEvent.RequestId is { Length: > 0 } requestId && task.Requests.Remove(requestId))
        {
            return true;
        }

        if (codexEvent.CallId is { Length: > 0 } callId)
        {
            foreach (var pair in task.Requests.ToArray())
            {
                if (string.Equals(pair.Key, callId, StringComparison.Ordinal))
                {
                    task.Requests.Remove(pair.Key);
                    return true;
                }
            }
        }

        return false;
    }

    private static void ClearRequests(MutableTask task, string turnId)
    {
        if (task.Requests.Count == 0)
        {
            task.NonBlockingInputPending = false;
            return;
        }

        foreach (var pair in task.Requests.ToArray())
        {
            if (turnId.Length == 0 || pair.Value.TurnId.Length == 0 ||
                string.Equals(pair.Value.TurnId, turnId, StringComparison.Ordinal))
            {
                task.Requests.Remove(pair.Key);
            }
        }

        task.NonBlockingInputPending = task.Requests.Values.Any(static request => !request.IsPermission && !request.IsBlocking);
    }

    private void RecomputeAttentionState(MutableTask task)
    {
        task.NonBlockingInputPending = task.Requests.Values.Any(static request => !request.IsPermission && !request.IsBlocking);
        var permission = task.Requests.Values.FirstOrDefault(static request => request.IsPermission);
        if (permission is not null)
        {
            task.AttentionCode = "permission-requested";
            SetState(task, AgentTaskState.NeedsApproval, EvidenceKind.Explicit, "permission-requested");
            return;
        }

        var blockingInput = task.Requests.Values.FirstOrDefault(static request => !request.IsPermission && request.IsBlocking);
        if (blockingInput is not null)
        {
            task.AttentionCode = "input-requested";
            SetState(task, AgentTaskState.NeedsInput, EvidenceKind.Explicit, "input-requested");
            return;
        }

        task.NonBlockingInputPending = task.Requests.Count > 0;
        task.AttentionCode = task.NonBlockingInputPending ? "input-requested-nonblocking" : null;
        if (task.State is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput)
        {
            var restored = task.Turns.TryGetValue(task.TurnId, out var turn) && turn.Terminal
                ? turn.Outcome switch
                {
                    TurnOutcome.Completed => AgentTaskState.Done,
                    TurnOutcome.Failed => AgentTaskState.Error,
                    _ => AgentTaskState.Idle,
                }
                : AgentTaskState.Working;
            SetState(task, restored, EvidenceKind.Explicit, "attention-resolved");
        }
    }

    private static void AddTool(MutableTask task, in RawCodexEvent codexEvent)
    {
        var callId = codexEvent.CallId ?? "call-" + codexEvent.SourceOrder.ToString(System.Globalization.CultureInfo.InvariantCulture);
        task.ActiveTools[callId] = new ToolRecord
        {
            CallId = callId,
            Category = codexEvent.Tool,
            Action = codexEvent.Action,
            DetailCode = codexEvent.ActionDetailCode,
            TurnId = codexEvent.TurnId,
            Order = task.ActivityOrder++,
        };
        task.ToolOrder.Enqueue(callId);
        while (task.ToolOrder.Count > task.ToolLimit)
        {
            task.ActiveTools.Remove(task.ToolOrder.Dequeue());
        }
    }

    private static void CompleteTool(MutableTask task, in RawCodexEvent codexEvent)
    {
        if (codexEvent.CallId is { Length: > 0 } callId)
        {
            task.ActiveTools.Remove(callId);
        }
    }

    private static void RecordActivity(MutableTask task, in RawCodexEvent codexEvent)
    {
        if (codexEvent.Action is null && codexEvent.Tool is ToolCallCategory.None)
        {
            return;
        }

        task.LastActivity = new ActivityRecord
        {
            Category = codexEvent.Tool,
            Action = codexEvent.Action,
            DetailCode = codexEvent.ActionDetailCode,
            TurnId = codexEvent.TurnId,
            Order = task.ActivityOrder++,
        };
        task.HasActivityInCurrentTurn = codexEvent.TurnId.Length == 0 ||
                                        string.Equals(codexEvent.TurnId, task.TurnId, StringComparison.Ordinal);
    }

    private AgentAction UpdateAction(MutableTask task)
    {
        var activeTools = new List<ActiveToolView>(task.ActiveTools.Count);
        foreach (var tool in task.ActiveTools.Values)
        {
            if (tool.TurnId.Length > 0 && task.TurnId.Length > 0 &&
                !string.Equals(tool.TurnId, task.TurnId, StringComparison.Ordinal))
            {
                continue;
            }

            activeTools.Add(new ActiveToolView(tool.Category, tool.Action, tool.DetailCode, tool.Order));
        }

        var hasActivity = task.LastActivity is not null &&
                          (task.LastActivity.TurnId.Length == 0 || task.TurnId.Length == 0 ||
                           string.Equals(task.LastActivity.TurnId, task.TurnId, StringComparison.Ordinal));

        var context = new ClassifierContext(
            task.State,
            task.Requests.Values.Any(static request => request.IsPermission),
            task.Requests.Values.Any(static request => !request.IsPermission && request.IsBlocking),
            activeTools,
            hasActivity ? task.LastActivity!.Action : null,
            hasActivity ? task.LastActivity!.DetailCode : null,
            hasActivity ? task.LastActivity!.Category : ToolCallCategory.None,
            hasActivity,
            task.CancellationObserved);

        return _classifier.Classify(context);
    }

    private AgentTask ToImmutable(MutableTask task) => new(
        task.Key,
        task.Number,
        task.ConversationId,
        task.ProfileId,
        task.Surface,
        task.Label,
        task.TurnId,
        task.State,
        task.Action,
        task.Evidence,
        task.FirstSeenAt,
        task.LastActivityAt,
        task.StateSince,
        task.StartedAt,
        task.CompletedAt,
        task.IsStale,
        !task.IdentityResolved,
        task.State is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput || task.NonBlockingInputPending,
        task.CancellationObserved,
        task.FailureCode is not null,
        task.AttentionCode,
        task.SourceFiles.Count);

    private sealed class MutableTask
    {
        public TaskKey Key;
        public int Number;
        public string ProfileId = string.Empty;
        public string ConversationId = string.Empty;
        public ClientSurface Surface = ClientSurface.Unknown;
        public string Label = string.Empty;
        public bool IdentityResolved;
        public string TurnId = string.Empty;
        public DateTimeOffset TurnStartedAt;
        public DateTimeOffset TurnLastActivityAt;
        public int TurnOrder;
        public AgentTaskState State;
        public EvidenceKind Evidence;
        public AgentAction Action = AgentAction.Idle;
        public string StateCauseCode = string.Empty;
        public DateTimeOffset FirstSeenAt;
        public DateTimeOffset LastActivityAt;
        public DateTimeOffset StateSince;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? CompletedAt;
        public bool HasExplicitLifecycle;
        public bool CancellationObserved;
        public string? FailureCode;
        public string? AttentionCode;
        public bool NonBlockingInputPending;
        public bool IsStale;
        public bool HasActivityInCurrentTurn;
        public int ActivityOrder;
        public readonly Dictionary<string, RequestRecord> Requests = new(StringComparer.Ordinal);
        public readonly Dictionary<string, TurnRecord> Turns = new(StringComparer.Ordinal);
        public readonly Queue<string> TurnSequence = new();
        public readonly Dictionary<string, ToolRecord> ActiveTools = new(StringComparer.Ordinal);
        public readonly Queue<string> ToolOrder = new();
        public readonly HashSet<string> SeenMarkers = new(StringComparer.Ordinal);
        public readonly Queue<string> SeenOrder = new();
        public readonly HashSet<int> SourceFiles = new();
        public ActivityRecord? LastActivity;
        public int TurnLimit = 64;
        public int RequestLimit = 64;
        public int ToolLimit = 32;
        public int SeenMarkerLimit = 512;
    }

    private sealed class RequestRecord
    {
        public string RequestId = string.Empty;
        public bool IsPermission;
        public bool IsBlocking;
        public string TurnId = string.Empty;
        public DateTimeOffset Since;
    }

    private sealed class TurnRecord
    {
        public string TurnId = string.Empty;
        public DateTimeOffset LastActivityAt;
        public bool Terminal;
        public TurnOutcome? Outcome;
        public int Order;
    }

    private sealed class ToolRecord
    {
        public string CallId = string.Empty;
        public ToolCallCategory Category;
        public AgentActionKind? Action;
        public string? DetailCode;
        public string TurnId = string.Empty;
        public int Order;
    }

    private sealed class ActivityRecord
    {
        public ToolCallCategory Category;
        public AgentActionKind? Action;
        public string? DetailCode;
        public string TurnId = string.Empty;
        public int Order;
    }
}
