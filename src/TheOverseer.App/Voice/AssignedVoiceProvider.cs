using System.IO;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice;

/// <summary>
/// User-assigned local recordings override the selected provider, without uploading
/// audio. The status is the selected provider's own status until an assigned
/// recording is actually used for an event; after that the recording outcome speaks
/// for itself, so the HUD never claims a pack is ready while another provider is
/// selected.
/// </summary>
public sealed class AssignedVoiceProvider(string dataDirectory, Func<VoiceSettings> settings, IVoiceProvider fallback) : IVoiceProvider, IDisposable
{
    private VoiceProviderStatus? _recording;

    public string Id => fallback.Id;

    public VoiceProviderStatus Status
    {
        get
        {
            var selected = fallback.Status;
            return _recording is null ? selected : _recording with { FallbackCount = selected.FallbackCount };
        }
    }

    public async ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken)
    {
        if (settings().Clips.TryGetValue(VoiceNotificationFormatter.EventCode(candidate.Kind), out var name))
        {
            var clip = await VoiceRecordingStore.ReadAsync(dataDirectory, name, cancellationToken).ConfigureAwait(false);
            _recording = new VoiceProviderStatus("recording", clip is not null, clip is null ? "recording-invalid" : "recording");
            return clip;
        }

        _recording = null;
        return await fallback.TryCreateAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => (fallback as IDisposable)?.Dispose();
}

public static class VoiceRecordingStore
{
    public static async Task<string> ImportAsync(string dataDirectory, string source, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(source, cancellationToken).ConfigureAwait(false);
        if (!VoicePackValidator.ValidateWav(bytes).IsValid)
            throw new InvalidDataException("Choose a valid PCM WAV, at most 15 seconds and 4 MiB.");
        var folder = Path.Combine(dataDirectory, "voiceclips");
        Directory.CreateDirectory(folder);
        if (HasReparsePoint(folder)) throw new IOException("The recordings folder must be a regular local folder.");
        var name = Guid.NewGuid().ToString("N") + ".wav";
        await File.WriteAllBytesAsync(Path.Combine(folder, name), bytes, cancellationToken).ConfigureAwait(false);
        return name;
    }

    public static async Task<VoiceClip?> ReadAsync(string dataDirectory, string name, CancellationToken cancellationToken)
    {
        if (!VoiceSettings.IsSafeClipName(name)) return null;
        var path = Path.Combine(dataDirectory, "voiceclips", name);
        try
        {
            if (HasReparsePoint(path)) return null;
            var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
            return VoicePackValidator.ValidateWav(bytes).IsValid ? new VoiceClip(bytes, "recording", "recording") : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length > VoicePackValidator.MaximumClipBytes) throw new InvalidDataException("Recording exceeds 4 MiB.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static bool HasReparsePoint(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}
