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
public sealed partial class HarnessBridgeSession
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
    private string _currentEpoch = string.Empty;
    private bool _hasEpoch;
    private readonly HashSet<string> _retiredEpochs = new(StringComparer.Ordinal);
    private bool _epochHistoryFull;

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
                return _epochHistoryFull || _producerIncomplete || _sessions.Values.Any(static lane => !lane.CurrentStateComplete);
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
            // Lost/rejected records may have answered any retained request. A
            // source-level warning alone must not leave its old task count confirmed.
            foreach (var lane in _sessions.Values) lane.CurrentStateComplete = false;
            var stamp = atUtc ?? DateTimeOffset.UtcNow;
            _producerIncompleteAt = stamp;
            // The lost records are history that was never observed: current state can
            // be recovered from a later snapshot, the history hole cannot.
            _producerGap = true;
            if (stamp > _lastGapAt) _lastGapAt = stamp;
        }
    }

    /// <summary>Older retained evidence was skipped during bounded startup.</summary>
    public void NoteHistoryGap(DateTimeOffset atUtc)
    {
        lock (_gate)
        {
            _producerGap = true;
            if (atUtc > _lastGapAt) _lastGapAt = atUtc;
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

            var epochReason = AdmitEpoch(record.SourceEpoch);
            if (epochReason is not null)
            {
                return new BridgeApplyResult(BridgeApplyOutcome.Superseded, null, epochReason);
            }

            var lane = GetOrCreate(record.SessionId);
            if (lane.HasEpoch && !string.Equals(lane.Epoch, record.SourceEpoch, StringComparison.Ordinal))
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
                lane.Approvals.Clear();
                lane.Inputs.Clear();
                lane.OpenCalls.Clear();
                lane.NeedsHuman = false;
                lane.AttentionCode = null;
                lane.State = AgentTaskState.Idle;
                lane.TurnId = string.Empty;
                lane.TurnEnded = false;
                lane.TurnOutcome = BridgeTurnOutcome.None;
            }
            else if (!lane.HasEpoch)
            {
                lane.Epoch = record.SourceEpoch;
                lane.HasEpoch = true;
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
            if (record.ProducerVersion.Length > 0) _producerVersion = record.ProducerVersion;
            lane.LastEventId = record.EventId;
            if (!string.IsNullOrWhiteSpace(record.ProjectRoot) && lane.ProjectRoot.Length == 0)
            {
                lane.ProjectRoot = record.ProjectRoot!;
            }

            var stamp = Stamp(lane, record.ObservedAtUtc, receiptAtUtc);
            // New evidence makes a previously retired session live again.
            lane.Retired = false;
            var observedAction = !IsLivenessOnly(record.Kind) && !IgnoresAction(lane, record);
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
                    _epochHistoryFull || (!lane.CurrentStateComplete && !(lane.AttentionObserved && lane.NeedsHuman)),
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
        (_epochHistoryFull ? " epochs=limit" : string.Empty) +
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
    private sealed class SessionLane
    {
        public string SessionId = string.Empty;
        public string Epoch = string.Empty;
        public bool HasEpoch;
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
        private bool _currentStateComplete;

        /// <summary>
        /// Losing history also withdraws confirmation of a pending request: the
        /// missing records may have answered it.
        /// </summary>
        public bool CurrentStateComplete
        {
            get => _currentStateComplete;
            set
            {
                _currentStateComplete = value;
                if (!value)
                {
                    WithdrawAttentionConfirmation();
                    OpenCallsComplete = false;
                    ApprovalBlocker = null;
                }
            }
        }

        /// <summary>
        /// The pending request itself was observed, with no loss since. That request
        /// is current evidence even when the session's earlier history is incomplete.
        /// </summary>
        public bool AttentionObserved => Approvals.HasObserved || Inputs.HasObserved;

        public void WithdrawAttentionConfirmation()
        {
            Approvals.WithdrawConfirmation();
            Inputs.WithdrawConfirmation();
        }

        public bool OpenCallsComplete;

        /// <summary>
        /// What an identity-less approval was observed to block: the only open call
        /// or the only pending question at that moment. Null when that is ambiguous.
        /// </summary>
        public string? ApprovalBlocker;
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
        public readonly PendingRequests Approvals = new();
        public readonly PendingRequests Inputs = new();
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
