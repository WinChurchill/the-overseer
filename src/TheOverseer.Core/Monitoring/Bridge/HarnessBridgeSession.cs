using System.Globalization;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.Bridge;

public enum BridgeApplyOutcome
{
    Applied,
    Duplicate,
    OutOfOrder,
    Ignored,
    Stale,
    Rejected,

    /// <summary>
    /// The record predates an authoritative current-state document that already
    /// folded it. It is neither a fresh observation nor a lost one.
    /// </summary>
    Superseded,
}

public sealed record BridgeAttentionEdge(
    AttentionEventKind Kind,
    TaskKey Key,
    int Number,
    string Label,
    AgentTaskState State,
    string TurnId,
    DateTimeOffset ObservedAt,
    string Code);

public sealed record BridgeApplyResult(BridgeApplyOutcome Outcome, BridgeAttentionEdge? Attention, string ReasonCode);

/// <summary>Explicit terminal outcome of a completed turn record.</summary>
public enum BridgeTurnOutcome
{
    None,
    Completed,
    Error,
    Aborted,

    /// <summary>Ended, but the producer stated no outcome we can interpret.</summary>
    Unknown,
}

/// <summary>
/// One source-local task lane. Liveness (<see cref="LastObservedAt"/>, refreshed by
/// any record including a heartbeat) is deliberately separate from action evidence
/// (<see cref="LastActionAt"/>, refreshed only by an observed action), so a healthy
/// host heartbeat can never renew a stale action. Coverage is split the same way:
/// <see cref="CurrentStateComplete"/> can be restored by an authoritative producer
/// snapshot, while <see cref="HistoryGap"/> is a permanent property of this epoch.
/// </summary>
public sealed record BridgeTaskState(
    TaskKey Key,
    string SessionId,
    string TurnId,
    AgentTaskState State,
    string DetailCode,
    string Category,
    bool NeedsHuman,
    string? AttentionCode,
    DateTimeOffset LastObservedAt,
    bool IsStale,
    bool CoverageIncomplete,
    string ProjectRoot,
    long LastSequence,
    DateTimeOffset FirstObservedAt = default,
    DateTimeOffset StateSince = default,
    DateTimeOffset? TurnStartedAt = null,
    DateTimeOffset? CompletedAt = null,
    DateTimeOffset LastHeartbeatAt = default,
    DateTimeOffset LastActionAt = default,
    bool ActionEvidenceFresh = false,
    bool HistoryGap = false,
    BridgeTurnOutcome TurnOutcome = BridgeTurnOutcome.None,
    string ProducerVersion = "");

/// <summary>
/// Folds one bridge source's metadata records into task state. It never reads
/// content, never returns an execution decision, and never lets a per-session
/// ordering gap, a dropped producer record or an expired lease silently look like
/// healthy work: every one of those lowers coverage or ages the task instead.
///
/// Recovery is explicit rather than by clearing flags: only an authoritative
/// producer current-state document that covers the lane (see
/// <see cref="ApplyCurrentState"/>) restores <c>CurrentStateComplete</c>. A history
/// gap stays on the lane for the lifetime of the producer epoch, because records
/// that were never observed cannot be invented afterwards.
/// </summary>
public sealed class HarnessBridgeSession
{
    /// <summary>
    /// How long an observed action stays trustworthy without a new observation.
    /// Liveness may continue (heartbeats), but the specific action becomes unknown.
    /// </summary>
    public static readonly TimeSpan DefaultActionLease = TimeSpan.FromSeconds(10);

    private readonly SourceInstance _source;
    private readonly TimeSpan _staleLease;
    private readonly TimeSpan _actionLease;
    private int _maximumSessions;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionLane> _sessions = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private bool _producerIncomplete;
    private DateTimeOffset _producerIncompleteAt;
    private string _producerReason = string.Empty;
    private long _applied;
    private long _duplicates;
    private long _gaps;
    private long _ignored;
    private long _superseded;
    private long _resyncs;
    private long _retired;
    private long _recovered;
    private string _lastResyncReason = "none";
    private long _evictedSessions;
    private string _producerVersion = string.Empty;
    private DateTimeOffset _lastSnapshotAtUtc = DateTimeOffset.MinValue;
    private bool _emptyAuthoritative;
    private bool _producerGap;
    private DateTimeOffset _lastGapAt;

