using System.IO;
using System.Text.Json;
using TheOverseer.App;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Monitoring.Bridge;
using TheOverseer.Core.Monitoring.Sessions;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class MultiHarnessReviewTests
{
    [Fact]
    public void IdentityProbeCannotExpandASmallShareToItsFormerFourKilobyteFloor()
    {
        using var home = new SyntheticCodexHome();
        var file = home.WriteRollout("one", new[] { Records.SessionMeta("one") });
        Assert.False(SessionIdentityReader.Read(file, 16).Found);
        Assert.True(SessionIdentityReader.Read(file, 4096).Found);
    }

    [Fact]
    public async Task AnOldAuthoritativeEmptySourceExpiresInsteadOfStayingHealthyForever()
    {
        using var home = new SyntheticCodexHome();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var source = new SourceInstance("dsh", "one", "DeepSeek", LocalSpoolDirectory: home.Root);
        File.WriteAllText(Path.Combine(home.Root, "current.json"), CurrentDocument());
        await using var adapter = new BridgeMonitorAdapter(source, home.Root, timeProvider: clock);
        await adapter.StartAsync(new CancellationToken(true));
        Assert.True(adapter.Poll().CoverageComplete);
        clock.AdvanceSeconds(901);
        var stale = adapter.Poll();
        Assert.False(stale.CoverageComplete);
        Assert.Contains("bridge-empty-snapshot-stale", stale.HealthReasons);
    }

    [Fact]
    public async Task DiscoveryFailureRemainsIncompleteAfterComposition()
    {
        using var home = new SyntheticCodexHome();
        var discovery = HarnessSources.DiscoverDetailed(home.Root, options: new HarnessDiscoveryOptions(
            ConfiguredRoots: new[] { new HarnessRootSetting { HarnessId = "dsh", Path = Path.Combine(home.Root, "missing") } }));
        Assert.True(discovery.Capped);
        var adapter = new ProbeAdapter();
        await using var composite = new CompositeMonitorSource(new[] { adapter }, discoveryIncomplete: discovery.Capped);
        await composite.StartAsync(CancellationToken.None);
        adapter.PublishHealthy(DateTimeOffset.UtcNow);
        Assert.False(composite.Current.Health.CoverageComplete);
        Assert.Contains(composite.Current.Health.Reasons, code => code.StartsWith("sources-truncated", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalSpoolPathDoesNotEnterSerializedSourceOrDiagnosticText()
    {
        var source = new SourceInstance("dsh", "one", "DeepSeek", LocalSpoolDirectory: @"C:\PRIVATE_CANARY\spool");
        Assert.DoesNotContain("PRIVATE_CANARY", JsonSerializer.Serialize(source));
        Assert.DoesNotContain("PRIVATE_CANARY", source.ToString());
    }

    [Fact]
    public async Task RealCodexReceivesSmallAggregateShareAndSessionLimit()
    {
        using var home = new SyntheticCodexHome();
        for (var index = 0; index < 3; index++)
        {
            var id = "01a0c700-0000-0000-0000-00000000000" + index;
            home.WriteRollout(id, new[] { Records.SessionMeta(id), Records.TaskStarted("turn") }
                .Concat(Enumerable.Repeat(Records.ItemCompleted("turn", "Reasoning", command: new string('x', 2048)), 100)));
        }
        var profile = new SessionProfile("codex", "Codex", home.Root, home.SessionsRoot, home.IndexPath, home.StateDatabasePath);
        var monitor = new CodexMonitor(MonitorOptions.Default, new[] { profile });
        await using var composite = new CompositeMonitorSource(new IAgentMonitorAdapter[]
        {
            new CodexMonitorAdapter(new CodexMonitorSource(monitor)), new ProbeAdapter(),
        }, aggregateBudget: new SourceBudget(32768, 2, "small"));
        Assert.Equal(16384, composite.Budget.BytesPerPass);
        long previous = 0;
        for (var pass = 0; pass < 8; pass++)
        {
            var snapshot = monitor.Refresh();
            Assert.InRange(snapshot.Observation.BytesRead - previous, 0, 16384);
            previous = snapshot.Observation.BytesRead;
            Assert.InRange(monitor.TrackedFileCount, 0, 1);
            Assert.InRange(snapshot.Tasks.Count, 0, 1);
        }
        Assert.True(previous > 0);
    }

    [Fact]
    public void MalformedReplacementCannotReturnThePreviousHealthySnapshot()
    {
        using var home = new SyntheticCodexHome();
        var reader = new BridgeSpoolReader(home.Root);
        var source = new SourceInstance("dsh", "one", "DeepSeek");
        File.WriteAllText(reader.CurrentPath, CurrentDocument());
        Assert.Equal(BridgeCurrentStateOutcome.Accepted, reader.ReadCurrentState(source).Outcome);
        File.WriteAllText(reader.CurrentPath, "{broken");
        Assert.Equal(BridgeCurrentStateOutcome.Malformed, reader.ReadCurrentState(source).Outcome);
        Assert.Equal(BridgeCurrentStateOutcome.Malformed, reader.ReadCurrentState(source).Outcome);
    }

    [Fact]
    public void SmallSharedBudgetIncludesHealthEventsAndIncrementalSnapshot()
    {
        using var home = new SyntheticCodexHome();
        var reader = new BridgeSpoolReader(home.Root) { MaximumBytesPerPass = 4096 };
        var source = new SourceInstance("dsh", "one", "DeepSeek");
        var longLine = new string('x', 9000);
        File.WriteAllText(reader.EventsPath, longLine + "\n");
        File.WriteAllText(reader.HealthPath, "{\"counters\":{\"dropped\":3}}");
        File.WriteAllText(reader.CurrentPath, CurrentDocument() + new string(' ', 16000));
        var received = new List<string>();
        BridgeCurrentStateParseResult? result = null;
        for (var pass = 0; pass < 20; pass++)
        {
            received.AddRange(reader.ReadNewLines(out _, out var drops));
            Assert.Equal(3, drops);
            result = reader.ReadCurrentState(source);
            Assert.InRange(reader.BytesReadThisPass, 0, 4096);
            if (result.Outcome == BridgeCurrentStateOutcome.Accepted && received.Count > 0) break;
        }
        Assert.Equal(longLine, Assert.Single(received));
        Assert.Equal(BridgeCurrentStateOutcome.Accepted, result!.Outcome);
    }

    [Fact]
    public void MalformedHealthCannotSilentlyEraseDrops()
    {
        using var home = new SyntheticCodexHome();
        var reader = new BridgeSpoolReader(home.Root);
        File.WriteAllText(reader.HealthPath, "{\"counters\":{\"dropped\":3}}");
        reader.ReadNewLines(out _, out var initial);
        Assert.Equal(3, initial);
        File.WriteAllText(reader.HealthPath, "{broken");
        reader.ReadNewLines(out var reason, out var after);
        Assert.Equal(3, after);
        Assert.Equal("bridge-health-malformed", reason);
    }

    private static string CurrentDocument() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, harnessId = "dsh", sourceInstanceId = "one", sourceEpoch = "epoch",
        updatedAtUtc = DateTimeOffset.UtcNow, sessions = Array.Empty<object>(),
    });

    [Fact]
    public async Task PreviouslyHealthySourceBecomesStaleWithoutAnyPeerPublication()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var adapter = new ProbeAdapter();
        await using var composite = new CompositeMonitorSource(new[] { adapter }, timeProvider: clock);
        await composite.StartAsync(CancellationToken.None);
        adapter.PublishHealthy(clock.GetUtcNow(), VoiceTestData.Task(state: AgentTaskState.Working));
        Assert.True(composite.Current.Health.CoverageComplete);
        clock.AdvanceSeconds(121);
        Assert.False(composite.Current.Health.CoverageComplete);
        Assert.True(Assert.Single(composite.Current.Tasks).IsStale);
        Assert.Contains("dsh:source-silent", composite.Current.Health.Reasons);
        adapter.PublishHealthy(clock.GetUtcNow(), VoiceTestData.Task(state: AgentTaskState.Working));
        Assert.True(composite.Current.Health.CoverageComplete);
        Assert.False(Assert.Single(composite.Current.Tasks).IsStale);
    }

    [Fact]
    public void RotationHonorsSharedBudgetAndReportsAnUnrecoverableTail()
    {
        using var home = new SyntheticCodexHome();
        var reader = new BridgeSpoolReader(home.Root) { MaximumBytesPerPass = 4096 };
        File.WriteAllText(reader.EventsPath, "first\n");
        Assert.Single(reader.ReadNewLines(out _, out _));
        File.AppendAllText(reader.EventsPath, string.Concat(Enumerable.Repeat("tail\n", 3000)));
        File.Move(reader.EventsPath, Path.Combine(home.Root, "events-old.jsonl"));
        File.WriteAllText(reader.EventsPath, "new\n");
        reader.ReadNewLines(out var reason, out _);
        Assert.Equal("bridge-rotated-unread", reason);
        Assert.InRange(reader.BytesReadThisPass, 0, 4096);
    }

    [Fact]
    public void RotationSkipsOlderRetainedFilesAndReassemblesPartialRecord()
    {
        using var home = new SyntheticCodexHome();
        var reader = new BridgeSpoolReader(home.Root);
        var old = Path.Combine(home.Root, "events-old.jsonl");
        File.WriteAllText(old, "do-not-replay\n");
        File.SetCreationTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(reader.EventsPath, "first\npart");
        Assert.Equal("first", Assert.Single(reader.ReadNewLines(out _, out _)));
        File.AppendAllText(reader.EventsPath, "ial\n");
        File.Move(reader.EventsPath, Path.Combine(home.Root, "events-recent.jsonl"));
        File.WriteAllText(reader.EventsPath, "next\n");
        var lines = reader.ReadNewLines(out var reason, out _);
        Assert.Equal("ok", reason);
        Assert.Equal(new[] { "partial", "next" }, lines);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"counters\":{\"droppedA\":2147483647,\"droppedB\":10}}")]
    public void HostileHealthShapesCannotFaultThePollingLoopOrOverflow(string health)
    {
        using var home = new SyntheticCodexHome();
        var reader = new BridgeSpoolReader(home.Root);
        File.WriteAllText(reader.HealthPath, health);
        reader.ReadNewLines(out var reason, out var drops);
        if (health.Contains("droppedA", StringComparison.Ordinal)) Assert.Equal(int.MaxValue, drops);
        else Assert.Equal("bridge-health-malformed", reason);
    }

    [Fact]
    public async Task RealCodexStartupDoesNotPreventTheFollowingBridgeFromStarting()
    {
        using var home = new SyntheticCodexHome();
        var monitor = new CodexMonitor(MonitorOptions.Default, Array.Empty<SessionProfile>());
        var peer = new ProbeAdapter();
        await using var composite = new CompositeMonitorSource(new IAgentMonitorAdapter[]
        {
            new CodexMonitorAdapter(new CodexMonitorSource(monitor)), peer,
        });
        await composite.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(peer.Started);
    }

    [Fact]
    public async Task ConfiguredSpoolIsTheDirectoryActuallyReadByTheAdapter()
    {
        using var home = new SyntheticCodexHome();
        var data = Path.Combine(home.Root, "data");
        var custom = Path.Combine(home.Root, "custom");
        var spool = Path.Combine(custom, "external");
        Directory.CreateDirectory(spool);
        File.WriteAllText(Path.Combine(spool, "events.jsonl"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, harnessId = "dsh", sourceInstanceId = "external", sourceEpoch = "epoch",
            sessionId = "session", sequence = 1, eventId = "one", observedAtUtc = DateTimeOffset.UtcNow,
            kind = "session/start", category = "idle", evidence = "explicit",
        }) + "\n");
        var source = Assert.Single(HarnessSources.Discover(data, options: new HarnessDiscoveryOptions(
            ConfiguredRoots: new[] { new HarnessRootSetting { HarnessId = "dsh", Path = custom } })));
        await using var adapter = new BridgeMonitorAdapter(source, data);
        await adapter.StartAsync(new CancellationToken(true));
        Assert.Single(adapter.Poll().Tasks);
        Assert.Equal(spool, adapter.SpoolDirectory);
    }

    [Fact]
    public void ConfiguredAliasDoesNotDuplicateTheSameSource()
    {
        using var home = new SyntheticCodexHome();
        var root = HarnessSources.DefaultRoot(home.Root, "dsh");
        Directory.CreateDirectory(Path.Combine(root, "instance"));
        var result = HarnessSources.DiscoverDetailed(home.Root, options: new HarnessDiscoveryOptions(
            ConfiguredRoots: new[] { new HarnessRootSetting { HarnessId = "dsh", Path = root } }));
        Assert.Single(result.Sources);
        Assert.False(result.Capped);
    }

    [Fact]
    public void DisabledSourceDoesNotConsumeTheEnabledDiscoveryLimit()
    {
        using var home = new SyntheticCodexHome();
        var root = HarnessSources.DefaultRoot(home.Root, "dsh");
        Directory.CreateDirectory(Path.Combine(root, "a-disabled"));
        Directory.CreateDirectory(Path.Combine(root, "z-enabled"));
        var result = HarnessSources.DiscoverDetailed(home.Root, new[] { "dsh/a-disabled" },
            new HarnessDiscoveryOptions(MaximumSources: 1));
        Assert.Contains(result.Sources, source => source.SourceInstanceId == "z-enabled" && source.IsEnabled);
        Assert.False(result.Capped);
    }

    private sealed class ProbeAdapter : IAgentMonitorAdapter
    {
        public bool Started { get; private set; }
        public SourceInstance Source { get; } = new("dsh", "probe", "Probe");
        public SourceSnapshot Current => SourceSnapshot.Empty(Source, DateTimeOffset.UtcNow, "unavailable", "probe");
        public event Action<SourceSnapshot>? SnapshotAvailable;
        public void PublishHealthy(DateTimeOffset now, params AgentTask[] tasks) => SnapshotAvailable?.Invoke(
            Current with { ObservedAt = now, Health = MonitoringHealthLevel.Healthy,
                CoverageComplete = true, HealthReasons = Array.Empty<string>(), Tasks = tasks });
        public Task StartAsync(CancellationToken cancellationToken) { Started = true; return Task.CompletedTask; }
        public void RequestReconcile() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
