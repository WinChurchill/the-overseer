using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Monitoring.Parsing;
using TheOverseer.Core.Monitoring.Sessions;
using TheOverseer.Core.Monitoring.State;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class ReviewRegressionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(Records.BaseSeconds);
    private static RawCodexEvent Event(RawCodexEventKind kind, int order, string turn = "one", string? request = null, bool? blocking = null) =>
        RawCodexEvent.Unknown("codex", "task", Now) with
        {
            Kind = kind, TurnId = turn, SourceFileId = 1, SourceOrder = order,
            EventTime = Now, RequestId = request, IsBlocking = blocking,
        };

    private static AgentStateReducer Reducer()
    {
        var reducer = new AgentStateReducer(MonitorOptions.Default, new ManualTimeProvider(Now));
        reducer.RegisterConversation("codex", "task", ClientSurface.Desktop, "fixture", true, Now);
        reducer.SetTrackedKeys(new[] { new TaskKey("codex", "task") });
        return reducer;
    }

    private static SessionProfile Profile(SyntheticCodexHome home) =>
        new("codex", "Codex", home.Root, home.SessionsRoot, home.IndexPath, home.StateDatabasePath);

    [Fact]
    public void HistoricalHydrationAndTokenTrafficDoNotRenewTheWorkLease()
    {
        var reducer = Reducer();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, 1) with { IsHydration = true, EventTime = Now.AddHours(-2) });
        reducer.Apply(Event(RawCodexEventKind.TokenUsage, 2));
        var task = Assert.Single(reducer.Snapshot(Now));
        Assert.True(task.IsStale);
        Assert.Equal(Now.AddHours(-2), task.LastActivityAt);
    }

    [Fact]
    public void RootTimestampAndTurnMetadataSurviveNormalization()
    {
        var context = new ParseContext("codex", "task", 1, 1, 1, 0, true, Now, 10000);
        var line = "{\"timestamp\":\"2020-01-01T00:00:00Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\",\"internal_chat_message_metadata_passthrough\":{\"turn_id\":\"old\"}}}";
        var parsed = CodexEventParser.ParseLine(line, context).Event!;
        Assert.Equal(2020, parsed.EventTime!.Value.Year);
        Assert.Equal("old", parsed.TurnId);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("stream_error")]
    public void TransientErrorsDoNotTerminateATurn(string kind)
    {
        var context = new ParseContext("codex", "task", 1, 1, 1, 0, false, Now, 10000);
        Assert.Null(CodexEventParser.ParseLine("{\"type\":\"event_msg\",\"payload\":{\"type\":\"" + kind + "\",\"turn_id\":\"one\"}}", context).Event);
    }

    [Fact]
    public void UserResponseMessageDoesNotInventWork()
    {
        var context = new ParseContext("codex", "task", 1, 1, 1, 0, false, Now, 10000);
        var parsed = CodexEventParser.ParseLine("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\"}}", context).Event!;
        var reducer = Reducer();
        reducer.Apply(parsed);
        Assert.Equal(AgentTaskState.Idle, Assert.Single(reducer.Snapshot(Now)).State);
    }

    [Fact]
    public void ApprovalPrecedenceAndRemainingNonblockingRequestsSurviveResolution()
    {
        var reducer = Reducer();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, 1));
        reducer.Apply(Event(RawCodexEventKind.PermissionRequested, 2, request: "approval"));
        reducer.Apply(Event(RawCodexEventKind.InputRequested, 3, request: "blocking", blocking: true));
        reducer.Apply(Event(RawCodexEventKind.InputRequested, 4, request: "async-a", blocking: false));
        reducer.Apply(Event(RawCodexEventKind.InputRequested, 5, request: "async-b", blocking: false));
        reducer.Apply(Event(RawCodexEventKind.InputResolved, 6, request: "async-a"));
        Assert.Equal(AgentTaskState.NeedsApproval, Assert.Single(reducer.Snapshot(Now)).State);
        reducer.Apply(Event(RawCodexEventKind.PermissionResolved, 7, request: "approval"));
        Assert.Equal(AgentTaskState.NeedsInput, Assert.Single(reducer.Snapshot(Now)).State);
        reducer.Apply(Event(RawCodexEventKind.InputResolved, 8, request: "blocking"));
        var task = Assert.Single(reducer.Snapshot(Now));
        Assert.Equal(AgentTaskState.Working, task.State);
        Assert.True(task.NeedsHuman);
    }

    [Fact]
    public void NewTurnDropsOldRequestsButDuplicateStartDoesNotClearCurrentRequests()
    {
        var reducer = Reducer();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, 1));
        reducer.Apply(Event(RawCodexEventKind.PermissionRequested, 2, request: "approval"));
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, 3));
        Assert.Equal(AgentTaskState.NeedsApproval, Assert.Single(reducer.Snapshot(Now)).State);
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, 4, turn: "two"));
        var task = Assert.Single(reducer.Snapshot(Now));
        Assert.Equal(AgentTaskState.Working, task.State);
        Assert.False(task.NeedsHuman);
    }

    [Fact]
    public void UncorrelatedLateActivityCannotReviveTerminalWork()
    {
        var reducer = Reducer();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, 1));
        reducer.Apply(Event(RawCodexEventKind.TaskCompleted, 2));
        var result = reducer.Apply(Event(RawCodexEventKind.ItemActivity, 3, turn: "") with { Action = AgentActionKind.Thinking });
        Assert.False(result.Applied);
        Assert.Equal(AgentTaskState.Done, Assert.Single(reducer.Snapshot(Now)).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AggregateCompletionRequiresFullCoverageAndNoNewRunningTask(bool coverageComplete)
    {
        var reducer = Reducer();
        var detector = new AttentionDetector(MonitorOptions.Default);
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, 1));
        detector.Detect(reducer.Snapshot(Now), Array.Empty<TaskTransition>(), Now);
        detector.MarkHydrationComplete();
        var completed = reducer.Apply(Event(RawCodexEventKind.TaskCompleted, 2)).Transition!;
        if (coverageComplete)
        {
            reducer.RegisterConversation("codex", "other", ClientSurface.Desktop, "other", true, Now);
            reducer.Apply(Event(RawCodexEventKind.TaskStarted, 3) with { ConversationId = "other" });
            reducer.SetTrackedKeys(new[] { new TaskKey("codex", "task"), new TaskKey("codex", "other") });
        }
        var events = detector.Detect(reducer.Snapshot(Now), new[] { completed }, Now, coverageComplete);
        Assert.DoesNotContain(events, item => item.Kind == AttentionEventKind.AllTasksCompleted);
    }

    [Fact]
    public void AttentionResolvedWithinOneCycleDoesNotNotify()
    {
        var reducer = Reducer();
        reducer.Apply(Event(RawCodexEventKind.TaskStarted, 1));
        var raised = reducer.Apply(Event(RawCodexEventKind.PermissionRequested, 2, request: "approval")).Transition!;
        reducer.Apply(Event(RawCodexEventKind.PermissionResolved, 3, request: "approval"));
        var detector = new AttentionDetector(MonitorOptions.Default);
        Assert.Empty(detector.Detect(reducer.Snapshot(Now), new[] { raised }, Now));
    }

    [Fact]
    public void StartupPartialRecordCompletesOnAppendAndSmallAppendsDoNotRehydrate()
    {
        using var home = new SyntheticCodexHome();
        var path = home.WriteRollout("task", new[] { Records.SessionMeta("task"), Records.TaskStarted("one") });
        var completion = Records.TaskComplete("one");
        File.AppendAllText(path, completion[..30]);
        var clock = new ManualTimeProvider(Now);
        using var monitor = new CodexMonitor(MonitorOptions.Default, new[] { Profile(home) }, timeProvider: clock);
        Assert.Equal(AgentTaskState.Working, Assert.Single(monitor.Refresh().Tasks).State);
        File.AppendAllText(path, completion[30..] + "\n");
        clock.Advance(TimeSpan.FromSeconds(11));
        var snapshot = monitor.Refresh();
        Assert.Equal(AgentTaskState.Done, Assert.Single(snapshot.Tasks).State);
        Assert.Contains(snapshot.Transitions, item => item.Kind == AttentionEventKind.TaskCompleted);
        Assert.Equal(1, snapshot.Observation.FilesHydrated);
    }

    [Fact]
    public void LargeHydrationIsBoundedAcrossCyclesAndUnanchoredCoverageStaysIncomplete()
    {
        using var home = new SyntheticCodexHome();
        home.WriteRollout("task", new[] { Records.SessionMeta("task") }.Concat(Enumerable.Repeat(Records.TokenCount(), 4000)));
        var options = MonitorOptions.Default with { GlobalBytesPerPass = 64 * 1024, PerFileBytesPerPass = 16 * 1024, HydrationMaxScanBytes = 256 * 1024 };
        using var monitor = new CodexMonitor(options, new[] { Profile(home) }, timeProvider: new ManualTimeProvider(Now));
        long previous = 0;
        MonitoringSnapshot snapshot = monitor.Current;
        for (var index = 0; index < 35; index++)
        {
            snapshot = monitor.Refresh();
            Assert.InRange(snapshot.Observation.BytesRead - previous, 0, options.PerFileBytesPerPass);
            previous = snapshot.Observation.BytesRead;
        }
        Assert.Equal(1, snapshot.Observation.FilesHydrated);
        Assert.False(snapshot.Health.CoverageComplete);
        Assert.Contains(HealthReasons.HydrationBudget, snapshot.Health.Reasons);
        Assert.Empty(snapshot.Transitions);
    }

    [Fact]
    public void ReaderRecordLimitPreservesEveryLaterRecord()
    {
        using var home = new SyntheticCodexHome();
        var path = home.WriteRollout("task", Enumerable.Range(0, 60).Select(index => "{\"n\":" + index + "}"));
        var reader = new IncrementalJsonlReader();
        var lines = new List<string>();
        do
        {
            var read = reader.ReadAppended(path, 4096, maximumRecords: 7);
            Assert.InRange(read.Lines.Count, 0, 7);
            lines.AddRange(read.Lines);
        } while (reader.HasUnreadData);
        Assert.Equal(60, lines.Count);
        Assert.Equal(60, lines.Distinct().Count());
    }

    [Fact]
    public void DiscoveryContinuesAcrossReconciliationIntervals()
    {
        using var home = new SyntheticCodexHome();
        for (var index = 0; index < 180; index++)
            Directory.CreateDirectory(Path.Combine(home.SessionsRoot, "empty-" + index));
        home.WriteRollout("task", new[] { Records.SessionMeta("task"), Records.TaskComplete("one") }, datePath: "000-last");
        var clock = new ManualTimeProvider(Now);
        var options = MonitorOptions.Default with { DiscoveryEntriesPerSlice = 64, DiscoveryDirectoriesPerSlice = 8, ReconcileInterval = TimeSpan.FromSeconds(1) };
        using var monitor = new CodexMonitor(options, new[] { Profile(home) }, timeProvider: clock);
        for (var index = 0; index < 45; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            monitor.Refresh();
        }
        Assert.Contains(monitor.Current.Tasks, task => task.ConversationId == "task");
    }

    [Fact]
    public void FailedLogRotationDoesNotGrowTheActiveLog()
    {
        using var home = new SyntheticCodexHome();
        var path = Path.Combine(home.Root, "review.log");
        File.WriteAllText(path, new string('x', 64 * 1024));
        Directory.CreateDirectory(path + ".1");
        using var log = new LocalLog(path, 64 * 1024, MonitorLogLevel.Info);
        log.Write(MonitorLogLevel.Info, "review");
        Assert.True(log.IsDegraded);
        Assert.Equal(64 * 1024, new FileInfo(path).Length);
    }

    [Fact]
    public void ReplacementRebuildsSameTurnAndPreservesConversationNumber()
    {
        using var home = new SyntheticCodexHome();
        var path = home.WriteRollout("task", new[] { Records.SessionMeta("task"), Records.TaskStarted("one"), Records.TaskComplete("one") });
        var clock = new ManualTimeProvider(Now);
        using var monitor = new CodexMonitor(MonitorOptions.Default, new[] { Profile(home) }, timeProvider: clock);
        var before = Assert.Single(monitor.Refresh().Tasks);
        File.WriteAllLines(path, new[] { Records.SessionMeta("task"), Records.TaskStarted("one") });
        clock.Advance(TimeSpan.FromSeconds(11));
        monitor.Refresh();
        var after = Assert.Single(monitor.Refresh().Tasks);
        Assert.Equal(before.Number, after.Number);
        Assert.Equal(AgentTaskState.Working, after.State);
    }

    [Fact]
    public void CompletingProvisionalHeaderRekeysAndFiltersInternalIdentity()
    {
        using var home = new SyntheticCodexHome();
        var id = "01a0c600-0000-0000-0000-000000000099";
        var meta = Records.SessionMeta(id, subagent: true);
        var path = home.WriteRollout(id, Array.Empty<string>());
        File.WriteAllText(path, meta[..40]);
        using var monitor = new CodexMonitor(MonitorOptions.Default, new[] { Profile(home) }, timeProvider: new ManualTimeProvider(Now));
        Assert.True(Assert.Single(monitor.Refresh().Tasks).IsProvisional);
        File.AppendAllText(path, meta[40..] + "\n");
        Assert.Empty(monitor.Refresh().Tasks);
    }

    [Fact]
    public void MalformedEvidenceStaysPartialUntilAnExplicitLifecycleAnchor()
    {
        using var home = new SyntheticCodexHome();
        var path = home.WriteRollout("task", new[] { Records.SessionMeta("task"), Records.TaskStarted("one"), Records.Malformed() });
        using var monitor = new CodexMonitor(MonitorOptions.Default, new[] { Profile(home) }, timeProvider: new ManualTimeProvider(Now));
        monitor.Refresh();
        Assert.False(monitor.Refresh().Health.CoverageComplete);
        SyntheticCodexHome.Append(path, new[] { Records.TaskComplete("one") });
        Assert.Equal(AgentTaskState.Done, Assert.Single(monitor.Refresh().Tasks).State);
        Assert.True(monitor.Refresh().Health.CoverageComplete);
    }

    [Fact]
    public void LowGlobalBudgetCannotBeExceededByManyHydratingFiles()
    {
        using var home = new SyntheticCodexHome();
        for (var index = 0; index < 12; index++)
            home.WriteRollout("task-" + index, new[] { Records.SessionMeta("task-" + index), Records.TaskStarted("one") }.Concat(Enumerable.Repeat(Records.TokenCount(), 700)));
        var options = MonitorOptions.Default with { GlobalBytesPerPass = 64 * 1024, PerFileBytesPerPass = 16 * 1024 };
        var clock = new ManualTimeProvider(Now);
        using var monitor = new CodexMonitor(options, new[] { Profile(home) }, timeProvider: clock);
        long previous = 0;
        for (var index = 0; index < 25; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            var snapshot = monitor.Refresh();
            Assert.InRange(snapshot.Observation.BytesRead - previous, 0, options.GlobalBytesPerPass);
            previous = snapshot.Observation.BytesRead;
        }
        Assert.Equal(12, monitor.TrackedFileCount);
    }

    [Fact]
    public async Task WatcherTimeoutsCancelTheirWaitsAndHonorCallerCancellation()
    {
        using var home = new SyntheticCodexHome();
        using var tracker = new SessionChangeTracker(home.SessionsRoot, 16);
        for (var index = 0; index < 10; index++)
            Assert.False(await tracker.WaitForChangeAsync(TimeSpan.FromMilliseconds(1), TimeProvider.System, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracker.WaitForChangeAsync(TimeSpan.FromSeconds(1), TimeProvider.System, cancellation.Token));
    }

    [Fact]
    public void HydrationRecordCapCannotLeaveAuthoritativeStateFromAnEarlyPrefix()
    {
        using var home = new SyntheticCodexHome();
        home.WriteRollout("task", new[] { Records.SessionMeta("task"), Records.TaskStarted("one") }
            .Concat(Enumerable.Repeat(Records.TokenCount(), 1000)).Append(Records.TaskComplete("one")));
        var options = MonitorOptions.Default with { MaxHydrationRecords = 256 };
        using var monitor = new CodexMonitor(options, new[] { Profile(home) }, timeProvider: new ManualTimeProvider(Now));
        for (var index = 0; index < 8; index++) monitor.Refresh();
        Assert.False(monitor.Current.Health.CoverageComplete);
        Assert.True(Assert.Single(monitor.Current.Tasks).IsStateUncertain);
        Assert.False(Assert.Single(monitor.Current.Tasks).HasCurrentState);
        Assert.Equal(CountValue.Partial(0), monitor.Current.Counts.Working);
        Assert.Empty(monitor.Current.Transitions);
    }
}
