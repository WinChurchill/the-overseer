using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class MonitorTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly SyntheticCodexHome _home = new();
    private readonly ManualTimeProvider _clock = new(Start);

    public void Dispose() => _home.Dispose();

    private SessionProfile Profile => new("codex", "Codex", _home.Root, _home.SessionsRoot, _home.IndexPath, _home.StateDatabasePath);

    private CodexMonitor Create(MonitorOptions? options = null, IRuntimeActivitySource? activity = null, IMonitorLog? log = null) =>
        new(options ?? MonitorOptions.Default, new[] { Profile }, activity, log, _clock);

    private AgentTask Single(MonitoringSnapshot snapshot)
    {
        Assert.Single(snapshot.Tasks);
        return snapshot.Tasks[0];
    }

    [Fact]
    public void HydratesUserConversationAndExcludesExplicitInternalRollouts()
    {
        _home.WriteRollout(
            "01a0c700-0000-0000-0000-000000000001",
            new[]
            {
                Records.SessionMeta("01a0c700-0000-0000-0000-000000000001", cwd: @"D:\work\overseer"),
                Records.TaskStarted("turn-1"),
                Records.ItemCompleted("turn-1", "CommandExecution", command: "dotnet test"),
            });

        _home.WriteRollout(
            "01a0c700-0000-0000-0000-000000000002",
            new[]
            {
                Records.SessionMeta("01a0c700-0000-0000-0000-000000000002", threadSource: "guardian_review", subagent: true),
                Records.TaskStarted("turn-g"),
                Records.TaskComplete("turn-g"),
            });

        using var monitor = Create();
        var snapshot = monitor.Refresh();

        var task = Single(snapshot);
        Assert.Equal("01a0c700-0000-0000-0000-000000000001", task.ConversationId);
        Assert.Equal(AgentTaskState.Working, task.State);
        Assert.Equal(AgentActionKind.RunningTests, task.Action.Kind);
        Assert.Equal(ClientSurface.Desktop, task.Surface);
        Assert.Equal(1, snapshot.Counts.Working.Value);
        Assert.Equal(1, snapshot.Counts.Total.Value);
    }

    [Fact]
    public void ContinuationFilesOfOneConversationCollapseToOneNumberedTask()
    {
        var id = "01a0c701-0000-0000-0000-000000000003";
        var older = _home.WriteRollout(
            id,
            new[]
            {
                Records.SessionMeta(id),
                Records.TaskStarted("turn-1", 1_767_323_000),
                Records.TaskComplete("turn-1", 1_767_323_100),
            },
            stamp: "2026-01-02T03-00-00");

        var newer = _home.WriteRollout(
            id,
            new[]
            {
                Records.SessionMeta(id),
                Records.TaskStarted("turn-2", 1_767_324_000),
            },
            fileNameSuffix: "01a0c702-0000-0000-0000-000000000004",
            stamp: "2026-01-02T03-10-00");

        using var monitor = Create();
        var snapshot = monitor.Refresh();

        var task = Single(snapshot);
        Assert.Equal(1, task.Number);
        Assert.Equal("turn-2", task.TurnId);
        Assert.Equal(AgentTaskState.Working, task.State);
        Assert.Equal(2, task.SourceFileCount);
        Assert.Equal(2, monitor.TrackedFileCount);
        Assert.True(File.Exists(older) && File.Exists(newer));
    }

    [Fact]
    public void LiveTransitionRaisesAttentionAfterHydrationBaseline()
    {
        var path = _home.WriteRollout(
            "01a0c703-0000-0000-0000-000000000005",
            new[] { Records.SessionMeta("01a0c703-0000-0000-0000-000000000005"), Records.TaskStarted("turn-1") });

        using var monitor = Create();
        var hydrated = monitor.Refresh();
        Assert.Empty(hydrated.Transitions);
        Assert.Equal(AgentTaskState.Working, Single(hydrated).State);

        SyntheticCodexHome.Append(path, new[] { Records.TaskComplete("turn-1") });
        var live = monitor.Refresh();

        Assert.Equal(AgentTaskState.Done, Single(live).State);
        Assert.Contains(live.Transitions, item => item.Kind == AttentionEventKind.TaskCompleted);
    }

    [Fact]
    public void OfficialTitleBecomesLabelAndWorkspaceLeafIsTheFallback()
    {
        var id = "01a0c704-0000-0000-0000-000000000006";
        var other = "01a0c705-0000-0000-0000-000000000007";
        _home.WriteRollout(id, new[] { Records.SessionMeta(id, cwd: @"D:\work\titled"), Records.TaskStarted("turn-1") });
        _home.WriteRollout(other, new[] { Records.SessionMeta(other, cwd: @"D:\work\untitled"), Records.TaskStarted("turn-1") });
        _home.WriteIndex(new[] { (id, "Aurora") });

        using var monitor = Create();
        var snapshot = monitor.Refresh();

        Assert.Contains(snapshot.Tasks, task => task.ConversationId == id && task.Label == "Aurora");
        Assert.Contains(snapshot.Tasks, task => task.ConversationId == other && task.Label == "untitled");
    }

    [Fact]
    public void MissingRootIsUnavailableAndCountsAreUnknownNotZero()
    {
        var missing = Profile with { SessionsRoot = Path.Combine(_home.Root, "missing-sessions"), RootPath = Path.Combine(_home.Root, "missing-root") };
        using var monitor = new CodexMonitor(MonitorOptions.Default, new[] { missing }, null, null, _clock);

        var snapshot = monitor.Refresh();
        Assert.Equal(MonitoringHealthLevel.Unavailable, snapshot.Health.Level);
        Assert.Contains(HealthReasons.RootMissing, snapshot.Health.Reasons);
        Assert.False(snapshot.Counts.Working.IsKnown);
        Assert.False(snapshot.Counts.NeedsHuman.IsKnown);
        Assert.Equal("working unknown \u00b7 needs you unknown", snapshot.Counts.ToSummary());
    }

    [Fact]
    public void MalformedRecordsDegradeHealthWithoutHidingTheTask()
    {
        var id = "01a0c706-0000-0000-0000-000000000008";
        _home.WriteRollout(
            id,
            new[]
            {
                Records.SessionMeta(id),
                Records.TaskStarted("turn-1"),
                Records.Malformed(),
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"future_thing\"}}",
            });

        using var monitor = Create();
        var snapshot = monitor.Refresh();

        Assert.Equal(MonitoringHealthLevel.Degraded, snapshot.Health.Level);
        Assert.Contains(HealthReasons.FileMalformed, snapshot.Health.Reasons);
        Assert.Equal(AgentTaskState.Working, Single(snapshot).State);
        Assert.Equal(1, snapshot.Observation.MalformedRecords);
    }

    [Fact]
    public void LockedRolloutIsTypedAndRecoversOnReconcile()
    {
        var id = "01a0c707-0000-0000-0000-000000000009";
        var path = _home.WriteRollout(id, new[] { Records.SessionMeta(id), Records.TaskStarted("turn-1") });

        using var monitor = Create();
        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var snapshot = monitor.Refresh();
            Assert.Contains(HealthReasons.FileLocked, snapshot.Health.Reasons);
            Assert.Empty(snapshot.Tasks);
            Assert.NotEqual(MonitoringHealthLevel.Healthy, snapshot.Health.Level);
        }

        _clock.Advance(TimeSpan.FromSeconds(11));
        var recovered = monitor.Refresh();
        Assert.Single(recovered.Tasks);
        Assert.Equal(AgentTaskState.Working, Single(recovered).State);
    }

    [Fact]
    public void SameSizeReplacementIsDetectedAndRehydrated()
    {
        var id = "01a0c708-0000-0000-0000-00000000000a";
        var path = _home.WriteRollout(id, new[] { Records.SessionMeta(id), Records.TaskStarted("turn-1"), Records.TaskComplete("turn-1") });

        using var monitor = Create();
        Assert.Equal(AgentTaskState.Done, Single(monitor.Refresh()).State);

        // Same-length replacement: identical byte count, different content.
        var original = File.ReadAllText(path);
        var replacement = original.Replace("turn-1", "turn-9");
        Assert.Equal(original.Length, replacement.Length);
        File.WriteAllText(path, replacement);
        _clock.Advance(TimeSpan.FromSeconds(11));

        var replaced = monitor.Refresh();
        Assert.True(
            replaced.Health.Reasons.Contains(HealthReasons.FileReplaced) || replaced.Health.Reasons.Contains(HealthReasons.FileTruncated),
            "replacement must be reported, reasons: " + replaced.Health.ReasonsDisplay);

        var rehydrated = monitor.Refresh();
        Assert.Equal("turn-9", Single(rehydrated).TurnId);
        Assert.Equal(AgentTaskState.Done, Single(rehydrated).State);
    }

    [Fact]
    public void TruncationOfATrackedFileIsDetectedAndRecovered()
    {
        var id = "01a0c711-0000-0000-0000-000000000012";
        var path = _home.WriteRollout(
            id,
            new[] { Records.SessionMeta(id), Records.TaskStarted("turn-1"), Records.TokenCount() });

        using var monitor = Create();
        Assert.Equal(AgentTaskState.Working, Single(monitor.Refresh()).State);

        // The tracked file loses data and is replaced by a shorter rollout.
        File.WriteAllLines(path, new[] { Records.SessionMeta(id), Records.TaskStarted("turn-2") });
        var truncated = monitor.Refresh();
        Assert.Contains(HealthReasons.FileTruncated, truncated.Health.Reasons);

        var recovered = monitor.Refresh();
        Assert.Equal("turn-2", Single(recovered).TurnId);
        Assert.Equal(AgentTaskState.Working, Single(recovered).State);
        Assert.True(recovered.Counts.Working.IsKnown);
    }

    [Fact]
    public void StaleTasksKeepLastKnownStateAndMakeCountsPartial()
    {
        var id = "01a0c709-0000-0000-0000-00000000000b";
        _home.WriteRollout(id, new[] { Records.SessionMeta(id), Records.TaskStarted("turn-1") });

        using var monitor = Create(MonitorOptions.Default with { StaleLease = TimeSpan.FromMinutes(5) });
        Assert.Equal(1, Single(monitor.Refresh()).Number);

        _clock.Advance(TimeSpan.FromMinutes(30));
        var snapshot = monitor.Refresh();

        Assert.Contains(HealthReasons.StaleTasks, snapshot.Health.Reasons);
        Assert.True(Single(snapshot).IsStale);
        Assert.Equal(AgentTaskState.Working, Single(snapshot).State);
        Assert.False(snapshot.Counts.Working.IsComplete);
        Assert.False(snapshot.Health.CoverageComplete);
    }

    [Fact]
    public void TrackedFileCapIsEnforcedAndReported()
    {
        for (var index = 0; index < 5; index++)
        {
            var id = "01a0c70a-0000-0000-0000-00000000001" + index;
            _home.WriteRollout(id, new[] { Records.SessionMeta(id), Records.TaskStarted("turn-1") });
        }

        using var monitor = Create(MonitorOptions.Default with { MaxTrackedFiles = 2 });
        var snapshot = monitor.Refresh();

        Assert.True(monitor.TrackedFileCount <= 2);
        Assert.Contains(HealthReasons.TrackedFileLimit, snapshot.Health.Reasons);
        Assert.False(snapshot.Counts.Working.IsComplete);
    }

    [Fact]
    public void OptionalRuntimeHintLocatesCandidateAndRejectsPathsOutsideRoot()
    {
        var id = "01a0c70b-0000-0000-0000-00000000000c";
        var outsideRoot = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"), "rollout-x.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideRoot)!);
        File.WriteAllLines(outsideRoot, new[] { Records.SessionMeta(id), Records.TaskStarted("turn-1") });

        var sideRoot = Path.Combine(_home.SessionsRoot, ".hint-only");
        Directory.CreateDirectory(sideRoot);
        var hintedPath = Path.Combine(sideRoot, "rollout-2026-01-02T03-04-05-" + id + ".jsonl");
        File.WriteAllLines(hintedPath, new[] { Records.SessionMeta(id), Records.TaskStarted("turn-1") });

        var source = new StubActivitySource(new[]
        {
            new RuntimeActivityHint("codex", "outside", outsideRoot, Start),
            new RuntimeActivityHint("codex", id, hintedPath, Start),
        });

        using var monitor = Create(activity: source);
        var snapshot = monitor.Refresh();

        Assert.Single(snapshot.Tasks);
        Assert.Equal(id, snapshot.Tasks[0].ConversationId);
        Assert.Equal(CapabilitySupport.LiveObserved, snapshot.Capabilities.RuntimeActivitySource.Support);
        Assert.DoesNotContain(snapshot.Tasks, task => task.ConversationId == "outside");

        try
        {
            Directory.Delete(Path.GetDirectoryName(outsideRoot)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RestartRebuildsStateFromBoundedHydration()
    {
        var id = "01a0c70c-0000-0000-0000-00000000000d";
        _home.WriteRollout(id, new[] { Records.SessionMeta(id), Records.TaskStarted("turn-1"), Records.ItemCompleted("turn-1", "FileChange") });

        using (var first = Create())
        {
            var snapshot = first.Refresh();
            Assert.Equal(1, Single(snapshot).Number);
        }

        using var second = Create();
        var rebuilt = second.Refresh();
        var task = Single(rebuilt);
        Assert.Equal(1, task.Number);
        Assert.Equal(AgentTaskState.Working, task.State);
        Assert.Equal(AgentActionKind.EditingFiles, task.Action.Kind);
    }

    [Fact]
    public void RootAppearingLaterIsRecoveredWithCompleteCounts()
    {
        var missingSessions = Path.Combine(_home.Root, "late-sessions");
        var profile = Profile with { SessionsRoot = missingSessions };
        using var monitor = new CodexMonitor(MonitorOptions.Default, new[] { profile }, null, null, _clock);

        Assert.Equal(MonitoringHealthLevel.Unavailable, monitor.Refresh().Health.Level);

        var id = "01a0c70d-0000-0000-0000-00000000000e";
        var directory = Path.Combine(missingSessions, "2026", "01", "02");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(
            Path.Combine(directory, "rollout-2026-01-02T03-04-05-" + id + ".jsonl"),
            new[] { Records.SessionMeta(id), Records.TaskStarted("turn-1") });

        _clock.Advance(TimeSpan.FromSeconds(11));
        var recovered = monitor.Refresh();
        Assert.Equal(id, Single(recovered).ConversationId);
        Assert.True(recovered.Counts.Working.IsKnown);
        Assert.NotEqual(MonitoringHealthLevel.Unavailable, recovered.Health.Level);
    }

    [Fact]
    public void BackgroundLoopPublishesSnapshotsAndStopsCleanly()
    {
        var id = "01a0c70e-0000-0000-0000-00000000000f";
        _home.WriteRollout(id, new[] { Records.SessionMeta(id), Records.TaskStarted("turn-1") });

        using var monitor = Create(MonitorOptions.Default with { KnownFilePollInterval = TimeSpan.FromSeconds(1) });
        var seen = 0;
        monitor.SnapshotAvailable += _ => Interlocked.Increment(ref seen);

        var loop = monitor.StartAsync();
        for (var index = 0; index < 20 && Volatile.Read(ref seen) == 0; index++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Thread.Sleep(20);
        }

        Assert.True(Volatile.Read(ref seen) > 0, "background loop must publish a snapshot");
        Assert.Equal(id, monitor.Current.Tasks[0].ConversationId);
    }

    [Fact]
    public void ObservationCountersReflectActualRecords()
    {
        var id = "01a0c70f-0000-0000-0000-000000000010";
        _home.WriteRollout(
            id,
            new[]
            {
                Records.SessionMeta(id),
                Records.TaskStarted("turn-1"),
                Records.ItemCompleted("turn-1", "CommandExecution", command: "dotnet build"),
                Records.CustomToolCall("turn-1", "call-1"),
                Records.CustomToolCallOutput("turn-1", "call-1"),
                Records.TokenCount(),
            });

        using var monitor = Create();
        var snapshot = monitor.Refresh();

        Assert.Equal(1, snapshot.Observation.SessionMetaRecords);
        Assert.Equal(1, snapshot.Observation.TaskStartedRecords);
        Assert.Equal(1, snapshot.Observation.ItemCompletedRecords);
        Assert.Equal(2, snapshot.Observation.ToolCallRecords);
        Assert.Equal(0, snapshot.Observation.PermissionRequestRecords);
        Assert.Equal(0, snapshot.Observation.InputRequestRecords);
        Assert.Equal(CapabilitySupport.LiveObserved, snapshot.Capabilities.LifecycleEvents.Support);
        Assert.Equal(CapabilitySupport.Unsupported, snapshot.Capabilities.PermissionRequests.Support);
        Assert.Equal(CapabilitySupport.NotExercised, snapshot.Capabilities.AttentionLiveVerification.Support);
    }

    [Fact]
    public void SyntheticStructuredAttentionDrivesStateAndCapabilityHonestly()
    {
        var id = "01a0c710-0000-0000-0000-000000000011";
        _home.WriteRollout(
            id,
            new[]
            {
                Records.SessionMeta(id),
                Records.TaskStarted("turn-1"),
                Records.PermissionRequest("turn-1", "req-1"),
            });

        using var monitor = Create();
        var snapshot = monitor.Refresh();

        Assert.Equal(AgentTaskState.NeedsApproval, Single(snapshot).State);
        Assert.True(Single(snapshot).NeedsHuman);
        Assert.Equal(1, snapshot.Counts.NeedsHuman.Value);
        Assert.Equal(1, snapshot.Observation.PermissionRequestRecords);
        Assert.Equal(AgentActionKind.WaitingForApproval, Single(snapshot).Action.Kind);
    }

    [Fact]
    public async Task DisposalIsIdempotentAndCancelsTheLoop()
    {
        var monitor = Create();
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();
    }

    private sealed class StubActivitySource : IRuntimeActivitySource
    {
        private readonly IReadOnlyList<RuntimeActivityHint> _hints;

        public StubActivitySource(IReadOnlyList<RuntimeActivityHint> hints) => _hints = hints;

        public RuntimeActivityResult Read(SessionProfile profile, DateTimeOffset cutoff, int maximumRows) =>
            new(RuntimeActivityAvailability.Available, _hints, "ok");
    }
}
