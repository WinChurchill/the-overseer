using System.Globalization;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Monitoring.Bridge;

namespace TheOverseer.App.Monitoring;

/// <summary>
/// One external metadata source (dsh observer or Claude hook bridge) as a monitor
/// adapter. Records are folded by <see cref="HarnessBridgeSession"/>, which owns
/// ordering, hydration, evidence freshness and coverage semantics; this adapter
/// reads the bounded spool, consumes the producer's authoritative current-state
/// document for resynchronization, and publishes the shared task contract.
/// </summary>
public sealed class BridgeMonitorAdapter : IAgentMonitorAdapter, IBudgetedMonitorAdapter
{
    private readonly string _dataDirectory;
    private readonly ProjectResolver? _projects;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _staleLease;
    private readonly TimeSpan _actionLease;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private HarnessBridgeSession? _session;
    private BridgeSpoolReader? _reader;
    private Task? _loop;
    private SourceSnapshot _current;
    private DateTimeOffset _attachedAt;
    private int _observedDrops;
    private SourceBudget _budget = SourceBudget.Default;
    private DateTimeOffset _lastResyncAt;
    private int _disposed;
    private int _started;

    /// <summary>The HUD's activity window: a gap older than this cannot be displayed.</summary>
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(60);

    public BridgeMonitorAdapter(
        SourceInstance source,
        string dataDirectory,
        ProjectResolver? projects = null,
        TimeSpan? pollInterval = null,
        TimeSpan? staleLease = null,
        TimeProvider? timeProvider = null,
        TimeSpan? actionLease = null)
    {
        Source = source;
        _dataDirectory = dataDirectory;
        _projects = projects;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _staleLease = staleLease ?? TimeSpan.FromMinutes(15);
        _actionLease = actionLease ?? HarnessBridgeSession.DefaultActionLease;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _current = SourceSnapshot.Empty(source, _timeProvider.GetUtcNow(), "not-started", "bridge");
    }

    public SourceInstance Source { get; }

    public string SpoolDirectory => HarnessSources.SpoolDirectory(_dataDirectory, Source);

    public SourceSnapshot Current => _current;

    /// <summary>Safe diagnostic summary of the folding session; counts and codes only.</summary>
    public string Describe() => _session?.Describe() ?? "bridge-not-started";

