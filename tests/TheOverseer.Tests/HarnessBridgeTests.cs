using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.Bridge;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class HarnessBridgeTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly SourceInstance Dsh = new(HarnessIds.Dsh, "dsh-default", "DeepSeek Harness");

    private static string Line(
        long sequence,
        string kind,
        string sessionId = "session-1",
        string? turnId = "turn-1",
        string? outcome = null,
        bool hydration = false,
        string category = "working",
        string? projectRoot = null,
        string? requestId = null,
        int schema = 1,
        string epoch = "epoch-1",
        string producer = "test/1.0.0")
    {
        var parts = new List<string>
        {
            "\"schemaVersion\":" + schema,
            "\"harnessId\":\"dsh\"",
            "\"sourceInstanceId\":\"dsh-default\"",
            "\"sourceEpoch\":\"" + epoch + "\"",
            "\"sessionId\":\"" + sessionId + "\"",
            "\"sequence\":" + sequence,
            "\"eventId\":\"dsh:" + sessionId + ":" + sequence + "\"",
            "\"observedAtUtc\":\"" + Start.AddSeconds(sequence).ToString("o") + "\"",
            "\"kind\":\"" + kind + "\"",
            "\"category\":\"" + category + "\"",
            "\"evidence\":\"explicit\"",
            "\"hydration\":" + (hydration ? "true" : "false"),
            "\"producerVersion\":\"" + producer + "\"",
        };
        if (turnId is not null) parts.Add("\"turnId\":\"" + turnId + "\"");
        if (outcome is not null) parts.Add("\"outcome\":\"" + outcome + "\"");
        if (projectRoot is not null) parts.Add("\"projectRoot\":\"" + projectRoot.Replace("\\", "\\\\") + "\"");
        if (requestId is not null) parts.Add("\"requestId\":\"" + requestId + "\"");
        return "{" + string.Join(",", parts) + "}";
    }

    private static HarnessBridgeRecord Parse(string line)
    {
        var result = HarnessBridgeParser.Parse(line);
        Assert.Equal(BridgeParseOutcome.Accepted, result.Outcome);
        Assert.NotNull(result.Record);
        return result.Record!;
    }

    [Theory]
    [InlineData("input")]
    [InlineData("approval")]
    public void ClaudeCallIdentityPreservesRequestsUntilTheirMatchingDecision(string requestKind)
    {
        var source = new SourceInstance(HarnessIds.Claude, "claude-test", "Claude");
        var session = new HarnessBridgeSession(source);
        HarnessBridgeRecord Record(long sequence, string kind, string? callId = null) => Parse(Line(sequence, kind)) with
        {
            HarnessId = HarnessIds.Claude, SourceInstanceId = source.SourceInstanceId, CallId = callId,
        };
        session.Apply(Record(1, "session/start"));
        session.Apply(Record(2, "turn/start"));
        session.Apply(Record(3, requestKind + "/asked", "question-call"));
        var pending = Assert.Single(session.Tasks(Start.AddSeconds(3)));
        Assert.True(pending.NeedsHuman);
        Assert.Equal(requestKind == "input" ? AgentTaskState.NeedsInput : AgentTaskState.NeedsApproval, pending.State);
        session.Apply(Record(4, "tool/start", "unrelated"));
        session.Apply(Record(5, "heartbeat")); // Child-agent notifications are liveness only.
        session.Apply(Record(6, requestKind == "input" ? "input/answered" : "approval/decided", "unrelated"));
        Assert.True(Assert.Single(session.Tasks(Start.AddSeconds(6))).NeedsHuman);
        session.Apply(Record(7, requestKind == "input" ? "input/answered" : "approval/decided", "question-call"));
        Assert.False(Assert.Single(session.Tasks(Start.AddSeconds(7))).NeedsHuman);
        Assert.Equal(AgentTaskState.Working, Assert.Single(session.Tasks(Start.AddSeconds(7))).State);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("approval")]
    public void UncorrelatedClaudeRequestSurvivesUnrelatedWorkUntilTurnEnd(string requestKind)
    {
        var source = new SourceInstance(HarnessIds.Claude, "claude-test", "Claude");
        var session = new HarnessBridgeSession(source);
        HarnessBridgeRecord Record(long sequence, string kind) => Parse(Line(sequence, kind)) with
        {
            HarnessId = HarnessIds.Claude, SourceInstanceId = source.SourceInstanceId,
        };
        session.Apply(Record(1, "session/start"));
        session.Apply(Record(2, "turn/start"));
        session.Apply(Record(3, requestKind + "/asked"));
        Assert.True(Assert.Single(session.Tasks(Start.AddSeconds(3))).NeedsHuman);
        session.Apply(Record(4, "tool/start"));
        session.Apply(Record(5, "plan/mode"));
        session.Apply(Record(6, requestKind == "input" ? "input/answered" : "approval/decided"));
        Assert.True(Assert.Single(session.Tasks(Start.AddSeconds(6))).NeedsHuman);
        session.Apply(Record(7, "turn/end") with { Outcome = "completed" });
        Assert.False(Assert.Single(session.Tasks(Start.AddSeconds(7))).NeedsHuman);
    }

    [Fact]
    public void RequestCapacityLossCannotBecomeConfirmedZeroAfterKnownReplies()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "session/start")));
        session.Apply(Parse(Line(2, "turn/start")));
        for (var index = 0; index < 80; index++)
            session.Apply(Parse(Line(index + 3, "input/asked", requestId: "request-" + index)));
        for (var index = 0; index < 80; index++)
            session.Apply(Parse(Line(index + 83, "input/answered", requestId: "request-" + index)));
        var task = Assert.Single(session.Tasks(Start.AddSeconds(163)));
        Assert.True(task.NeedsHuman); // Overflow is retained as bounded uncertainty.
        Assert.True(task.CoverageIncomplete);
        session.Apply(Parse(Line(164, "session/end")));
        Assert.False(Assert.Single(session.Tasks(Start.AddSeconds(164))).NeedsHuman);
    }

    [Fact]
    public void UpgradedDshQuestionsPersistAcrossCompletionAndSettleByRequest()
    {
        var session = new HarnessBridgeSession(Dsh);
        HarnessBridgeRecord Record(long seq, string kind, string? id = null, string? outcome = null, string turn = "turn-1") =>
            Parse(Line(seq, kind, turnId: turn, requestId: id, outcome: outcome, producer: "dsh-observer/1.1.0"));
        session.Apply(Record(0, "session/start"));
        session.Apply(Record(1, "turn/start"));
        Assert.Equal(AttentionEventKind.InputRequired, session.Apply(Record(2, "input/asked", "q1")).Attention!.Kind);
        session.Apply(Record(3, "input/asked", "q2"));
        Assert.Null(session.Apply(Record(4, "turn/end", outcome: "completed")).Attention);
        Assert.Equal(AgentTaskState.NeedsInput, Assert.Single(session.Tasks(Start.AddSeconds(5))).State);
        session.Apply(Record(5, "input/answered", "q1"));
        Assert.Equal(AgentTaskState.NeedsInput, Assert.Single(session.Tasks(Start.AddSeconds(6))).State);
        session.Apply(Record(6, "input/answered", "unrelated"));
        Assert.Equal(AgentTaskState.NeedsInput, Assert.Single(session.Tasks(Start.AddSeconds(7))).State);
        session.Apply(Record(7, "input/answered", "q2"));
        Assert.Equal(AgentTaskState.Done, Assert.Single(session.Tasks(Start.AddSeconds(8))).State);
    }

    [Fact]
    public void ContinuingQuestionsSurviveNewTurnAndApprovalHasPriority()
    {
        var session = new HarnessBridgeSession(Dsh);
        void Apply(long seq, string kind, string? id = null, string turn = "turn-1") =>
            session.Apply(Parse(Line(seq, kind, turnId: turn, requestId: id, producer: "dsh-observer/1.1.0")));
        Apply(0, "session/start");
        Apply(1, "turn/start");
        Apply(2, "input/asked", "q1");
        Apply(3, "turn/start", turn: "turn-2");
        Assert.Equal(AgentTaskState.NeedsInput, Assert.Single(session.Tasks(Start.AddSeconds(4))).State);
        Apply(4, "approval/asked", "a1", "turn-2");
        Apply(5, "input/asked", "q2", "turn-2");
        Assert.Equal(AgentTaskState.NeedsApproval, Assert.Single(session.Tasks(Start.AddSeconds(6))).State);
        Apply(6, "approval/decided", "a1", "turn-2");
        Assert.Equal(AgentTaskState.NeedsInput, Assert.Single(session.Tasks(Start.AddSeconds(7))).State);
        Apply(7, "session/end");
        Assert.Equal(AgentTaskState.Idle, Assert.Single(session.Tasks(Start.AddSeconds(8))).State);
    }

    [Fact]
    public void UnknownTurnOutcomeRetainsQuestionsWithoutClaimingCompleteCoverage()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(0, "session/start", producer: "dsh-observer/1.1.0")));
        session.Apply(Parse(Line(1, "turn/start", producer: "dsh-observer/1.1.0")));
        session.Apply(Parse(Line(2, "input/asked", requestId: "q1", producer: "dsh-observer/1.1.0")));
        session.Apply(Parse(Line(3, "turn/end", outcome: "unrecognized", producer: "dsh-observer/1.1.0")));
        var task = Assert.Single(session.Tasks(Start.AddSeconds(4)));
        Assert.Equal(AgentTaskState.NeedsInput, task.State);
        Assert.True(task.CoverageIncomplete);
        Assert.Equal(BridgeTurnOutcome.Unknown, task.TurnOutcome);
    }

    [Fact]
    public void CurrentStateRestoresInputWithoutTurnAndKeepsUnknownCoverageVisible()
    {
        var session = new HarnessBridgeSession(Dsh);
        var baseline = CurrentDocument(5, openTurnId: null) with { ProducerVersion = "dsh-observer/1.1.0" };
        var pending = baseline with { Sessions = baseline.Sessions.Select(e => e with { PendingInputRequestIds = new[] { "q1" } }).ToArray() };
        var result = session.ApplyCurrentState(pending, Start.AddSeconds(5));
        Assert.True(result.Accepted);
        var task = Assert.Single(session.Tasks(Start.AddSeconds(6)));
        Assert.Equal(AgentTaskState.NeedsInput, task.State);
        Assert.True(task.NeedsHuman);
        Assert.False(task.CoverageIncomplete);
        var incomplete = pending with { UpdatedAtUtc = Start.AddSeconds(7), Sessions = pending.Sessions.Select(e => e with { InputStateComplete = false }).ToArray() };
        session.ApplyCurrentState(incomplete, Start.AddSeconds(7));
        Assert.True(Assert.Single(session.Tasks(Start.AddSeconds(8))).CoverageIncomplete);
        var clear = baseline with { UpdatedAtUtc = Start.AddSeconds(9) };
        session.ApplyCurrentState(clear, Start.AddSeconds(9));
        Assert.Equal(AgentTaskState.Idle, Assert.Single(session.Tasks(Start.AddSeconds(10))).State);
    }

    [Theory]
    [InlineData("[\"q1\",\"q2\"]", true)]
    [InlineData("[\"q1\",\"q1\"]", false)]
    [InlineData("[\" q1\"]", false)]
    [InlineData("[null]", false)]
    [InlineData("null", false)]
    public void CurrentStateInputIdsAreStrictlyBounded(string inputs, bool accepted)
    {
        var json = "{\"schemaVersion\":1,\"harnessId\":\"dsh\",\"sourceInstanceId\":\"dsh-default\",\"updatedAtUtc\":\"2026-09-24T12:00:00Z\",\"sessions\":[{\"sessionId\":\"s\",\"pendingInputRequestIds\":" + inputs + "}]}";
        var parsed = HarnessBridgeCurrentStateParser.Parse(json, Dsh);
        Assert.Equal(accepted ? BridgeCurrentStateOutcome.Accepted : BridgeCurrentStateOutcome.Malformed, parsed.Outcome);
        if (accepted) Assert.Equal(new[] { "q1", "q2" }, Assert.Single(parsed.Document!.Sessions).PendingInputRequestIds);
    }

    [Fact]
    public void InputArrayOverflowAndOversizedIdsRejectTheDocument()
    {
        var shell = "{\"schemaVersion\":1,\"harnessId\":\"dsh\",\"sourceInstanceId\":\"dsh-default\",\"updatedAtUtc\":\"2026-09-24T12:00:00Z\",\"sessions\":[{\"sessionId\":\"s\",\"pendingInputRequestIds\":INPUTS}]}";
        foreach (var ids in new[] { Enumerable.Range(0, 33).Select(i => "q" + i).ToArray(), new[] { new string('x', 129) } })
        {
            var json = shell.Replace("INPUTS", System.Text.Json.JsonSerializer.Serialize(ids), StringComparison.Ordinal);
            Assert.Equal(BridgeCurrentStateOutcome.Malformed, HarnessBridgeCurrentStateParser.Parse(json, Dsh).Outcome);
        }
    }

    [Fact]
    public void DshQuestionCapabilityRequiresTheQualifiedProducer()
    {
        foreach (var version in new[] { "", "dsh-observer/1.0.0", "dsh-observer/9.0.0" })
            Assert.Equal(CapabilitySupport.Unsupported, MonitorCapabilities.ForBridge(HarnessIds.Dsh, true, version).InputRequests.Support);
        Assert.Equal(CapabilitySupport.Supported, MonitorCapabilities.ForBridge(HarnessIds.Dsh, true, "dsh-observer/1.1.0").InputRequests.Support);
        Assert.Equal(CapabilitySupport.Supported, MonitorCapabilities.ForBridge(HarnessIds.Claude, false).InputRequests.Support);
    }

    [Fact]
    public void AcceptedRecordCarriesHarnessQualifiedIdentity()
    {
        var record = Parse(Line(1, "turn/start", projectRoot: @"D:\work\app"));

        Assert.Equal(HarnessIds.Dsh, record.Key.HarnessId);
        Assert.Equal("dsh-default", record.Key.ProfileId);
        Assert.Equal("session-1", record.Key.ConversationId);
        Assert.Equal(@"D:\work\app", record.ProjectRoot);
        Assert.Equal("turn-1", record.TurnId);
    }

    [Fact]
    public void OtherHarnessCannotWriteIntoASourceWithTheSameInstanceId()
    {
        var session = new HarnessBridgeSession(Dsh, TimeSpan.FromSeconds(10));
        var record = Parse(Line(1, "turn/start")) with { HarnessId = HarnessIds.Claude };
        Assert.Equal(BridgeApplyOutcome.Rejected, session.Apply(record).Outcome);
        Assert.Empty(session.Tasks(Start));
    }

    [Fact]
    public void ActionChangesAndLaterRecordsDoNotResetTurnOrCompletionClocks()
    {
        var session = new HarnessBridgeSession(Dsh, TimeSpan.FromMinutes(15));
        session.Apply(Parse(Line(1, "turn/start")));
        session.Apply(Parse(Line(2, "tool/start")));
        var working = Assert.Single(session.Tasks(Start.AddSeconds(2)));
        Assert.Equal(Start.AddSeconds(1), working.FirstObservedAt);
        Assert.Equal(Start.AddSeconds(1), working.TurnStartedAt);
        Assert.Equal(Start.AddSeconds(1), working.StateSince);
        session.Apply(Parse(Line(3, "turn/end", outcome: "completed")));
        session.Apply(Parse(Line(4, "heartbeat")));
        Assert.Equal(Start.AddSeconds(3), Assert.Single(session.Tasks(Start.AddSeconds(4))).CompletedAt);
        session.Apply(Parse(Line(5, "turn/start", turnId: "turn-2")));
        var next = Assert.Single(session.Tasks(Start.AddSeconds(5)));
        Assert.Equal(Start.AddSeconds(5), next.TurnStartedAt);
        Assert.Null(next.CompletedAt);
    }

    [Theory]
    [InlineData("", BridgeParseOutcome.Ignored, "empty")]
    [InlineData("not json", BridgeParseOutcome.Malformed, "malformed-json")]
    [InlineData("[1,2]", BridgeParseOutcome.Malformed, "not-an-object")]
    [InlineData("{\"harnessId\":\"dsh\"}", BridgeParseOutcome.Malformed, "missing-schema")]
    [InlineData("{\"schemaVersion\":9,\"harnessId\":\"dsh\",\"sourceInstanceId\":\"x\",\"sessionId\":\"s\",\"kind\":\"heartbeat\",\"observedAtUtc\":\"2026-09-24T12:00:00Z\"}", BridgeParseOutcome.UnsupportedSchema, "unsupported-schema")]
    [InlineData("{\"schemaVersion\":1,\"harnessId\":\"dsh\",\"sourceInstanceId\":\"x\",\"sessionId\":\"s\",\"kind\":\"unknown/thing\",\"observedAtUtc\":\"2026-09-24T12:00:00Z\"}", BridgeParseOutcome.UnknownKind, "unknown-kind")]
    [InlineData("{\"schemaVersion\":1,\"harnessId\":\"dsh\",\"sourceInstanceId\":\"x\",\"sessionId\":\"s\",\"kind\":\"heartbeat\"}", BridgeParseOutcome.Malformed, "missing-observed-at")]
    public void RejectedRecordsCarryAPreciseReason(string line, BridgeParseOutcome outcome, string reason)
    {
        var result = HarnessBridgeParser.Parse(line);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Null(result.Record);
    }

    [Fact]
    public void OversizedAndContentBearingRecordsLeakNothing()
    {
        var oversized = "{\"schemaVersion\":1,\"pad\":\"" + new string('x', HarnessBridgeParser.MaximumRecordChars) + "\"}";
        Assert.Equal(BridgeParseOutcome.Malformed, HarnessBridgeParser.Parse(oversized).Outcome);
        Assert.Equal("record-too-large", HarnessBridgeParser.Parse(oversized).ReasonCode);

        // Privacy sentinels: a hostile payload cannot smuggle content into the record.
        const string sentinel = "SENTINEL-PROMPT-TEXT";
        var hostile = Line(1, "tool/start")[..^1] +
            ",\"prompt\":\"" + sentinel + "\",\"toolOutput\":\"" + sentinel + "\",\"transcript\":\"" + sentinel + "\"}";
        var record = Parse(hostile);

        // ToString covers every field of the record: no content key can survive.
        Assert.DoesNotContain(sentinel, record.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HydratedAttachIsSilentAndMarksPreAttachCoverageUnknown()
    {
        var session = new HarnessBridgeSession(Dsh);

        var applied = session.Apply(Parse(Line(1, "turn/start", hydration: true, category: "thinking")));

        Assert.Equal(BridgeApplyOutcome.Applied, applied.Outcome);
        Assert.Null(applied.Attention);
        var task = Assert.Single(session.Tasks(Start.AddSeconds(1)));
        Assert.Equal(AgentTaskState.Working, task.State);
        Assert.True(task.CoverageIncomplete);
    }

    [Fact]
    public void SequenceGapsAndDuplicatesAreVisibleInsteadOfSilent()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "turn/start")));
        // Attaching mid-session means earlier activity was never observed, so the
        // session starts honestly incomplete; only a gap-free snapshot clears it.
        Assert.True(session.CoverageIncomplete);

        var duplicate = session.Apply(Parse(Line(1, "turn/start")));
        Assert.Equal(BridgeApplyOutcome.Duplicate, duplicate.Outcome);

        var outOfOrder = session.Apply(Parse(Line(0, "turn/start")));
        Assert.Equal(BridgeApplyOutcome.OutOfOrder, outOfOrder.Outcome);

        session.Apply(Parse(Line(5, "tool/start")));
        Assert.True(session.CoverageIncomplete);
        Assert.Contains("gaps=1", session.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AbortedTurnIsSilentWhileAnErrorTurnRaisesAttention()
    {
        var aborted = new HarnessBridgeSession(Dsh);
        aborted.Apply(Parse(Line(1, "turn/start")));
        var abortedEnd = aborted.Apply(Parse(Line(2, "turn/end", outcome: "aborted")));

        Assert.Null(abortedEnd.Attention);
        Assert.Equal(AgentTaskState.Idle, Assert.Single(aborted.Tasks(Start.AddSeconds(3))).State);

        var failed = new HarnessBridgeSession(Dsh);
        failed.Apply(Parse(Line(1, "turn/start")));
        var failedEnd = failed.Apply(Parse(Line(2, "turn/end", outcome: "error")));

        Assert.NotNull(failedEnd.Attention);
        Assert.Equal(AttentionEventKind.TaskError, failedEnd.Attention!.Kind);
        Assert.Equal(AgentTaskState.Error, Assert.Single(failed.Tasks(Start.AddSeconds(3))).State);
    }

    [Fact]
    public void ALateToolStartCannotReopenACompletedTurn()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "turn/start")));
        var completed = session.Apply(Parse(Line(2, "turn/end", outcome: "completed")));
        Assert.NotNull(completed.Attention);

        var late = session.Apply(Parse(Line(3, "tool/start")));

        Assert.Equal(BridgeApplyOutcome.Applied, late.Outcome);
        var task = Assert.Single(session.Tasks(Start.AddSeconds(4)));
        Assert.Equal(AgentTaskState.Done, task.State);
        Assert.Equal("turn-complete", task.DetailCode);
    }

    [Fact]
    public void ApprovalIsCorrelatedByRequestIdAndResolutionSettlesTheTask()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "turn/start")));
        var asked = session.Apply(Parse(Line(2, "approval/asked", requestId: "req-1", category: "waiting")));

        Assert.Equal(AttentionEventKind.PermissionRequired, asked.Attention!.Kind);
        var waiting = Assert.Single(session.Tasks(Start.AddSeconds(3)));
        Assert.Equal(AgentTaskState.NeedsApproval, waiting.State);
        Assert.True(waiting.NeedsHuman);

        session.Apply(Parse(Line(3, "approval/decided", requestId: "req-1")));
        var settled = Assert.Single(session.Tasks(Start.AddSeconds(4)));
        Assert.False(settled.NeedsHuman);
        Assert.Equal(AgentTaskState.Working, settled.State);
    }

    [Fact]
    public void PlanModeIsAPointMarkerAndProducerDropsLowerCoverage()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "turn/start")));
        session.Apply(Parse(Line(2, "plan/mode", category: "planning")));

        var planned = Assert.Single(session.Tasks(Start.AddSeconds(3)));
        Assert.Equal("plan-update", planned.DetailCode);
        Assert.Equal("planning", planned.Category);
        session.NoteProducerIncomplete("producer-dropped:3");
        Assert.True(session.CoverageIncomplete);
        Assert.Contains("producer-dropped:3", session.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExpiredLeaseAgesTheTaskInsteadOfLeavingItWorking()
    {
        var session = new HarnessBridgeSession(Dsh, staleLease: TimeSpan.FromMinutes(1));
        session.Apply(Parse(Line(1, "turn/start")));

        Assert.False(Assert.Single(session.Tasks(Start.AddSeconds(10))).IsStale);
        var aged = Assert.Single(session.Tasks(Start.AddMinutes(30)));
        Assert.True(aged.IsStale);
        Assert.Equal(AgentTaskState.Working, aged.State);
    }

    [Fact]
    public void ANewProducerEpochResetsOrderingAndMarksTheGap()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(10, "turn/start", epoch: "epoch-1")));

        session.Apply(Parse(Line(1, "heartbeat", epoch: "epoch-2")));

        Assert.True(session.CoverageIncomplete);
        Assert.Equal(1, Assert.Single(session.Tasks(Start.AddSeconds(2))).LastSequence);
    }

    private static HarnessBridgeCurrentState CurrentDocument(
        long lastSequence,
        string sessionId = "session-1",
        DateTimeOffset? updatedAt = null,
        string epoch = "epoch-1",
        string? openTurnId = "turn-1",
        string? projectRoot = null,
        bool disposed = false,
        string category = "working",
        string? pendingApproval = null,
        long evicted = 0,
        IReadOnlyList<string>? sessions = null)
    {
        var updated = (updatedAt ?? Start.AddSeconds(lastSequence)).ToString("o");
        var entries = new List<string>();
        foreach (var id in sessions ?? new[] { sessionId })
        {
            entries.Add("{" +
                "\"sessionId\":\"" + id + "\"," +
                "\"parentSessionId\":null," +
                "\"projectRoot\":" + (projectRoot is null ? "null" : "\"" + projectRoot.Replace("\\", "\\\\") + "\"") + "," +
                "\"createdAtUtc\":\"" + Start.ToString("o") + "\"," +
                "\"firstSeenUtc\":\"" + Start.ToString("o") + "\"," +
                "\"lastObservedUtc\":\"" + updated + "\"," +
                "\"openTurnId\":" + (openTurnId is null ? "null" : "\"" + openTurnId + "\"") + "," +
                "\"planMode\":false," +
                "\"pendingApprovalRequestId\":" + (pendingApproval is null ? "null" : "\"" + pendingApproval + "\"") + "," +
                "\"category\":\"" + category + "\"," +
                "\"disposed\":" + (disposed ? "true" : "false") + "," +
                "\"lastSequence\":" + lastSequence + "," +
                "\"lastHostSeq\":" + lastSequence + "," +
                "\"lastKind\":\"turn/start\"," +
                "\"lastEventId\":\"dsh:" + id + ":" + lastSequence + "\"," +
                "\"records\":" + lastSequence + "}");
        }

        var json = "{\"schemaVersion\":1,\"harnessId\":\"dsh\",\"sourceInstanceId\":\"dsh-default\"," +
                   "\"sourceEpoch\":\"" + epoch + "\",\"producerVersion\":\"dsh-observer/1.0.0\"," +
                   "\"updatedAtUtc\":\"" + updated + "\",\"evictedSessions\":" + evicted + "," +
                   "\"sessions\":[" + string.Join(",", entries) + "]}";
        var parsed = HarnessBridgeCurrentStateParser.Parse(json, Dsh);
        Assert.Equal(BridgeCurrentStateOutcome.Accepted, parsed.Outcome);
        Assert.NotNull(parsed.Document);
        return parsed.Document!;
    }

    [Fact]
    public void HeartbeatProvesLivenessWithoutRenewingTheObservedAction()
    {
        var session = new HarnessBridgeSession(Dsh, staleLease: TimeSpan.FromMinutes(15), actionLease: TimeSpan.FromSeconds(10));
        session.Apply(Parse(Line(1, "turn/start", category: "working")));
        session.Apply(Parse(Line(2, "heartbeat", category: "working")));

        var fresh = Assert.Single(session.Tasks(Start.AddSeconds(5)));
        Assert.True(fresh.ActionEvidenceFresh);
        Assert.Equal(Start.AddSeconds(1), fresh.LastActionAt);
        Assert.Equal(Start.AddSeconds(2), fresh.LastHeartbeatAt);

        var aged = Assert.Single(session.Tasks(Start.AddSeconds(30)));
        Assert.False(aged.ActionEvidenceFresh);
        // The host is still alive; only the action evidence expired.
        Assert.False(aged.IsStale);
        Assert.Equal(Start.AddSeconds(1), aged.LastActionAt);
        Assert.Equal(AgentTaskState.Working, aged.State);
    }

    [Fact]
    public void HeartbeatCategoryCannotOverwriteObservedActionEvidence()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "tool/start", category: "working")));
        session.Apply(Parse(Line(2, "heartbeat", category: "idle")));

        var task = Assert.Single(session.Tasks(Start.AddSeconds(3)));
        Assert.Equal("working", task.Category);
        Assert.Equal("working-activity", task.DetailCode);
    }

    [Theory]
    [InlineData("completed", AgentTaskState.Done, BridgeTurnOutcome.Completed, "turn-complete")]
    [InlineData("error", AgentTaskState.Error, BridgeTurnOutcome.Error, "turn-error")]
    [InlineData("aborted", AgentTaskState.Idle, BridgeTurnOutcome.Aborted, "turn-aborted")]
    [InlineData(null, AgentTaskState.Idle, BridgeTurnOutcome.Unknown, "turn-outcome-unknown")]
    [InlineData("something-new", AgentTaskState.Idle, BridgeTurnOutcome.Unknown, "turn-outcome-unknown")]
    public void TurnEndOutcomesAreExplicitAndUnknownIsNeverACompletion(
        string? outcome,
        AgentTaskState expectedState,
        BridgeTurnOutcome expectedOutcome,
        string expectedDetail)
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "turn/start")));
        var ended = session.Apply(Parse(Line(2, "turn/end", outcome: outcome)));

        var task = Assert.Single(session.Tasks(Start.AddSeconds(3)));
        Assert.Equal(expectedState, task.State);
        Assert.Equal(expectedOutcome, task.TurnOutcome);
        Assert.Equal(expectedDetail, task.DetailCode);
        switch (expectedOutcome)
        {
            case BridgeTurnOutcome.Completed:
                Assert.Equal(AttentionEventKind.TaskCompleted, ended.Attention!.Kind);
                break;
            case BridgeTurnOutcome.Error:
                Assert.Equal(AttentionEventKind.TaskError, ended.Attention!.Kind);
                break;
            default:
                // Cancellation and an unobservable outcome raise nothing.
                Assert.Null(ended.Attention);
                break;
        }

        if (expectedOutcome == BridgeTurnOutcome.Unknown)
        {
            // The turn ended but its outcome is unobservable: that is reduced
            // coverage, never a silent success.
            Assert.True(task.CoverageIncomplete);
            Assert.Equal("unknown", task.Category);
        }
    }

    [Fact]
    public void LateTurnEndForAnOlderTurnDoesNotCloseTheCurrentTurn()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "turn/start", turnId: "turn-1")));
        session.Apply(Parse(Line(2, "turn/end", turnId: "turn-1", outcome: "completed")));
        session.Apply(Parse(Line(3, "turn/start", turnId: "turn-2")));

        var late = session.Apply(Parse(Line(4, "turn/end", turnId: "turn-1", outcome: "error")));

        Assert.Equal(BridgeApplyOutcome.Applied, late.Outcome);
        Assert.Null(late.Attention);
        var task = Assert.Single(session.Tasks(Start.AddSeconds(5)));
        Assert.Equal(AgentTaskState.Working, task.State);
        Assert.Equal("turn-2", task.TurnId);
    }

    [Fact]
    public void AuthoritativeSnapshotSupersedesOlderRecordsAndRestoresCoverage()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "turn/start")));
        Assert.True(session.CoverageIncomplete);

        var resync = session.ApplyCurrentState(CurrentDocument(5), Start.AddSeconds(10));

        Assert.True(resync.Accepted);
        Assert.Equal(1, resync.Recovered);
        Assert.False(session.CoverageIncomplete);
        var recovered = Assert.Single(session.Tasks(Start.AddSeconds(10)));
        Assert.Equal(AgentTaskState.Working, recovered.State);
        Assert.Equal(5, recovered.LastSequence);

        // A record the snapshot already folded is not a duplicate delivery and not
        // an ordering violation; it must not inflate the counters or reopen anything.
        Assert.Equal(BridgeApplyOutcome.Superseded, session.Apply(Parse(Line(3, "tool/start"))).Outcome);
        Assert.Equal(BridgeApplyOutcome.Applied, session.Apply(Parse(Line(6, "tool/start"))).Outcome);
    }

    [Fact]
    public void StaleSnapshotCannotUnproveNewerCoverageAndRetiresAbsentSessions()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "turn/start")));
        Assert.True(session.ApplyCurrentState(CurrentDocument(5, updatedAt: Start.AddSeconds(20)), Start.AddSeconds(20)).Accepted);

        Assert.Equal("snapshot-stale", session.ApplyCurrentState(CurrentDocument(5, updatedAt: Start.AddSeconds(10)), Start.AddSeconds(20)).ReasonCode);

        // A session the producer no longer lists, in the same epoch, after our last
        // observation of it, is not live any more: it is retired instead of being
        // left as a permanent "working" ghost.
        var retired = session.ApplyCurrentState(
            CurrentDocument(9, sessionId: "other-session", updatedAt: Start.AddSeconds(30), openTurnId: null),
            Start.AddSeconds(30));

        Assert.Equal(1, retired.Retired);
        var ghost = Assert.Single(session.Tasks(Start.AddSeconds(30)), task => task.SessionId == "session-1");
        Assert.Equal(AgentTaskState.Idle, ghost.State);
        Assert.Equal("session-unobserved", ghost.DetailCode);
    }

    [Fact]
    public void ConcurrentToolCallsAndRequestsStayCorrelatedByTheirOwnIds()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "turn/start")));
        session.Apply(Parse(Line(2, "tool/start", turnId: "turn-1")) with { CallId = "call-a" });
        session.Apply(Parse(Line(3, "approval/asked", requestId: "req-1", category: "waiting")));
        session.Apply(Parse(Line(4, "tool/start")) with { CallId = "call-b" });

        // One correlated tool finishing does not settle the other, and an
        // unrelated request does not clear the pending approval.
        session.Apply(Parse(Line(5, "tool/end")) with { CallId = "call-a", Outcome = "ok" });
        var waiting = Assert.Single(session.Tasks(Start.AddSeconds(6)));
        Assert.Equal(AgentTaskState.NeedsApproval, waiting.State);
        Assert.True(waiting.NeedsHuman);

        // The correlated decision settles exactly the request it names.
        session.Apply(Parse(Line(6, "approval/decided", requestId: "req-1")));
        var settled = Assert.Single(session.Tasks(Start.AddSeconds(7)));
        Assert.False(settled.NeedsHuman);
        Assert.Equal(AgentTaskState.Working, settled.State);
    }

    [Fact]
    public void ClockSkewCannotCreateFutureEvidenceOrMoveItBackwards()
    {
        var session = new HarnessBridgeSession(Dsh, actionLease: TimeSpan.FromSeconds(10));
        var receipt = Start.AddSeconds(2);
        // The producer's clock is ten minutes ahead: the record is bounded by the
        // moment the collector actually read it.
        session.Apply(Parse(Line(1, "turn/start")) with { ObservedAtUtc = Start.AddMinutes(10) }, receipt);
        var skewed = Assert.Single(session.Tasks(receipt));
        Assert.Equal(receipt, skewed.LastActionAt);
        Assert.True(skewed.ActionEvidenceFresh);

        // A later-sequenced record whose timestamp went backwards does not move the
        // evidence watermark backwards either.
        session.Apply(Parse(Line(2, "tool/start")) with { ObservedAtUtc = Start.AddSeconds(1) }, Start.AddSeconds(5));
        var after = Assert.Single(session.Tasks(Start.AddSeconds(5)));
        Assert.Equal(receipt, after.LastActionAt);
    }

    [Fact]
    public void ASessionThatBecomesActiveAgainIsNoLongerRetired()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "turn/start")));
        session.ApplyCurrentState(CurrentDocument(5, updatedAt: Start.AddSeconds(10)), Start.AddSeconds(10));

        // The producer stops listing it: it is gone, not working.
        session.ApplyCurrentState(
            CurrentDocument(9, sessionId: "other-session", updatedAt: Start.AddSeconds(20), openTurnId: null),
            Start.AddSeconds(20));
        Assert.Equal("session-unobserved", Assert.Single(session.Tasks(Start.AddSeconds(20)), task => task.SessionId == "session-1").DetailCode);

        // A new observation for the same session makes it live again, and a later
        // snapshot that omits it can retire it once more.
        session.Apply(Parse(Line(6, "turn/start", turnId: "turn-2")));
        var revived = Assert.Single(session.Tasks(Start.AddSeconds(21)), task => task.SessionId == "session-1");
        Assert.Equal(AgentTaskState.Working, revived.State);

        session.ApplyCurrentState(
            CurrentDocument(9, sessionId: "other-session", updatedAt: Start.AddSeconds(30), openTurnId: null),
            Start.AddSeconds(30));
        Assert.Equal("session-unobserved", Assert.Single(session.Tasks(Start.AddSeconds(30)), task => task.SessionId == "session-1").DetailCode);
    }

    [Fact]
    public void UnsupportedOrForeignSnapshotCannotRestoreCoverage()
    {
        Assert.Equal(
            BridgeCurrentStateOutcome.UnsupportedSchema,
            HarnessBridgeCurrentStateParser.Parse("{\"schemaVersion\":9,\"harnessId\":\"dsh\",\"sourceInstanceId\":\"dsh-default\",\"updatedAtUtc\":\"2026-09-24T12:00:00Z\"}", Dsh).Outcome);
        Assert.Equal(
            BridgeCurrentStateOutcome.WrongSource,
            HarnessBridgeCurrentStateParser.Parse("{\"schemaVersion\":1,\"harnessId\":\"dsh\",\"sourceInstanceId\":\"other\",\"updatedAtUtc\":\"2026-09-24T12:00:00Z\"}", Dsh).Outcome);
        Assert.Equal(
            BridgeCurrentStateOutcome.Malformed,
            HarnessBridgeCurrentStateParser.Parse("{\"schemaVersion\":1,\"harnessId\":\"dsh\",\"sourceInstanceId\":\"dsh-default\"}", Dsh).Outcome);
    }

    [Fact]
    public void ProducerDropsLowerCoverageUntilASnapshotPublishedAfterThemArrives()
    {
        var session = new HarnessBridgeSession(Dsh);
        session.Apply(Parse(Line(1, "turn/start", hydration: true)));
        session.NoteProducerIncomplete("producer-dropped:6", Start.AddSeconds(10));

        Assert.True(session.CoverageIncomplete);

        // An authoritative document restores current coverage, and the caller always
        // applies the document BEFORE noting a drop, so at least one full pass reports
        // the loss honestly before it can be cleared.
        session.ApplyCurrentState(CurrentDocument(5, updatedAt: Start.AddSeconds(5)), Start.AddSeconds(11));
        Assert.False(session.CoverageIncomplete);
        // The lost records stay lost: current state recovered, history did not.
        Assert.True(session.HistoryGap);

        // A lane the document does not mention, and cannot retire because it was
        // observed after the document was written, stays unproven while a drop is
        // outstanding: the lost records could have been its own.
        var other = new HarnessBridgeSession(Dsh);
        other.Apply(Parse(Line(1, "turn/start")));
        other.Apply(Parse(Line(1, "tool/start", sessionId: "session-2")));
        other.ApplyCurrentState(CurrentDocument(5, updatedAt: Start.AddSeconds(5)), Start.AddSeconds(5));
        Assert.False(other.CoverageIncomplete);
        other.Apply(Parse(Line(6, "tool/start", sessionId: "session-2")));
        other.NoteProducerIncomplete("producer-dropped:2", Start.AddSeconds(7));
        other.ApplyCurrentState(CurrentDocument(9, updatedAt: Start.AddSeconds(6)), Start.AddSeconds(8));
        Assert.True(other.CoverageIncomplete);
        var uncovered = Assert.Single(other.Tasks(Start.AddSeconds(8)), task => task.SessionId == "session-2");
        Assert.True(uncovered.CoverageIncomplete);
    }
}

