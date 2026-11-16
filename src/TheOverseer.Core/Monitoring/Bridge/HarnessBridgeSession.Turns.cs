using System.Globalization;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.Bridge;

public sealed partial class HarnessBridgeSession
{
    private static string? RequestIdentity(HarnessBridgeRecord record) =>
        !string.IsNullOrWhiteSpace(record.RequestId) ? record.RequestId :
        !string.IsNullOrWhiteSpace(record.CallId) ? record.CallId : null;

    /// <summary>
    /// Calls whose end was never observed must not accumulate. At the bound no new
    /// call is tracked, and uniqueness cannot be inferred until the next boundary.
    /// </summary>
    private const int MaximumOpenCalls = 64;

    private static void TrackRequest(SessionLane lane, PendingRequests requests, HarnessBridgeRecord record)
    {
        if (!requests.Add(RequestIdentity(record), observed: !record.Hydration))
            lane.CurrentStateComplete = false;
    }

    /// <summary>
    /// Claude reports a permission prompt without the call it belongs to and never
    /// reports that it was granted. When exactly one thing was waiting at that
    /// moment, its own completion is the evidence that the prompt was resolved.
    /// Anything ambiguous keeps the request until the turn or session ends.
    /// </summary>
    private void BindUncorrelatedApproval(SessionLane lane, bool alreadyPending)
    {
        var input = lane.Inputs.SingleIdentity;
        var blocker = Source.HarnessId != HarnessIds.Claude || !lane.OpenCallsComplete ? null
            : lane.OpenCalls.Count == 1 && lane.Inputs.Count == 0 ? lane.OpenCalls.First()
            : lane.OpenCalls.Count == 0 && input is not null ? input
            : null;
        lane.ApprovalBlocker = alreadyPending && !string.Equals(lane.ApprovalBlocker, blocker, StringComparison.Ordinal)
            ? null : blocker;
    }

    private static void ResolveBlockedApproval(SessionLane lane, string? finished)
    {
        if (finished is null || !string.Equals(lane.ApprovalBlocker, finished, StringComparison.Ordinal)) return;
        lane.ApprovalBlocker = null;
        lane.Approvals.ClearUncorrelated();
    }

