using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Monitoring.Bridge;
using TheOverseer.Tests.Support;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

/// <summary>Frozen host timelines through production spool reader, reducer, composite and HUD.</summary>
public sealed class BridgeBehaviorScenarioTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("dsh")]
    public async Task MalformedIdentityQualifiesRetainedAttentionUntilACoveringSnapshot(string harness)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath(harness + "-S1-identities")));
        var stages = fixture.RootElement.GetProperty("steps").EnumerateArray().Take(4).ToArray();
        using var home = new SyntheticCodexHome();
        var clock = new ManualTimeProvider(stages[0].GetProperty("at").GetDateTimeOffset());
        var source = new SourceInstance(harness, "behavior", "Behavior", LocalSpoolDirectory: home.Root);
        var adapter = new BridgeMonitorAdapter(source, home.Root, timeProvider: clock);
        await using var composite = new CompositeMonitorSource(new[] { adapter }, timeProvider: clock);
        await composite.StartAsync(new CancellationToken(canceled: true));
        var path = Path.Combine(home.Root, "events.jsonl");
        var records = stages.SelectMany(stage => stage.GetProperty("records").EnumerateArray()).ToArray();
        File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
        clock.AdvanceSeconds(3);
        Assert.True(Assert.Single(adapter.Poll().Tasks).HasCurrentState);

        // Simulate an old/malformed producer's answer, not output of the fixed mapper.
        var invalid = JsonNode.Parse(records[^1].GetRawText())!;
        var sequence = records[^1].GetProperty("sequence").GetInt64();
        invalid["kind"] = "input/answered";
        invalid["sequence"] = sequence + 1;
        invalid["requestId"] = new string('x', 129);
        File.AppendAllText(path, invalid.ToJsonString() + "\n");
        clock.AdvanceSeconds(1);
        var rejected = adapter.Poll();
        var task = Assert.Single(rejected.Tasks);
        Assert.True(task.NeedsHuman);
        Assert.True(task.IsStateUncertain);
        Assert.False(task.HasCurrentState);
        Assert.Equal(CountValue.Partial(0), composite.Current.Counts.NeedsHuman);
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(composite.Current);
        Assert.Contains("Input status unconfirmed", Assert.Single(vm.Tasks).RowSubtitle);

        object Snapshot(long lastSequence) => new { schemaVersion = 1, harnessId = harness,
            sourceInstanceId = "behavior", sourceEpoch = "E1", producerVersion = records[^1].GetProperty("producerVersion").GetString(),
            updatedAtUtc = clock.GetUtcNow(), sessions = new[] { new { sessionId = "session", lastSequence,
                lastObservedUtc = clock.GetUtcNow(), openTurnId = "turn", category = "working", inputStateComplete = true } } };
        var current = Path.Combine(home.Root, "current.json");
        var writeTime = clock.GetUtcNow().UtcDateTime;
        var olderStamp = ReplaceSnapshot(current, JsonSerializer.Serialize(Snapshot(sequence - 1)), writeTime);
        Assert.True(Assert.Single(adapter.Poll().Tasks).IsStateUncertain);
        Assert.Equal(olderStamp, ReplaceSnapshot(current, JsonSerializer.Serialize(Snapshot(sequence + 1)), writeTime));
        Assert.True(Assert.Single(adapter.Poll().Tasks).HasCurrentState);
        Assert.Equal(CountValue.Known(0), composite.Current.Counts.NeedsHuman);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameSizeSameTimestampReplacementInvalidatesCachedAndPartialSnapshots(bool readInChunks)
    {
        using var home = new SyntheticCodexHome();
        var source = new SourceInstance("dsh", "behavior", "Behavior");
        var reader = new BridgeSpoolReader(home.Root) { MaximumBytesPerPass = 4096 };
        var writeTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string Snapshot(long sequence) => JsonSerializer.Serialize(new
        {
            schemaVersion = 1, harnessId = source.HarnessId, sourceInstanceId = source.SourceInstanceId,
            sourceEpoch = "E1", updatedAtUtc = writeTime,
            sessions = new[] { new { sessionId = "session", lastSequence = sequence } },
        }) + (readInChunks ? new string(' ', 8192) : "");

        var originalStamp = ReplaceSnapshot(reader.CurrentPath, Snapshot(3), writeTime);
        var original = reader.ReadCurrentState(source);
        if (readInChunks)
            Assert.Equal("snapshot-budget-pending", original.ReasonCode);
        else
            Assert.Equal(3L, Assert.Single(original.Document!.Sessions).LastSequence);

        Assert.Equal(originalStamp, ReplaceSnapshot(reader.CurrentPath, Snapshot(5), writeTime));
        BridgeCurrentStateParseResult result = original;
        for (var pass = 0; pass < 4; pass++)
        {
            reader.ReadNewLines(out _, out _);
            result = reader.ReadCurrentState(source);
            Assert.InRange(reader.BytesReadThisPass, 0, reader.MaximumBytesPerPass);
            if (result.Outcome == BridgeCurrentStateOutcome.Accepted) break;
            Assert.Equal("snapshot-budget-pending", result.ReasonCode);
        }
        Assert.Equal(BridgeCurrentStateOutcome.Accepted, result.Outcome);
        Assert.Equal(5L, Assert.Single(result.Document!.Sessions).LastSequence);
    }

    [Theory]
    [InlineData("claude-S1-identities")]
    [InlineData("dsh-S1-identities")]
    [InlineData("claude-S2-epochs")]
    [InlineData("dsh-S2-epochs")]
    [InlineData("claude-S3-recovery")]
    [InlineData("dsh-S3-recovery")]
    [InlineData("claude-S4-transport")]
    [InlineData("dsh-S4-transport")]
    [InlineData("claude-C1-questions")]
    [InlineData("claude-C2-auxiliary")]
    [InlineData("claude-C3-approval")]
    [InlineData("claude-C4-failure")]
    [InlineData("dsh-D1-timed")]
    [InlineData("dsh-D2-blocking")]
    [InlineData("dsh-D3-priority")]
    [InlineData("dsh-D4-coverage")]
    public async Task FixedProducerTimelinePreservesItsBehaviorContract(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath(name)));
        using var home = new SyntheticCodexHome();
        var fixture = document.RootElement;
        var harness = fixture.GetProperty("harness").GetString()!;
        var steps = fixture.GetProperty("steps").EnumerateArray().ToArray();
        var clock = new ManualTimeProvider(steps[0].GetProperty("at").GetDateTimeOffset());
        var spool = Path.Combine(home.Root, "primary");
        Directory.CreateDirectory(spool);
        var source = new SourceInstance(harness, "behavior", "Behavior", LocalSpoolDirectory: spool);
        BridgeMonitorAdapter Adapter() => new(source, home.Root, new ProjectResolver(), timeProvider: clock);
        BridgeMonitorAdapter adapter = Adapter();
        CompositeMonitorSource? composite = null;
        var peerSpool = Path.Combine(home.Root, "peer");
        Directory.CreateDirectory(peerSpool);
        var peer = name.Contains("S4", StringComparison.Ordinal) ? new BridgeMonitorAdapter(
            new SourceInstance(harness == "claude" ? "dsh" : "claude", "behavior", "Peer", LocalSpoolDirectory: peerSpool),
            home.Root, new ProjectResolver(), timeProvider: clock) : null;
        string pending = "";
        var utf8 = new UTF8Encoding(false);
        try
        {
            for (var index = 0; index < steps.Length; index++)
            {
                var step = steps[index];
                var at = step.GetProperty("at").GetDateTimeOffset();
                clock.Advance(at - clock.GetUtcNow());
                if (step.TryGetProperty("restart", out var restart) && restart.GetBoolean())
                {
                    if (composite is not null) await composite.DisposeAsync();
                    else await adapter.DisposeAsync();
                    adapter = Adapter();
                    composite = null;
                }
                if (composite is null)
                {
                    composite = new CompositeMonitorSource(peer is null ? new IAgentMonitorAdapter[] { adapter }
                        : new IAgentMonitorAdapter[] { adapter, peer }, timeProvider: clock);
                    await composite.StartAsync(new CancellationToken(canceled: true));
                }
                var path = Path.Combine(spool, "events.jsonl");
                var raw = string.Concat(step.GetProperty("records").EnumerateArray().Select(record => JsonSerializer.Serialize(record) + "\n"));
                var operation = step.TryGetProperty("operation", out var op) ? op.GetString() : "";
                if (operation is "split-start" or "rotate-split-start")
                {
                    if (operation == "rotate-split-start") File.Move(path, Path.Combine(spool, "events-20260101-000004.jsonl"));
                    var split = raw.IndexOf('\n') / 2;
                    pending = raw[split..];
                    raw = raw[..split];
                }
                else if (operation == "split-finish") { raw = pending; pending = ""; }
                File.AppendAllText(path, raw, utf8);
                var current = Path.Combine(spool, "current.json");
                if (step.TryGetProperty("current", out var snapshot)) File.WriteAllText(current, snapshot.GetRawText(), utf8);

                // The other harness deliberately shares source/session/request IDs.
                // Its unresolved question must survive every primary answer/rotation.
                if (peer is not null && index == 0)
                {
                    File.WriteAllText(Path.Combine(peerSpool, "events.jsonl"), string.Concat(fixture.GetProperty("peerRecords")
                        .EnumerateArray().Select(record => JsonSerializer.Serialize(record) + "\n")), utf8);
                    peer.Poll();
                }
                var actual = adapter.Poll();
                var expected = step.GetProperty("expected");
                Assert.True(actual.Tasks.Count == 1, $"{name} checkpoint {index}: {adapter.Describe()}; {string.Join(',', actual.HealthReasons)}");
                var task = Assert.Single(actual.Tasks);
                var state = Enum.Parse<AgentTaskState>(expected.GetProperty("state").GetString()!);
                Assert.True(task.State == state, $"{name} checkpoint {index}: expected {state}, got {task.State}");
                Assert.Equal(expected.GetProperty("needsHuman").GetBoolean(), task.NeedsHuman);
                Assert.True(task.IsStateUncertain == expected.GetProperty("uncertain").GetBoolean(),
                    $"{name} checkpoint {index}: uncertain={task.IsStateUncertain}; {adapter.Describe()}; {string.Join(',', actual.HealthReasons)}");
                Assert.Equal(expected.GetProperty("stale").GetBoolean(), task.IsStale);
                Assert.Equal(expected.GetProperty("current").GetBoolean(), task.HasCurrentState);
                Assert.True(actual.CoverageComplete == expected.GetProperty("complete").GetBoolean(),
                    $"{name} checkpoint {index}: coverage {string.Join(',', actual.HealthReasons)}");
                Assert.Equal(expected.GetProperty("watermark").GetDateTimeOffset(), actual.EvidenceAt);
                Assert.Equal(expected.GetProperty("attention").EnumerateArray().Select(value => value.GetString()),
                    actual.Transitions.Select(edge => edge.Kind.ToString()));
                Assert.All(actual.Transitions, edge => Assert.Equal(task.Key, edge.Key));

                var view = composite.Current;
                var peerCount = peer is null ? 0 : 1;
                var humanCount = task.HasCurrentState && task.HasAttention ? 1 : 0;
                Assert.Equal(humanCount + peerCount, view.Counts.NeedsHuman.Value);
                Assert.True(view.Counts.NeedsHuman.IsKnown);
                Assert.Equal(actual.CoverageComplete, view.Counts.NeedsHuman.IsComplete);
                foreach (var pair in new[] {
                    (view.Counts.Working, AgentTaskState.Working), (view.Counts.NeedsApproval, AgentTaskState.NeedsApproval),
                    (view.Counts.NeedsInput, AgentTaskState.NeedsInput), (view.Counts.Error, AgentTaskState.Error),
                    (view.Counts.Done, AgentTaskState.Done), (view.Counts.Idle, AgentTaskState.Idle) })
                {
                    Assert.True(pair.Item1.IsKnown);
                    Assert.Equal((task.HasCurrentState && state == pair.Item2 ? 1 : 0) +
                        (peer is not null && pair.Item2 == AgentTaskState.NeedsInput ? 1 : 0), pair.Item1.Value);
                    Assert.Equal(actual.CoverageComplete, pair.Item1.IsComplete);
                }
                Assert.Equal((task.HasCurrentState ? 1 : 0) + peerCount, view.Counts.Total.Value);
                var vm = new HudViewModel(HudSettings.Default);
                vm.ApplySnapshot(view);
                Assert.Equal(humanCount + peerCount, vm.Projects.Sum(project => project.NeedsHumanCount));
                var visible = task.HasAttention || task.HasCurrentState && (task.State != AgentTaskState.Idle);
                Assert.Equal(visible ? 1 : 0, vm.Tasks.Count(row => row.Key == task.Key));
                if (task.HasUnconfirmedAttention)
                    Assert.Contains("status unconfirmed", Assert.Single(vm.Tasks, row => row.Key == task.Key).RowSubtitle);
                if (peer is not null)
                {
                    Assert.Equal(2, view.Tasks.Count);
                    Assert.Contains(vm.Tasks, row => row.Key.HarnessId == peer.Source.HarnessId);
                    Assert.DoesNotContain(actual.Transitions, edge => edge.Key.HarnessId == peer.Source.HarnessId);
                }
                Assert.DoesNotContain("SECRET_", actual.ToString() + string.Join('\n', actual.Tasks));
                Assert.Empty(adapter.Poll().Transitions); // Re-reading bytes/current state never replays an edge.
            }
            if (fixture.TryGetProperty("identitySessions", out var sessions))
            {
                var identitySpool = Path.Combine(home.Root, "identities");
                Directory.CreateDirectory(identitySpool);
                File.WriteAllText(Path.Combine(identitySpool, "events.jsonl"), string.Concat(sessions.EnumerateArray()
                    .Select(record => JsonSerializer.Serialize(record) + "\n")), utf8);
                await using var identities = new BridgeMonitorAdapter(source with { LocalSpoolDirectory = identitySpool },
                    home.Root, timeProvider: new ManualTimeProvider(steps[0].GetProperty("at").GetDateTimeOffset()));
                await identities.StartAsync(new CancellationToken(canceled: true));
                var snapshot = identities.Poll();
                Assert.Equal(4, snapshot.Tasks.Count);
                Assert.Equal(4, snapshot.Tasks.Select(task => task.Key).Distinct().Count());
                Assert.All(snapshot.Tasks, task => { Assert.Equal(AgentTaskState.NeedsInput, task.State); Assert.True(task.HasCurrentState); });
            }
            if (peer is not null)
            {
                var bounded = new BridgeSpoolReader(spool) { MaximumBytesPerPass = 128 };
                var lines = new List<string>();
                for (var pass = 0; pass < 128; pass++)
                {
                    lines.AddRange(bounded.ReadNewLines(out _, out _));
                    Assert.InRange(bounded.BytesReadThisPass, 0, 128);
                }
                Assert.Single(lines);
                Assert.Equal("input/answered", TheOverseer.Core.Monitoring.Bridge.HarnessBridgeParser.Parse(lines[0]).Record!.Kind);
            }
        }
        finally
        {
            if (composite is not null) await composite.DisposeAsync();
            else await adapter.DisposeAsync();
        }
    }

    private static (long Length, DateTime LastWrite) ReplaceSnapshot(string path, string json, DateTime writeTime)
    {
        var staging = path + ".staged";
        File.WriteAllText(staging, json);
        File.SetLastWriteTimeUtc(staging, writeTime);
        File.Move(staging, path, overwrite: true);
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static string FixturePath(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TheOverseer.sln")))
                return Path.Combine(directory.FullName, "integrations", "fixtures", "behavior", name + ".json");
        throw new InvalidOperationException("Repository root not found");
    }
}
