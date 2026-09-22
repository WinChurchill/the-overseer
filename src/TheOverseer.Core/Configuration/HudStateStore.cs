using System.Text.Json;

namespace TheOverseer.Core.Configuration;

public enum HudStateLoadStatus
{
    Loaded,
    Missing,
    Recovered,
    Failed,
}

public sealed record HudStateLoadResult(HudWindowState State, HudStateLoadStatus Status, string ReasonCode);

/// <summary>
/// Bounded JSON persistence for placement/expansion state. Reads are capped,
/// corruption falls back to defaults without deleting the file, and writes are
/// serialized through a temporary file in the same directory.
/// </summary>
public static class HudStateStore
{
    public const string FileName = "state.json";
    public const int MaximumBytes = 16 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static HudStateLoadResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new HudStateLoadResult(HudWindowState.Default, HudStateLoadStatus.Missing, "state-missing");
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes)
            {
                return new HudStateLoadResult(HudWindowState.Default, HudStateLoadStatus.Failed, "state-too-large");
            }

            using var reader = new StreamReader(stream);
            var buffer = new char[MaximumBytes + 1];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count == buffer.Length)
            {
                return new HudStateLoadResult(HudWindowState.Default, HudStateLoadStatus.Failed, "state-too-large");
            }

            var text = new string(buffer, 0, count);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new HudStateLoadResult(HudWindowState.Default, HudStateLoadStatus.Missing, "state-empty");
            }

            var state = JsonSerializer.Deserialize<HudWindowState>(text, SerializerOptions);
            if (state is null)
            {
                return new HudStateLoadResult(HudWindowState.Default, HudStateLoadStatus.Recovered, "state-null");
            }

            if (state.Version != HudWindowState.CurrentVersion)
            {
                // Unknown future/older shape: keep the expand flag only when it
                // decoded, and re-request a default placement.
                return new HudStateLoadResult(
                    new HudWindowState { IsExpanded = state.IsExpanded },
                    HudStateLoadStatus.Recovered,
                    "state-version");
            }

            return new HudStateLoadResult(state.Normalized(), HudStateLoadStatus.Loaded, "state-loaded");
        }
        catch (JsonException)
        {
            return new HudStateLoadResult(HudWindowState.Default, HudStateLoadStatus.Recovered, "state-malformed");
        }
        catch (IOException)
        {
            return new HudStateLoadResult(HudWindowState.Default, HudStateLoadStatus.Failed, "state-io-error");
        }
        catch (UnauthorizedAccessException)
        {
            return new HudStateLoadResult(HudWindowState.Default, HudStateLoadStatus.Failed, "state-access-denied");
        }
    }

    /// <summary>Atomic save. Returns false instead of throwing when the write fails.</summary>
    public static bool TrySave(string path, HudWindowState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var normalized = state.Normalized();
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
