using System.IO;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Monitoring.Bridge;
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

    [Theory]
    [InlineData(HarnessIds.Codex)]
    [InlineData(HarnessIds.Claude)]
    [InlineData(HarnessIds.Dsh)]
    public async Task UnconfirmedRequestCannotInflateCountsOrHideAnotherHarnessRequest(string harness)
    {
        var clock = new ManualTimeProvider(Start);
        var uncertainSource = new SourceInstance(harness, "uncertain", "Uncertain");
        var peerSource = new SourceInstance(harness == HarnessIds.Codex ? HarnessIds.Dsh : HarnessIds.Codex, "peer", "Peer");
        var uncertain = new FakeAdapter(uncertainSource);
        var peer = new FakeAdapter(peerSource);
        await using var composite = new CompositeMonitorSource(new IAgentMonitorAdapter[] { uncertain, peer }, timeProvider: clock);
        await composite.StartAsync(CancellationToken.None);
        var lastKnown = BuildTask(harness, "uncertain", "same-id", AgentTaskState.NeedsInput) with
        {
            NeedsHuman = true, IsStateUncertain = true,
        };
        var known = BuildTask(peerSource.HarnessId, "peer", "same-id", AgentTaskState.NeedsInput) with { NeedsHuman = true };
        uncertain.Push(MonitoringHealthLevel.Degraded, false, new[] { lastKnown });
        var edge = VoiceTestData.Event(AttentionEventKind.InputRequired, key: known.Key, observedAt: Start);
        peer.Push(MonitoringHealthLevel.Healthy, true, new[] { known }, new[] { edge });
        var snapshot = composite.Current;
        Assert.Equal(CountValue.Partial(1), snapshot.Counts.NeedsHuman);
        Assert.Equal(2, snapshot.Tasks.Count);
        Assert.Equal(known.Key, Assert.Single(snapshot.Transitions).Key);
        var vm = new TheOverseer.UI.ViewModels.HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(snapshot);
        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal("Needs you", vm.StatusHeadline);
        Assert.Contains(vm.Tasks, row => row.Key == lastKnown.Key && row.RowSubtitle.Contains("Input status unconfirmed"));
        Assert.Equal(1, vm.Projects.Sum(project => project.NeedsHumanCount));
    }

    [Fact]
    public async Task DisplayNumbersQualifyIdentityAndAgreeWithAttentionAcrossSources()
    {
        var clock = new ManualTimeProvider(Start);
        var codex = new FakeAdapter(Codex);
        var dsh = new FakeAdapter(Dsh);
        var claudeSource = new SourceInstance(HarnessIds.Claude, "claude", "Claude");
        var claude = new FakeAdapter(claudeSource);
        await using var composite = new CompositeMonitorSource(new IAgentMonitorAdapter[] { codex, dsh, claude }, timeProvider: clock);
        await composite.StartAsync(CancellationToken.None);
        var a = BuildTask(HarnessIds.Codex, "codex", "same-id", AgentTaskState.Working) with { Number = 1 };
        var b = BuildTask(HarnessIds.Dsh, "dsh-default", "same-id", AgentTaskState.NeedsInput) with { Number = 8570 };
        var c = BuildTask(HarnessIds.Claude, "claude", "same-id", AgentTaskState.Working) with { Number = 6949 };
        codex.Push(MonitoringHealthLevel.Healthy, true, new[] { a });
        dsh.Push(MonitoringHealthLevel.Healthy, true, new[] { b }, new[] { VoiceTestData.Event(AttentionEventKind.InputRequired, key: b.Key, number: 8570, observedAt: Start) });
        var edge = Assert.Single(composite.Current.Transitions);
        Assert.Equal(2, edge.Number);
        claude.Push(MonitoringHealthLevel.Healthy, true, new[] { c });
        Assert.Equal(new[] { 1, 2, 3 }, composite.Current.Tasks.Select(task => task.Number));
        dsh.Push(MonitoringHealthLevel.Unavailable, false, new[] { b with { IsStale = true } });
        Assert.Equal(new[] { 1, 2, 3 }, composite.Current.Tasks.Select(task => task.Number));
        // A short source gap does not give the chat another number.
        dsh.Push(MonitoringHealthLevel.Healthy, true, Array.Empty<AgentTask>());
        clock.Advance(TimeSpan.FromSeconds(30));
        dsh.Push(MonitoringHealthLevel.Healthy, true, new[] { b });
        Assert.Equal(2, composite.Current.Tasks.Single(task => task.Key == b.Key).Number);
        // Source-local renumbering is irrelevant to the shared sequence.
        codex.Push(MonitoringHealthLevel.Healthy, true, new[] { a with { Number = 99 } });
        Assert.Equal(1, composite.Current.Tasks.Single(task => task.Key == a.Key).Number);
    }

    [Fact]
    public async Task ExpiredDisplayNumbersWaitForCooldownAndCannotCollideWithLivePeers()
    {
        var clock = new ManualTimeProvider(Start);
        var adapter = new FakeAdapter(Codex);
        await using var composite = new CompositeMonitorSource(new[] { adapter }, timeProvider: clock);
        await composite.StartAsync(CancellationToken.None);
        AgentTask Task(string id) => BuildTask(HarnessIds.Codex, "codex", id, AgentTaskState.Working);
        adapter.Push(MonitoringHealthLevel.Healthy, true, new[] { Task("old"), Task("peer") });
        adapter.Push(MonitoringHealthLevel.Healthy, true, new[] { Task("peer") });
        clock.Advance(TimeSpan.FromMinutes(3));
        adapter.Push(MonitoringHealthLevel.Healthy, true, new[] { Task("peer"), Task("new") });
        Assert.Equal(new[] { 2, 3 }, composite.Current.Tasks.Select(task => task.Number));
        clock.Advance(TimeSpan.FromMinutes(3));
        adapter.Push(MonitoringHealthLevel.Healthy, true, new[] { Task("peer"), Task("new"), Task("reused") });
        Assert.Equal(new[] { 2, 3, 1 }, composite.Current.Tasks.Select(task => task.Number));
    }

    [Fact]
    public async Task AbsentDisplayIdentityCacheIsBounded()
    {
        var adapter = new FakeAdapter(Codex);
        var clock = new ManualTimeProvider(Start);
        await using var composite = new CompositeMonitorSource(new[] { adapter }, timeProvider: clock);
        await composite.StartAsync(CancellationToken.None);
        var tasks = Enumerable.Range(1, 520).Select(index => BuildTask(HarnessIds.Codex, "codex", "task-" + index, AgentTaskState.Working)).ToArray();
        adapter.Push(MonitoringHealthLevel.Healthy, true, tasks);
        adapter.Push(MonitoringHealthLevel.Healthy, true, Array.Empty<AgentTask>());
        adapter.Push(MonitoringHealthLevel.Healthy, true, new[] { tasks[0], tasks[^1] });
        Assert.Equal(new[] { 521, 520 }, composite.Current.Tasks.Select(task => task.Number));
    }

    [Fact]
    public async Task BatchedAttentionWithEqualSourceAndConversationIdsKeepsBothHarnesses()
    {
        var codex = new FakeAdapter(new SourceInstance(HarnessIds.Codex, "same", "Codex"));
        var dsh = new FakeAdapter(new SourceInstance(HarnessIds.Dsh, "same", "DSH"));
        await using var composite = new CompositeMonitorSource(new IAgentMonitorAdapter[] { codex, dsh });
        await composite.StartAsync(CancellationToken.None);
        var a = BuildTask(HarnessIds.Codex, "same", "same", AgentTaskState.NeedsInput);
        var b = BuildTask(HarnessIds.Dsh, "same", "same", AgentTaskState.NeedsInput);
        var received = new List<AttentionEvent>();
        composite.SnapshotAvailable += snapshot => received.AddRange(snapshot.Transitions);
        // Exercise one aggregation batch, including an edge before its peer row.
        codex.Push(MonitoringHealthLevel.Healthy, true, new[] { a }, new[] {
            VoiceTestData.Event(AttentionEventKind.InputRequired, key: a.Key),
            VoiceTestData.Event(AttentionEventKind.InputRequired, key: b.Key) });
        dsh.Push(MonitoringHealthLevel.Healthy, true, new[] { b });
        Assert.Equal(2, received.Count);
        Assert.Equal(new[] { 1, 2 }, received.Select(edge => edge.Number));
        Assert.Equal(2, composite.Current.Tasks.Single(task => task.Key == b.Key).Number);
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
    public async Task MalformedUnicodeInEventsAndSnapshotDoesNotStopLiveOrRestartedPolling()
    {
        using var home = new SyntheticCodexHome();
        var spool = HarnessSources.SpoolDirectory(home.Root, Dsh);
        Directory.CreateDirectory(spool);
        var events = Path.Combine(spool, "events.jsonl");
        string Record(string session, string path) => """
            {"schemaVersion":1,"harnessId":"dsh","sourceInstanceId":"dsh-default","sourceEpoch":"epoch",
             "sessionId":"SESSION","sequence":1,"observedAtUtc":"TIME","kind":"session/start","projectRoot":"PATH"}
            """.ReplaceLineEndings("").Replace("SESSION", session).Replace("PATH", path)
            .Replace("TIME", DateTimeOffset.UtcNow.ToString("o"));
        File.WriteAllLines(events, new[] { Record("bad", @"C:/bad\uD83D"), Record("good", "C:/good") });
        File.WriteAllText(Path.Combine(spool, "current.json"), """
            {"schemaVersion":1,"harnessId":"dsh","sourceInstanceId":"dsh-default","sourceEpoch":"epoch",
             "updatedAtUtc":"2026-11-08T00:00:00Z","sessions":[{"sessionId":"bad","projectRoot":"C:/bad\uD83D"}]}
            """);

        await using (var adapter = new BridgeMonitorAdapter(Dsh, home.Root, pollInterval: TimeSpan.FromMilliseconds(20)))
        {
            var good = new TaskCompletionSource<SourceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var later = new TaskCompletionSource<SourceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.SnapshotAvailable += snapshot =>
            {
                if (snapshot.Tasks.Any(task => task.ConversationId == "good")) good.TrySetResult(snapshot);
                if (snapshot.Tasks.Any(task => task.ConversationId == "later")) later.TrySetResult(snapshot);
            };
            await adapter.StartAsync(CancellationToken.None);
            var initial = await good.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("good", Assert.Single(initial.Tasks).ConversationId);
            Assert.Contains("bridge-coverage-incomplete", initial.HealthReasons);
            File.AppendAllLines(events, new[] { Record("later", "C:/later") });
            Assert.Equal(2, (await later.Task.WaitAsync(TimeSpan.FromSeconds(5))).Tasks.Count);
        }

        await using var restarted = new BridgeMonitorAdapter(Dsh, home.Root);
        await restarted.StartAsync(new CancellationToken(canceled: true));
        Assert.Equal(2, restarted.Poll().Tasks.Count);
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
    public void ASameSizeFileReplacementIsDetectedInsteadOfBeingSkipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var events = Path.Combine(root, "events.jsonl");
            File.WriteAllText(events, "{\"a\":1}\n");
            var reader = new BridgeSpoolReader(root);
            Assert.Single(reader.ReadNewLines(out _, out _));

            // Replace the file in place with different content of exactly the same
            // length: a length-only check would see "nothing new" here.
            File.WriteAllText(events, "{\"b\":2}\n");
            var replaced = reader.ReadNewLines(out _, out _);

            Assert.Equal("{\"b\":2}", Assert.Single(replaced));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnUnchangedCurrentDocumentIsStillAppliedWithoutReparsing()
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = new SourceInstance(HarnessIds.Dsh, "dsh-default", "DeepSeek Harness");
            var document = "{\"schemaVersion\":1,\"harnessId\":\"dsh\",\"sourceInstanceId\":\"dsh-default\"," +
                           "\"sourceEpoch\":\"e\",\"updatedAtUtc\":\"2026-09-24T12:00:00Z\",\"sessions\":[]}";
            File.WriteAllText(Path.Combine(root, "current.json"), document);
            var reader = new BridgeSpoolReader(root);

            var first = reader.ReadCurrentState(source);
            var second = reader.ReadCurrentState(source);

            Assert.Equal(BridgeCurrentStateOutcome.Accepted, first.Outcome);
            Assert.Equal(BridgeCurrentStateOutcome.Accepted, second.Outcome);
            Assert.Same(first.Document, second.Document);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceBudget.Share(1, 1, 64));
        Assert.Equal(SourceBudget.DefaultAggregateBytesPerPass / 8, SourceBudget.Share(SourceBudget.DefaultAggregateBytesPerPass, 1024, 8).BytesPerPass);
        Assert.Equal(SourceBudget.DefaultAggregateBytesPerPass, SourceBudget.Share(SourceBudget.DefaultAggregateBytesPerPass, 1024, 1).BytesPerPass);
        Assert.Equal(1, SourceBudget.Share(SourceBudget.DefaultAggregateBytesPerPass, 8, 8).MaximumSessions);
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

    /// <summary>
    /// The composite carries typed per-source health, each with its own evidence time
    /// and identity, so a consumer never parses reason prefixes or borrows a peer's
    /// freshness.
    /// </summary>
    [Fact]
    public async Task SnapshotCarriesTypedPerSourceHealthWithItsOwnEvidenceTime()
    {
        var codex = new FakeAdapter(Codex);
        var dsh = new FakeAdapter(Dsh);
        await using var composite = new CompositeMonitorSource(new IAgentMonitorAdapter[] { codex, dsh }, NullMonitorLog.Instance);
        await composite.StartAsync(CancellationToken.None);

        var silentAt = Start.AddMinutes(-5);
        codex.Push(MonitoringHealthLevel.Healthy, true, new[] { BuildTask(HarnessIds.Codex, "codex", "conv-1", AgentTaskState.Working) }, observedAt: Start, evidenceAt: Start);
        dsh.Push(MonitoringHealthLevel.Unavailable, false, Array.Empty<AgentTask>(), observedAt: silentAt, evidenceAt: silentAt);

        var snapshot = composite.Current;
        Assert.Equal(2, snapshot.Sources.Count);
        // Deterministic order: harness, then instance identity.
        Assert.Equal(new[] { HarnessIds.Codex, HarnessIds.Dsh }, snapshot.Sources.Select(static source => source.HarnessId).ToArray());

        var codexHealth = snapshot.Sources[0];
        var dshHealth = snapshot.Sources[1];
        Assert.Equal("codex", codexHealth.SourceInstanceId);
        Assert.Equal(Start, codexHealth.EvidenceAt);
        Assert.True(codexHealth.IsReadable);

        Assert.Equal("dsh-default", dshHealth.SourceInstanceId);
        Assert.Equal(silentAt, dshHealth.EvidenceAt);
        Assert.False(dshHealth.IsReadable);
        Assert.False(dshHealth.CoverageComplete);

        // The aggregate evidence time is the newest readable source; the silent source
        // keeps its own older time and can never be presented as freshly read.
        Assert.Equal(Start, snapshot.Health.LastSuccessfulReadAt);
        Assert.NotEqual(snapshot.Health.LastSuccessfulReadAt, dshHealth.EvidenceAt);
    }
}