    private BridgeAttentionEdge? Fold(SessionLane lane, HarnessBridgeRecord record, DateTimeOffset stamp, bool gapDetected)
    {
        lane.LastStamp = stamp;
        var turn = string.IsNullOrWhiteSpace(record.TurnId) ? lane.TurnId : record.TurnId!;
        var quiet = record.Hydration;
        var livenessOnly = IsLivenessOnly(record.Kind);
        if (IgnoresAction(lane, record))
        {
            // Late lifecycle records are history, not current category/action evidence.
            _ignored++;
            return null;
        }
        if (!livenessOnly && !string.IsNullOrWhiteSpace(record.Category))
        {
            // A heartbeat reports the tracked category; that is not new evidence.
            lane.Category = record.Category;
        }

        switch (record.Kind)
        {
            case "session/start":
                lane.EpochSeen = true;
                // The session began under observation and no record was missed
                // since, so its current state is known without a producer snapshot.
                lane.CurrentStateComplete = !gapDetected;
                lane.OpenCalls.Clear();
                lane.OpenCallsComplete = !gapDetected;
                lane.HistoryGap = lane.HistoryGap || gapDetected;
                lane.Retired = false;
                lane.State = AgentTaskState.Idle;
                lane.TurnId = string.Empty;
                lane.DetailCode = "session-start";
                lane.NeedsHuman = false;
                lane.WithdrawAttentionConfirmation();
                lane.AttentionCode = null;
                lane.TurnEnded = false;
                lane.OutcomeUnknown = false;
                lane.TurnOutcome = BridgeTurnOutcome.None;
                lane.Approvals.Clear();
                lane.Inputs.Clear();
                lane.ApprovalBlocker = null;
                return null;

            case "session/end":
                // Claude closes a desktop session seconds after its last turn ends.
                // A turn that finished keeps its result (Done or failed) so the finish
                // is still visible and reviewable; only an unfinished session goes idle.
                if (lane.TurnEnded && lane.State is AgentTaskState.Done or AgentTaskState.Error)
                {
                    lane.NeedsHuman = false;
                    lane.WithdrawAttentionConfirmation();
                    lane.OpenCalls.Clear();
                    lane.OpenCallsComplete = false;
                    lane.AttentionCode = null;
                    lane.Approvals.Clear();
                    lane.Inputs.Clear();
                    lane.ApprovalBlocker = null;
                    return null;
                }

                lane.State = AgentTaskState.Idle;
                lane.TurnId = string.Empty;
                lane.DetailCode = "session-end";
                lane.NeedsHuman = false;
                lane.WithdrawAttentionConfirmation();
                lane.OpenCalls.Clear();
                lane.OpenCallsComplete = false;
                lane.AttentionCode = null;
                lane.TurnEnded = true;
                lane.Approvals.Clear();
                lane.Inputs.Clear();
                lane.ApprovalBlocker = null;
                return null;

            case "turn/start":
                lane.State = AgentTaskState.Working;
                lane.TurnId = turn;
                lane.DetailCode = "turn-active";
                lane.Category = record.Category == "unknown" ? "thinking" : record.Category;
                lane.NeedsHuman = false;
                lane.WithdrawAttentionConfirmation();
                lane.AttentionCode = null;
                lane.TurnEnded = false;
                lane.OutcomeUnknown = false;
                lane.TurnOutcome = BridgeTurnOutcome.None;
                // A call left open by an earlier turn is not what a new prompt blocks.
                lane.OpenCalls.Clear();
                lane.OpenCallsComplete = !gapDetected;
                // A new prompt in a session already under observation means every
                // earlier request was answered, so records the producer lost before
                // it cannot change what this task is doing now. With no gap in this
                // session's own sequence, the current state is known again, as after
                // a session start; otherwise one producer drop would leave every
                // long-lived session unconfirmed until OVERSEER restarts. A prompt
                // that is the first record seen (attached mid-session) proves nothing,
                // and a session whose own sequence had a gap waits for a snapshot.
                if (!gapDetected && !lane.HistoryGap && !quiet && lane.RecordsFolded > 0) lane.CurrentStateComplete = true;
                lane.ApprovalBlocker = null;
                if (lane.Approvals.Count > 0 || lane.Inputs.Count > 0) Settle(lane);
                return null;

            case "turn/end":
                return EndTurn(lane, record, stamp, quiet, turn);

            case "tool/start":
                lane.TurnId = turn;
                if (string.IsNullOrWhiteSpace(record.CallId) ||
                    (lane.OpenCalls.Count >= MaximumOpenCalls && !lane.OpenCalls.Contains(record.CallId)))
                {
                    lane.OpenCallsComplete = false;
                    lane.ApprovalBlocker = null;
                }
                else
                {
                    lane.OpenCalls.Add(record.CallId!);
                }

                if (lane.Approvals.Count > 0 || lane.Inputs.Count > 0)
                {
                    // Work continuing behind an unanswered request does not make the
                    // task look attended to: the human is still the blocker.
                    lane.NeedsHuman = true;
                    lane.State = lane.Approvals.Count > 0 ? AgentTaskState.NeedsApproval : AgentTaskState.NeedsInput;
                    lane.AttentionCode = lane.Approvals.Count > 0 ? "approval-pending" : "input-pending";
                    lane.Category = "waiting";
                    return null;
                }

                lane.State = AgentTaskState.Working;
                lane.DetailCode = SafeDetail(record);
                lane.Category = record.Category == "unknown" ? "working" : record.Category;
                return null;

            case "tool/end":
                if (!string.IsNullOrWhiteSpace(record.CallId))
                {
                    lane.OpenCalls.Remove(record.CallId!);
                    if (lane.ApprovalBlocker is not null)
                    {
                        // The one call the prompt was blocking has now run.
                        ResolveBlockedApproval(lane, record.CallId);
                        if (lane.ApprovalBlocker is null) Settle(lane);
                    }
                }
                else
                {
                    lane.OpenCallsComplete = false;
                    lane.ApprovalBlocker = null;
                }

                // A failed tool is not a failed task.
                if (lane.State is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput)
                {
                    // The request is still unanswered: keep it visible.
                    lane.Category = "waiting";
                    return null;
                }

                lane.DetailCode = string.Equals(record.Outcome, "failed", StringComparison.OrdinalIgnoreCase)
                    ? "tool-failed"
                    : SafeDetail(record);
                if (lane.State == AgentTaskState.Working)
                {
                    lane.Category = record.Category == "unknown" ? "working" : record.Category;
                }

                return null;

            case "approval/asked":
                lane.NeedsHuman = true;
                lane.State = AgentTaskState.NeedsApproval;
                lane.TurnId = turn;
                lane.AttentionCode = "approval-pending";
                lane.DetailCode = "waiting-for-approval";
                lane.Category = "waiting";
                var approvalPending = lane.Approvals.Uncorrelated;
                TrackRequest(lane, lane.Approvals, record);
                if (RequestIdentity(record) is null) BindUncorrelatedApproval(lane, approvalPending);

                // Claude also emits permission notifications for AskUserQuestion.
                // Reuse the same complete-call-set correlation that clears this
                // duplicate on answer; keep the explicit input episode and cue.
                if (!approvalPending && record.Evidence == "inferred" && RequestIdentity(record) is null &&
                    lane.Approvals.Count == 1 && lane.ApprovalBlocker is { } question &&
                    string.Equals(question, lane.Inputs.SingleIdentity, StringComparison.Ordinal))
                {
                    lane.Approvals.ClearUncorrelated();
                    lane.ApprovalBlocker = null;
                    lane.State = AgentTaskState.NeedsInput;
                    lane.AttentionCode = "input-pending";
                    lane.DetailCode = "waiting-for-input";
                    return null;
                }

                return quiet ? null : Edge(lane, record, AttentionEventKind.PermissionRequired, "approval-pending");

            case "approval/decided":
                if (RequestIdentity(record) is { } approvalId)
                {
                    lane.Approvals.Remove(approvalId);
                    // An explicit decision for the blocked call settles its prompt,
                    // and a refused call will never report an end.
                    ResolveBlockedApproval(lane, approvalId);
                    if (!string.IsNullOrWhiteSpace(record.CallId)) lane.OpenCalls.Remove(record.CallId!);
                }

                Settle(lane);
                return null;

            case "input/asked":
                lane.NeedsHuman = true;
                lane.State = AgentTaskState.NeedsInput;
                lane.TurnId = turn;
                lane.AttentionCode = "input-pending";
                lane.DetailCode = "waiting-for-input";
                lane.Category = "waiting";
                TrackRequest(lane, lane.Inputs, record);

                Settle(lane);

                return quiet ? null : Edge(lane, record, AttentionEventKind.InputRequired, "input-pending");

            case "input/answered":
                if (RequestIdentity(record) is { } inputId)
                {
                    lane.Inputs.Remove(inputId);
                    // Older bridges report a question's own dialog as an approval.
                    ResolveBlockedApproval(lane, inputId);
                }

                Settle(lane);
                return null;

            case "plan/mode":
                // The producer maps both pre- and post-tool plan hooks here, so
                // this marker cannot prove whether its call is still open.
                lane.OpenCallsComplete = false;
                lane.ApprovalBlocker = null;
                // Plan mode is a point marker, never sustained planning duration, and
                // it never hides an unanswered request either.
                lane.DetailCode = "plan-update";
                if (lane.Approvals.Count > 0 || lane.Inputs.Count > 0)
                {
                    lane.Category = "waiting";
                    lane.State = lane.Approvals.Count > 0 ? AgentTaskState.NeedsApproval : AgentTaskState.NeedsInput;
                    return null;
                }

                lane.Category = "planning";
                if (lane.State == AgentTaskState.Idle)
                {
                    lane.State = AgentTaskState.Working;
                }

                return null;

            case "snapshot":
                return null;

            default:
                // heartbeat: liveness only, no state change.
                return null;
        }
    }

