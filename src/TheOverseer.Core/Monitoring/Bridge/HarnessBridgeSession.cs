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
    DateTimeOffset? CompletedAt = null);

/// <summary>
/// Folds one bridge source's metadata records into task state. It never reads
/// content, never returns an execution decision, and never lets a per-session
/// ordering gap, a dropped producer record or an expired lease silently look like
/// healthy work: every one of those lowers coverage or ages the task instead.
/// </summary>
public sealed class HarnessBridgeSession
{
    private readonly SourceInstance _source;
    private readonly TimeSpan _staleLease;
    private readonly int _maximumSessions;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionLane> _sessions = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private bool _producerIncomplete;
    private string _producerReason = string.Empty;
    private long _applied;
    private long _duplicates;
    private long _gaps;
    private long _ignored;

    public HarnessBridgeSession(SourceInstance source, TimeSpan? staleLease = null, int maximumSessions = 512)
    {
        _source = source;
        _staleLease = staleLease ?? TimeSpan.FromMinutes(15);
        _maximumSessions = Math.Clamp(maximumSessions, 16, 4096);
    }

    public SourceInstance Source => _source;

    public bool CoverageIncomplete
    {
        get
        {
            lock (_gate)
            {
                return _producerIncomplete || _gaps > 0 || _sessions.Values.Any(static lane => lane.CoverageIncomplete);
            }
        }
    }

    /// <summary>
    /// Producer-reported drops/overflow. They are coverage evidence: the bridge
    /// itself stayed healthy, but what OVERSEER saw is knowingly incomplete.
    /// </summary>
    public void NoteProducerIncomplete(string reasonCode)
    {
        lock (_gate)
        {
            _producerIncomplete = true;
            _producerReason = reasonCode;
        }
    }

    public BridgeApplyResult Apply(HarnessBridgeRecord record)
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

            var lane = GetOrCreate(record.SessionId);
            if (lane.Epoch.Length > 0 && !string.Equals(lane.Epoch, record.SourceEpoch, StringComparison.Ordinal))
            {
                // A reconnect with a new producer epoch is not a new voice episode,
                // but the records between the epochs were never observed.
                lane.CoverageIncomplete = true;
                lane.LastSequence = -1;
                lane.Epoch = record.SourceEpoch;
            }
            else if (lane.Epoch.Length == 0)
            {
                lane.Epoch = record.SourceEpoch;
                lane.FirstAttach = true;
            }

            if (record.Sequence <= lane.LastSequence)
            {
                _duplicates++;
                var duplicate = record.EventId.Length > 0 && string.Equals(lane.LastEventId, record.EventId, StringComparison.Ordinal);
                return new BridgeApplyResult(duplicate ? BridgeApplyOutcome.Duplicate : BridgeApplyOutcome.OutOfOrder, null, duplicate ? "duplicate" : "out-of-order");
            }

            if (lane.LastSequence >= 0 && record.Sequence > lane.LastSequence + 1)
            {
                // A sequence gap means records were lost: coverage drops, and the
                // gap is visible instead of being smoothed over.
                lane.CoverageIncomplete = true;
                _gaps++;
            }

            lane.LastSequence = record.Sequence;
            lane.LastEventId = record.EventId;
            lane.LastObservedAt = record.ObservedAtUtc;
            if (!string.IsNullOrWhiteSpace(record.ProjectRoot) && lane.ProjectRoot.Length == 0)
            {
                lane.ProjectRoot = record.ProjectRoot!;
            }

