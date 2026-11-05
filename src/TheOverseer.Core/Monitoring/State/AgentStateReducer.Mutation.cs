using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.State;

public sealed partial class AgentStateReducer
{
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
                // A synchronous input tool returns when the question is resolved.
                // Async input returns only an acknowledgement, so its matching
                // output must not clear the outstanding question.
                if (codexEvent.CallId is { } inputCall &&
                    task.Requests.TryGetValue(inputCall, out var inputRequest) &&
                    !inputRequest.IsPermission && inputRequest.IsBlocking)
                {
                    task.Requests.Remove(inputCall);
                    RecomputeAttentionState(task);
                    return TaskTransitionKind.AttentionCleared;
                }
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
            TurnId = codexEvent.TurnId.Length > 0 ? codexEvent.TurnId : task.TurnId,
            Since = codexEvent.EventTime ?? codexEvent.ObservedAt,
            QuestionCount = codexEvent.InputQuestionCount,
        };
        while (task.Requests.Count > task.RequestLimit)
        {
            task.RequestOverflowed = true;
            var oldest = task.Requests.Values.MinBy(static request => request.Since)!.RequestId;
            if (task.Requests.Remove(oldest) && task.Requests.Count == 0)
            {
                task.NonBlockingInputPending = false;
            }
        }
    }

    private static bool RemoveRequest(MutableTask task, in RawCodexEvent codexEvent)
    {
        if (codexEvent.InputAnswers.Count > 0)
        {
            var removed = false;
            foreach (var answer in codexEvent.InputAnswers)
            {
                if (!task.Requests.TryGetValue(answer.CallId, out var request) || request.IsPermission || request.IsBlocking ||
                    request.QuestionCount is not (> 0 and <= 64) || answer.QuestionIndex < 0 || answer.QuestionIndex >= request.QuestionCount ||
                    (codexEvent.EventTime ?? codexEvent.ObservedAt) < request.Since ||
                    (request.TurnId.Length > 0 && codexEvent.TurnId.Length > 0 && request.TurnId != codexEvent.TurnId))
                    continue;
                request.AnsweredQuestions.Add(answer.QuestionIndex);
                if (request.AnsweredQuestions.Count == request.QuestionCount)
                    removed |= task.Requests.Remove(answer.CallId);
            }
            return removed;
        }
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
        if (turnId.Length == 0 || task.TurnId == turnId) task.RequestOverflowed = false;
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

}
