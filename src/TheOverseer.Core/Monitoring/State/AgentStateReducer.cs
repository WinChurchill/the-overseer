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
public sealed partial class AgentStateReducer
{
    private MonitorOptions _options;
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

    internal void SetSessionLimit(int limit) =>
        _options = _options with { MaxTrackedConversations = Math.Min(_options.MaxTrackedConversations, limit) };

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
        DateTimeOffset now,
        string workspaceRoot = "",
        ProjectDescriptor? project = null)
    {
        var key = new TaskKey(profileId, conversationId);
        var task = GetOrCreate(key, now);
        if (surface != ClientSurface.Unknown && task.Surface == ClientSurface.Unknown)
        {
            task.Surface = surface;
        }

        if (workspaceRoot.Length > 0)
        {
            // The first resolved root wins for the session: an incidental shell cd
            // must not silently move a whole session between projects.
            if (task.WorkspaceRoot.Length == 0)
            {
                task.WorkspaceRoot = workspaceRoot;
            }
        }

        if (project is not null && task.Project is null)
        {
            task.Project = project;
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
            WorkspaceRoot = old.WorkspaceRoot, Project = old.Project,
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


}
