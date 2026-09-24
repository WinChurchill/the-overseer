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
/// </summary>
public sealed class BridgeSpoolReader
{
    private const int MaximumBytesPerPass = 1 * 1024 * 1024;
    private const int MaximumHealthBytes = 64 * 1024;
    private long _eventsOffset;
    private long _eventsLength = -1;

    public BridgeSpoolReader(string directory) => Directory = directory;

    public string Directory { get; }

    public string EventsPath => Path.Combine(Directory, "events.jsonl");

    public string HealthPath => Path.Combine(Directory, "health.json");

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
                return lines;
            }

            var info = new FileInfo(EventsPath);
            if (info.Length < _eventsLength || (_eventsLength < 0 && _eventsOffset > 0))
            {
                // Rotation or truncation: start over from the beginning of the file.
                _eventsOffset = 0;
            }

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

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0)
            {
                // A partially written record is left for the next pass.
                return lines;
            }

            var consumed = lastNewline + 1;
            _eventsOffset += Encoding.UTF8.GetByteCount(text[..consumed]);
            foreach (var line in text[..consumed].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                lines.Add(line);
            }

            if (info.Length - _eventsOffset > 0 && lines.Count == 0)
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

            var dropped = 0;
            foreach (var property in counters.EnumerateObject())
            {
                if (!property.Name.StartsWith("dropped", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.StartsWith("overflow", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value) && value > 0)
                {
                    dropped += value;
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
}

/// <summary>
/// One external metadata source (dsh observer or Claude hook bridge) as a monitor
/// adapter. Records are folded by <see cref="HarnessBridgeSession"/>, which owns
/// ordering, hydration and coverage semantics; this adapter only maps that state
/// onto the shared task contract and publishes.
/// </summary>
public sealed class BridgeMonitorAdapter : IAgentMonitorAdapter
{
    private readonly string _dataDirectory;
    private readonly ProjectResolver? _projects;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _staleLease;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private HarnessBridgeSession? _session;
    private BridgeSpoolReader? _reader;
    private Task? _loop;
    private SourceSnapshot _current;
    private int _numberSeed;

    public BridgeMonitorAdapter(
        SourceInstance source,
        string dataDirectory,
        ProjectResolver? projects = null,
        TimeSpan? pollInterval = null,
        TimeSpan? staleLease = null,
        TimeProvider? timeProvider = null)
    {
        Source = source;
        _dataDirectory = dataDirectory;
        _projects = projects;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _staleLease = staleLease ?? TimeSpan.FromMinutes(15);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _current = SourceSnapshot.Empty(source, _timeProvider.GetUtcNow(), "not-started", "bridge");
    }

    public SourceInstance Source { get; }

    public string SpoolDirectory => Path.Combine(_dataDirectory, "bridges", Source.HarnessId, Source.SourceInstanceId);

    public SourceSnapshot Current => _current;

    public event Action<SourceSnapshot>? SnapshotAvailable;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _session = new HarnessBridgeSession(Source, _staleLease);
        _reader = new BridgeSpoolReader(SpoolDirectory);
        _loop ??= Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    public void RequestReconcile() => Poll();

    /// <summary>One bounded pass: read new records, fold them, publish.</summary>
    public SourceSnapshot Poll()
    {
        if (_session is null || _reader is null)
        {
            return Publish("not-started", "bridge");
        }

        if (!_gate.Wait(TimeSpan.Zero))
        {
            // A pass is already running: never queue unbounded work.
            return _current;
        }

        try
        {
            var lines = _reader.ReadNewLines(out var readReason, out var droppedByProducer);
            var rejected = 0;
            foreach (var line in lines)
            {
                var parsed = HarnessBridgeParser.Parse(line);
                if (parsed.Outcome != BridgeParseOutcome.Accepted || parsed.Record is null)
                {
                    rejected++;
                    continue;
                }

                _session.Apply(parsed.Record);
            }

            if (droppedByProducer > 0)
            {
                // The producer told us it dropped records: what we saw is knowingly
                // incomplete, and that is visible instead of a silent gap.
                _session.NoteProducerIncomplete("producer-dropped:" + droppedByProducer.ToString(CultureInfo.InvariantCulture));
            }

            if (rejected > 0)
            {
                _session.NoteProducerIncomplete("bridge-rejected:" + rejected.ToString(CultureInfo.InvariantCulture));
            }

            return Publish(readReason, "bridge");
        }
        finally
        {
            _gate.Release();
        }
    }

    private SourceSnapshot Publish(string readReason, string adapterCode)
    {
        var now = _timeProvider.GetUtcNow();
        var lanes = _session?.Tasks(now) ?? Array.Empty<BridgeTaskState>();
        var tasks = new List<AgentTask>(lanes.Count);
        var transitions = new List<AttentionEvent>();
        foreach (var lane in lanes)
        {
            tasks.Add(ToTask(lane));
        }

        var reasons = new List<string>();
        var complete = true;
        if (!string.Equals(readReason, "ok", StringComparison.Ordinal))
        {
            reasons.Add(readReason);
            complete = false;
        }

        if (_session?.CoverageIncomplete == true)
        {
            reasons.Add("bridge-coverage-incomplete");
            complete = false;
        }

        if (tasks.Count == 0)
        {
            reasons.Add("bridge-no-sessions");
            complete = false;
        }

        var health = reasons.Count == 0
            ? MonitoringHealthLevel.Healthy
            : tasks.Count > 0 ? MonitoringHealthLevel.Degraded : MonitoringHealthLevel.Unavailable;

        _current = new SourceSnapshot(
            Source,
            now,
            health,
            reasons,
            complete,
            tasks,
            transitions,
            MonitorCapabilities.Initial,
            adapterCode);
        SnapshotAvailable?.Invoke(_current);
        return _current;
    }

    private AgentTask ToTask(BridgeTaskState lane)
    {
        var assignment = _projects?.Resolve(lane.ProjectRoot) ?? ProjectAssignment.None;
        var label = lane.SessionId.Length <= 8 ? lane.SessionId : lane.SessionId[..8];
        return new AgentTask(
            lane.Key,
            NumberFor(lane.SessionId),
            lane.SessionId,
            Source.SourceInstanceId,
            ClientSurface.Unknown,
            label,
            lane.TurnId,
            lane.State,
            new AgentAction(ActionFor(lane), EvidenceFor(lane), lane.DetailCode),
            EvidenceFor(lane),
            lane.LastObservedAt,
            lane.LastObservedAt,
            lane.LastObservedAt,
            lane.TurnId.Length > 0 ? lane.LastObservedAt : null,
            lane.State is AgentTaskState.Done ? lane.LastObservedAt : null,
            lane.IsStale,
            false,
            lane.NeedsHuman,
            lane.State == AgentTaskState.Idle && lane.DetailCode == "turn-aborted",
            lane.State == AgentTaskState.Error,
            lane.AttentionCode,
            1,
            lane.ProjectRoot,
            assignment.Project);
    }

    private static AgentActionKind ActionFor(BridgeTaskState lane) => lane.DetailCode switch
    {
        "waiting-for-approval" => AgentActionKind.WaitingForApproval,
        "waiting-for-input" => AgentActionKind.WaitingForInput,
        "turn-complete" => AgentActionKind.Completed,
        "turn-error" => AgentActionKind.Error,
        "tool-failed" => AgentActionKind.RunningCommand,
        "session-start" or "session-end" => AgentActionKind.Idle,
        _ => lane.Category switch
        {
            "thinking" => AgentActionKind.Thinking,
            "planning" => AgentActionKind.Thinking,
            "waiting" => AgentActionKind.WaitingForInput,
            "error" => AgentActionKind.Error,
            "complete" => AgentActionKind.Completed,
            "idle" => AgentActionKind.Idle,
            "working" => AgentActionKind.RunningCommand,
            _ => AgentActionKind.Thinking,
        },
    };

    private static EvidenceKind EvidenceFor(BridgeTaskState lane) => lane.Category switch
    {
        "unknown" => EvidenceKind.None,
        _ => lane.Key.HarnessId.Length > 0 ? EvidenceKind.Structured : EvidenceKind.Inferred,
    };

    private int NumberFor(string sessionId)
    {
        // Stable per-session numbering inside one source: no reordering when a
        // session appears later.
        var hash = 0;
        foreach (var character in sessionId)
        {
            hash = unchecked((hash * 31) + character);
        }

        return (Math.Abs(hash) % 9000) + 1000 + (++_numberSeed % 1);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                Poll();
                await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
        _gate.Dispose();
    }
}