    private static bool IgnoresAction(SessionLane lane, HarnessBridgeRecord record) =>
        record.Kind == "tool/start" && lane.TurnEnded ||
        record.Kind == "turn/end" && lane.TurnId.Length > 0 && !string.IsNullOrWhiteSpace(record.TurnId) &&
        !string.Equals(lane.TurnId, record.TurnId, StringComparison.Ordinal);

    private BridgeAttentionEdge? EndTurn(SessionLane lane, HarnessBridgeRecord record, DateTimeOffset stamp, bool quiet, string turn)
    {
        if (lane.TurnId.Length > 0 && turn.Length > 0 && !string.Equals(lane.TurnId, turn, StringComparison.Ordinal))
        {
            // A late end for a turn that is no longer the tracked turn must not
            // close the current one.
            _ignored++;
            return null;
        }

        lane.TurnEnded = true;
        lane.NeedsHuman = false;
        lane.WithdrawAttentionConfirmation();
        lane.OpenCallsComplete = false;
        lane.ApprovalBlocker = null;
        lane.OpenCalls.Clear();
        lane.Approvals.Clear();
        var continuingInput = MonitorCapabilities.HasDshQuestionEvents(Source.HarnessId, _producerVersion) && lane.Inputs.Count > 0;
        if (!continuingInput) lane.Inputs.Clear();
        lane.AttentionCode = null;
        var outcome = (record.Outcome ?? string.Empty).Trim().ToLowerInvariant();
        if (continuingInput)
        {
            lane.TurnOutcome = outcome switch
            {
                "ok" or "completed" or "complete" => BridgeTurnOutcome.Completed,
                "error" or "failed" => BridgeTurnOutcome.Error,
                "aborted" or "cancelled" or "canceled" => BridgeTurnOutcome.Aborted,
                _ => BridgeTurnOutcome.Unknown,
            };
            lane.OutcomeUnknown = lane.TurnOutcome == BridgeTurnOutcome.Unknown;
            if (lane.OutcomeUnknown) lane.CurrentStateComplete = false;
            Settle(lane);
            // A completed turn with an answerable question is still awaiting the
            // user. Do not emit a competing task-complete voice notification.
            return null;
        }
        switch (outcome)
        {
            case "ok":
            case "completed":
            case "complete":
                lane.State = AgentTaskState.Done;
                lane.DetailCode = "turn-complete";
                lane.Category = "complete";
                lane.TurnOutcome = BridgeTurnOutcome.Completed;
                lane.OutcomeUnknown = false;
                return quiet ? null : Edge(lane, record, AttentionEventKind.TaskCompleted, "turn-complete");
            case "error":
            case "failed":
                lane.State = AgentTaskState.Error;
                lane.DetailCode = "turn-error";
                lane.Category = "error";
                lane.TurnOutcome = BridgeTurnOutcome.Error;
                lane.OutcomeUnknown = false;
                return quiet ? null : Edge(lane, record, AttentionEventKind.TaskError, "turn-error");
            case "aborted":
            case "cancelled":
            case "canceled":
                // Cancellation is silent by design.
                lane.State = AgentTaskState.Idle;
                lane.DetailCode = "turn-aborted";
                lane.Category = "idle";
                lane.TurnOutcome = BridgeTurnOutcome.Aborted;
                lane.OutcomeUnknown = false;
                return null;
            default:
                // An unstated or unrecognized outcome is not a completion: the turn
                // ended, but whether the work succeeded is genuinely unknown.
                lane.State = AgentTaskState.Idle;
                lane.DetailCode = "turn-outcome-unknown";
                lane.Category = "unknown";
                lane.TurnOutcome = BridgeTurnOutcome.Unknown;
                lane.OutcomeUnknown = true;
                lane.CurrentStateComplete = false;
                return null;
        }
    }

