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
        string epoch = "epoch-1")
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
            "\"producerVersion\":\"test/1.0.0\"",
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
}
