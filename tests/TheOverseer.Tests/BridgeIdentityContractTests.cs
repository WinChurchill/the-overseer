using System.Text.Json;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.Bridge;

namespace TheOverseer.Tests;

public sealed class BridgeIdentityContractTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Theory]
    [InlineData("claude")]
    [InlineData("dsh")]
    public void RetiredEpochCannotMutateCurrentRequestsOrSnapshot(string harness)
    {
        var source = new SourceInstance(harness, "test", "Test");
        var session = new HarnessBridgeSession(source);
        HarnessBridgeRecord Record(string epoch, long seq, string kind, string? request = null) => new(
            1, harness, "test", epoch, "session", seq, epoch + seq, Start.AddSeconds(seq), null,
            kind, "turn", request, request, "C:/synthetic", "working", "explicit", null, false,
            harness == "dsh" ? "dsh-observer/1.1.0" : "overseer-claude-bridge/1.0.4");
        session.Apply(Record("E1", 10, "session/start"));
        session.Apply(Record("E2", 1, "session/start"));
        session.Apply(Record("E2", 2, "turn/start"));
        session.Apply(Record("E2", 3, "input/asked", "new-question"));
        var before = Assert.Single(session.Tasks(Start.AddSeconds(20)));
        Assert.Equal(BridgeApplyOutcome.Superseded, session.Apply(Record("E1", 11, "session/start")).Outcome);
        Assert.Equal(BridgeApplyOutcome.Superseded, session.Apply(Record("E1", 12, "input/answered", "new-question")).Outcome);
        var snapshot = new HarnessBridgeCurrentState(1, harness, "test", "E1", "old/0", Start.AddSeconds(25), 0,
            new[] { new BridgeCurrentSessionState("session", "", "", null, null, null, "", false, "", "idle", false, 30, "old", 1) });
        Assert.False(session.ApplyCurrentState(snapshot).Accepted);
        Assert.Equal(before, Assert.Single(session.Tasks(Start.AddSeconds(20))));
    }

    [Theory]
    [InlineData("sessionId")]
    [InlineData("requestId")]
    [InlineData("callId")]
    [InlineData("turnId")]
    [InlineData("eventId")]
    [InlineData("sourceEpoch")]
    public void OversizeWireIdentityIsRejectedRatherThanAliased(string field)
    {
        var envelope = new Dictionary<string, object?> {
            ["schemaVersion"] = 1, ["harnessId"] = "dsh", ["sourceInstanceId"] = "test",
            ["sourceEpoch"] = "epoch", ["sessionId"] = "session", ["sequence"] = 1,
            ["eventId"] = "event", ["observedAtUtc"] = Start, ["kind"] = "input/asked",
            [field] = new string('x', 129),
        };
        Assert.Equal(BridgeParseOutcome.Malformed, HarnessBridgeParser.Parse(JsonSerializer.Serialize(envelope)).Outcome);
    }

    [Fact]
    public void NewEpochSnapshotMayResetSequenceWithoutReplayingOldRequests()
    {
        var source = new SourceInstance("dsh", "test", "Test");
        var session = new HarnessBridgeSession(source);
        HarnessBridgeCurrentState Snapshot(string epoch, long sequence, int seconds) => new(1, "dsh", "test", epoch,
            "dsh-observer/1.1.0", Start.AddSeconds(seconds), 0, new[] {
                new BridgeCurrentSessionState("session", "", "", null, null, null, "turn", false, "", "working", false, sequence, "event", 1)
                { PendingInputRequestIds = new[] { epoch + "-question" } }
            });
        session.ApplyCurrentState(Snapshot("E1", 100, 1));
        Assert.Equal(1, session.ApplyCurrentState(Snapshot("E2", 1, 2)).Applied);
        Assert.Equal(1, Assert.Single(session.Tasks(Start.AddSeconds(2))).LastSequence);
    }

    [Fact]
    public void EpochRetirementSurvivesLaneEvictionAndSaturatesWithoutForgetting()
    {
        var source = new SourceInstance("dsh", "test", "Test");
        var session = new HarnessBridgeSession(source, maximumSessions: 1);
        HarnessBridgeRecord Record(int epoch, string id = "session") => new(1, "dsh", "test", "E" + epoch,
            id, 0, "event", Start.AddSeconds(epoch), null, "session/start", null, null, null, null,
            "idle", "explicit", null, false, "dsh-observer/1.1.0");
        for (var epoch = 0; epoch <= 64; epoch++) Assert.Equal(BridgeApplyOutcome.Applied, session.Apply(Record(epoch)).Outcome);
        session.Apply(Record(64, "replacement"));
        Assert.Equal(BridgeApplyOutcome.Superseded, session.Apply(Record(0)).Outcome);
        Assert.Equal("epoch-history-limit", session.Apply(Record(65)).ReasonCode);
        Assert.Equal("replacement", Assert.Single(session.Tasks(Start.AddSeconds(65))).SessionId);
        Assert.True(session.CoverageIncomplete);
        Assert.True(Assert.Single(session.Tasks(Start.AddSeconds(65))).CoverageIncomplete);
    }

    [Fact]
    public void LegacyEmptyEpochCanBeRetiredWithoutKeepingItsSequenceWatermark()
    {
        var session = new HarnessBridgeSession(new SourceInstance("dsh", "test", "Test"));
        var legacy = new HarnessBridgeRecord(1, "dsh", "test", "", "session", 100, "old", Start, null,
            "session/start", null, null, null, null, "idle", "explicit", null, false, "test/0");
        session.Apply(legacy);
        Assert.Equal(BridgeApplyOutcome.Applied, session.Apply(legacy with { SourceEpoch = "new", Sequence = 0, EventId = "new" }).Outcome);
        Assert.Equal(BridgeApplyOutcome.Superseded, session.Apply(legacy with { Sequence = 101 }).Outcome);
        Assert.Equal(0, Assert.Single(session.Tasks(Start)).LastSequence);
    }

    [Theory]
    [InlineData("sessionId")]
    [InlineData("openTurnId")]
    [InlineData("pendingApprovalRequestId")]
    [InlineData("lastEventId")]
    public void SnapshotCannotAliasOversizeSessionOrRequestIdentity(string field)
    {
        var source = new SourceInstance("dsh", "test", "Test");
        var entry = new Dictionary<string, object?> { ["sessionId"] = "session", [field] = new string('x', 129) };
        var json = JsonSerializer.Serialize(new { schemaVersion = 1, harnessId = "dsh", sourceInstanceId = "test",
            sourceEpoch = "E1", updatedAtUtc = Start, sessions = new[] { entry } });
        Assert.Equal(BridgeCurrentStateOutcome.Malformed, HarnessBridgeCurrentStateParser.Parse(json, source).Outcome);
    }

    [Theory]
    [InlineData("dsh-observer/1.1.0", CapabilitySupport.Supported)]
    [InlineData("dsh-observer/1.0.0", CapabilitySupport.Unsupported)]
    [InlineData("dsh-observer/1.1.1", CapabilitySupport.Unsupported)]
    [InlineData("dsh-observer/1.1.0-future", CapabilitySupport.Unsupported)]
    public void NativeQuestionMetadataQualificationIsVersionSpecific(string version, CapabilitySupport expected) =>
        Assert.Equal(expected, MonitorCapabilities.ForBridge("dsh", continuousHeartbeat: true, producerVersion: version).InputRequests.Support);
}
