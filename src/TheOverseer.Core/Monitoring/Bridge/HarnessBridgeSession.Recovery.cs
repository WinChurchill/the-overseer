using System.Globalization;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.Bridge;

public sealed partial class HarnessBridgeSession
{
    /// <summary>
    /// Applies an authoritative producer current-state document. This is the only
    /// way a lane recovers complete current-state coverage: the document is an
    /// atomic, complete statement of the producer's tracked state at
    /// <c>updatedAtUtc</c>, so a lane it covers is fully known again even after a
    /// late attach, a dropped record or a rotated event file. It never raises
    /// attention and never invents history; a lane whose observed sequence is ahead
    /// of the document is left untouched instead of being downgraded.
    /// </summary>
    public BridgeResyncResult ApplyCurrentState(HarnessBridgeCurrentState document, DateTimeOffset? receiptAtUtc = null)
    {
        if (document is null)
        {
            return new BridgeResyncResult(0, 0, 0, 0, false, false, "snapshot-null");
        }

        lock (_gate)
        {
            if (!string.Equals(document.HarnessId, _source.HarnessId, StringComparison.Ordinal) ||
                !string.Equals(document.SourceInstanceId, _source.SourceInstanceId, StringComparison.Ordinal))
            {
                return new BridgeResyncResult(0, 0, 0, 0, false, false, "snapshot-wrong-source");
            }

            if (document.UpdatedAtUtc < _lastSnapshotAtUtc)
            {
                // A stale document cannot un-prove what a newer one established.
                return new BridgeResyncResult(0, 0, 0, 0, false, false, "snapshot-stale");
            }

            var hadDropNote = _producerIncomplete;
            _lastSnapshotAtUtc = document.UpdatedAtUtc;
            _resyncs++;
            _lastResyncReason = "snapshot-applied";
            if (document.ProducerVersion.Length > 0) _producerVersion = document.ProducerVersion;

            var applied = 0;
            var recovered = 0;
            var superseded = 0;
            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Sessions)
            {
                var lane = GetOrCreate(entry.SessionId);
                changed.Add(entry.SessionId);
                var epochChanged = lane.Epoch.Length > 0 && !string.Equals(lane.Epoch, document.SourceEpoch, StringComparison.Ordinal);
                if (epochChanged)
                {
                    lane.HistoryGap = true;
                    lane.LastEventId = string.Empty;
                    lane.SnapshotWatermark = -1;
                    lane.TurnEnded = false;
                }

                if (entry.LastSequence < lane.LastSequence)
                {
                    // The document predates records already folded: never go back.
                    superseded++;
                    continue;
                }

                var before = lane.CurrentStateComplete;
                lane.Retired = false;
                var stamp = Bound(document.UpdatedAtUtc, receiptAtUtc);
                var sessionObserved = entry.LastObservedUtc is { } last && last <= stamp ? last : stamp;
                lane.Epoch = document.SourceEpoch;
                lane.LastSequence = entry.LastSequence;
                lane.SnapshotWatermark = entry.LastSequence;
                if (entry.LastEventId.Length > 0) lane.LastEventId = entry.LastEventId;
                if (entry.ProjectRoot.Length > 0) lane.ProjectRoot = entry.ProjectRoot;
                if (entry.FirstSeenUtc is { } firstSeen && (lane.FirstObservedAt == default || firstSeen < lane.FirstObservedAt))
                {
                    lane.FirstObservedAt = firstSeen;
                }

                lane.LastObservedAt = Later(lane.LastObservedAt, sessionObserved);
                lane.LastActionAt = Later(lane.LastActionAt, sessionObserved);
                lane.LastHeartbeatAt = Later(lane.LastHeartbeatAt, sessionObserved);
                lane.TurnId = entry.OpenTurnId;
                if (!string.IsNullOrWhiteSpace(entry.Category)) lane.Category = entry.Category;
                lane.NeedsHuman = false;
                lane.AttentionCode = null;
                lane.Approvals.Clear();
                lane.Inputs.Clear();
                if (!entry.Disposed) foreach (var id in entry.PendingInputRequestIds) lane.Inputs.Add(id);
                lane.TurnEnded = entry.Disposed || entry.OpenTurnId.Length == 0;
                lane.TurnOutcome = BridgeTurnOutcome.None;
                lane.OutcomeUnknown = false;

                if (entry.Disposed)
                {
                    lane.State = AgentTaskState.Idle;
                    lane.DetailCode = "session-end";
                    lane.Category = "idle";
                }
                else if (entry.PendingApprovalRequestId.Length > 0)
                {
                    lane.Approvals.Add(entry.PendingApprovalRequestId);
                    lane.NeedsHuman = true;
                    lane.State = AgentTaskState.NeedsApproval;
                    lane.AttentionCode = "approval-pending";
                    lane.DetailCode = "waiting-for-approval";
                    lane.Category = "waiting";
                }
                else if (entry.OpenTurnId.Length > 0)
                {
                    lane.State = AgentTaskState.Working;
                    lane.DetailCode = entry.PlanMode ? "plan-active" : "turn-active";
                    if (lane.Category is "unknown" or "complete" or "error") lane.Category = "working";
                }
                else if (lane.Inputs.Count > 0)
                {
                    lane.State = AgentTaskState.NeedsInput;
                    lane.DetailCode = "waiting-for-input";
                }
                else
                {
                    lane.State = AgentTaskState.Idle;
                    lane.DetailCode = "session-idle";
                }

                if (!entry.Disposed && lane.Inputs.Count > 0) Settle(lane);

                if (lane.StateSince == default || lane.State != AgentTaskState.Working) lane.StateSince = sessionObserved;
                lane.CurrentStateComplete = entry.InputStateComplete;
                applied++;
                if (!before && lane.CurrentStateComplete) recovered++;
            }

            // A session the producer no longer lists, in the same epoch, at a time
            // after we last observed it, is no longer live: retire it instead of
            // leaving a ghost task that looks like ongoing work forever. On an epoch
            // change we cannot tell "gone" from "not yet re-attached", so the lane
            // only loses current-state coverage.
            var retired = 0;
            foreach (var pair in _sessions)
            {
                var lane = pair.Value;
                if (changed.Contains(lane.SessionId) || lane.Retired) continue;
                if (string.Equals(lane.Epoch, document.SourceEpoch, StringComparison.Ordinal) &&
                    document.UpdatedAtUtc > lane.LastObservedAt &&
                    lane.LastObservedAt != default)
                {
                    lane.Retired = true;
                    lane.State = AgentTaskState.Idle;
                    lane.TurnId = string.Empty;
                    lane.DetailCode = "session-unobserved";
                    lane.Category = "idle";
                    lane.NeedsHuman = false;
                    lane.AttentionCode = null;
                    lane.TurnEnded = true;
                    lane.CurrentStateComplete = true;
                    retired++;
                }
                else if (!string.Equals(lane.Epoch, document.SourceEpoch, StringComparison.Ordinal))
                {
                    lane.CurrentStateComplete = false;
                }
            }

            _retired += retired;
            _recovered += recovered;
            if (document.EvictedSessions > _evictedSessions)
            {
                // The producer evicted tracked sessions: some members of this epoch
                // may no longer be summarized anywhere. Current state may still be
                // complete for the remaining sessions, but history is not.
                _evictedSessions = document.EvictedSessions;
                if (document.UpdatedAtUtc > _lastGapAt) _lastGapAt = document.UpdatedAtUtc;
                foreach (var lane in _sessions.Values)
                {
                    lane.HistoryGap = true;
                    lane.LastGapAt = document.UpdatedAtUtc;
                }
            }
            else
            {
                _evictedSessions = document.EvictedSessions;
            }

            _emptyAuthoritative = document.Sessions.Count == 0;
            // A document applied AFTER a drop was noted is the producer's complete
            // statement of current state, so the lost records no longer make current
            // state unknown. The producer stages this document on every emit, drops
            // included, and the caller notes drops after applying the document, so at
            // least one honest pass always reports the drop before it clears. The lost
            // records stay lost: this restores current state, never history.
            if (hadDropNote)
            {
                // A session the document does not mention could have been affected by
                // the lost records, so its current state stays unproven.
                foreach (var lane in _sessions.Values)
                {
                    if (!changed.Contains(lane.SessionId) && !lane.Retired) lane.CurrentStateComplete = false;
                }
            }

            _producerIncomplete = false;
            _producerReason = string.Empty;

            return new BridgeResyncResult(applied, recovered, retired, superseded, false, _emptyAuthoritative, "ok");
        }
    }

}
