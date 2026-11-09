using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;
using TheOverseer.App.Voice;

namespace TheOverseer.Tests;

public sealed class Plan3ReviewTests
{
    private static readonly DateTimeOffset Start = VoiceTestData.Start;

    [Fact]
    public void LaterHydrationNeverCreatesAnAttentionEvent()
    {
        var policy = new VoiceEventPolicy();
        policy.Observe(VoiceTestData.Snapshot(Start));
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(1),
            new[] { VoiceTestData.Task(state: AgentTaskState.NeedsApproval) })));
    }

    [Fact]
    public void DifferentEventTimestampDoesNotRepeatAnActiveCondition()
    {
        var policy = new VoiceEventPolicy();
        var tasks = new[] { VoiceTestData.Task(state: AgentTaskState.NeedsApproval) };
        Assert.Single(policy.Observe(VoiceTestData.Snapshot(Start, tasks,
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired) })));
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(12), tasks,
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, observedAt: Start.AddSeconds(12), code: "another-request") })));
    }

    [Fact]
    public void ResolvedCooldownCandidateNeverSpeaks()
    {
        var policy = new VoiceEventPolicy();
        var waiting = new[] { VoiceTestData.Task(state: AgentTaskState.NeedsApproval) };
        var working = new[] { VoiceTestData.Task() };
        policy.Observe(VoiceTestData.Snapshot(Start, waiting, new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired) }));
        policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(1), working));
        policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(2), waiting,
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, observedAt: Start.AddSeconds(2)) }));
        Assert.Equal(1, policy.PendingCount);
        policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(3), working));
        Assert.Empty(policy.Pump(Start.AddSeconds(12)));
    }

    [Fact]
    public void CompletionFromPreviousTurnDoesNotDescribeNewDoneTurn()
    {
        var policy = new VoiceEventPolicy();
        policy.Observe(VoiceTestData.Snapshot(Start,
            new[] { VoiceTestData.Task(state: AgentTaskState.Done) },
            new[] { VoiceTestData.Event(AttentionEventKind.TaskCompleted) }));
        policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(1),
            new[] { VoiceTestData.Task(state: AgentTaskState.Done, turnId: "turn-2") }));
        Assert.Empty(policy.Pump(Start.AddSeconds(2)));
    }

    [Fact]
    public void IncompleteCoverageInvalidatesGlobalEvenWithKnownZeroCount()
    {
        var policy = new VoiceEventPolicy();
        policy.Observe(VoiceTestData.Snapshot(Start,
            new[] { VoiceTestData.Task(state: AgentTaskState.Done) },
            new[] { VoiceTestData.Event(AttentionEventKind.AllTasksCompleted) }, coverageComplete: false));
        Assert.Empty(policy.Pump(Start.AddSeconds(2)));
    }

    [Fact]
    public async Task MutedCompletionCannotBecomeUnmuteBacklog()
    {
        var clock = new ManualTimeProvider(Start);
        var policy = new VoiceEventPolicy();
        var player = new FakeVoicePlayer();
        var manager = new VoiceManager(new FakeVoiceProvider(), player, timeProvider: clock);
        await using var coordinator = new NotificationCoordinator(new ActivityHistory(), policy, manager, clock, VoiceSettings.Default);
        coordinator.Start();
        coordinator.OnSnapshot(VoiceTestData.Snapshot(Start,
            new[] { VoiceTestData.Task(state: AgentTaskState.Done) },
            new[] { VoiceTestData.Event(AttentionEventKind.TaskCompleted) }));
        coordinator.SetMuted(false);
        clock.Advance(TimeSpan.FromSeconds(3));
        coordinator.Pump();
        await Task.Delay(100);
        Assert.Empty(player.Played);
        Assert.Equal(0, manager.QueuedCount);
    }

    [Fact]
    public async Task ConditionIsRevalidatedAfterProviderReturns()
    {
        var provider = new FakeVoiceProvider { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var player = new FakeVoicePlayer();
        var relevant = 1;
        await using var manager = new VoiceManager(provider, player, revalidate: _ => Volatile.Read(ref relevant) == 1, muted: false);
        manager.Start();
        manager.Enqueue(new[] { new VoiceCandidate(VoiceEventKind.PermissionRequired, VoiceTestData.Key(), 1, "Task 1", "turn-1", DateTimeOffset.UtcNow, "permission", false, 1, "") });
        for (var i = 0; i < 200 && provider.Calls == 0; i++) await Task.Delay(10);
        Assert.Equal(1, provider.Calls);
        Volatile.Write(ref relevant, 0);
        provider.Gate.SetResult();
        for (var i = 0; i < 200 && manager.Status.IsSpeaking; i++) await Task.Delay(10);
        Assert.Empty(player.Played);
    }

    [Fact]
    public void StoppedHistoryTrimsItsStoredBuckets()
    {
        var history = new ActivityHistory();
        history.Record(VoiceTestData.Snapshot(Start));
        Assert.Empty(history.Snapshot(Start.AddHours(2)).Samples);
        Assert.Equal(0, history.BucketCount);
    }

    [Theory]
    [InlineData(4, int.MaxValue)]
    [InlineData(4, 36)]
    [InlineData(28, 1)]
    [InlineData(32, 32766)]
    public void WavRejectsInconsistentLengthsAndRate(int offset, int value)
    {
        var bytes = VoicePackValidator.CreateTone(440, 1);
        if (offset == 32) BitConverter.GetBytes((short)value).CopyTo(bytes, offset);
        else BitConverter.GetBytes(value).CopyTo(bytes, offset);
        Assert.False(VoicePackValidator.ValidateWav(bytes).IsValid);
    }

    [Fact]
    public void StringManifestVersionIsInvalidWithoutThrowing()
    {
        Assert.False(VoicePackValidator.ParseManifest("{\"id\":\"example\",\"version\":\"1\",\"events\":{\"task_complete\":[\"ok.wav\"]}}").IsValid);
    }

    [Fact]
    public async Task LowerPriorityCannotEvictQueuedPermission()
    {
        var provider = new RecordingProvider();
        await using var manager = new VoiceManager(provider, new FakeVoicePlayer(),
            VoiceManagerOptions.Default with { QueueCapacity = 1, MinimumStartSpacing = TimeSpan.Zero }, muted: false);
        var permission = Candidate();
        manager.Enqueue(new[] { permission });
        manager.Enqueue(new[] { permission with { Kind = VoiceEventKind.TaskCompleted } });
        manager.Start();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(VoiceEventKind.PermissionRequired, provider.Candidate!.Kind);
        provider.Release.TrySetResult();
    }

    [Fact]
    public async Task ShutdownFencesAProviderThatIgnoresCancellation()
    {
        var provider = new RecordingProvider();
        var player = new FakeVoicePlayer();
        var manager = new VoiceManager(provider, player, muted: false);
        manager.Start();
        manager.Enqueue(new[] { Candidate() });
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var disposal = manager.DisposeAsync().AsTask();
        Assert.Same(disposal, manager.DisposeAsync().AsTask());
        provider.Release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task NativePlayerRejectsAlreadyCancelledPlaybackBeforeStartingAudio()
    {
        var player = new WavPlaybackService();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => player.PlayAsync(
            new VoiceClip(VoicePackValidator.CreateTone(440, .05), "test", "test"), new CancellationToken(true)).AsTask());
        Assert.False(player.DeviceUnavailable);
    }

    [Theory]
    [InlineData("..\\escape.wav")]
    [InlineData("audio/.. /escape.wav")]
    [InlineData("CON.wav")]
    [InlineData("COM1.wav")]
    public void WindowsPathAliasesCannotEscapeOrOpenDevices(string path) =>
        Assert.False(VoicePackValidator.IsSafeRelativeClip(path));

    [Fact]
    public void GroupMembersAreRevalidatedAfterAggregation()
    {
        var policy = new VoiceEventPolicy();
        var other = VoiceTestData.Key("other");
        policy.Observe(VoiceTestData.Snapshot(Start,
            new[] { VoiceTestData.Task(state: AgentTaskState.Done), VoiceTestData.Task(other, state: AgentTaskState.Done) },
            new[] { VoiceTestData.Event(AttentionEventKind.TaskCompleted), VoiceTestData.Event(AttentionEventKind.TaskCompleted, other) }));
        var group = Assert.Single(policy.Pump(Start.AddSeconds(2)));
        policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(3),
            new[] { VoiceTestData.Task(state: AgentTaskState.Done), VoiceTestData.Task(other, turnId: "turn-2") }));
        Assert.False(policy.IsRelevant(group, Start.AddSeconds(3)));
    }

    private static VoiceCandidate Candidate() => new(VoiceEventKind.PermissionRequired, VoiceTestData.Key(), 1,
        "Task 1", "turn-1", DateTimeOffset.UtcNow, "permission", false, 1, "");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PackRejectsDirectoryJunctionAtManifestOrClip(bool manifestLink)
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-review-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside");
        var pack = Path.Combine(root, "voicepacks", "example");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.GetDirectoryName(pack)!);
        var link = manifestLink ? pack : Path.Combine(pack, "audio");
        if (!manifestLink) Directory.CreateDirectory(pack);
        var manifest = "{\"id\":\"example\",\"version\":1,\"events\":{\"permission_required\":[\"" +
            (manifestLink ? "clip.wav" : "audio/clip.wav") + "\"]}}";
        File.WriteAllText(Path.Combine(manifestLink ? outside : pack, "manifest.json"), manifest);
        File.WriteAllBytes(Path.Combine(outside, "clip.wav"), VoicePackValidator.CreateTone(440, .05));
        try
        {
            await WindowsTestLinks.CreateJunctionAsync(link, outside);
            using var provider = new VoicePackProvider(root, () => new VoiceSettings { PackId = "example" });
            Assert.Null(await provider.TryCreateAsync(Candidate(), CancellationToken.None));
            Assert.Contains("reparse", provider.Status.LastCode, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(outside, "clip.wav")));
        }
        finally
        {
            // Remove only the created junction before recursive fixture cleanup.
            if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingProvider : IVoiceProvider
    {
        public string Id => "recording";
        public VoiceProviderStatus Status => new(Id, true, "ok");
        public VoiceCandidate? Candidate { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken)
        {
            Candidate = candidate;
            Entered.TrySetResult();
            await Release.Task;
            return new VoiceClip(VoicePackValidator.CreateTone(440, .05), "test", "test");
        }
    }
}
