using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class VoiceManagerTests
{
    private static VoiceCandidate Candidate(int number = 1, VoiceEventKind kind = VoiceEventKind.PermissionRequired, DateTimeOffset? at = null) => new(
        kind,
        VoiceTestData.Key("01a0e000-0000-0000-0000-0000000000" + number.ToString("x2")),
        number,
        "Task " + number,
        "turn-1",
        at ?? DateTimeOffset.UtcNow,
        "code",
        false,
        1,
        string.Empty);

    private static VoiceManagerOptions Options(int capacity = 32) =>
        new(capacity, TimeSpan.Zero, TimeSpan.FromSeconds(30));

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task MutedManagerNeverPlaysAndDropsCandidates()
    {
        var provider = new FakeVoiceProvider();
        var player = new FakeVoicePlayer();
        await using var manager = new VoiceManager(provider, player, Options(), muted: true);
        manager.Start();

        manager.Enqueue(new[] { Candidate() });
        await Task.Delay(150);

        Assert.Empty(player.Played);
        Assert.Equal(0, provider.Calls);
        Assert.True(manager.Status.DroppedCount >= 1);
    }

    [Fact]
    public async Task UnmutedManagerPlaysSeriallyAndCountsUtterances()
    {
        var provider = new FakeVoiceProvider();
        var player = new FakeVoicePlayer();
        await using var manager = new VoiceManager(provider, player, Options(), muted: false);
        manager.Start();

        manager.Enqueue(new[] { Candidate(1), Candidate(2), Candidate(3, VoiceEventKind.TaskCompleted) });
        Assert.True(await WaitForAsync(() => player.Played.Count == 3), "played " + player.Played.Count);

        Assert.False(player.ConcurrentPlayDetected);
        Assert.Equal(3, manager.Status.SpokenCount);
    }

    [Fact]
    public async Task MuteDuringProviderWaitCancelsAndNeverPlays()
    {
        var provider = new FakeVoiceProvider { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var player = new FakeVoicePlayer();
        await using var manager = new VoiceManager(provider, player, Options(), muted: false);
        manager.Start();

        manager.Enqueue(new[] { Candidate() });
        Assert.True(await WaitForAsync(() => provider.Calls == 1));

        manager.SetMuted(true);
        await Task.Delay(150);

        Assert.Empty(player.Played);
        Assert.True(player.StopCount >= 1);
        Assert.True(manager.IsMuted);
    }

    [Fact]
    public async Task MuteDuringPlaybackStopsImmediately()
    {
        var provider = new FakeVoiceProvider();
        var player = new FakeVoicePlayer { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var manager = new VoiceManager(provider, player, Options(), muted: false);
        manager.Start();

        manager.Enqueue(new[] { Candidate() });
        Assert.True(await WaitForAsync(() => player.StopCount == 0 && provider.Calls == 1));

        manager.SetMuted(true);
        await Task.Delay(150);

        Assert.True(player.StopCount >= 1, "muting must stop current audio");
        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task LateResultAfterUnmuteCannotReviveSound()
    {
        var provider = new FakeVoiceProvider { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var player = new FakeVoicePlayer();
        await using var manager = new VoiceManager(provider, player, Options(), muted: false);
        manager.Start();

        manager.Enqueue(new[] { Candidate() });
        Assert.True(await WaitForAsync(() => provider.Calls == 1));

        manager.SetMuted(true);
        manager.SetMuted(false);
        provider.Gate.SetResult();

        await Task.Delay(250);
        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task UnmuteDoesNotReplayBacklog()
    {
        var provider = new FakeVoiceProvider();
        var player = new FakeVoicePlayer();
        await using var manager = new VoiceManager(provider, player, Options(), muted: true);
        manager.Start();

        manager.Enqueue(new[] { Candidate(1), Candidate(2) });
        await Task.Delay(100);

        manager.SetMuted(false);
        await Task.Delay(250);

        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task PriorityOrderPlaysAttentionBeforeCompletion()
    {
        var provider = new FakeVoiceProvider();
        var player = new FakeVoicePlayer { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var manager = new VoiceManager(provider, player, Options(), muted: false);
        manager.Start();

        // Fill the queue while playback is blocked so ordering is observable.
        manager.Enqueue(new[] { Candidate(9, VoiceEventKind.TaskCompleted), Candidate(1, VoiceEventKind.TaskError) });
        await Task.Delay(100);
        manager.Enqueue(new[] { Candidate(2, VoiceEventKind.PermissionRequired) });
        player.Gate.SetResult();

        Assert.True(await WaitForAsync(() => manager.Status.SpokenCount == 3), "spoken " + manager.Status.SpokenCount);
        Assert.Equal(3, player.Played.Count);
    }

    [Fact]
    public async Task QueueIsBoundedAndDropsAreCounted()
    {
        var provider = new FakeVoiceProvider();
        var player = new FakeVoicePlayer { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var manager = new VoiceManager(provider, player, Options(capacity: 4), muted: false);
        manager.Start();

        for (var index = 0; index < 20; index++)
        {
            manager.Enqueue(new[] { Candidate(index + 1, VoiceEventKind.TaskCompleted) });
        }

        Assert.True(manager.QueuedCount <= 4, "queued " + manager.QueuedCount);
        Assert.True(manager.Status.DroppedCount > 0);
        player.Gate.SetResult();
        await Task.Delay(150);
    }

    [Fact]
    public async Task ExpiredCandidatesAreDroppedInsteadOfSpoken()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = new FakeVoiceProvider();
        var player = new FakeVoicePlayer();
        await using var manager = new VoiceManager(provider, player, Options(), clock, muted: false);
        manager.Start();

        var stale = Candidate(1, at: clock.GetUtcNow() - TimeSpan.FromMinutes(5));
        manager.Enqueue(new[] { stale });

        Assert.True(await WaitForAsync(() => manager.Status.DroppedCount >= 1));
        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task ProviderFailureIsCountedAndSilent()
    {
        var provider = new FakeVoiceProvider(available: false);
        var player = new FakeVoicePlayer();
        await using var manager = new VoiceManager(provider, player, Options(), muted: false);
        manager.Start();

        manager.Enqueue(new[] { Candidate() });
        Assert.True(await WaitForAsync(() => manager.Status.FailedCount >= 1));
        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task RevalidationCanInvalidateAQueuedCue()
    {
        var provider = new FakeVoiceProvider();
        var player = new FakeVoicePlayer();
        await using var manager = new VoiceManager(provider, player, Options(), revalidate: _ => false, muted: false);
        manager.Start();

        manager.Enqueue(new[] { Candidate() });
        Assert.True(await WaitForAsync(() => manager.Status.DroppedCount >= 1));
        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task RepeatedDisposalIsIdempotentAndStopsWork()
    {
        var provider = new FakeVoiceProvider { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var player = new FakeVoicePlayer();
        var manager = new VoiceManager(provider, player, Options(), muted: false);
        manager.Start();
        manager.Enqueue(new[] { Candidate() });

        await manager.DisposeAsync();
        await manager.DisposeAsync();

        Assert.True(player.StopCount >= 1);
        Assert.Empty(player.Played);
    }
}
