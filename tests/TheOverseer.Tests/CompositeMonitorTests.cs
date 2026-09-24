using System.IO;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class CompositeMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly SourceInstance Codex = new(HarnessIds.Codex, "codex", "Codex");
    private static readonly SourceInstance Dsh = new(HarnessIds.Dsh, "dsh-default", "DeepSeek Harness");

    private static AgentTask BuildTask(string harnessId, string source, string conversation, AgentTaskState state) =>
        VoiceTestData.Task(state: state) with
        {
            Key = TaskKey.For(harnessId, source, conversation),
            ProfileId = source,
            ConversationId = conversation,
            LastActivityAt = Start,
            FirstSeenAt = Start,
            StateSince = Start,
        };

    private sealed class FakeAdapter : IAgentMonitorAdapter, IBudgetedMonitorAdapter
    {
        public FakeAdapter(SourceInstance source) => Source = source;

        public SourceInstance Source { get; }

        public SourceBudget? AppliedBudget { get; private set; }

        public void ApplyBudget(SourceBudget budget) => AppliedBudget = budget;

        public SourceSnapshot Current { get; private set; } =
            SourceSnapshot.Empty(new SourceInstance(HarnessIds.Codex, "unset", "unset"), Start, "not-started", "fake");

        public event Action<SourceSnapshot>? SnapshotAvailable;

        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public void RequestReconcile()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Push(
            MonitoringHealthLevel level,
            bool complete,
            IReadOnlyList<AgentTask> tasks,
            IReadOnlyList<AttentionEvent>? transitions = null,
            DateTimeOffset? observedAt = null,
            DateTimeOffset? evidenceAt = null,
            MonitorCapabilities? capabilities = null)
        {
            Current = new SourceSnapshot(
                Source,
                observedAt ?? Start,
                level,
                level == MonitoringHealthLevel.Healthy ? Array.Empty<string>() : new[] { "source-degraded" },
                complete,
                tasks,
                transitions ?? Array.Empty<AttentionEvent>(),
                capabilities ?? MonitorCapabilities.Initial,
                "fake",
                evidenceAt ?? observedAt ?? Start);
            SnapshotAvailable?.Invoke(Current);
        }
    }

    [Fact]
    public async Task CompleteSourcesProduceKnownCountsAndForwardEachTransitionOnce()
    {
        var codex = new FakeAdapter(Codex);
        var dsh = new FakeAdapter(Dsh);
        await using var composite = new CompositeMonitorSource(new IAgentMonitorAdapter[] { codex, dsh }, NullMonitorLog.Instance);
        await composite.StartAsync(CancellationToken.None);

        var received = new List<AttentionEvent>();
        composite.SnapshotAvailable += publication => received.AddRange(publication.Transitions);

        var key = TaskKey.For(HarnessIds.Codex, "codex", "conv-1");
        var transition = VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start);
        codex.Push(MonitoringHealthLevel.Healthy, true, new[] { BuildTask(HarnessIds.Codex, "codex", "conv-1", AgentTaskState.Working) }, new[] { transition });
        codex.Push(MonitoringHealthLevel.Healthy, true, new[] { BuildTask(HarnessIds.Codex, "codex", "conv-1", AgentTaskState.Working) }, new[] { transition });
        dsh.Push(MonitoringHealthLevel.Healthy, true, new[] { BuildTask(HarnessIds.Dsh, "dsh-default", "conv-2", AgentTaskState.Done) });

        var snapshot = composite.Current;

        Assert.Equal(2, snapshot.Tasks.Count);
        Assert.Equal(CountValue.Known(1), snapshot.Counts.Working);
        Assert.Equal(CountValue.Known(2), snapshot.Counts.Total);
        Assert.True(snapshot.Health.CoverageComplete);
        Assert.Empty(snapshot.Transitions);
        Assert.Single(received);
        composite.RequestReconcile();
        Assert.Single(received);
    }

    [Fact]
    public async Task AFailingSourceKeepsHealthyWorkVisibleAndMakesCountsPartial()
    {
        var codex = new FakeAdapter(Codex);
        var dsh = new FakeAdapter(Dsh);
        await using var composite = new CompositeMonitorSource(new IAgentMonitorAdapter[] { codex, dsh }, NullMonitorLog.Instance);
        await composite.StartAsync(CancellationToken.None);

        codex.Push(MonitoringHealthLevel.Healthy, true, new[] { BuildTask(HarnessIds.Codex, "codex", "conv-1", AgentTaskState.Working) });
        dsh.Push(MonitoringHealthLevel.Unavailable, false, Array.Empty<AgentTask>());

        var snapshot = composite.Current;

        // The healthy source is still visible; the failure never becomes a healthy zero.
        Assert.Single(snapshot.Tasks);
        Assert.Equal(AgentTaskState.Working, snapshot.Tasks[0].State);
        Assert.False(snapshot.Counts.Working.IsComplete);
        Assert.Equal(1, snapshot.Counts.Working.Value);
        Assert.False(snapshot.Health.CoverageComplete);
        Assert.Equal(MonitoringHealthLevel.Degraded, snapshot.Health.Level);
        Assert.Contains(snapshot.Health.Reasons, reason => reason.StartsWith("dsh:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GlobalCompletionIsSuppressedWhileAnySourceIsIncomplete()
    {
        var codex = new FakeAdapter(Codex);
        var dsh = new FakeAdapter(Dsh);
        await using var composite = new CompositeMonitorSource(new IAgentMonitorAdapter[] { codex, dsh }, NullMonitorLog.Instance);
        await composite.StartAsync(CancellationToken.None);

        var allComplete = VoiceTestData.Event(AttentionEventKind.AllTasksCompleted, observedAt: Start, state: AgentTaskState.Done);
        codex.Push(MonitoringHealthLevel.Healthy, true, new[] { BuildTask(HarnessIds.Codex, "codex", "conv-1", AgentTaskState.Done) }, new[] { allComplete });
        dsh.Push(MonitoringHealthLevel.Degraded, false, new[] { BuildTask(HarnessIds.Dsh, "dsh-default", "conv-2", AgentTaskState.Working) });

        Assert.DoesNotContain(composite.Current.Transitions, transition => transition.Kind == AttentionEventKind.AllTasksCompleted);

        dsh.Push(MonitoringHealthLevel.Healthy, true,
            new[] { BuildTask(HarnessIds.Dsh, "dsh-default", "conv-2", AgentTaskState.Done) },
            new[] { VoiceTestData.Event(AttentionEventKind.TaskCompleted,
                key: TaskKey.For(HarnessIds.Dsh, "dsh-default", "conv-2"), observedAt: Start, state: AgentTaskState.Done) });

        Assert.Contains(composite.Current.Transitions, transition => transition.Kind == AttentionEventKind.AllTasksCompleted);
    }

    [Fact]
    public async Task UnreportedEnabledPeerPreventsCompleteCounts()
    {
        var codex = new FakeAdapter(Codex);
        var dsh = new FakeAdapter(Dsh);
        await using var composite = new CompositeMonitorSource(new[] { codex, dsh });
        await composite.StartAsync(CancellationToken.None);
        codex.Push(MonitoringHealthLevel.Healthy, true, Array.Empty<AgentTask>());
        Assert.False(composite.Current.Health.CoverageComplete);
        Assert.False(composite.Current.Counts.Total.IsComplete);
    }

    [Fact]
    public async Task DisabledPeerIsNotStartedAndDoesNotAffectCoverage()
    {
        var codex = new FakeAdapter(Codex);
        var dsh = new FakeAdapter(Dsh with { IsEnabled = false });
        await using var composite = new CompositeMonitorSource(new[] { codex, dsh });
        await composite.StartAsync(CancellationToken.None);
        codex.Push(MonitoringHealthLevel.Healthy, true, Array.Empty<AgentTask>());
        Assert.False(dsh.Started);
        Assert.True(composite.Current.Health.CoverageComplete);
    }

    [Fact]
    public async Task SourceLocalCompletionCannotAnnounceWhileAnotherHealthySourceWorks()
    {
        var codex = new FakeAdapter(Codex);
        var dsh = new FakeAdapter(Dsh);
        await using var composite = new CompositeMonitorSource(new[] { codex, dsh });
        await composite.StartAsync(CancellationToken.None);
        dsh.Push(MonitoringHealthLevel.Healthy, true, new[] { BuildTask(HarnessIds.Dsh, "dsh-default", "work", AgentTaskState.Working) });
        codex.Push(MonitoringHealthLevel.Healthy, true, new[] { BuildTask(HarnessIds.Codex, "codex", "done", AgentTaskState.Done) },
            new[] { VoiceTestData.Event(AttentionEventKind.AllTasksCompleted, observedAt: Start) });
        Assert.DoesNotContain(composite.Current.Transitions, item => item.Kind == AttentionEventKind.AllTasksCompleted);
    }

    [Fact]
    public void ClaudeDiscoveryAndReaderUseTheShippedProducerSpoolLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "spool", "claude", "claude-test");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Assert.Single(HarnessSources.Discover(root));
            Assert.Equal(HarnessIds.Claude, source.HarnessId);
            Assert.Equal(directory, new BridgeMonitorAdapter(source, root).SpoolDirectory);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AbsentBridgeSpoolsAreNotPhantomSources()
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "bridges", "dsh", "dsh-default"));
        try
        {
            var discovered = HarnessSources.Discover(root);
            var source = Assert.Single(discovered);
            Assert.Equal(HarnessIds.Dsh, source.HarnessId);
            Assert.True(source.IsEnabled);
            Assert.DoesNotContain(HarnessSources.Discover(root, new[] { "dsh/dsh-default" }), item => item.IsEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BridgeHydratesBacklogSilentlyThenForwardsFreshAttentionOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        var clock = new ManualTimeProvider(Start);
        var adapter = new BridgeMonitorAdapter(Dsh, root, timeProvider: clock);
        Directory.CreateDirectory(adapter.SpoolDirectory);
        string Record(long sequence, DateTimeOffset observed) => System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1, harnessId = "dsh", sourceInstanceId = "dsh-default",
            sourceEpoch = "epoch", sessionId = "session", sequence, eventId = "event-" + sequence,
            observedAtUtc = observed, kind = "approval/asked", requestId = "request-" + sequence,
            category = "waiting", evidence = "explicit", hydration = false,
        }) + "\n";
        var received = new List<AttentionEvent>();
        adapter.SnapshotAvailable += snapshot => received.AddRange(snapshot.Transitions);
        try
        {
            File.WriteAllText(Path.Combine(adapter.SpoolDirectory, "events.jsonl"), Record(1, Start.AddSeconds(-1)));
            // Cancellation prevents the background loop; explicit Poll uses the same reader.
            await adapter.StartAsync(new CancellationToken(canceled: true));
            adapter.Poll();
            Assert.Empty(received);
            clock.AdvanceSeconds(1);
            File.AppendAllText(Path.Combine(adapter.SpoolDirectory, "events.jsonl"), Record(2, clock.GetUtcNow()));
            adapter.Poll();
            adapter.Poll();
            Assert.Equal(AttentionEventKind.PermissionRequired, Assert.Single(received).Kind);
            Assert.False(adapter.Current.CoverageComplete); // Attach has not proved complete membership.
        }
        finally
        {
            await adapter.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidUtf8DoesNotMoveTheReaderPastTheNextRecord()
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "events.jsonl");
            File.WriteAllBytes(path, new byte[] { 0xff, (byte)'\n' });
            var reader = new BridgeSpoolReader(root);
            Assert.Single(reader.ReadNewLines(out _, out _));
            File.AppendAllText(path, "next\n");
            Assert.Equal("next", Assert.Single(reader.ReadNewLines(out _, out _)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void BridgeSpoolReaderReadsOnlyCompleteNewLinesAndReportsProducerDrops()
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var events = Path.Combine(root, "events.jsonl");
            var health = Path.Combine(root, "health.json");
            File.WriteAllText(events, "{\"a\":1}\n{\"b\":2}\n{\"partial\"");
            File.WriteAllText(health, "{\"counters\":{\"droppedLockTimeout\":2,\"droppedWriteFailed\":1,\"recordsWritten\":9}}");
            var reader = new BridgeSpoolReader(root);

            var first = reader.ReadNewLines(out var reason, out var dropped);

            Assert.Equal("ok", reason);
            Assert.Equal(3, dropped);
            Assert.Equal(2, first.Count);

            // A partially written record is not consumed twice or half-read.
            File.AppendAllText(events, ":1}\n");
            var second = reader.ReadNewLines(out _, out _);
            Assert.Single(second);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AggregateBudgetIsDividedBetweenSourcesInsteadOfMultiplied()
    {
        var codex = new FakeAdapter(Codex);
        var dsh = new FakeAdapter(Dsh);
        var claude = new FakeAdapter(new SourceInstance(HarnessIds.Claude, "claude-1", "Claude Code"));
        await using var composite = new CompositeMonitorSource(
            new IAgentMonitorAdapter[] { codex, dsh, claude },
            NullMonitorLog.Instance,
            aggregateBudget: new SourceBudget(3 * 1024 * 1024, 900, "test-aggregate"));

        Assert.Equal(1024 * 1024, composite.Budget.BytesPerPass);
        Assert.Equal(300, composite.Budget.MaximumSessions);
        Assert.Equal(composite.Budget, codex.AppliedBudget);
        Assert.Equal(composite.Budget, dsh.AppliedBudget);
        Assert.Equal(composite.Budget, claude.AppliedBudget);
        // Adding sources must not raise what the machine reads per pass.
        Assert.True(composite.Budget.BytesPerPass * 3 <= 3 * 1024 * 1024);
    }

    [Fact]
    public void ABudgetShareIsClampedSoNeitherConfigNorCallerCanRemoveTheBound()
    {
        Assert.Equal(SourceBudget.MinimumBytesPerPass, SourceBudget.Share(1, 1, 64).BytesPerPass);
        Assert.Equal(SourceBudget.DefaultAggregateBytesPerPass / 8, SourceBudget.Share(SourceBudget.DefaultAggregateBytesPerPass, 1024, 8).BytesPerPass);
        Assert.Equal(SourceBudget.DefaultAggregateBytesPerPass, SourceBudget.Share(SourceBudget.DefaultAggregateBytesPerPass, 1024, 1).BytesPerPass);
        Assert.Equal(SourceBudget.MinimumSessions, SourceBudget.Share(SourceBudget.DefaultAggregateBytesPerPass, 8, 8).MaximumSessions);
    }

    [Fact]
    public async Task MoreSourcesThanTheLimitAreTruncatedWithAVisibleReason()
    {
        var adapters = Enumerable.Range(0, SourceBudget.MaximumSourceInstances + 3)
            .Select(index => new FakeAdapter(new SourceInstance(HarnessIds.Dsh, "dsh-" + index, "DeepSeek Harness " + index)))
            .ToArray();
        await using var composite = new CompositeMonitorSource(adapters, NullMonitorLog.Instance);
        await composite.StartAsync(CancellationToken.None);
        adapters[0].Push(MonitoringHealthLevel.Healthy, true, Array.Empty<AgentTask>());

        Assert.Equal(SourceBudget.MaximumSourceInstances, composite.Sources.Count);
        Assert.True(composite.SourcesTruncated);
        Assert.Contains(composite.Current.Health.Reasons, reason => reason.StartsWith("sources-truncated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeadSourceIsNotRefreshedByAFreshPeer()
    {
        var clock = new ManualTimeProvider(Start);
        var codex = new FakeAdapter(Codex);
        var dsh = new FakeAdapter(Dsh);
        await using var composite = new CompositeMonitorSource(
            new IAgentMonitorAdapter[] { codex, dsh }, NullMonitorLog.Instance, timeProvider: clock);
        await composite.StartAsync(CancellationToken.None);

        dsh.Push(MonitoringHealthLevel.Unavailable, false, Array.Empty<AgentTask>(), observedAt: Start, evidenceAt: Start);
        var dead = composite.SourceSnapshots.Single(source => source.Source.HarnessId == HarnessIds.Dsh);
        clock.AdvanceSeconds(600);
        codex.Push(MonitoringHealthLevel.Healthy, true, new[] { BuildTask(HarnessIds.Codex, "codex", "conv-1", AgentTaskState.Working) }, observedAt: clock.GetUtcNow(), evidenceAt: clock.GetUtcNow());

        var afterPeer = composite.SourceSnapshots.Single(source => source.Source.HarnessId == HarnessIds.Dsh);
        Assert.Equal(dead.ObservedAt, afterPeer.ObservedAt);
        Assert.Equal(dead.EffectiveEvidenceAt, afterPeer.EffectiveEvidenceAt);
        // The peer is readable, so the peer's work stays visible...
        Assert.Equal(MonitoringHealthLevel.Degraded, composite.Current.Health.Level);
        Assert.Contains(composite.Current.Health.Reasons, reason => reason.StartsWith("dsh:", StringComparison.Ordinal));
        // ...but merged freshness is the peer's evidence, never the wall clock, and
        // never a claim that the dead source was just read.
        Assert.Equal(clock.GetUtcNow(), composite.Current.Health.LastSuccessfulReadAt);
        Assert.Equal(clock.GetUtcNow(), composite.Current.Health.ObservedAt);
    }

    [Fact]
    public async Task StalledSourcePublicationDoesNotRefreshMergedFreshness()
    {
        var clock = new ManualTimeProvider(Start);
        var codex = new FakeAdapter(Codex);
        await using var composite = new CompositeMonitorSource(
            new IAgentMonitorAdapter[] { codex }, NullMonitorLog.Instance, timeProvider: clock);
        await composite.StartAsync(CancellationToken.None);
        codex.Push(MonitoringHealthLevel.Healthy, true, Array.Empty<AgentTask>(), observedAt: clock.GetUtcNow(), evidenceAt: Start.AddSeconds(-30));

        clock.AdvanceSeconds(120);
        codex.Push(MonitoringHealthLevel.Healthy, true, Array.Empty<AgentTask>(), observedAt: clock.GetUtcNow(), evidenceAt: Start.AddSeconds(-30));

        // The adapter republished, but nothing was actually observed since -30s.
        Assert.Equal(Start.AddSeconds(-30), composite.Current.Health.LastSuccessfulReadAt);
        Assert.Equal(clock.GetUtcNow(), composite.Current.Health.ObservedAt);
    }

    [Fact]
    public async Task CapabilitiesAreAggregatedFromEveryReadableSource()
    {
        var codex = new FakeAdapter(Codex);
        var dsh = new FakeAdapter(Dsh);
        await using var composite = new CompositeMonitorSource(new IAgentMonitorAdapter[] { codex, dsh }, NullMonitorLog.Instance);
        await composite.StartAsync(CancellationToken.None);

        var codexCapabilities = MonitorCapabilities.Initial with
        {
            PermissionRequests = new CapabilityStatus(CapabilitySupport.Unsupported, "permission-requests", "codex"),
            SessionTitles = new CapabilityStatus(CapabilitySupport.LiveObserved, "session-titles", "codex titles"),
        };
        var dshCapabilities = MonitorCapabilities.Initial with
        {
            PermissionRequests = new CapabilityStatus(CapabilitySupport.Supported, "permission-requests", "dsh approvals"),
            SessionTitles = new CapabilityStatus(CapabilitySupport.Unsupported, "session-titles", "no titles in dsh"),
        };
        var dead = MonitorCapabilities.Initial with
        {
            PermissionRequests = new CapabilityStatus(CapabilitySupport.LiveObserved, "permission-requests", "should not count"),
        };
        codex.Push(MonitoringHealthLevel.Healthy, true, Array.Empty<AgentTask>(), capabilities: codexCapabilities);
        dsh.Push(MonitoringHealthLevel.Degraded, false, Array.Empty<AgentTask>(), capabilities: dshCapabilities);

        var merged = composite.Current.Capabilities;

        // Union of readable sources, not "the last healthy source wins".
        Assert.Equal(CapabilitySupport.Supported, merged.PermissionRequests.Support);
        Assert.Equal(CapabilitySupport.LiveObserved, merged.SessionTitles.Support);
        Assert.Contains("codex:", merged.PermissionRequests.Note, StringComparison.Ordinal);
        Assert.Contains("dsh:", merged.PermissionRequests.Note, StringComparison.Ordinal);

        // An unavailable source's claims never enter the aggregate.
        codex.Push(MonitoringHealthLevel.Unavailable, false, Array.Empty<AgentTask>(), capabilities: dead);
        Assert.Equal(CapabilitySupport.Supported, composite.Current.Capabilities.PermissionRequests.Support);
    }

    [Fact]
    public void DiscoveryIsBoundedDeterministicAndHonoursConfiguredRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        var dshRoot = Path.Combine(root, "bridges", "dsh");
        var extraRoot = Path.Combine(root, "extra");
        try
        {
            Directory.CreateDirectory(dshRoot);
            for (var index = 0; index < 40; index++)
            {
                Directory.CreateDirectory(Path.Combine(dshRoot, "source-" + index.ToString("D2")));
            }

            Directory.CreateDirectory(Path.Combine(extraRoot, "configured-one"));
            var bounded = HarnessSources.DiscoverDetailed(root, options: new HarnessDiscoveryOptions(MaximumSources: 5, MaximumEntriesPerRoot: 16));
            Assert.Equal(5, bounded.Sources.Count);
            Assert.True(bounded.Capped);

            // A configured root is scanned even when the default root already has
            // entries; the limit still applies across every root.
            var other = Path.Combine(root, "second-data-directory");
            Directory.CreateDirectory(other);
            var configured = HarnessSources.DiscoverDetailed(
                other,
                options: new HarnessDiscoveryOptions(ConfiguredRoots: new[] { new HarnessRootSetting { HarnessId = HarnessIds.Dsh, Path = extraRoot } }));
            Assert.Equal("configured-one", Assert.Single(configured.Sources).SourceInstanceId);

            // Deterministic order: the same inputs give the same list twice.
            var first = HarnessSources.Discover(root);
            var second = HarnessSources.Discover(root);
            Assert.Equal(first.Select(source => source.Describe()), second.Select(source => source.Describe()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnknownConfiguredRootsAreIgnoredAndDisabledSourcesStayExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        var dshRoot = Path.Combine(root, "bridges", "dsh");
        try
        {
            Directory.CreateDirectory(Path.Combine(dshRoot, "one"));
            Directory.CreateDirectory(Path.Combine(dshRoot, "two"));
            var result = HarnessSources.DiscoverDetailed(
                root,
                disabled: new[] { "dsh/one" },
                options: new HarnessDiscoveryOptions(
                    ConfiguredRoots: new[]
                    {
                        new HarnessRootSetting { HarnessId = HarnessIds.Dsh, Path = Path.Combine(root, "does-not-exist") },
                        new HarnessRootSetting { HarnessId = HarnessIds.Dsh, Path = "   " },
                    }));

            Assert.Equal(2, result.Sources.Count);
            Assert.False(result.Sources.Single(source => source.SourceInstanceId == "one").IsEnabled);
            Assert.True(result.Sources.Single(source => source.SourceInstanceId == "two").IsEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
