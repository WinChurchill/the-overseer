using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheOverseer.Core.Monitoring.Sessions;

public enum TitleIndexAvailability
{
    Unavailable,
    Available,
    Failed,
}

public sealed record TitleIndexStatus(
    TitleIndexAvailability Availability,
    string ReasonCode,
    int EntryCount,
    bool Backlog);

/// <summary>
/// Bounded title cache over the official session_index.jsonl. Adapted from
/// codex-monitor-hud src-dotnet/CodexMonitorHud.Core/Sessions/SessionTitleIndex.cs
/// (revision df1d5b9e3d4b701de979b5c84a006db7cca5112f, MIT). The unbounded
/// dictionary, full-file reads and swallowed failures were replaced with a
/// bounded cache, an incremental byte budget and typed status.
/// </summary>
public sealed partial class SessionTitleIndex
{
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly IncrementalJsonlReader _reader;
    private readonly int _maxEntries;
    private readonly int _bytesPerPass;
    private DateTime _lastWriteTimeUtc = DateTime.MinValue;
    private DateTime _creationTimeUtc = DateTime.MinValue;
    private ulong _firstRecordSignature;
    private string _path = string.Empty;

    public SessionTitleIndex(int maxEntries, int bytesPerPass)
    {
        _maxEntries = Math.Max(32, maxEntries);
        _bytesPerPass = Math.Max(4096, bytesPerPass);
        _reader = new IncrementalJsonlReader();
    }

    public TitleIndexStatus Status { get; private set; } =
        new(TitleIndexAvailability.Unavailable, "not-read", 0, false);

    public int EntryCount => _titles.Count;

    public bool HasBacklog => _reader.HasUnreadData;

    public bool Refresh(string path, IReadOnlySet<string>? priorityIds = null, int? maximumBytes = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Status = new TitleIndexStatus(TitleIndexAvailability.Unavailable, "index-missing", _titles.Count, false);
            return false;
        }

        try
        {
            var file = new FileInfo(path);
            file.Refresh();
            var writeTime = file.LastWriteTimeUtc;
            var signature = ReadFirstRecordSignature(path);
            var identityChanged = _reader.Offset > 0 &&
                ((_creationTimeUtc != DateTime.MinValue && file.CreationTimeUtc != _creationTimeUtc) ||
                 (_firstRecordSignature != 0 && signature != _firstRecordSignature));
            var samePath = string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
            if (samePath && !identityChanged && writeTime <= _lastWriteTimeUtc && file.Length == _reader.Offset)
            {
                Status = new TitleIndexStatus(TitleIndexAvailability.Available, "unchanged", _titles.Count, _reader.HasUnreadData);
                return false;
            }

            var reset = !samePath || file.Length < _reader.Offset || identityChanged;
            if (reset)
            {
                _reader.Reset();
                _titles.Clear();
                _order.Clear();
            }

            var changed = reset;
            var result = _reader.ReadAppended(path, Math.Min(_bytesPerPass, maximumBytes ?? _bytesPerPass));
            foreach (var line in result.Lines)
            {
                if (!TryApply(line))
                {
                    continue;
                }

                changed = true;
            }

            _path = path;
            _lastWriteTimeUtc = writeTime;
            _creationTimeUtc = file.CreationTimeUtc;
            _firstRecordSignature = signature;
            Trim(priorityIds);
            Status = new TitleIndexStatus(
                TitleIndexAvailability.Available,
                result.OversizeDiscarded ? "oversize-record" : "ok",
                _titles.Count,
                _reader.HasUnreadData);
            return changed;
        }
        catch (IOException)
        {
            Status = new TitleIndexStatus(TitleIndexAvailability.Failed, "io-error", _titles.Count, _reader.HasUnreadData);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Status = new TitleIndexStatus(TitleIndexAvailability.Failed, "access-denied", _titles.Count, _reader.HasUnreadData);
            return false;
        }
    }

    public string GetTitle(string? conversationId) =>
        !string.IsNullOrWhiteSpace(conversationId) && _titles.TryGetValue(conversationId, out var title)
            ? title
            : string.Empty;

    private bool TryApply(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var id = root.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String
                ? idNode.GetString() ?? string.Empty
                : string.Empty;
            var title = root.TryGetProperty("thread_name", out var titleNode) && titleNode.ValueKind == JsonValueKind.String
                ? titleNode.GetString() ?? string.Empty
                : string.Empty;
            if (!root.TryGetProperty("thread_name", out _) &&
                root.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String)
            {
                title = nameNode.GetString() ?? string.Empty;
            }

            title = WhitespaceRegex().Replace(title, " ").Trim();
            if (id.Length == 0 || title.Length == 0)
            {
                return false;
            }

            if (title.Length > CompactTitleLength)
            {
                title = title[..CompactTitleLength].TrimEnd() + '\u2026';
            }

            if (_titles.TryGetValue(id, out var previous))
            {
                if (string.Equals(previous, title, StringComparison.Ordinal))
                {
                    return false;
                }

                _titles[id] = title;
                return true;
            }

            _titles[id] = title;
            _order.Enqueue(id);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void Trim(IReadOnlySet<string>? priorityIds)
    {
        // Bounded attempts: prioritized entries are rotated at most once, so a
        // cache full of prioritized ids still terminates.
        var attempts = _order.Count;
        while (_titles.Count > _maxEntries && _order.Count > 0 && attempts-- > 0)
        {
            var oldest = _order.Dequeue();
            if (priorityIds is not null && priorityIds.Contains(oldest))
            {
                _order.Enqueue(oldest);
                continue;
            }

            _titles.Remove(oldest);
        }

        // Anything still over the ceiling after the prioritized rotation is
        // evicted oldest-first; the cache bound always wins.
        while (_titles.Count > _maxEntries && _order.Count > 0)
        {
            _titles.Remove(_order.Dequeue());
        }
    }

    private const int CompactTitleLength = 64;

    private static ulong ReadFirstRecordSignature(string path)
    {
        Span<byte> buffer = stackalloc byte[4096];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = stream.Read(buffer);
        var newline = buffer[..read].IndexOf((byte)'\n');
        var count = newline >= 0 ? newline + 1 : read;
        if (count == 0)
        {
            return 0;
        }

        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var value in buffer[..count])
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
