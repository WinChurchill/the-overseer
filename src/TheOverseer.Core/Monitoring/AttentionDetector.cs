using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.State;

namespace TheOverseer.Core.Monitoring;

/// <summary>
/// Semantic attention layer. It announces meaningful live transitions only:
/// hydration is baseline, stale or unsupported evidence announces nothing, and
/// "all tasks completed" requires known previously running work that reached
/// completion rather than disappearance, error or cancellation.
/// </summary>
public sealed class AttentionDetector
{
    private readonly int _maxHistory;
    private readonly Dictionary<TaskKey, AgentTaskState> _lastStates = new();
    private readonly List<AttentionEvent> _history = new();
    private bool _allCompleteArmed = true;

    public AttentionDetector(MonitorOptions options)
    {
        _maxHistory = options.Clamped().MaxAttentionHistory;
    }

    public bool HydrationComplete { get; private set; }

    public IReadOnlyList<AttentionEvent> History => _history;

    public void MarkHydrationComplete() => HydrationComplete = true;

    public void Reset()
    {
        _lastStates.Clear();
        _history.Clear();
        _allCompleteArmed = true;
        HydrationComplete = false;
    }

    public IReadOnlyList<AttentionEvent> Detect(
        IReadOnlyList<AgentTask> tasks,
        IReadOnlyList<TaskTransition> transitions,
        DateTimeOffset now,
        bool coverageComplete = true)
    {
        var events = new List<AttentionEvent>();
        var byKey = new Dictionary<TaskKey, AgentTask>();
        foreach (var task in tasks)
        {
            byKey[task.Key] = task;
        }

        var previouslyRunning = new HashSet<TaskKey>();
        foreach (var pair in _lastStates)
        {
            if (pair.Value is AgentTaskState.Working or AgentTaskState.NeedsInput or AgentTaskState.NeedsApproval)
            {
                previouslyRunning.Add(pair.Key);
            }
        }

        foreach (var transition in transitions)
        {
            if (transition.IsHydration || transition.Kind != TaskTransitionKind.StateChanged)
            {
                continue;
            }

            if (!byKey.TryGetValue(transition.Key, out var task) || !task.HasCurrentState ||
                task.State != transition.To || task.TurnId != transition.TurnId)
            {
                continue;
            }

            var kind = transition.To switch
            {
                AgentTaskState.NeedsApproval => AttentionEventKind.PermissionRequired,
                AgentTaskState.NeedsInput => AttentionEventKind.InputRequired,
                AgentTaskState.Error => AttentionEventKind.TaskError,
                AgentTaskState.Done when transition.From is AgentTaskState.Working or AgentTaskState.NeedsInput or AgentTaskState.NeedsApproval
                    => AttentionEventKind.TaskCompleted,
                _ => (AttentionEventKind?)null,
            };

            if (kind is null)
            {
                continue;
            }

            if (kind == AttentionEventKind.TaskCompleted && (task.CancellationObserved || task.TerminalFailureObserved))
            {
                continue;
            }

            events.Add(new AttentionEvent(
                kind.Value,
                task.Key,
                task.Number,
                task.DisplayLabel,
                task.State,
                task.Action.Kind,
                task.TurnId,
                now,
                transition.CauseCode));
        }

        foreach (var transition in transitions)
        {
            if (transition.IsHydration || transition.Kind != TaskTransitionKind.NonBlockingInputRaised)
            {
                continue;
            }

            if (!byKey.TryGetValue(transition.Key, out var task) || !task.HasCurrentState ||
                !task.NeedsHuman || task.TurnId != transition.TurnId)
            {
                continue;
            }

            // A verified unresolved non-blocking request raises attention
            // without turning WORKING into NEEDS_INPUT.
            events.Add(new AttentionEvent(
                AttentionEventKind.InputRequired,
                task.Key,
                task.Number,
                task.DisplayLabel,
                task.State,
                task.Action.Kind,
                task.TurnId,
                now,
                "input-requested-nonblocking"));
        }

        foreach (var task in tasks)
        {
            _lastStates[task.Key] = task.State;
        }

        TrimLastStates(tasks);

        if (HydrationComplete && coverageComplete &&
            !tasks.Any(static task => !task.HasCurrentState ||
                task.State is AgentTaskState.Working or AgentTaskState.NeedsInput or AgentTaskState.NeedsApproval) &&
            previouslyRunning.Count > 0 &&
            events.Exists(static item => item.Kind == AttentionEventKind.TaskCompleted))
        {
            var allDone = true;
            foreach (var key in previouslyRunning)
            {
                if (!byKey.TryGetValue(key, out var task) ||
                    task.State != AgentTaskState.Done ||
                    !task.HasCurrentState ||
                    task.CancellationObserved ||
                    task.TerminalFailureObserved)
                {
                    allDone = false;
                    break;
                }
            }

            if (allDone && _allCompleteArmed)
            {
                events.Add(new AttentionEvent(
                    AttentionEventKind.AllTasksCompleted,
                    default,
                    0,
                    string.Empty,
                    AgentTaskState.Done,
                    AgentActionKind.Completed,
                    string.Empty,
                    now,
                    "all-tasks-completed"));
                _allCompleteArmed = false;
            }
        }

        foreach (var task in tasks)
        {
            if (task.State is AgentTaskState.Working or AgentTaskState.NeedsInput or AgentTaskState.NeedsApproval)
            {
                _allCompleteArmed = true;
                break;
            }
        }

        foreach (var item in events)
        {
            _history.Add(item);
            while (_history.Count > _maxHistory)
            {
                _history.RemoveAt(0);
            }
        }

        return events;
    }

    private void TrimLastStates(IReadOnlyList<AgentTask> tasks)
    {
        var alive = new HashSet<TaskKey>();
        foreach (var task in tasks)
        {
            alive.Add(task.Key);
        }

        foreach (var key in _lastStates.Keys.ToArray())
        {
            if (!alive.Contains(key))
            {
                _lastStates.Remove(key);
            }
        }
    }
}
