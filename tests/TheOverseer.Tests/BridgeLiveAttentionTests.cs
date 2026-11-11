using System.Text.Json;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class BridgeLiveAttentionTests
{
    [Theory]
    [InlineData("claude", "input")]
    [InlineData("claude", "approval")]
    [InlineData("dsh", "input")]
    [InlineData("dsh", "approval")]
    public async Task AnObservedRequestOutlivesTheActionLeaseButStillExpiresWithItsSource(string harness, string kind)
    {
        await using var flow = await LiveFlow.Create(harness);
        flow.Append(kind + "/asked", requestId: "request");
        flow.Clock.AdvanceSeconds(20);
        var waiting = Assert.Single(flow.Adapter.Poll().Tasks);
        Assert.True(waiting.HasCurrentState);
        Assert.True(waiting.NeedsHuman);
        Assert.Equal(EvidenceKind.Structured, waiting.Evidence);

        flow.Clock.AdvanceSeconds(901);
        var stale = Assert.Single(flow.Adapter.Poll().Tasks);
        Assert.True(stale.IsStale);
        Assert.False(stale.HasCurrentState);
    }

    [Theory]
    [InlineData("claude", "input")]
    [InlineData("claude", "approval")]
    [InlineData("dsh", "input")]
    [InlineData("dsh", "approval")]
    public async Task MissingRecordsWithdrawConfirmationOfAnOlderRequest(string harness, string kind)
    {
        await using var flow = await LiveFlow.Create(harness);
        flow.Append(kind + "/asked", requestId: "request");
        flow.SkipRecord();
        flow.Append("heartbeat");
        flow.Clock.AdvanceSeconds(20);
        var uncertain = Assert.Single(flow.Adapter.Poll().Tasks);
        Assert.True(uncertain.NeedsHuman);
        Assert.False(uncertain.HasCurrentState);
        Assert.True(uncertain.IsStateUncertain);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("dsh")]
    public async Task WorkStillLosesActionEvidenceAfterTheExistingTenSecondLease(string harness)
    {
        await using var flow = await LiveFlow.Create(harness);
        Assert.True(Assert.Single(flow.Adapter.Poll().Tasks).HasCurrentState);
        flow.Clock.AdvanceSeconds(11);
        var aged = Assert.Single(flow.Adapter.Poll().Tasks);
        Assert.False(aged.HasCurrentState);
        Assert.Equal(EvidenceKind.None, aged.Evidence);
    }

    [Fact]
    public async Task ClaudeQuestionAndItsIdentitylessPermissionNotificationStayOneInputEpisode()
    {
        await using var flow = await LiveFlow.Create("claude");
        var input = flow.Append("input/asked", requestId: "question-call");
        Assert.Equal(AttentionEventKind.InputRequired, Assert.Single(input.Transitions).Kind);
        flow.Append("heartbeat", evidence: "inferred");
        var notification = flow.Append("approval/asked", evidence: "inferred");
        var pending = Assert.Single(notification.Tasks);
        Assert.Equal(AgentTaskState.NeedsInput, pending.State);
        Assert.Equal("input-pending", pending.AttentionCode);
        Assert.Empty(notification.Transitions);
        Assert.Equal(AgentTaskState.NeedsInput, Assert.Single(flow.Append("tool/start", callId: "background").Tasks).State);
        flow.Append("tool/end", callId: "background");
        flow.Clock.AdvanceSeconds(20);
        Assert.True(Assert.Single(flow.Adapter.Poll().Tasks).HasCurrentState);

        var answered = Assert.Single(flow.Append("input/answered", requestId: "question-call").Tasks);
        Assert.Equal(AgentTaskState.Working, answered.State);
        Assert.False(answered.NeedsHuman);
        Assert.Null(answered.AttentionCode);
    }

    [Theory]
    [InlineData("claude", true, "inferred")]
    [InlineData("claude", false, "explicit")]
    [InlineData("dsh", false, "inferred")]
    public async Task ASeparateOrAmbiguousApprovalKeepsItsOwnAttention(string harness, bool otherCall, string evidence)
    {
        await using var flow = await LiveFlow.Create(harness);
        flow.Append("input/asked", requestId: "question-call");
        if (otherCall) flow.Append("tool/start", callId: "another-call");
        var approval = flow.Append("approval/asked", evidence: evidence);
        Assert.Equal(AgentTaskState.NeedsApproval, Assert.Single(approval.Tasks).State);
        Assert.Equal(AttentionEventKind.PermissionRequired, Assert.Single(approval.Transitions).Kind);
    }

    [Fact]
    public async Task ClaudePlanModePermissionStaysSeparateFromAPendingQuestion()
    {
        await using var flow = await LiveFlow.Create("claude");
        flow.Append("input/asked", requestId: "question-call");
        // The producer maps PreToolUse ExitPlanMode to plan/mode. Its delayed
        // permission_prompt has no call identity and maps to inferred approval.
        flow.Append("plan/mode", callId: "exit-plan-call");
        var approval = flow.Append("approval/asked", evidence: "inferred");
        Assert.Equal(AgentTaskState.NeedsApproval, Assert.Single(approval.Tasks).State);
        Assert.Equal(AttentionEventKind.PermissionRequired, Assert.Single(approval.Transitions).Kind);

        var afterQuestion = Assert.Single(flow.Append("input/answered", requestId: "question-call").Tasks);
        Assert.True(afterQuestion.NeedsHuman);
        Assert.Equal(AgentTaskState.NeedsApproval, afterQuestion.State);
    }

    [Fact]
    public async Task ADelayedNotificationCannotEraseAnEarlierExplicitApproval()
    {
        await using var flow = await LiveFlow.Create("claude");
        flow.Append("input/asked", requestId: "question-call");
        flow.Append("approval/asked", evidence: "explicit");
        var notification = flow.Append("approval/asked", evidence: "inferred");
        var pending = Assert.Single(notification.Tasks);
        Assert.True(pending.NeedsHuman);
        Assert.Equal(AgentTaskState.NeedsApproval, pending.State);
        Assert.Equal("approval-pending", pending.AttentionCode);
    }

    private sealed class LiveFlow : IAsyncDisposable
    {
        private readonly SyntheticCodexHome _home = new();
        private readonly string _harness;
        private long _sequence;
        public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        public BridgeMonitorAdapter Adapter { get; }

        private LiveFlow(string harness)
        {
            _harness = harness;
            File.WriteAllText(Path.Combine(_home.Root, "events.jsonl"), string.Empty);
            Adapter = new BridgeMonitorAdapter(new SourceInstance(harness, "live-test", "Live test",
                LocalSpoolDirectory: _home.Root), _home.Root, timeProvider: Clock);
        }

        public static async Task<LiveFlow> Create(string harness)
        {
            var flow = new LiveFlow(harness);
            await flow.Adapter.StartAsync(new CancellationToken(canceled: true));
            flow.Adapter.Poll(); // Establish the Claude attach cursor before live records.
            flow.Append("session/start");
            flow.Append("turn/start");
            return flow;
        }

        public void SkipRecord() => _sequence++;

        public SourceSnapshot Append(string kind, string? requestId = null, string? callId = null, string evidence = "explicit")
        {
            Clock.AdvanceSeconds(1);
            _sequence++;
            var line = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, harnessId = _harness, sourceInstanceId = "live-test", sourceEpoch = "epoch",
                sessionId = "session", sequence = _sequence, eventId = "event-" + _sequence,
                observedAtUtc = Clock.GetUtcNow(), kind, turnId = "turn", requestId, callId,
                category = kind.EndsWith("asked", StringComparison.Ordinal) ? "waiting" : "working",
                evidence, hydration = false, producerVersion = "test/1.0.0",
            });
            File.AppendAllText(Path.Combine(_home.Root, "events.jsonl"), line + "\n");
            return Adapter.Poll();
        }

        public async ValueTask DisposeAsync()
        {
            await Adapter.DisposeAsync();
            _home.Dispose();
        }
    }
}
