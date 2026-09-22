using System.Text.Json;

namespace TheOverseer.Core.Voice;

/// <summary>Validated pack manifest (frozen specification §10 shape).</summary>
public sealed record VoicePackManifest(
    string Id,
    string Name,
    string Language,
    int Version,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Events);

public sealed record VoicePackValidation(bool IsValid, VoicePackManifest? Manifest, string ReasonCode);

public sealed record WavValidation(
    bool IsValid,
    string ReasonCode,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    TimeSpan Duration,
    int DataBytes);

/// <summary>
/// Pure validation for voice packs and WAV payloads. Nothing here reads files or
/// network: it decides whether already-bounded bytes are acceptable.
/// </summary>
public static class VoicePackValidator
{
    public const int MaximumManifestBytes = 64 * 1024;
    public const int MaximumClipsPerEvent = 16;
    public const int MaximumClipBytes = 4 * 1024 * 1024;
    public const int MaximumPackIdChars = 32;
    public static readonly TimeSpan MaximumClipDuration = TimeSpan.FromSeconds(15);
    public static readonly string[] KnownEvents =
    {
        "permission_required",
        "input_required",
        "task_error",
        "task_complete",
        "all_tasks_complete",
    };

    private static readonly HashSet<int> SupportedRates = new() { 8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000 };