    private static string SafeDetail(HarnessBridgeRecord record) =>
        string.IsNullOrWhiteSpace(record.Category) || record.Category == "unknown"
            ? "action-unknown"
            : record.Category + "-activity";

    private void Settle(SessionLane lane)
    {
        if (lane.Approvals.Count > 0)
        {
            lane.NeedsHuman = true;
            lane.State = AgentTaskState.NeedsApproval;
            lane.AttentionCode = "approval-pending";
            lane.DetailCode = "waiting-for-approval";
            lane.Category = "waiting";
            return;
        }

        if (lane.Inputs.Count > 0)
        {
            lane.NeedsHuman = true;
            lane.State = AgentTaskState.NeedsInput;
            lane.AttentionCode = "input-pending";
            lane.DetailCode = "waiting-for-input";
            lane.Category = "waiting";
            return;
        }

        lane.NeedsHuman = false;
        lane.AttentionCode = null;
        if (lane.TurnEnded && lane.State is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput)
        {
            lane.State = lane.TurnOutcome switch
            {
                BridgeTurnOutcome.Completed => AgentTaskState.Done,
                BridgeTurnOutcome.Error => AgentTaskState.Error,
                _ => AgentTaskState.Idle,
            };
            lane.Category = lane.State == AgentTaskState.Done ? "complete" : lane.State == AgentTaskState.Error ? "error" : "idle";
            lane.DetailCode = "input-settled";
        }
        if (!lane.TurnEnded && lane.State is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput)
        {
            lane.State = AgentTaskState.Working;
            lane.Category = "working";
            lane.DetailCode = "action-unknown";
        }
    }

    private static BridgeAttentionEdge Edge(SessionLane lane, HarnessBridgeRecord record, AttentionEventKind kind, string code) => new(
        kind,
        TaskKey.For(record.HarnessId, record.SourceInstanceId, record.SessionId),
        0,
        string.Empty,
        lane.State,
        lane.TurnId,
        lane.LastStamp,
        code);

}
