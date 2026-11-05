using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Monitoring.Bridge;

namespace TheOverseer.App.Monitoring;

/// <summary>
/// Bounded reader for one bridge spool directory
/// (<c>&lt;data&gt;/bridges/&lt;harness&gt;/&lt;sourceInstance&gt;</c>). It reads only
/// what is new since the last pass, caps the bytes it will read, and treats a
/// producer-reported drop or an unreadable record as reduced coverage rather than
/// as evidence about the tasks.
///
/// Rotation is handled without silently losing records: the shipped producer
/// renames <c>events.jsonl</c> to <c>events-&lt;stamp&gt;.jsonl</c>, so the unread
/// tail of the file this reader was following is drained before the new file
/// starts. Replacement is detected by file identity (creation time plus a bounded
/// head fingerprint), which also covers a same-size replacement that a length
/// check alone would miss.
/// </summary>
public sealed class BridgeSpoolReader
{
    /// <summary>Hard ceiling for one source's share, whatever the caller asks for.</summary>
    public const int HardMaximumBytesPerPass = 1 * 1024 * 1024;

    private const int MaximumHealthBytes = 64 * 1024;
    private const int MaximumHealthReceipts = 64;
    private const int MaximumHealthReceiptBytes = 512;
    private const int MaximumCurrentBytes = 512 * 1024;
    private const int MaximumRotatedFilesPerPass = 4;
    private const int HeadFingerprintBytes = 64;

    private (long Length, DateTime LastWrite) _currentStamp = (-1, DateTime.MinValue);
    private BridgeCurrentStateParseResult? _currentCache;
    private (long Length, DateTime LastWrite) _pendingStamp = (-1, DateTime.MinValue);
    private byte[]? _pendingCurrent;
    private int _pendingCurrentRead;
    private int _remainingBytes = HardMaximumBytesPerPass;
    private int _eventAllowance;
    private readonly List<byte> _partialLine = new();
    private bool _discardingLine;
    private int _lastProducerDrops;
    private string _healthReason = "ok";
    private const int MaximumRecordBytes = 64 * 1024;
    private long _eventsOffset;
    private long _eventsLength = -1;
    private DateTime _eventsCreationUtc;
    private ulong _eventsHeadHash;

    private int _maximumBytesPerPass = HardMaximumBytesPerPass;

