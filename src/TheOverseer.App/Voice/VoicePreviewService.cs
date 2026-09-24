using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice;

/// <summary>Outcome of one explicit preview. Codes are bounded provider codes, never text.</summary>
public sealed record VoicePreviewOutcome(bool ClipCreated, bool Played, string Code, int ByteCount)
{
    public static VoicePreviewOutcome Unavailable(string code) => new(false, false, code, 0);
}

/// <summary>
/// One explicit preview of a single catalog situation: the assigned recording when
/// present, otherwise exactly the graph normal notification playback uses, so a
/// preview can never speak a provider the user did not select. The candidate carries
/// the catalog kind only; no task name, path or transcript ever reaches synthesis.
/// </summary>
public sealed class VoicePreviewService(
    string dataDirectory,
    Func<VoiceSettings> settings,
    IVoicePlayer player,
    VoiceProviderBuilders? builders = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>
    /// The only candidate a preview ever uses: the catalog kind plus a fixed label.
    /// No task name, path, command or transcript can enter it.
    /// </summary>
    public static VoiceCandidate Candidate(VoiceEventKind kind, TimeProvider? timeProvider = null) => new(
        kind,
        new TaskKey("preview", "preview"),
        1,
        "Task 1",
        "preview",
        (timeProvider ?? TimeProvider.System).GetUtcNow(),
        "preview",
        kind == VoiceEventKind.AllTasksCompleted,
        1,
        "preview");

    /// <summary>Creates and plays one preview clip; never plays when no clip exists.</summary>
    public async Task<VoicePreviewOutcome> PreviewAsync(VoiceEventKind kind, CancellationToken cancellationToken)
    {
        var candidate = Candidate(kind, timeProvider);
        using var provider = VoiceProviderFactory.Create(dataDirectory, settings, builders);
        var clip = await provider.TryCreateAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (clip is null)
        {
            return VoicePreviewOutcome.Unavailable(provider.Status.LastCode);
        }

        var played = await player.PlayAsync(clip, cancellationToken).ConfigureAwait(false);
        return new VoicePreviewOutcome(true, played, clip.DetailCode, clip.ByteCount);
    }
}
