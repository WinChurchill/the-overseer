using System.Text.Json;
using TheOverseer.App.Voice;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

/// <summary>
/// Plan 4 hardening regressions: completion bursts, wall-clock jumps, pack and
/// provider failures, and the shipped example pack. These cover spec section 18
/// paths that the P1-P3 suites did not exercise.
/// </summary>
public sealed class Plan4HardeningTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompletionBurstIsAggregatedIntoOneBoundedCue()
    {
        var policy = new VoiceEventPolicy();
        var tasks = new List<AgentTask>();
        var events = new List<AttentionEvent>();
        for (var index = 0; index < 50; index++)
        {
            var key = VoiceTestData.Key("01a0f000-0000-0000-0000-0000000000" + index.ToString("D2"));
            tasks.Add(VoiceTestData.Task(key: key, number: index + 1, state: AgentTaskState.Done));
            events.Add(VoiceTestData.Event(
                AttentionEventKind.TaskCompleted,
                key: key,
                number: index + 1,
                observedAt: Start,
                state: AgentTaskState.Done));
        }

        var snapshot = VoiceTestData.Snapshot(Start, tasks, events);
        Assert.Empty(policy.Observe(snapshot));

        var ready = policy.Pump(Start.AddSeconds(3));

        // One aggregate cue, bounded membership, and nothing left pending.
        var aggregate = Assert.Single(ready);
        Assert.True(aggregate.IsAggregate);
        Assert.True(aggregate.TaskCount <= VoicePolicyOptions.Default.MaximumGroupMembers, "count " + aggregate.TaskCount);
        Assert.True(aggregate.Members.Count <= VoicePolicyOptions.Default.MaximumGroupMembers, "members " + aggregate.Members.Count);
        Assert.Equal(aggregate.TaskCount, aggregate.Members.Count);
        Assert.Empty(policy.Pump(Start.AddSeconds(4)));
    }

    [Fact]
    public void CompletionBurstWithFullCoveragePrefersTheAllCompleteNotice()
    {
        var policy = new VoiceEventPolicy();
        var tasks = new List<AgentTask>();
        var events = new List<AttentionEvent>();
        for (var index = 0; index < 40; index++)
        {
            var key = VoiceTestData.Key("01a0f100-0000-0000-0000-0000000000" + index.ToString("D2"));
            tasks.Add(VoiceTestData.Task(key: key, number: index + 1, state: AgentTaskState.Done));
            events.Add(VoiceTestData.Event(
                AttentionEventKind.TaskCompleted,
                key: key,
                number: index + 1,
                observedAt: Start,
                state: AgentTaskState.Done));
        }

        events.Add(VoiceTestData.Event(AttentionEventKind.AllTasksCompleted, observedAt: Start, state: AgentTaskState.Done));
        var snapshot = VoiceTestData.Snapshot(Start, tasks, events, working: CountValue.Known(0), coverageComplete: true);

        Assert.Empty(policy.Observe(snapshot));
        var ready = policy.Pump(Start.AddSeconds(3));

        var single = Assert.Single(ready);
        Assert.Equal(VoiceEventKind.AllTasksCompleted, single.Kind);
        Assert.True(single.Members.Count <= 40);
    }

    [Fact]
    public void WallClockJumpForwardDoesNotRepeatAnAnnouncedCondition()
    {
        var policy = new VoiceEventPolicy();
        var key = VoiceTestData.Key();
        var task = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval);
        var attention = VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: Start);

        var first = policy.Observe(VoiceTestData.Snapshot(Start, new[] { task }, new[] { attention }));
        Assert.Single(first);

        // A jump of an hour (sleep, time sync, manual clock change) with the same
        // condition and the same episode must not produce a second cue.
        var jumped = Start.AddHours(1);
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(jumped, new[] { task }, new[] { attention })));
        Assert.Empty(policy.Pump(jumped));

        // A genuinely new episode after the jump is still announced exactly once.
        var second = VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval, turnId: "turn-2");
        var secondEvent = VoiceTestData.Event(
            AttentionEventKind.PermissionRequired,
            key: key,
            turnId: "turn-2",
            observedAt: jumped.AddSeconds(1));
        Assert.Single(policy.Observe(VoiceTestData.Snapshot(jumped.AddSeconds(1), new[] { second }, new[] { secondEvent })));
    }

    [Fact]
    public async Task MissingClipFileIsTypedAndRecoversWhenTheClipReturns()
    {
        using var root = new TempPackRoot();
        var pack = root.WritePack("example", "{\"id\":\"example\",\"version\":1,\"events\":{\"permission_required\":[\"clip.wav\"]}}");
        using var provider = new VoicePackProvider(root.Path, () => new VoiceSettings { PackId = "example" });

        Assert.Null(await provider.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.Equal("clip-missing", provider.Status.LastCode);

        File.WriteAllBytes(Path.Combine(pack, "clip.wav"), VoicePackValidator.CreateTone(660, 0.05));
        var clip = await provider.TryCreateAsync(Candidate(), CancellationToken.None);
        Assert.NotNull(clip);
        Assert.Equal("ok", provider.Status.LastCode);
    }

    [Fact]
    public async Task MalformedPcmClipIsRejectedByTheProvider()
    {
        using var root = new TempPackRoot();
        var pack = root.WritePack("example", "{\"id\":\"example\",\"version\":1,\"events\":{\"permission_required\":[\"clip.wav\"]}}");
        File.WriteAllBytes(Path.Combine(pack, "clip.wav"), new byte[512]);

        using var provider = new VoicePackProvider(root.Path, () => new VoiceSettings { PackId = "example" });
        Assert.Null(await provider.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.NotEqual("ok", provider.Status.LastCode);
    }

    [Fact]
    public async Task RemovedPackIsReportedAsMissingInsteadOfThrowing()
    {
        using var root = new TempPackRoot();
        root.WritePack("example", "{\"id\":\"example\",\"version\":1,\"events\":{\"permission_required\":[\"clip.wav\"]}}");
        Directory.Delete(Path.Combine(root.Path, VoicePackProvider.PackFolderName), recursive: true);

        using var provider = new VoicePackProvider(root.Path, () => new VoiceSettings { PackId = "example" });
        Assert.Null(await provider.TryCreateAsync(Candidate(), CancellationToken.None));
        Assert.Equal("pack-missing", provider.Status.LastCode);
    }

    [Fact]
    public void ShippedExamplePackCoversEveryKnownEventWithValidCues()
    {
        var packRoot = Path.Combine(RepositoryRoot(), "assets", "voicepacks", "example");
        Assert.True(Directory.Exists(packRoot), "missing shipped example pack: " + packRoot);

        var manifestPath = Path.Combine(packRoot, VoicePackProvider.ManifestFileName);
        var validation = VoicePackValidator.ParseManifest(File.ReadAllText(manifestPath));
        Assert.True(validation.IsValid, "invalid example manifest: " + validation.ReasonCode);
        var manifest = validation.Manifest!;
        Assert.Equal("example", manifest.Id);
        Assert.True(manifest.Version >= 1);

        foreach (var eventKey in VoicePackValidator.KnownEvents)
        {
            Assert.True(manifest.Events.TryGetValue(eventKey, out var clips), "missing event " + eventKey);
            Assert.NotEmpty(clips!);
            foreach (var clip in clips!)
            {
                Assert.True(VoicePackValidator.IsSafeRelativeClip(clip), "unsafe clip path " + clip);
                var full = Path.GetFullPath(Path.Combine(packRoot, clip.Replace('/', Path.DirectorySeparatorChar)));
                Assert.True(VoicePackValidator.IsContained(packRoot, full), "clip escaped the pack: " + clip);
                Assert.True(File.Exists(full), "missing shipped clip " + clip);
                var bytes = File.ReadAllBytes(full);
                Assert.True(bytes.Length <= VoicePackValidator.MaximumClipBytes);
                var wav = VoicePackValidator.ValidateWav(bytes);
                Assert.True(wav.IsValid, "invalid shipped clip " + clip + ": " + wav.ReasonCode);
                Assert.InRange(wav.Duration, TimeSpan.Zero, VoicePackValidator.MaximumClipDuration);
            }
        }
    }

    [Fact]
    public async Task ShippedExamplePackLoadsThroughTheProvider()
    {
        // The shipped pack must work through the same containment and validation
        // path an installed pack uses, copied into an isolated data directory.
        using var root = new TempPackRoot();
        var destination = Path.Combine(root.Path, VoicePackProvider.PackFolderName, "example");
        Directory.CreateDirectory(destination);
        var source = Path.Combine(RepositoryRoot(), "assets", "voicepacks", "example");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        using var provider = new VoicePackProvider(root.Path, () => new VoiceSettings { PackId = "example" });
        var clip = await provider.TryCreateAsync(Candidate(), CancellationToken.None);

        Assert.NotNull(clip);
        Assert.Equal("pack:example", clip!.SourceCode);
        Assert.True(VoicePackValidator.ValidateWav(clip.Wav).IsValid);
    }

    private static VoiceCandidate Candidate() => new(
        VoiceEventKind.PermissionRequired,
        VoiceTestData.Key(),
        1,
        "Task 1",
        "turn-1",
        DateTimeOffset.UtcNow,
        "cause",
        false,
        1,
        string.Empty);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TheOverseer.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
    }

    /// <summary>Isolated data directory holding <c>voicepacks</c> fixtures.</summary>
    private sealed class TempPackRoot : IDisposable
    {
        public TempPackRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, VoicePackProvider.PackFolderName));
        }

        public string Path { get; }

        public string WritePack(string packId, string manifestJson)
        {
            var pack = System.IO.Path.Combine(Path, VoicePackProvider.PackFolderName, packId);
            Directory.CreateDirectory(pack);
            File.WriteAllText(System.IO.Path.Combine(pack, VoicePackProvider.ManifestFileName), manifestJson);
            return pack;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
