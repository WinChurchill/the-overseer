using System.IO;
using TheOverseer.App.Monitoring;
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

    private sealed class FakeAdapter : IAgentMonitorAdapter
    {
        public FakeAdapter(SourceInstance source) => Source = source;

        public SourceInstance Source { get; }

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
            IReadOnlyList<AttentionEvent>? transitions = null)
        {
            Current = new SourceSnapshot(
                Source,
                Start,
                level,
                level == MonitoringHealthLevel.Healthy ? Array.Empty<string>() : new[] { "source-degraded" },
                complete,
                tasks,
                transitions ?? Array.Empty<AttentionEvent>(),
                MonitorCapabilities.Initial,
                "fake");
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
        Assert.Single(snapshot.Transitions);
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

        dsh.Push(MonitoringHealthLevel.Healthy, true, new[] { BuildTask(HarnessIds.Dsh, "dsh-default", "conv-2", AgentTaskState.Done) });

        Assert.Contains(composite.Current.Transitions, transition => transition.Kind == AttentionEventKind.AllTasksCompleted);
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
}