    public static string? SafePackId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaximumPackIdChars)
        {
            return null;
        }

        var trimmed = id.Trim();
        if (trimmed is "." or "..")
        {
            return null;
        }

        foreach (var character in trimmed)
        {
            var allowed = char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
            if (!allowed)
            {
                return null;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Rejects absolute/UNC/drive-qualified, traversal, alternate-data-stream and
    /// non-.wav relative clip names.
    /// </summary>
    public static bool IsSafeRelativeClip(string? clip)
    {
        if (string.IsNullOrWhiteSpace(clip) || clip.Length > 200)
        {
            return false;
        }

        var value = clip.Trim();
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        if (value.StartsWith('/') || value.StartsWith('\\') || value.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        if (!value.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = value.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="candidate"/> stays inside <paramref name="root"/>.</summary>
    public static bool IsContained(string root, string candidate)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullCandidate = Path.GetFullPath(candidate);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            {
                fullRoot += Path.DirectorySeparatorChar;
            }

            return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static VoicePackValidation ParseManifest(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new VoicePackValidation(false, null, "manifest-empty");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
        {
            return new VoicePackValidation(false, null, "manifest-too-large");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new VoicePackValidation(false, null, "manifest-shape");
            }

            var id = SafePackId(GetString(root, "id"));
            if (id is null)
            {
                return new VoicePackValidation(false, null, "manifest-id");
            }

            var version = root.TryGetProperty("version", out var versionNode) && versionNode.TryGetInt32(out var parsedVersion)
                ? parsedVersion
                : 0;
            if (version != 1)
            {
                return new VoicePackValidation(false, null, "manifest-version");
            }

            if (!root.TryGetProperty("events", out var eventsNode) || eventsNode.ValueKind != JsonValueKind.Object)
            {
                return new VoicePackValidation(false, null, "manifest-events");
            }

            var events = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var property in eventsNode.EnumerateObject())
            {
                if (!KnownEvents.Contains(property.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    return new VoicePackValidation(false, null, "manifest-event-shape");
                }

                var clips = new List<string>();
                foreach (var clip in property.Value.EnumerateArray())
                {
                    var value = clip.ValueKind == JsonValueKind.String ? clip.GetString() : null;
                    if (!IsSafeRelativeClip(value))
                    {
                        return new VoicePackValidation(false, null, "manifest-clip-path");
                    }

                    clips.Add(value!.Trim());
                    if (clips.Count > MaximumClipsPerEvent)
                    {
                        return new VoicePackValidation(false, null, "manifest-clip-count");
                    }
                }

                if (clips.Count == 0)
                {
                    return new VoicePackValidation(false, null, "manifest-clip-empty");
                }

                events[property.Name] = clips;
            }

            if (events.Count == 0)
            {
                return new VoicePackValidation(false, null, "manifest-no-known-events");
            }

            return new VoicePackValidation(
                true,
                new VoicePackManifest(id, GetString(root, "name") ?? id, GetString(root, "language") ?? "en", version, events),
                "ok");
        }
        catch (JsonException)
        {
            return new VoicePackValidation(false, null, "manifest-json");
        }
    }

    /// <summary>
    /// Structural RIFF/WAVE validation: chunks, lengths, PCM/float format and a
    /// bounded duration. A .wav extension is never sufficient by itself.
    /// </summary>
    public static WavValidation ValidateWav(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44)
        {
            return new WavValidation(false, "wav-too-small", 0, 0, 0, TimeSpan.Zero, 0);
        }

        if (bytes.Length > MaximumClipBytes)
        {
            return new WavValidation(false, "wav-too-large", 0, 0, 0, TimeSpan.Zero, 0);
        }

        if (!Matches(bytes, 0, "RIFF") || !Matches(bytes, 8, "WAVE"))
        {
            return new WavValidation(false, "wav-header", 0, 0, 0, TimeSpan.Zero, 0);
        }

        var declaredRiff = ReadInt32(bytes, 4);
        if (declaredRiff < 0 || declaredRiff + 8 > bytes.Length)
        {
            return new WavValidation(false, "wav-length", 0, 0, 0, TimeSpan.Zero, 0);
        }

        var offset = 12;
        var format = -1;
        var channels = 0;
        var sampleRate = 0;
        var bitsPerSample = 0;
        var dataBytes = -1;
        var blockAlign = 0;

        while (offset + 8 <= bytes.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(bytes.Slice(offset, 4));
            var chunkSize = ReadInt32(bytes, offset + 4);
            var chunkStart = offset + 8;
            if (chunkSize < 0 || chunkStart + (long)chunkSize > bytes.Length)
            {
                return new WavValidation(false, "wav-chunk-length", 0, 0, 0, TimeSpan.Zero, 0);
            }

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                {
                    return new WavValidation(false, "wav-fmt-size", 0, 0, 0, TimeSpan.Zero, 0);
                }

                format = ReadInt16(bytes, chunkStart);
                channels = ReadInt16(bytes, chunkStart + 2);
                sampleRate = ReadInt32(bytes, chunkStart + 4);
                blockAlign = ReadInt16(bytes, chunkStart + 12);
                bitsPerSample = ReadInt16(bytes, chunkStart + 14);
            }
            else if (chunkId == "data")
            {
                dataBytes = chunkSize;
            }

            offset = chunkStart + chunkSize + (chunkSize % 2);
        }

        if (format is not (1 or 3))
        {
            return new WavValidation(false, "wav-format", 0, 0, 0, TimeSpan.Zero, 0);
        }

        if (channels is < 1 or > 2 || !SupportedRates.Contains(sampleRate) || bitsPerSample is not (8 or 16 or 24 or 32))
        {
            return new WavValidation(false, "wav-parameters", sampleRate, channels, bitsPerSample, TimeSpan.Zero, 0);
        }

        if (dataBytes <= 0 || blockAlign <= 0)
        {
            return new WavValidation(false, "wav-no-data", sampleRate, channels, bitsPerSample, TimeSpan.Zero, 0);
        }

        var seconds = dataBytes / (double)(sampleRate * blockAlign);
        var duration = TimeSpan.FromSeconds(seconds);
        if (duration > MaximumClipDuration)
        {
            return new WavValidation(false, "wav-duration", sampleRate, channels, bitsPerSample, duration, dataBytes);
        }

        return new WavValidation(true, "ok", sampleRate, channels, bitsPerSample, duration, dataBytes);
    }

    /// <summary>Writes a small PCM WAV tone. Used by tests and the voice fixture only.</summary>
    public static byte[] CreateTone(int frequencyHz, double seconds, int sampleRate = 22050, short amplitude = 6000)
    {
        var frames = Math.Max(1, (int)(sampleRate * Math.Clamp(seconds, 0.05, 15)));
        var dataBytes = frames * 2;
        var buffer = new byte[44 + dataBytes];
        WriteAscii(buffer, 0, "RIFF");
        WriteInt32(buffer, 4, 36 + dataBytes);
        WriteAscii(buffer, 8, "WAVE");
        WriteAscii(buffer, 12, "fmt ");
        WriteInt32(buffer, 16, 16);
        WriteInt16(buffer, 20, 1);
        WriteInt16(buffer, 22, 1);
        WriteInt32(buffer, 24, sampleRate);
        WriteInt32(buffer, 28, sampleRate * 2);
        WriteInt16(buffer, 32, 2);
        WriteInt16(buffer, 34, 16);
        WriteAscii(buffer, 36, "data");
        WriteInt32(buffer, 40, dataBytes);
        for (var index = 0; index < frames; index++)
        {
            var value = (short)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * index / sampleRate));
            WriteInt16(buffer, 44 + (index * 2), value);
        }

        return buffer;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private static bool Matches(ReadOnlySpan<byte> bytes, int offset, string value)
    {
        if (offset + value.Length > bytes.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (bytes[offset + index] != (byte)value[index])
            {
                return false;
            }
        }

        return true;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        offset + 4 <= bytes.Length ? BitConverter.ToInt32(bytes.Slice(offset, 4)) : 0;

    private static short ReadInt16(ReadOnlySpan<byte> bytes, int offset) =>
        offset + 2 <= bytes.Length ? BitConverter.ToInt16(bytes.Slice(offset, 2)) : (short)0;

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            buffer[offset + index] = (byte)value[index];
        }
    }

    private static void WriteInt32(byte[] buffer, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteInt16(byte[] buffer, int offset, short value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
}