            if (lane.FirstObservedAt == default) lane.FirstObservedAt = record.ObservedAtUtc;
            var previousState = lane.State;
            var previousTurn = lane.TurnId;
            var edge = Fold(lane, record);
            if (lane.StateSince == default || previousState != lane.State) lane.StateSince = record.ObservedAtUtc;
            if (record.Kind == "turn/start" && (lane.TurnStartedAt is null || previousTurn != lane.TurnId))
            {
                lane.TurnStartedAt = record.ObservedAtUtc;
                lane.CompletedAt = null;
            }
            if (record.Kind == "turn/end" && lane.State == AgentTaskState.Done && lane.CompletedAt is null)
                lane.CompletedAt = record.ObservedAtUtc;
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
                    lane.CoverageIncomplete || _producerIncomplete || lane.FirstAttach,
                    lane.ProjectRoot,
                    lane.LastSequence,
                    lane.FirstObservedAt,
                    lane.StateSince,
                    lane.TurnStartedAt,
                    lane.CompletedAt));
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
        " sessions=" + _sessions.Count.ToString(CultureInfo.InvariantCulture) +
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

    /// <summary>
    /// Maps one record to state. Hydrated records (attach/reconnect snapshots) set
    /// state silently; only fresh edges raise attention, and a late tool start can
    /// never reopen a turn that already ended.
    /// </summary>
    private BridgeAttentionEdge? Fold(SessionLane lane, HarnessBridgeRecord record)
    {
        lane.Category = record.Category;
        var turn = string.IsNullOrWhiteSpace(record.TurnId) ? lane.TurnId : record.TurnId!;
        var quiet = record.Hydration;

        switch (record.Kind)
        {
            case "session/start":
                lane.State = AgentTaskState.Idle;
                lane.TurnId = string.Empty;
                lane.DetailCode = "session-start";
                lane.NeedsHuman = false;
                lane.AttentionCode = null;
                lane.TurnEnded = false;
                lane.Approvals.Clear();
                lane.Inputs.Clear();
                return null;

            case "session/end":
                lane.State = AgentTaskState.Idle;
                lane.TurnId = string.Empty;
                lane.DetailCode = "session-end";
                lane.NeedsHuman = false;
                lane.AttentionCode = null;
                return null;

            case "turn/start":
                lane.State = AgentTaskState.Working;
                lane.TurnId = turn;
                lane.DetailCode = "turn-active";
                lane.Category = record.Category == "unknown" ? "thinking" : record.Category;
                lane.NeedsHuman = false;
                lane.AttentionCode = null;
                lane.TurnEnded = false;
                lane.CoverageIncomplete |= lane.FirstAttach;
                return null;

            case "turn/end":
                lane.TurnEnded = true;
                lane.NeedsHuman = false;
                lane.Approvals.Clear();
                lane.Inputs.Clear();
                lane.AttentionCode = null;
                switch ((record.Outcome ?? "completed").ToLowerInvariant())
                {
                    case "aborted":
                        // Cancellation is silent by design.
                        lane.State = AgentTaskState.Idle;
                        lane.DetailCode = "turn-aborted";
                        return null;
                    case "error":
                        lane.State = AgentTaskState.Error;
                        lane.DetailCode = "turn-error";
                        lane.Category = "error";
                        return quiet ? null : Edge(lane, record, AttentionEventKind.TaskError, "turn-error");
                    default:
                        lane.State = AgentTaskState.Done;
                        lane.DetailCode = "turn-complete";
                        lane.Category = "complete";
                        return quiet ? null : Edge(lane, record, AttentionEventKind.TaskCompleted, "turn-complete");
                }

            case "tool/start":
                if (lane.TurnEnded)
                {
                    // A late tool start cannot reopen a correlated completed turn.
                    _ignored++;
                    return null;
                }

                lane.State = AgentTaskState.Working;
                lane.TurnId = turn;
                lane.DetailCode = SafeDetail(record);
                lane.Category = record.Category == "unknown" ? "working" : record.Category;
                if (!string.IsNullOrWhiteSpace(record.CallId))
                {
                    lane.OpenCalls.Add(record.CallId!);
                }

                return null;

            case "tool/end":
                if (!string.IsNullOrWhiteSpace(record.CallId))
                {
                    lane.OpenCalls.Remove(record.CallId!);
                }

                // A failed tool is not a failed task.
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

                return quiet ? null : Edge(lane, record, AttentionEventKind.InputRequired, "input-pending");

            case "input/answered":
                if (!string.IsNullOrWhiteSpace(record.RequestId))
                {
                    lane.Inputs.Remove(record.RequestId!);
                }

                Settle(lane);
                return null;

            case "plan/mode":
                // Plan mode is a point marker, never sustained planning duration.
                lane.DetailCode = "plan-update";
                lane.Category = "planning";
                if (lane.State == AgentTaskState.Idle)
                {
                    lane.State = AgentTaskState.Working;
                }

                return null;

            case "snapshot":
                lane.CoverageIncomplete |= quiet;
                return null;

            default:
                // heartbeat: liveness only, no state change.
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
            lane.State = AgentTaskState.NeedsApproval;
            lane.AttentionCode = "approval-pending";
            return;
        }

        if (lane.Inputs.Count > 0)
        {
            lane.State = AgentTaskState.NeedsInput;
            lane.AttentionCode = "input-pending";
            return;
        }

        lane.NeedsHuman = false;
        lane.AttentionCode = null;
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
        record.ObservedAtUtc,
        code);

    private sealed class SessionLane
    {
        public string SessionId = string.Empty;
        public string Epoch = string.Empty;
        public string LastEventId = string.Empty;
        public long LastSequence = -1;
        public string TurnId = string.Empty;
        public AgentTaskState State = AgentTaskState.Idle;
        public string DetailCode = "no-observation";
        public string Category = "unknown";
        public bool NeedsHuman;
        public string? AttentionCode;
        public bool TurnEnded;
        public bool FirstAttach;
        public bool CoverageIncomplete;
        public string ProjectRoot = string.Empty;
        public DateTimeOffset LastObservedAt;
        public DateTimeOffset FirstObservedAt;
        public DateTimeOffset StateSince;
        public DateTimeOffset? TurnStartedAt;
        public DateTimeOffset? CompletedAt;
        public readonly HashSet<string> Approvals = new(StringComparer.Ordinal);
        public readonly HashSet<string> Inputs = new(StringComparer.Ordinal);
        public readonly HashSet<string> OpenCalls = new(StringComparer.Ordinal);
    }
}
