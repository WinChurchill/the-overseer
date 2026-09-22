using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class VoicePackTests
{
    private const string ValidManifest = """
        {
          "id": "example",
          "name": "Example",
          "language": "en",
          "version": 1,
          "events": {
            "permission_required": ["permission.wav"],
            "input_required": ["input.wav", "input2.wav"],
            "task_error": ["error.wav"],
            "task_complete": ["complete.wav"],
            "all_tasks_complete": ["all.wav"]
          }
        }
        """;

    [Fact]
    public void ValidManifestParses()
    {
        var result = VoicePackValidator.ParseManifest(ValidManifest);
        Assert.True(result.IsValid, result.ReasonCode);
        Assert.Equal("example", result.Manifest!.Id);
        Assert.Equal(1, result.Manifest.Version);
        Assert.Equal(5, result.Manifest.Events.Count);
        Assert.Equal(2, result.Manifest.Events["input_required"].Count);
    }

    [Fact]
    public void UnknownEventKeysAreIgnoredButKnownOnesSurvive()
    {
        var manifest = ValidManifest.Replace("\"all_tasks_complete\": [\"all.wav\"]", "\"all_tasks_complete\": [\"all.wav\"], \"future_event\": [\"x.wav\"]");
        var result = VoicePackValidator.ParseManifest(manifest);
        Assert.True(result.IsValid, result.ReasonCode);
        Assert.False(result.Manifest!.Events.ContainsKey("future_event"));
        Assert.Equal(5, result.Manifest.Events.Count);
    }

    [Theory]
    [InlineData("{\"id\":\"example\",\"version\":2,\"events\":{\"task_error\":[\"a.wav\"]}}", "manifest-version")]
    [InlineData("{\"id\":\"example\",\"version\":1,\"events\":{}}", "manifest-no-known-events")]
    [InlineData("{\"id\":\"example\",\"events\":{}}", "manifest-version")]
    [InlineData("{\"version\":1,\"events\":{\"task_error\":[\"a.wav\"]}}", "manifest-id")]
    [InlineData("{\"id\":\"../escape\",\"version\":1,\"events\":{\"task_error\":[\"a.wav\"]}}", "manifest-id")]
    [InlineData("not json", "manifest-json")]
    [InlineData("[]", "manifest-shape")]
    public void InvalidManifestsAreRejected(string json, string expected)
    {
        var result = VoicePackValidator.ParseManifest(json);
        Assert.False(result.IsValid);
        Assert.Equal(expected, result.ReasonCode);
    }

    [Theory]
    [InlineData("/absolute.wav")]
    [InlineData("C:/drive.wav")]
    [InlineData("\\\\server\\share\\a.wav")]
    [InlineData("../escape.wav")]
    [InlineData("sub/../../escape.wav")]
    [InlineData("clip.wav:stream")]
    [InlineData("clip.mp3")]
    [InlineData("")]
    public void UnsafeClipNamesAreRejected(string clip)
    {
        Assert.False(VoicePackValidator.IsSafeRelativeClip(clip));

        var manifest = "{\"id\":\"example\",\"version\":1,\"events\":{\"task_error\":[\"" + clip.Replace("\\", "\\\\") + "\"]}}";
        var result = VoicePackValidator.ParseManifest(manifest);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ContainmentRejectsEscapes()
    {
        var root = Path.Combine(Path.GetTempPath(), "overseer-voice", "example");
        Assert.True(VoicePackValidator.IsContained(root, Path.Combine(root, "sub", "clip.wav")));
        Assert.False(VoicePackValidator.IsContained(root, Path.Combine(root, "..", "other", "clip.wav")));
        Assert.False(VoicePackValidator.IsContained(root, Path.Combine(Path.GetTempPath(), "elsewhere", "clip.wav")));
    }

    [Fact]
    public void SafePackIdRejectsTraversalAndSeparators()
    {
        Assert.Equal("example", VoicePackValidator.SafePackId("example"));
        Assert.Null(VoicePackValidator.SafePackId(".."));
        Assert.Null(VoicePackValidator.SafePackId("../x"));
        Assert.Null(VoicePackValidator.SafePackId("a/b"));
        Assert.Null(VoicePackValidator.SafePackId("a\\b"));
        Assert.Null(VoicePackValidator.SafePackId(new string('x', 40)));
    }

    [Fact]
    public void ToneWavPassesValidation()
    {
        var bytes = VoicePackValidator.CreateTone(440, 0.1);
        var result = VoicePackValidator.ValidateWav(bytes);
        Assert.True(result.IsValid, result.ReasonCode);
        Assert.Equal(22050, result.SampleRate);
        Assert.Equal(1, result.Channels);
        Assert.Equal(16, result.BitsPerSample);
        Assert.InRange(result.Duration.TotalSeconds, 0.05, 0.2);
    }

    [Fact]
    public void NonWavAndTruncatedPayloadsAreRejected()
    {
        Assert.Equal("wav-too-small", VoicePackValidator.ValidateWav(new byte[10]).ReasonCode);

        var tone = VoicePackValidator.CreateTone(440, 0.1);
        var notRiff = (byte[])tone.Clone();
        notRiff[0] = (byte)'X';
        Assert.Equal("wav-header", VoicePackValidator.ValidateWav(notRiff).ReasonCode);

        var truncated = tone.AsSpan(0, 40).ToArray();
        Assert.Equal("wav-too-small", VoicePackValidator.ValidateWav(truncated).ReasonCode);

        var badChunk = (byte[])tone.Clone();
        BitConverter.GetBytes(int.MaxValue).CopyTo(badChunk, 16);
        Assert.Equal("wav-chunk-length", VoicePackValidator.ValidateWav(badChunk).ReasonCode);
    }

    [Fact]
    public void UnsupportedFormatAndDurationAreRejected()
    {
        var tone = VoicePackValidator.CreateTone(440, 0.1);
        var badFormat = (byte[])tone.Clone();
        BitConverter.GetBytes((short)99).CopyTo(badFormat, 20);
        Assert.Equal("wav-format", VoicePackValidator.ValidateWav(badFormat).ReasonCode);

        var badRate = (byte[])tone.Clone();
        BitConverter.GetBytes(12345).CopyTo(badRate, 24);
        Assert.Equal("wav-parameters", VoicePackValidator.ValidateWav(badRate).ReasonCode);

        // A duration beyond the 15-second ceiling is refused by header math even
        // when the payload itself is short.
        var longTone = (byte[])tone.Clone();
        BitConverter.GetBytes(tone.Length * 400).CopyTo(longTone, 40);
        var result = VoicePackValidator.ValidateWav(longTone);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void OversizedPayloadIsRejectedBeforeParsing()
    {
        var bytes = new byte[VoicePackValidator.MaximumClipBytes + 1];
        Assert.Equal("wav-too-large", VoicePackValidator.ValidateWav(bytes).ReasonCode);
    }

    [Fact]
    public void NotificationTextIsShortAndNeverUsesTitles()
    {
        var candidate = new VoiceCandidate(
            VoiceEventKind.PermissionRequired,
            VoiceTestData.Key(),
            3,
            "Private project title",
            "turn-1",
            VoiceTestData.Start,
            "permission-requested",
            false,
            1,
            string.Empty);

        var request = VoiceNotificationFormatter.ToRequest(candidate);
        Assert.Equal("Task 3 needs your approval.", request.Text);
        Assert.Equal("permission_required", request.EventCode);
        Assert.Equal("Task 3", request.Task);
        Assert.DoesNotContain("Private", request.Text, StringComparison.Ordinal);
        Assert.True(request.Text.Length <= VoiceNotificationFormatter.MaximumCharacters);
    }

    [Fact]
    public void AggregateNotificationUsesCountsNotIdentifiers()
    {
        var aggregate = new VoiceCandidate(
            VoiceEventKind.AllTasksCompleted,
            default,
            0,
            "All tasks",
            string.Empty,
            VoiceTestData.Start,
            "all-tasks-completed",
            true,
            0,
            "episode:1");
        Assert.Equal("All tasks finished.", VoiceNotificationFormatter.Text(aggregate));

        var group = aggregate with { Kind = VoiceEventKind.TaskCompleted, TaskCount = 3, Alias = "3 tasks" };
        Assert.Equal("3 tasks finished.", VoiceNotificationFormatter.Text(group));
    }
}
