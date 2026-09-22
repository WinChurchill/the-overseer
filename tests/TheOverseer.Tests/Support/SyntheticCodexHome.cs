using System.Text.Json;

namespace TheOverseer.Tests.Support;

/// <summary>
/// Temporary synthetic Codex home. Only generated fixtures are written; no real
/// user rollout, index or database content is ever read or copied.
/// </summary>
public sealed class SyntheticCodexHome : IDisposable
{
    public SyntheticCodexHome()
    {
        Root = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        SessionsRoot = Path.Combine(Root, "sessions");
        Directory.CreateDirectory(SessionsRoot);
    }

    public string Root { get; }

    public string SessionsRoot { get; }

    public string IndexPath => Path.Combine(Root, "session_index.jsonl");

    public string StateDatabasePath => Path.Combine(Root, "state_5.sqlite");

    public string WriteRollout(
        string conversationId,
        IEnumerable<string> records,
        string datePath = "2026/01/02",
        string? fileNameSuffix = null,
        string stamp = "2026-01-02T03-04-05")
    {
        var directory = Path.Combine(SessionsRoot, datePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var name = "rollout-" + stamp + "-" + conversationId + (fileNameSuffix is null ? string.Empty : "_" + fileNameSuffix) + ".jsonl";
        var path = Path.Combine(directory, name);
        File.WriteAllLines(path, records);
        return path;
    }

    public static void Append(string path, IEnumerable<string> records)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        foreach (var record in records)
        {
            writer.WriteLine(record);
        }
    }

    public void WriteIndex(IEnumerable<(string Id, string Title)> entries)
    {
        using var stream = new FileStream(IndexPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        foreach (var (id, title) in entries)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                id,
                thread_name = title,
                updated_at = "2026-01-02T03:04:05Z",
            }));
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
