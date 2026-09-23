using System.Text.Json;
using TheOverseer.App.Diagnostics;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class Plan4ReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateProviderFailureCannotOverwriteSuspendStatus(bool throws)
    {
        var provider = new LateProvider(throws);
        await using var manager = new VoiceManager(provider, new FakeVoicePlayer(), muted: false);
        manager.Start();
        manager.Enqueue(new[] { new VoiceCandidate(VoiceEventKind.PermissionRequired, VoiceTestData.Key(), 1,
            "Task 1", "turn-1", DateTimeOffset.UtcNow, "test", false, 1, "") });
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        manager.DiscardPendingWork("suspended");
        provider.Release.SetResult();
        var deadline = Environment.TickCount64 + 3000;
        while (manager.Status.IsSpeaking && Environment.TickCount64 < deadline) await Task.Delay(10);
        Assert.False(manager.Status.IsSpeaking);
        Assert.Equal("suspended", manager.Status.LastOutcomeCode);
        Assert.Equal(0, manager.Status.FailedCount);
    }

    private sealed class LateProvider(bool throws) : IVoiceProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "late";
        public VoiceProviderStatus Status => new(Id, true, "unavailable");
        public async ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Release.Task;
            if (throws) throw new IOException("late failure");
            return null;
        }
    }

    [Fact]
    public async Task SnapshotArrivingAfterSuspendCannotQueueAudio()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var policy = new VoiceEventPolicy();
        var manager = new VoiceManager(new FakeVoiceProvider(), new FakeVoicePlayer(), timeProvider: clock, muted: false);
        await using var coordinator = new NotificationCoordinator(new ActivityHistory(), policy, manager, clock, new VoiceSettings { Muted = false });
        coordinator.OnSuspend();
        var now = clock.GetUtcNow();
        coordinator.OnSnapshot(VoiceTestData.Snapshot(now,
            new[] { VoiceTestData.Task(state: AgentTaskState.NeedsApproval) },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, observedAt: now) }));
        coordinator.Pump();
        Assert.Equal(0, manager.QueuedCount);
        Assert.False(coordinator.Settings.Muted);

        clock.Advance(TimeSpan.FromSeconds(1));
        coordinator.OnResume();
        // An in-progress cycle delivered after wake still contains an old event.
        var oldKey = VoiceTestData.Key("old");
        coordinator.OnSnapshot(VoiceTestData.Snapshot(clock.GetUtcNow(),
            new[] { VoiceTestData.Task(key: oldKey, state: AgentTaskState.NeedsApproval) },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: oldKey, observedAt: now) }));
        Assert.Equal(0, manager.QueuedCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        var freshKey = VoiceTestData.Key("fresh");
        coordinator.OnSnapshot(VoiceTestData.Snapshot(clock.GetUtcNow(),
            new[] { VoiceTestData.Task(key: freshKey, state: AgentTaskState.NeedsApproval) },
            new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: freshKey, observedAt: clock.GetUtcNow()) }));
        Assert.Equal(1, manager.QueuedCount);
    }

    [Fact]
    public void PackCheckRejectsMissingSecondVariant()
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        var pack = Path.Combine(root, "voicepacks", "example");
        Directory.CreateDirectory(pack);
        try
        {
            File.WriteAllBytes(Path.Combine(pack, "good.wav"), VoicePackValidator.CreateTone(440, 0.05));
            var events = Enum.GetValues<VoiceEventKind>().ToDictionary(
                kind => VoiceNotificationFormatter.EventCode(kind), kind => new[] { "good.wav" });
            events["permission_required"] = new[] { "good.wav", "missing.wav" };
            File.WriteAllText(Path.Combine(pack, "manifest.json"), JsonSerializer.Serialize(new { id = "example", version = 1, events }));
            File.WriteAllText(Path.Combine(root, "config.json"), "{\"voice\":{\"provider\":\"pack\",\"packId\":\"example\"}}");
            Assert.True(StartupArguments.TryParse(new[] { "--voice-pack-check", "--data-directory", root }, out var args, out _));
            Assert.Equal(1, VoicePackCheckRunner.Run(args));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
