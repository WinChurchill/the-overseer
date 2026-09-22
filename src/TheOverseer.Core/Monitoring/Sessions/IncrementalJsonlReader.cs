using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TheOverseer.Core.Monitoring.Sessions;

public sealed record JsonlReadResult(
    IReadOnlyList<string> Lines,
    int BytesRead,
    bool Truncated,
    bool OversizeDiscarded,
    bool BudgetExhausted,
    bool HasUnreadData)
{
    public static JsonlReadResult Empty { get; } = new(
        Array.Empty<string>(), 0, false, false, false, false);
}

/// <summary>
/// Byte-bounded incremental JSONL reader with partial-record and partial-UTF-8
/// preservation across reads. Adapted from codex-monitor-hud
/// src-dotnet/CodexMonitorHud.Core/Sessions/IncrementalJsonlReader.cs
/// (revision df1d5b9e3d4b701de979b5c84a006db7cca5112f, MIT) with an explicit
/// pending-record byte cap, generation tracking and typed diagnostics.
/// </summary>
public sealed class IncrementalJsonlReader
{
    private readonly List<byte> _pending;
    private readonly int _maxPendingBytes;
    private bool _discardingOversizeRecord;

    public IncrementalJsonlReader(long initialOffset = 0, int maxPendingBytes = 1024 * 1024)
    {
        Offset = Math.Max(0, initialOffset);
        _maxPendingBytes = Math.Max(4096, maxPendingBytes);
        _pending = new List<byte>(Math.Min(_maxPendingBytes, 64 * 1024));
    }

    /// <summary>Byte position of the last consumed record boundary.</summary>
    public long Offset { get; private set; }

    /// <summary>Incremented whenever the reader discards continuity, e.g. truncation.</summary>
    public int Generation { get; private set; }

    public bool HasUnreadData { get; private set; }

    public int LastReadBytes { get; private set; }

    public bool DiscardingOversizeRecord => _discardingOversizeRecord;

    /// <summary>
    /// Moves the read position without invalidating continuity; used after a
    /// bounded tail hydration so appends during hydration are still consumed.
    /// </summary>
    public void SeekTo(long offset)
    {
        Offset = Math.Max(0, offset);
        _pending.Clear();
        _discardingOversizeRecord = false;
        HasUnreadData = false;
    }

    public void Reset()
    {
        Offset = 0;
        _pending.Clear();
        _discardingOversizeRecord = false;
        HasUnreadData = false;
        LastReadBytes = 0;
        Generation++;
    }

    public JsonlReadResult ReadAppended(string path, int maximumBytes)
    {
        LastReadBytes = 0;
        var budget = Math.Max(4096, maximumBytes);
        var lines = new List<string>();
        var truncated = false;
        var oversize = false;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);

        var length = stream.Length;
        if (length < Offset)
        {
            // Truncation or same-path replacement by a shorter file.
            Reset();
            truncated = true;
        }

        if (length == Offset)
        {
            HasUnreadData = false;
            return new JsonlReadResult(lines, 0, truncated, oversize, false, false);
        }

        stream.Seek(Offset, SeekOrigin.Begin);
        var readStart = Offset;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var remaining = budget;
        try
        {
            int read;
            while (remaining > 0 &&
                   (read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining))) > 0)
            {
                remaining -= read;
                var start = 0;
                for (var index = 0; index < read; index++)
                {
                    if (buffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    if (!_discardingOversizeRecord)
                    {
                        if (!AppendPending(buffer.AsSpan(start, index - start)))
                        {
                            _discardingOversizeRecord = true;
                            oversize = true;
                        }
                        else
                        {
                            var text = TakePendingLine();
                            if (text.Length > 0)
                            {
                                lines.Add(text);
                            }
                        }
                    }

                    _pending.Clear();
                    _discardingOversizeRecord = false;
                    start = index + 1;
                }

                if (start < read && !_discardingOversizeRecord && !AppendPending(buffer.AsSpan(start, read - start)))
                {
                    _discardingOversizeRecord = true;
                    oversize = true;
                }
            }

            Offset = stream.Position;
            LastReadBytes = checked((int)Math.Min(int.MaxValue, Offset - readStart));
            HasUnreadData = Offset < stream.Length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // A budget boundary can land after a complete JSON object but before its
        // newline. Emit it only at actual EOF so the newline is not consumed as
        // an empty second record.
        if (!HasUnreadData && !_discardingOversizeRecord && _pending.Count > 0)
        {
            var candidate = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_pending));
            var trimmed = candidate.TrimEnd();
            if (IsCompleteJsonObject(trimmed))
            {
                lines.Add(candidate.TrimEnd('\r'));
                _pending.Clear();
            }
        }

        return new JsonlReadResult(lines, LastReadBytes, truncated, oversize, remaining <= 0 && HasUnreadData, HasUnreadData);
    }

    private string TakePendingLine()
    {
        var count = _pending.Count;
        if (count > 0 && _pending[count - 1] == (byte)'\r')
        {
            count--;
        }

        var text = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_pending)[..count]);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return text.Trim();
    }

    private bool AppendPending(ReadOnlySpan<byte> bytes)
    {
        if (_pending.Count + (long)bytes.Length > _maxPendingBytes)
        {
            _pending.Clear();
            return false;
        }

        var previousCount = _pending.Count;
        CollectionsMarshal.SetCount(_pending, previousCount + bytes.Length);
        bytes.CopyTo(CollectionsMarshal.AsSpan(_pending)[previousCount..]);
        return true;
    }

    private static bool IsCompleteJsonObject(string candidate)
    {
        if (!candidate.StartsWith('{') || !candidate.EndsWith('}'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(candidate);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