    public event Action<SourceSnapshot>? SnapshotAvailable;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;
        _attachedAt = _timeProvider.GetUtcNow();
        _session = new HarnessBridgeSession(Source, _staleLease, _budget.MaximumSessions, _actionLease);
        _reader = new BridgeSpoolReader(SpoolDirectory) { MaximumBytesPerPass = _budget.BytesPerPass };
        _loop ??= Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies this source's share of the aggregate budget. The composite calls this
    /// before <see cref="StartAsync"/> so it cannot race a running reader.
    /// </summary>
    public void ApplyBudget(SourceBudget budget)
    {
        if (_started != 0) throw new InvalidOperationException("Apply the source budget before starting the adapter.");
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget.BytesPerPass, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget.MaximumSessions, 1);
        _budget = budget;
    }

    public void RequestReconcile() => Poll();

    /// <summary>One bounded pass: read new records, fold them, resynchronize, publish.</summary>
    public SourceSnapshot Poll()
    {
        if (_disposed != 0) return _current;
        if (_session is null || _reader is null)
        {
            return Publish("not-started", "bridge");
        }

        if (!_gate.Wait(TimeSpan.Zero))
        {
            // A pass is already running: never queue unbounded work.
            return _current;
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            var lines = _reader.ReadNewLines(out var readReason, out var droppedByProducer);
            var rejected = 0;
            var transitions = new List<AttentionEvent>();
            foreach (var line in lines)
            {
                var parsed = HarnessBridgeParser.Parse(line);
                if (parsed.Outcome != BridgeParseOutcome.Accepted || parsed.Record is null)
                {
                    if (parsed.Outcome is BridgeParseOutcome.Malformed or BridgeParseOutcome.UnsupportedSchema)
                    {
                        rejected++;
                    }

                    continue;
                }

                var applied = _session.Apply(parsed.Record, now);
                // Retained backlog is hydration, not a new notification episode.
                if (applied.Attention is { } edge && !parsed.Record.Hydration &&
                    applied.Outcome == BridgeApplyOutcome.Applied &&
                    edge.ObservedAt > _attachedAt && edge.ObservedAt <= now)
                {
                    transitions.Add(new AttentionEvent(edge.Kind, edge.Key, NumberFor(edge.Key.ConversationId),
                        edge.Label, edge.State, edge.Kind switch
                        {
                            AttentionEventKind.PermissionRequired => AgentActionKind.WaitingForApproval,
                            AttentionEventKind.InputRequired => AgentActionKind.WaitingForInput,
                            AttentionEventKind.TaskError => AgentActionKind.Error,
                            _ => AgentActionKind.Completed,
                        }, edge.TurnId, edge.ObservedAt, edge.Code));
                }
            }

            // Authoritative resynchronization: the producer's atomic current-state
            // document restores current coverage for everything a late attach, a
            // dropped record or a rotated file could not deliver. History gaps stay
            // recorded on the lane; they are never rewritten as observed activity.
            var current = _reader.ReadCurrentState(Source);
            switch (current.Outcome)
            {
                case BridgeCurrentStateOutcome.Accepted when current.Document is not null:
                    if (_session.ApplyCurrentState(current.Document, now).Accepted)
                        _lastResyncAt = current.Document.UpdatedAtUtc > now ? now : current.Document.UpdatedAtUtc;
                    break;
                case BridgeCurrentStateOutcome.Malformed:
                case BridgeCurrentStateOutcome.UnsupportedSchema:
                    // An unreadable authoritative document is exactly the case the
                    // resynchronization exists for: current state stays unproven.
                    rejected++;
                    break;
                case BridgeCurrentStateOutcome.Ignored when current.ReasonCode != "snapshot-absent" || _lastResyncAt != default:
                    if (readReason == "ok") readReason = current.ReasonCode;
                    break;
            }

            // Producer-reported drops are coverage evidence recorded after the
            // resync, so a document that already folded the lost records clears it
            // on the next pass instead of latching forever. The producer's counters
            // are cumulative, so only a NEW total is a new observation: re-noting the
            // same total every poll would keep the source degraded forever. A lower
            // total means the producer restarted and its counters began again.
            if (droppedByProducer > _observedDrops)
            {
                _observedDrops = droppedByProducer;
                _session.NoteProducerIncomplete("producer-dropped:" + droppedByProducer.ToString(CultureInfo.InvariantCulture), now);
            }
            else if (droppedByProducer < _observedDrops)
            {
                _observedDrops = droppedByProducer;
            }

            if (rejected > 0)
            {
                _session.NoteProducerIncomplete("bridge-rejected:" + rejected.ToString(CultureInfo.InvariantCulture), now);
            }

            if (readReason is "bridge-rotated-unread" or "bridge-record-oversize" or "bridge-events-missing")
                _session.NoteProducerIncomplete(readReason, now);

            return Publish(readReason, "bridge", transitions);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SourceSnapshot Publish(
        string readReason,
        string adapterCode,
        IReadOnlyList<AttentionEvent>? transitions = null)
    {
        var now = _timeProvider.GetUtcNow();
        var lanes = _session?.Tasks(now) ?? Array.Empty<BridgeTaskState>();
        var tasks = new List<AgentTask>(lanes.Count);
        foreach (var lane in lanes)
        {
            tasks.Add(ToTask(lane));
        }

        var reasons = new List<string>();
        var complete = !lanes.Any(lane => lane.IsStale || lane.CoverageIncomplete) &&
            tasks.All(static task => task.HasCurrentState);
        if (!complete) reasons.Add("bridge-stale-or-incomplete");
        if (!string.Equals(readReason, "ok", StringComparison.Ordinal))
        {
            reasons.Add(readReason);
            complete = false;
        }

        if (_session?.CoverageIncomplete == true)
        {
            reasons.Add("bridge-coverage-incomplete");
            complete = false;
        }

        if (_session?.HistoryGap == true && now - _session.LastHistoryGapAt <= HistoryWindow)
        {
            // A history gap lowers trust in derived activity, not in current state,
            // and only while it can still affect the displayed activity window. An
            // older hole is kept on the lanes rather than degrading the source
            // forever.
            reasons.Add("bridge-history-gap");
        }

        if (tasks.Count == 0)
        {
            if (_session?.EmptySourceAuthoritative == true && now - _lastResyncAt > _staleLease)
            {
                reasons.Add("bridge-empty-snapshot-stale");
                complete = false;
            }
            // An authoritative document that lists no sessions is a complete
            // statement about an empty source, not a missing observation.
            if (_session?.EmptySourceAuthoritative == true && complete)
            {
                reasons.Remove("bridge-no-sessions");
            }
            else
            {
                reasons.Add("bridge-no-sessions");
                complete = false;
            }
        }

        var anyReadable = tasks.Count > 0 || _session?.EmptySourceAuthoritative == true;
        var health = !anyReadable
            ? MonitoringHealthLevel.Unavailable
            : reasons.Count == 0 ? MonitoringHealthLevel.Healthy : MonitoringHealthLevel.Degraded;

        // Evidence time is when the producer last observed something, not when this
        // adapter happened to publish: an idle spool cannot look freshly read.
        var evidenceAt = lanes.Count == 0
            ? (_session?.EmptySourceAuthoritative == true ? _lastResyncAt : DateTimeOffset.MinValue)
            : lanes.Max(static lane => lane.LastObservedAt);
        _current = new SourceSnapshot(
            Source,
            now,
            health,
            reasons,
            complete,
            tasks,
            transitions ?? Array.Empty<AttentionEvent>(),
            MonitorCapabilities.ForBridge(Source.HarnessId, continuousHeartbeat: Source.HarnessId != HarnessIds.Claude, producerVersion: _session?.ProducerVersion ?? string.Empty),
            adapterCode,
            evidenceAt == DateTimeOffset.MinValue ? now : evidenceAt);
        SnapshotAvailable?.Invoke(_current);
        return _current;
    }

    private AgentTask ToTask(BridgeTaskState lane)
    {
        var assignment = _projects?.Resolve(lane.ProjectRoot) ?? ProjectAssignment.None;
        var label = lane.SessionId.Length <= 8 ? lane.SessionId : lane.SessionId[..8];
        return new AgentTask(
            lane.Key,
            NumberFor(lane.SessionId),
            lane.SessionId,
            Source.SourceInstanceId,
            ClientSurface.Unknown,
            label,
            lane.TurnId,
            lane.State,
            new AgentAction(ActionFor(lane), EvidenceFor(lane), lane.DetailCode),
            EvidenceFor(lane),
            lane.FirstObservedAt,
            lane.LastActionAt != default ? lane.LastActionAt : lane.LastObservedAt,
            lane.StateSince,
            lane.TurnStartedAt,
            lane.CompletedAt,
            lane.IsStale,
            false,
            lane.NeedsHuman,
            lane.State == AgentTaskState.Idle && lane.DetailCode == "turn-aborted",
            lane.State == AgentTaskState.Error,
            lane.AttentionCode,
            1,
            lane.ProjectRoot,
            assignment.Project)
        {
            IsStateUncertain = lane.CoverageIncomplete || EvidenceFor(lane) == EvidenceKind.None,
        };
    }

    private static AgentActionKind ActionFor(BridgeTaskState lane) => lane.DetailCode switch
    {
        "waiting-for-approval" => AgentActionKind.WaitingForApproval,
        "waiting-for-input" => AgentActionKind.WaitingForInput,
        "turn-complete" => AgentActionKind.Completed,
        "turn-error" => AgentActionKind.Error,
        "tool-failed" => AgentActionKind.RunningCommand,
        "session-start" or "session-end" or "session-unobserved" or "session-idle" => AgentActionKind.Idle,
        "plan-active" => AgentActionKind.Thinking,
        "turn-active" => AgentActionKind.Thinking,
        _ => lane.Category switch
        {
            "thinking" => AgentActionKind.Thinking,
            "planning" => AgentActionKind.Thinking,
            "waiting" => AgentActionKind.WaitingForInput,
            "error" => AgentActionKind.Error,
            "complete" => AgentActionKind.Completed,
            "idle" => AgentActionKind.Idle,
            "working" => AgentActionKind.RunningCommand,
            _ => AgentActionKind.Thinking,
        },
    };

    /// <summary>
    /// Evidence strength for the displayed state. An active task whose last real
    /// action is older than the action lease falls back to unknown coverage: a host
    /// heartbeat proves the source is alive, never that the same tool is still
    /// running. Terminal and settled observations are historical facts and keep
    /// their own evidence.
    /// </summary>
    private static EvidenceKind EvidenceFor(BridgeTaskState lane)
    {
        if (lane.Category == "unknown")
        {
            return EvidenceKind.None;
        }

        var active = lane.State is AgentTaskState.Working or AgentTaskState.NeedsInput or AgentTaskState.NeedsApproval;
        if (active && !lane.ActionEvidenceFresh)
        {
            return EvidenceKind.None;
        }

        return EvidenceKind.Structured;
    }

    private int NumberFor(string sessionId)
    {
        // Stable per-session numbering inside one source: no reordering when a
        // session appears later.
        var hash = 0;
        foreach (var character in sessionId)
        {
            hash = unchecked((hash * 31) + character);
        }

        return (int)((uint)hash % 9000) + 1000;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                Poll();
                await Task.Delay(_pollInterval, _timeProvider, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
        // A timed-out filesystem read may still own the gate. SemaphoreSlim has
        // no native handle here; let it be collected after that poll releases it.
    }
}