    public BridgeSpoolReader(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>
    /// This source's share of the aggregate monitoring budget. Small shares are
    /// honored; incomplete records and documents progress across polls.
    /// </summary>
    public int MaximumBytesPerPass
    {
        get => _maximumBytesPerPass;
        set { _maximumBytesPerPass = Math.Clamp(value, 1, HardMaximumBytesPerPass); _remainingBytes = _maximumBytesPerPass; }
    }

    /// <summary>Actual payload bytes read in the current poll, including metadata.</summary>
    public int BytesReadThisPass { get; private set; }

    private int ReadBudgeted(Stream stream, byte[] buffer, int offset, int count)
    {
        var read = stream.Read(buffer, offset, Math.Min(count, _remainingBytes));
        _remainingBytes -= read;
        BytesReadThisPass += read;
        return read;
    }

    public string EventsPath => Path.Combine(Directory, "events.jsonl");

    public string HealthPath => Path.Combine(Directory, "health.json");

    public string CurrentPath => Path.Combine(Directory, "current.json");

    /// <summary>Reads newly appended records. Never throws; failures are reported.</summary>
    public IReadOnlyList<string> ReadNewLines(out string reasonCode, out int droppedByProducer)
    {
        reasonCode = "ok";
        droppedByProducer = 0;
        _remainingBytes = _maximumBytesPerPass;
        BytesReadThisPass = 0;
        // Leave room for authoritative snapshots even under sustained event traffic.
        _eventAllowance = File.Exists(CurrentPath) ? _maximumBytesPerPass / 2 : _maximumBytesPerPass;
        var lines = new List<string>();
        try
        {
            droppedByProducer = ReadProducerDrops();
            if (_healthReason != "ok") reasonCode = _healthReason;
            if (!File.Exists(EventsPath))
            {
                if (_eventsLength >= 0) reasonCode = "bridge-events-missing";
                _eventsLength = -1;
                _eventsOffset = 0;
                _partialLine.Clear();
                _discardingLine = false;
                return lines;
            }

            var info = new FileInfo(EventsPath);
            var creation = info.CreationTimeUtc;
            var head = ReadHeadFingerprint();
            var replaced = _eventsLength >= 0 &&
                (info.Length < _eventsOffset ||
                 creation != _eventsCreationUtc ||
                 (info.Length <= _eventsOffset && head != _eventsHeadHash));
            if (replaced)
            {
                // The file this reader was following was rotated or replaced.
                // Drain what is left of it from the producer's rotated copy first.
                var recovered = DrainRotatedTail(lines);
                if (!recovered)
                {
                    reasonCode = "bridge-rotated-unread";
                }

                _eventsOffset = 0;
                _partialLine.Clear();
                _discardingLine = false;
            }

            _eventsCreationUtc = creation;
            _eventsHeadHash = head;
            _eventsLength = info.Length;
            if (info.Length <= _eventsOffset)
            {
                return lines;
            }

            var budget = (int)Math.Min(Math.Min(_remainingBytes, _eventAllowance), info.Length - _eventsOffset);
            using var stream = new FileStream(EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_eventsOffset, SeekOrigin.Begin);
            var buffer = new byte[budget];
            var read = 0;
            while (read < budget)
            {
                var next = ReadBudgeted(stream, buffer, read, budget - read);
                if (next <= 0)
                {
                    break;
                }

                read += next;
            }

            if (read == 0)
            {
                if (reasonCode == "ok") reasonCode = "bridge-budget-pending";
                return lines;
            }
            _eventsOffset += read;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == (byte)'\n')
                {
                    if (!_discardingLine && _partialLine.Count > 0)
                        lines.Add(Encoding.UTF8.GetString(_partialLine.ToArray()));
                    _partialLine.Clear();
                    _discardingLine = false;
                }
                else if (!_discardingLine)
                {
                    if (_partialLine.Count >= MaximumRecordBytes)
                    {
                        _partialLine.Clear();
                        _discardingLine = true;
                        reasonCode = "bridge-record-oversize";
                    }
                    else _partialLine.Add(buffer[index]);
                }
            }

            return lines;
        }
        catch (IOException)
        {
            reasonCode = "bridge-io";
            return lines;
        }
        catch (UnauthorizedAccessException)
        {
            reasonCode = "bridge-denied";
            return lines;
        }
    }

    /// <summary>
    /// Reads the producer's authoritative current-state document within the same
    /// poll allowance as events. Larger documents continue on subsequent polls.
    /// </summary>
    public BridgeCurrentStateParseResult ReadCurrentState(SourceInstance source)
    {
        try
        {
            if (!File.Exists(CurrentPath))
            {
                _pendingCurrent = null;
                _currentCache = null;
                return new(BridgeCurrentStateOutcome.Ignored, null, "snapshot-absent");
            }

            var stamp = new FileInfo(CurrentPath);
            var identity = (stamp.Length, stamp.LastWriteTimeUtc);
            if (identity == _currentStamp && _currentCache is not null) return _currentCache;
            if (stamp.Length <= 0 || stamp.Length > MaximumCurrentBytes)
                return new(BridgeCurrentStateOutcome.Malformed, null, "snapshot-size");

            // Large documents progress across polls without borrowing another
            // source's budget. Cache identity and value are published together.
            if (_pendingCurrent is null || identity != _pendingStamp)
            {
                _pendingStamp = identity;
                _pendingCurrent = new byte[(int)stamp.Length];
                _pendingCurrentRead = 0;
            }
            using var stream = new FileStream(CurrentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_pendingCurrentRead, SeekOrigin.Begin);
            while (_pendingCurrentRead < _pendingCurrent.Length && _remainingBytes > 0)
            {
                var read = ReadBudgeted(stream, _pendingCurrent, _pendingCurrentRead, _pendingCurrent.Length - _pendingCurrentRead);
                if (read == 0) break;
                _pendingCurrentRead += read;
            }
            var after = new FileInfo(CurrentPath);
            if ((after.Length, after.LastWriteTimeUtc) != identity)
            {
                _pendingCurrent = null;
                return new(BridgeCurrentStateOutcome.Ignored, null, "snapshot-changing");
            }
            if (_pendingCurrentRead != _pendingCurrent.Length)
                return new(BridgeCurrentStateOutcome.Ignored, null, "snapshot-budget-pending");

            var parsed = HarnessBridgeCurrentStateParser.Parse(Encoding.UTF8.GetString(_pendingCurrent), source);
            _pendingCurrent = null;
            _currentStamp = identity;
            _currentCache = parsed;
            return parsed;
        }
        catch (IOException) { return new(BridgeCurrentStateOutcome.Ignored, null, "snapshot-io"); }
        catch (UnauthorizedAccessException) { return new(BridgeCurrentStateOutcome.Ignored, null, "snapshot-denied"); }
    }

    /// <summary>Producer drop/overflow counters, which lower coverage when non-zero.</summary>
    public int ReadProducerDrops()
    {
        _healthReason = "ok";
        try
        {
            var healthBudget = Math.Min(MaximumHealthBytes, Math.Min(_maximumBytesPerPass / 8, _remainingBytes));
            using var document = ReadHealthDocument(HealthPath, MaximumHealthBytes, ref healthBudget);
            var dropped = document is null ? 0 : CountProducerDrops(document.RootElement.GetProperty("counters"));
            var pending = Path.Combine(Directory, "health-pending");
            if (File.Exists(Path.Combine(Directory, "health-incomplete")))
            {
                _healthReason = "bridge-health-accounting-incomplete";
            }
            if (System.IO.Directory.Exists(pending))
            {
                // Fixed slot names bound metadata work, even if unrelated files fill the directory.
                for (var slot = 0; slot < MaximumHealthReceipts; slot++)
                {
                    var key = slot.ToString("D2", CultureInfo.InvariantCulture);
                    using var receipt = ReadHealthDocument(Path.Combine(pending, key + ".json"), MaximumHealthReceiptBytes, ref healthBudget, receipt: true);
                    if (receipt is null) continue;
                    var root = receipt.RootElement;
                    var id = root.GetProperty("id").GetString();
                    if (document is not null && document.RootElement.TryGetProperty("accountedReceipts", out var accounted) &&
                        accounted.ValueKind == JsonValueKind.Object &&
                        accounted.TryGetProperty(slot.ToString(CultureInfo.InvariantCulture), out var committed) &&
                        committed.ValueKind == JsonValueKind.String && committed.GetString() == id)
                    {
                        continue;
                    }
                    foreach (var counter in root.GetProperty("counters").EnumerateArray())
                    {
                        if (counter.GetString()!.StartsWith("dropped", StringComparison.Ordinal))
                            dropped = (int)Math.Min(int.MaxValue, (long)dropped + 1);
                    }
                }
                // Folding commits a new summary before deleting/reusing receipts. Never
                // combine a receipt snapshot with another summary's deduplication IDs.
                using var after = ReadHealthDocument(HealthPath, MaximumHealthBytes, ref healthBudget);
                if (HealthRevision(document) != HealthRevision(after))
                {
                    _healthReason = "bridge-health-updating";
                    return _lastProducerDrops;
                }
            }
            return _lastProducerDrops = dropped;
        }
        catch (JsonException)
        {
            if (_healthReason == "ok") _healthReason = "bridge-health-malformed";
            return _lastProducerDrops;
        }
        catch (IOException)
        {
            _healthReason = "bridge-health-io";
            return _lastProducerDrops;
        }
        catch (UnauthorizedAccessException)
        {
            _healthReason = "bridge-health-denied";
            return _lastProducerDrops;
        }
    }

    private static string? HealthRevision(JsonDocument? document) =>
        document is not null && document.RootElement.TryGetProperty("healthRevision", out var revision) && revision.ValueKind == JsonValueKind.String
            ? revision.GetString() : null;

    private JsonDocument? ReadHealthDocument(string file, int maximumBytes, ref int healthBudget, bool receipt = false)
    {
        FileStream stream;
        try { stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (stream)
        {
            if (stream.Length > Math.Min(maximumBytes, healthBudget))
            {
                _healthReason = "bridge-health-size";
                throw new JsonException();
            }
            var buffer = new byte[(int)stream.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = ReadBudgeted(stream, buffer, read, buffer.Length - read);
                if (count == 0) throw new JsonException();
                read += count;
            }
            healthBudget -= read;
            var document = JsonDocument.Parse(buffer);
            var root = document.RootElement;
            var counters = default(JsonElement);
            var valid = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("counters", out counters) &&
                (receipt ? counters.ValueKind == JsonValueKind.Array : counters.ValueKind == JsonValueKind.Object);
            if (valid && receipt)
            {
                valid = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                    Guid.TryParseExact(id.GetString(), "D", out _) && counters.GetArrayLength() <= 14 &&
                    counters.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString() is
                        "recordsWritten" or "droppedOversizeInput" or "droppedEmptyInput" or "droppedMalformedInput" or
                        "droppedStdinDeadline" or "droppedStdinError" or "droppedLockTimeout" or "droppedOversizeRecord" or
                        "droppedWriteFailed" or "droppedCorruptSourceEpoch" or "droppedUnsupportedPayload" or
                        "droppedInternalError" or "recoveredCorruptFile" or "unknownEvents");
            }
            if (valid && !receipt && root.TryGetProperty("healthRevision", out var revision))
                valid = revision.ValueKind == JsonValueKind.String;
            if (valid && !receipt && root.TryGetProperty("accountedReceipts", out var accounted))
                valid = accounted.ValueKind == JsonValueKind.Object && accounted.EnumerateObject().All(item =>
                    item.Value.ValueKind == JsonValueKind.String && Guid.TryParseExact(item.Value.GetString(), "D", out _));
            if (!valid)
            {
                document.Dispose();
                throw new JsonException();
            }
            return document;
        }
    }

    private static int CountProducerDrops(JsonElement counters)
    {
        // The shipped producers publish a total `dropped` counter plus its
        // breakdown (droppedOldest, droppedOversize, ...). Summing both would
        // double-count the same loss, so the total wins when it is present.
        if (counters.TryGetProperty("dropped", out var total) &&
            total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out var value))
        {
            return Math.Max(0, value);
        }

        var dropped = 0;
        foreach (var property in counters.EnumerateObject())
        {
            if (!property.Name.StartsWith("dropped", StringComparison.OrdinalIgnoreCase) &&
                !property.Name.StartsWith("overflow", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var breakdown) && breakdown > 0)
            {
                dropped = (int)Math.Min(int.MaxValue, (long)dropped + breakdown);
            }
        }

        return dropped;
    }

    private ulong ReadHeadFingerprint()
    {
        try
        {
            using var stream = new FileStream(EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[HeadFingerprintBytes];
            var read = ReadBudgeted(stream, buffer, 0, buffer.Length);
            return Hash(buffer, read);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static ulong Hash(byte[] buffer, int length)
    {
        ulong hash = 1469598103934665603UL;
        for (var index = 0; index < length; index++)
        {
            hash = (hash ^ buffer[index]) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>
    /// Drains the unread tail of this reader's rotated file. The producer renames
    /// (never copies), so the rotated file still holds exactly the bytes that were
    /// in <c>events.jsonl</c> when the rotation happened.
    /// </summary>
    private bool DrainRotatedTail(List<string> lines)
    {
        if (_eventsOffset <= 0) return true;
        try
        {
            // Bound enumeration before sorting. Older retained rotations are not
            // assumed to be the live file we just lost.
            var files = new DirectoryInfo(Directory).EnumerateFiles("events-*.jsonl")
                .Take(MaximumRotatedFilesPerPass + 1).ToArray();
            if (files.Length == 0 || files.Length > MaximumRotatedFilesPerPass) return false;
            Array.Sort(files, (left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));
            var matching = -1;
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                if (file.CreationTimeUtc != _eventsCreationUtc || file.Length < _eventsOffset) continue;
                var size = (int)Math.Min(HeadFingerprintBytes, _eventsLength);
                if (_remainingBytes < size) return false;
                using var probe = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var head = new byte[size];
                var count = ReadBudgeted(probe, head, 0, size);
                if (count == size && Hash(head, count) == _eventsHeadHash) { matching = index; break; }
            }
            if (matching < 0 || _discardingLine) return false;

            for (var index = matching; index < files.Length; index++)
            {
                var file = files[index];
                var start = index == matching ? _eventsOffset - _partialLine.Count : 0;
                var available = file.Length - start;
                if (available == 0) continue;
                var budget = (int)Math.Min(Math.Min(_remainingBytes, _eventAllowance), available);
                if (budget <= 0) return false;
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[budget];
                var read = 0;
                while (read < budget)
                {
                    var next = ReadBudgeted(stream, buffer, read, budget - read);
                    if (next <= 0) break;
                    read += next;
                }
                _eventAllowance -= read;
                if (read == 0) return false;
                var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);
                if (lastNewline >= 0) AppendLines(lines, Encoding.UTF8.GetString(buffer, 0, lastNewline + 1));
                // Partial recovery is a reported history gap, never "recovered".
                if (read < available || lastNewline != read - 1) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void AppendLines(List<string> lines, string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            lines.Add(line);
        }
    }
}