    public HarnessBridgeSession(
        SourceInstance source,
        TimeSpan? staleLease = null,
        int maximumSessions = 512,
        TimeSpan? actionLease = null)
    {
        _source = source;
        _staleLease = staleLease ?? TimeSpan.FromMinutes(15);
        _maximumSessions = Math.Clamp(maximumSessions, 1, 4096);
        _actionLease = actionLease ?? DefaultActionLease;
    }

    public SourceInstance Source => _source;

    /// <summary>Tracked-session ceiling, bounded by the composite's shared budget.</summary>
    public int MaximumSessions => _maximumSessions;

    /// <summary>
    /// Applies a new tracked-session ceiling. Lowering it evicts the oldest lanes on
    /// the next observation rather than growing past the aggregate budget.
    /// </summary>
    public void SetSessionLimit(int maximumSessions)
    {
        lock (_gate)
        {
            _maximumSessions = Math.Clamp(maximumSessions, 1, 4096);
        }
    }

    /// <summary>Producer version last seen in a document or record, for diagnostics.</summary>
    public string ProducerVersion => _producerVersion;

    /// <summary>
    /// True when current state is not fully known: an attach without a covering
    /// snapshot, a lost record since the last covering snapshot, or producer drops.
    /// A history gap does not by itself make current state unknown.
    /// </summary>
    public bool CoverageIncomplete
    {
        get
        {
            lock (_gate)
            {
                return _producerIncomplete || _sessions.Values.Any(static lane => !lane.CurrentStateComplete);
            }
        }
    }

    /// <summary>True when a history gap was observed in this source epoch.</summary>
    public bool HistoryGap
    {
        get
        {
            lock (_gate)
            {
                return _producerGap || _gaps > 0 || _sessions.Values.Any(static lane => lane.HistoryGap);
            }
        }
    }

    /// <summary>
    /// When the most recent history gap happened. A gap older than the activity
    /// window no longer affects anything the HUD can display, so callers can stop
    /// disclosing it without pretending it never happened.
    /// </summary>
    public DateTimeOffset LastHistoryGapAt
    {
        get
        {
            lock (_gate)
            {
                var latest = _lastGapAt;
                foreach (var lane in _sessions.Values)
                {
                    if (lane.HistoryGap && lane.LastGapAt > latest) latest = lane.LastGapAt;
                }

                return latest;
            }
        }
    }

    /// <summary>Whether the last authoritative resynchronization proved an empty source.</summary>
    public bool EmptySourceAuthoritative
    {
        get
        {
            lock (_gate)
            {
                return _emptyAuthoritative;
            }
        }
    }

    /// <summary>
    /// Producer-reported drops/overflow. They are coverage evidence: the bridge
    /// itself stayed healthy, but what OVERSEER saw is knowingly incomplete. A
    /// current-state document published after the drop restores current state.
    /// </summary>
    public void NoteProducerIncomplete(string reasonCode, DateTimeOffset? atUtc = null)
    {
        lock (_gate)
        {
            _producerIncomplete = true;
            _producerReason = reasonCode;
            var stamp = atUtc ?? DateTimeOffset.UtcNow;
            _producerIncompleteAt = stamp;
            // The lost records are history that was never observed: current state can
            // be recovered from a later snapshot, the history hole cannot.
            _producerGap = true;
            if (stamp > _lastGapAt) _lastGapAt = stamp;
        }
    }

