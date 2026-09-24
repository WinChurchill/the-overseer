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
    private const int MaximumBytesPerPass = 1 * 1024 * 1024;
    private const int MaximumHealthBytes = 64 * 1024;
    private const int MaximumCurrentBytes = 512 * 1024;
    private const int MaximumRotatedFilesPerPass = 4;
    private const int HeadFingerprintBytes = 64;

    private (long Length, DateTime LastWrite) _currentStamp = (-1, DateTime.MinValue);
    private long _eventsOffset;
    private long _eventsLength = -1;
    private DateTime _eventsCreationUtc;
    private DateTime _eventsLastWriteUtc;
    private ulong _eventsHeadHash;

    public BridgeSpoolReader(string directory) => Directory = directory;

    public string Directory { get; }

    public string EventsPath => Path.Combine(Directory, "events.jsonl");

    public string HealthPath => Path.Combine(Directory, "health.json");

    public string CurrentPath => Path.Combine(Directory, "current.json");

    /// <summary>Reads newly appended records. Never throws; failures are reported.</summary>
    public IReadOnlyList<string> ReadNewLines(out string reasonCode, out int droppedByProducer)
    {
        reasonCode = "ok";
        droppedByProducer = 0;
        var lines = new List<string>();
        try
        {
            droppedByProducer = ReadProducerDrops();
            if (!File.Exists(EventsPath))
            {
                _eventsLength = -1;
                return lines;
            }

            var info = new FileInfo(EventsPath);
            var creation = info.CreationTimeUtc;
            var lastWrite = info.LastWriteTimeUtc;
            var head = ReadHeadFingerprint();
            var replaced = _eventsLength >= 0 &&
                (info.Length < _eventsOffset ||
                 creation != _eventsCreationUtc ||
                 (info.Length <= _eventsOffset && head != _eventsHeadHash));
            if (replaced)
            {
                // The file this reader was following was rotated or replaced.
                // Drain what is left of it from the producer's rotated copy first.
                var recovered = DrainRotatedTail(lines, lastWrite);
                if (!recovered)
                {
                    reasonCode = "bridge-rotated-unread";
                }

                _eventsOffset = 0;
            }

            _eventsCreationUtc = creation;
            _eventsLastWriteUtc = lastWrite;
            _eventsHeadHash = head;
            _eventsLength = info.Length;
            if (info.Length <= _eventsOffset)
            {
                return lines;
            }

            var budget = (int)Math.Min(MaximumBytesPerPass, info.Length - _eventsOffset);
            using var stream = new FileStream(EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_eventsOffset, SeekOrigin.Begin);
            var buffer = new byte[budget];
            var read = 0;
            while (read < budget)
            {
                var next = stream.Read(buffer, read, budget - read);
                if (next <= 0)
                {
                    break;
                }

                read += next;
            }

            if (read == 0) return lines;
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);
            if (lastNewline < 0)
            {
                // A partially written record is left for the next pass.
                return lines;
            }

            var consumed = lastNewline + 1;
            _eventsOffset += consumed;
            var text = Encoding.UTF8.GetString(buffer, 0, consumed);
            AppendLines(lines, text);

            if (info.Length - _eventsOffset > 0 && lines.Count == 0 && string.Equals(reasonCode, "ok", StringComparison.Ordinal))
            {
                reasonCode = "bridge-no-progress";
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
    /// Reads the producer's authoritative current-state document. One bounded read
    /// with a single retry: the shipped producer replaces the file atomically, but a
    /// foreign or older producer may still be observed mid-write.
    /// </summary>
    public BridgeCurrentStateParseResult ReadCurrentState(SourceInstance source)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string text;
            try
            {
                if (!File.Exists(CurrentPath))
                {
                    return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Ignored, null, "snapshot-absent");
                }

                var stamp = new FileInfo(CurrentPath);
                var identity = (stamp.Length, stamp.LastWriteTimeUtc);
                if (identity == _currentStamp)
                {
                    // The atomically replaced document is byte-identical to the one
                    // already applied: re-parsing it every poll would cost bounded
                    // but pointless work.
                    return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Ignored, null, "snapshot-unchanged");
                }

                _currentStamp = identity;
                using var stream = new FileStream(CurrentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var budget = (int)Math.Min(MaximumCurrentBytes, stream.Length);
                if (budget <= 0)
                {
                    return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Ignored, null, "snapshot-empty");
                }

                var buffer = new byte[budget];
                var read = 0;
                while (read < budget)
                {
                    var next = stream.Read(buffer, read, budget - read);
                    if (next <= 0)
                    {
                        break;
                    }

                    read += next;
                }

                text = Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (IOException)
            {
                return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Ignored, null, "snapshot-io");
            }
            catch (UnauthorizedAccessException)
            {
                return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Ignored, null, "snapshot-denied");
            }

            var parsed = HarnessBridgeCurrentStateParser.Parse(text, source);
            if (parsed.Outcome != BridgeCurrentStateOutcome.Malformed || attempt == 1)
            {
                return parsed;
            }

            Thread.Sleep(5);
        }

        return new BridgeCurrentStateParseResult(BridgeCurrentStateOutcome.Malformed, null, "snapshot-malformed");
    }

    /// <summary>Producer drop/overflow counters, which lower coverage when non-zero.</summary>
    public int ReadProducerDrops()
    {
        try
        {
            if (!File.Exists(HealthPath))
            {
                return 0;
            }

            using var stream = new FileStream(HealthPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var budget = (int)Math.Min(MaximumHealthBytes, stream.Length);
            var buffer = new byte[budget];
            var read = stream.Read(buffer, 0, budget);
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, read));
            if (!document.RootElement.TryGetProperty("counters", out var counters) || counters.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

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
                    dropped += breakdown;
                }
            }

            return dropped;
        }
        catch (JsonException)
        {
            return 0;
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

    private ulong ReadHeadFingerprint()
    {
        try
        {
            using var stream = new FileStream(EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[HeadFingerprintBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
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
    private bool DrainRotatedTail(List<string> lines, DateTime observedLastWriteUtc)
    {
        if (_eventsOffset <= 0)
        {
            return true;
        }

        List<(string Path, DateTime LastWrite, long Length)> rotated;
        try
        {
            rotated = new DirectoryInfo(Directory)
                .EnumerateFiles("events-*.jsonl")
                .Where(file => file.Length > 0)
                .Select(file => (file.FullName, file.LastWriteTimeUtc, file.Length))
                .OrderBy(file => file.LastWriteTimeUtc)
                .Take(MaximumRotatedFilesPerPass)
                .ToList();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (rotated.Count == 0)
        {
            return false;
        }

        var recoveredAny = false;
        var remaining = MaximumBytesPerPass;
        for (var index = 0; index < rotated.Count; index++)
        {
            var file = rotated[index];
            if (remaining <= 0)
            {
                break;
            }

            // The oldest rotated file is the one this reader was following; later
            // ones are entirely unread. A file shorter than our offset cannot be
            // that file, so it is read from the beginning.
            var start = index == 0 && file.Length >= _eventsOffset ? _eventsOffset : 0;
            var budget = (int)Math.Min(remaining, file.Length - start);
            if (budget <= 0)
            {
                // Everything in this file was already consumed before the rotation.
                if (index == 0 && file.Length <= _eventsOffset) recoveredAny = true;
                continue;
            }

            try
            {
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[budget];
                var read = 0;
                while (read < budget)
                {
                    var next = stream.Read(buffer, read, budget - read);
                    if (next <= 0) break;
                    read += next;
                }

                var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);
                if (lastNewline >= 0)
                {
                    AppendLines(lines, Encoding.UTF8.GetString(buffer, 0, lastNewline + 1));
                    recoveredAny = true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            _ = observedLastWriteUtc;
        }

        return recoveredAny || rotated.Count == 0;
    }

    private static void AppendLines(List<string> lines, string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            lines.Add(line);
        }
    }
}
