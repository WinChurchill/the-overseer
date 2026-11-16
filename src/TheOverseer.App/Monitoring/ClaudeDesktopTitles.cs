using System.IO;
using System.Text.Json;
using TheOverseer.Core.Models;

namespace TheOverseer.App.Monitoring;

/// <summary>
/// Looks up the title the Claude desktop app shows in its sidebar for one desktop
/// session, so Claude rows have a name instead of an id. Claude Code hooks send no
/// title; the desktop app keeps one small JSON file per session at
/// %LOCALAPPDATA%\Claude-Data\claude_*\claude-code-sessions\&lt;account&gt;\&lt;org&gt;\local_&lt;id&gt;.json.
///
/// Read-only and bounded: only files named by a validated desktop id are opened,
/// links are refused, reads are capped, only the top-level "title" string is used,
/// and results are cached per file write time. The format is undocumented; any
/// failure returns null and the row keeps its fallback label.
/// </summary>
public sealed class ClaudeDesktopTitles
{
    public const int MaximumFileBytes = 1024 * 1024;
    public const int MaximumTitleChars = 160;
    private const int MaximumCachedTitles = 256;
    private const int MaximumFolders = 32;
    private static readonly TimeSpan FolderRefresh = TimeSpan.FromMinutes(1);

    private readonly Func<IEnumerable<string>> _roots;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime WriteTime, string? Title)> _cache = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _folders = Array.Empty<string>();
    private DateTime _foldersAt = DateTime.MinValue;

    /// <param name="roots">Override for tests: folders that contain claude-code-sessions.</param>
    public ClaudeDesktopTitles(Func<IEnumerable<string>>? roots = null)
    {
        _roots = roots ?? DefaultRoots;
    }

    public string? TitleFor(string? desktopSessionId)
    {
        if (!ClaudeDesktopSession.IsValidId(desktopSessionId)) return null;
        try
        {
            var file = Find(desktopSessionId!);
            if (file is null) return null;
            var writeTime = file.LastWriteTimeUtc;
            lock (_gate)
            {
                if (_cache.TryGetValue(desktopSessionId!, out var cached) && cached.WriteTime == writeTime) return cached.Title;
            }

            var title = ReadTitle(file);
            lock (_gate)
            {
                if (_cache.Count >= MaximumCachedTitles && !_cache.ContainsKey(desktopSessionId!)) _cache.Clear();
                _cache[desktopSessionId!] = (writeTime, title);
            }
            return title;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private FileInfo? Find(string id)
    {
        foreach (var folder in Folders())
        {
            var file = new FileInfo(Path.Combine(folder, id + ".json"));
            if (!file.Exists) continue;
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > MaximumFileBytes) return null;
            return file;
        }
        return null;
    }

    /// <summary>The account/org folders under each claude-code-sessions root, refreshed each minute.</summary>
    private IReadOnlyList<string> Folders()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (now - _foldersAt < FolderRefresh) return _folders;
        }

        var found = new List<string>();
        foreach (var root in _roots())
        {
            var sessions = new DirectoryInfo(root);
            if (!sessions.Exists || (sessions.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var account in sessions.EnumerateDirectories().Take(MaximumFolders))
            {
                if ((account.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var org in account.EnumerateDirectories().Take(MaximumFolders))
                {
                    if ((org.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    found.Add(org.FullName);
                    if (found.Count >= MaximumFolders) break;
                }
                if (found.Count >= MaximumFolders) break;
            }
        }

        lock (_gate)
        {
            _folders = found;
            _foldersAt = now;
            return _folders;
        }
    }

    private static string? ReadTitle(FileInfo file)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumFileBytes) return null;
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String)
            return null;
        var text = DisplayText.Clean(title.GetString());
        if (text.Length == 0) return null;
        return text.Length <= MaximumTitleChars ? text : text[..(MaximumTitleChars - 1)] + "…";
    }

    private static IEnumerable<string> DefaultRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) yield break;
        // Store (MSIX) installs: Claude-Data\claude_<publisher hash>\claude-code-sessions.
        var data = new DirectoryInfo(Path.Combine(local, "Claude-Data"));
        if (data.Exists)
        {
            foreach (var package in data.EnumerateDirectories("claude_*").Take(4))
                yield return Path.Combine(package.FullName, "claude-code-sessions");
        }
        // Classic installs keep app data under Roaming\Claude.
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(roaming)) yield return Path.Combine(roaming, "Claude", "claude-code-sessions");
    }
}