    public BridgeApplyResult Apply(HarnessBridgeRecord record, DateTimeOffset? receiptAtUtc = null)
    {
        if (record is null)
        {
            return new BridgeApplyResult(BridgeApplyOutcome.Rejected, null, "null-record");
        }

        lock (_gate)
        {
            if (!string.Equals(record.SourceInstanceId, _source.SourceInstanceId, StringComparison.Ordinal) ||
                !string.Equals(record.HarnessId, _source.HarnessId, StringComparison.Ordinal))
            {
                return new BridgeApplyResult(BridgeApplyOutcome.Rejected, null, "wrong-source");
            }

            if (record.ProducerVersion.Length > 0)
            {
                _producerVersion = record.ProducerVersion;
            }

            var lane = GetOrCreate(record.SessionId);
            if (lane.Epoch.Length > 0 && !string.Equals(lane.Epoch, record.SourceEpoch, StringComparison.Ordinal))
            {
                // A reconnect with a new producer epoch is not a new voice episode,
                // but the records between the epochs were never observed and the
                // previous epoch's ordering watermark no longer applies.
                lane.HistoryGap = true;
                lane.LastGapAt = record.ObservedAtUtc;
                if (record.ObservedAtUtc > _lastGapAt) _lastGapAt = record.ObservedAtUtc;
                lane.CurrentStateComplete = false;
                lane.LastSequence = -1;
                lane.SnapshotWatermark = -1;
                lane.LastEventId = string.Empty;
                lane.Epoch = record.SourceEpoch;
            }
            else if (lane.Epoch.Length == 0)
            {
                lane.Epoch = record.SourceEpoch;
                lane.CurrentStateComplete = false;
            }

            if (record.Sequence <= lane.LastSequence)
            {
                if (lane.SnapshotWatermark >= 0 && record.Sequence <= lane.SnapshotWatermark)
                {
                    // The authoritative snapshot already folded this record; it is
                    // neither a duplicate delivery nor evidence of a lost record.
                    _superseded++;
                    return new BridgeApplyResult(BridgeApplyOutcome.Superseded, null, "superseded-by-snapshot");
                }

                _duplicates++;
                var duplicate = record.EventId.Length > 0 && string.Equals(lane.LastEventId, record.EventId, StringComparison.Ordinal);
                return new BridgeApplyResult(duplicate ? BridgeApplyOutcome.Duplicate : BridgeApplyOutcome.OutOfOrder, null, duplicate ? "duplicate" : "out-of-order");
            }

            var gapDetected = lane.LastSequence >= 0 && record.Sequence > lane.LastSequence + 1;
            if (gapDetected)
            {
                // A sequence gap means records were lost: coverage drops, and the
                // gap is visible instead of being smoothed over.
                lane.HistoryGap = true;
                lane.LastGapAt = record.ObservedAtUtc;
                if (record.ObservedAtUtc > _lastGapAt) _lastGapAt = record.ObservedAtUtc;
                lane.CurrentStateComplete = false;
                _gaps++;
            }

            lane.LastSequence = record.Sequence;
            lane.LastEventId = record.EventId;
            if (!string.IsNullOrWhiteSpace(record.ProjectRoot) && lane.ProjectRoot.Length == 0)
            {
                lane.ProjectRoot = record.ProjectRoot!;
            }

            var stamp = Stamp(lane, record.ObservedAtUtc, receiptAtUtc);
            // New evidence makes a previously retired session live again.
            lane.Retired = false;
            var observedAction = !IsLivenessOnly(record.Kind);
            lane.LastObservedAt = Later(lane.LastObservedAt, stamp);
            if (observedAction)
            {
                lane.LastActionAt = Later(lane.LastActionAt, stamp);
            }
            else
            {
                lane.LastHeartbeatAt = Later(lane.LastHeartbeatAt, stamp);
            }

            if (lane.FirstObservedAt == default) lane.FirstObservedAt = stamp;
            var previousState = lane.State;
            var previousTurn = lane.TurnId;
            var edge = Fold(lane, record, stamp, gapDetected);
            if (lane.StateSince == default || previousState != lane.State) lane.StateSince = stamp;
            if (record.Kind == "turn/start" && (lane.TurnStartedAt is null || previousTurn != lane.TurnId))
            {
                lane.TurnStartedAt = stamp;
                lane.CompletedAt = null;
            }

            if (record.Kind == "turn/end" && lane.State == AgentTaskState.Done && lane.CompletedAt is null)
                lane.CompletedAt = stamp;
            _applied++;
            return new BridgeApplyResult(BridgeApplyOutcome.Applied, edge, "ok");
        }
    }

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

    public IReadOnlyList<BridgeTaskState> Tasks(DateTimeOffset now)
    {
        lock (_gate)
        {
            var tasks = new List<BridgeTaskState>(_order.Count);
            foreach (var sessionId in _order)
            {
                if (!_sessions.TryGetValue(sessionId, out var lane))
                {
                    continue;
                }

                var stale = lane.LastObservedAt != default && now - lane.LastObservedAt > _staleLease;
                var actionFresh = lane.LastActionAt != default && now - lane.LastActionAt <= _actionLease;
                tasks.Add(new BridgeTaskState(
                    TaskKey.For(_source.HarnessId, _source.SourceInstanceId, sessionId),
                    sessionId,
                    lane.TurnId,
                    lane.State,
                    lane.DetailCode,
                    lane.Category,
                    lane.NeedsHuman,
                    lane.AttentionCode,
                    lane.LastObservedAt,
                    stale,
                    !lane.CurrentStateComplete,
                    lane.ProjectRoot,
                    lane.LastSequence,
                    lane.FirstObservedAt,
                    lane.StateSince,
                    lane.TurnStartedAt,
                    lane.CompletedAt,
                    lane.LastHeartbeatAt,
                    lane.LastActionAt,
                    actionFresh,
                    lane.HistoryGap,
                    lane.OutcomeUnknown ? BridgeTurnOutcome.Unknown : lane.TurnOutcome,
                    _producerVersion));
            }

            return tasks;
        }
    }

    /// <summary>Safe diagnostic summary: counts and codes only.</summary>
    public string Describe() =>
        "applied=" + _applied.ToString(CultureInfo.InvariantCulture) +
        " duplicates=" + _duplicates.ToString(CultureInfo.InvariantCulture) +
        " gaps=" + _gaps.ToString(CultureInfo.InvariantCulture) +
        " ignored=" + _ignored.ToString(CultureInfo.InvariantCulture) +
        " superseded=" + _superseded.ToString(CultureInfo.InvariantCulture) +
        " resyncs=" + _resyncs.ToString(CultureInfo.InvariantCulture) +
        " recovered=" + _recovered.ToString(CultureInfo.InvariantCulture) +
        " retired=" + _retired.ToString(CultureInfo.InvariantCulture) +
        " sessions=" + _sessions.Count.ToString(CultureInfo.InvariantCulture) +
        " resync=" + _lastResyncReason +
        (_producerIncomplete ? " producer=" + _producerReason : string.Empty);

    private SessionLane GetOrCreate(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        if (_sessions.Count >= _maximumSessions)
        {
            var oldest = _order[0];
            _order.RemoveAt(0);
            _sessions.Remove(oldest);
        }

        var lane = new SessionLane { SessionId = sessionId };
        _sessions[sessionId] = lane;
        _order.Add(sessionId);
        return lane;
    }

    /// <summary>Heartbeats and attach snapshots prove liveness, never activity.</summary>
    private static bool IsLivenessOnly(string kind) => kind is "heartbeat" or "snapshot";

    private static DateTimeOffset Later(DateTimeOffset current, DateTimeOffset candidate) => candidate > current ? candidate : current;

    /// <summary>
    /// Freshness watermark for one record: never earlier than the previous stamp
    /// (a replayed record cannot renew anything) and never later than the moment the
    /// collector actually read it (a skewed producer clock cannot create a future
    /// interval).
    /// </summary>
    private static DateTimeOffset Stamp(SessionLane lane, DateTimeOffset observedAt, DateTimeOffset? receiptAtUtc)
    {
        var stamp = Bound(observedAt, receiptAtUtc);
        return lane.LastStamp != default && stamp < lane.LastStamp ? lane.LastStamp : stamp;
    }

    private static DateTimeOffset Bound(DateTimeOffset observedAt, DateTimeOffset? receiptAtUtc) =>
        receiptAtUtc is { } receipt && observedAt > receipt ? receipt : observedAt;

    /// <summary>
    /// Maps one record to state. Hydrated records (attach/reconnect snapshots) set
    /// state silently; only fresh edges raise attention, and a late tool start can
    /// never reopen a turn that already ended.
    /// </summary>
    private BridgeAttentionEdge? Fold(SessionLane lane, HarnessBridgeRecord record, DateTimeOffset stamp, bool gapDetected)
    {
        lane.LastStamp = stamp;
        var turn = string.IsNullOrWhiteSpace(record.TurnId) ? lane.TurnId : record.TurnId!;
        var quiet = record.Hydration;
        var livenessOnly = IsLivenessOnly(record.Kind);
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
                lane.HistoryGap = lane.HistoryGap || gapDetected;
                lane.Retired = false;
                lane.State = AgentTaskState.Idle;
                lane.TurnId = string.Empty;
                lane.DetailCode = "session-start";
                lane.NeedsHuman = false;
                lane.AttentionCode = null;
                lane.TurnEnded = false;
                lane.OutcomeUnknown = false;
                lane.TurnOutcome = BridgeTurnOutcome.None;
                lane.Approvals.Clear();
                lane.Inputs.Clear();
                return null;

            case "session/end":
                lane.State = AgentTaskState.Idle;
                lane.TurnId = string.Empty;
                lane.DetailCode = "session-end";
                lane.NeedsHuman = false;
                lane.AttentionCode = null;
                lane.TurnEnded = true;
                lane.Approvals.Clear();
                lane.Inputs.Clear();
                return null;

            case "turn/start":
                lane.State = AgentTaskState.Working;
                lane.TurnId = turn;
                lane.DetailCode = "turn-active";
                lane.Category = record.Category == "unknown" ? "thinking" : record.Category;
                lane.NeedsHuman = false;
                lane.AttentionCode = null;
                lane.TurnEnded = false;
                lane.OutcomeUnknown = false;
                lane.TurnOutcome = BridgeTurnOutcome.None;
                if (MonitorCapabilities.HasDshQuestionEvents(Source.HarnessId, _producerVersion) && lane.Inputs.Count > 0) Settle(lane);
                return null;

            case "turn/end":
                return EndTurn(lane, record, stamp, quiet, turn);

            case "tool/start":
                if (lane.TurnEnded)
                {
                    // A late tool start cannot reopen a correlated completed turn.
                    _ignored++;
                    return null;
                }

                lane.TurnId = turn;
                if (!string.IsNullOrWhiteSpace(record.CallId))
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
                if (!string.IsNullOrWhiteSpace(record.RequestId))
                {
                    lane.Approvals.Add(record.RequestId!);
                }

                return quiet ? null : Edge(lane, record, AttentionEventKind.PermissionRequired, "approval-pending");

            case "approval/decided":
                if (!string.IsNullOrWhiteSpace(record.RequestId))
                {
                    lane.Approvals.Remove(record.RequestId!);
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
                if (!string.IsNullOrWhiteSpace(record.RequestId))
                {
                    lane.Inputs.Add(record.RequestId!);
                }

                Settle(lane);

                return quiet ? null : Edge(lane, record, AttentionEventKind.InputRequired, "input-pending");

            case "input/answered":
                if (!string.IsNullOrWhiteSpace(record.RequestId))
                {
                    lane.Inputs.Remove(record.RequestId!);
                }

                Settle(lane);
                return null;

            case "plan/mode":
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

    private sealed class SessionLane
    {
        public string SessionId = string.Empty;
        public string Epoch = string.Empty;
        public string LastEventId = string.Empty;
        public long LastSequence = -1;
        public long SnapshotWatermark = -1;
        public string TurnId = string.Empty;
        public AgentTaskState State = AgentTaskState.Idle;
        public string DetailCode = "no-observation";
        public string Category = "unknown";
        public bool NeedsHuman;
        public string? AttentionCode;
        public bool TurnEnded;
        public bool EpochSeen;
        public bool Retired;
        public bool CurrentStateComplete;
        public bool HistoryGap;
        public DateTimeOffset LastGapAt;
        public bool OutcomeUnknown;
        public BridgeTurnOutcome TurnOutcome = BridgeTurnOutcome.None;
        public string ProjectRoot = string.Empty;
        public DateTimeOffset LastObservedAt;
        public DateTimeOffset LastHeartbeatAt;
        public DateTimeOffset LastActionAt;
        public DateTimeOffset LastStamp;
        public DateTimeOffset FirstObservedAt;
        public DateTimeOffset StateSince;
        public DateTimeOffset? TurnStartedAt;
        public DateTimeOffset? CompletedAt;
        public readonly HashSet<string> Approvals = new(StringComparer.Ordinal);
        public readonly HashSet<string> Inputs = new(StringComparer.Ordinal);
        public readonly HashSet<string> OpenCalls = new(StringComparer.Ordinal);
    }
}

/// <summary>Outcome of one authoritative resynchronization.</summary>
public sealed record BridgeResyncResult(
    int Applied,
    int Recovered,
    int Retired,
    int Superseded,
    bool EpochChanged,
    bool EmptySource,
    string ReasonCode)
{
    public bool Accepted => string.Equals(ReasonCode, "ok", StringComparison.Ordinal);
}
