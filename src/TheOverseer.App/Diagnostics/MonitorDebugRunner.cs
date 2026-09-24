using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Finite, console-only monitoring harness used by Plan 1 review. It shows no
/// WPF window, prints health/capabilities/actual counts/task rows, and exits
/// with a deterministic code: 0 when monitoring was readable, 2 when essential
/// monitoring stayed unavailable, 1 for bad arguments or an unhandled failure.
/// </summary>
internal static class MonitorDebugRunner
{
    private const int DefaultDurationSeconds = 60;
    private const int MaximumDurationSeconds = 3600;
    private const int MaximumTasksPrintedInDetail = 10;

    public static bool IsDebugInvocation(IReadOnlyList<string> args)
    {
        foreach (var argument in args)
        {
            if (string.Equals(argument, "--monitor-debug", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static int Run(IReadOnlyList<string> args)
    {
        var consoleAvailable = ConsoleHost.Ensure();
        try
        {
            return Task.Run(() => RunCoreAsync(args, consoleAvailable)).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Write("monitor-debug failed: " + exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(IReadOnlyList<string> args, bool consoleAvailable)
    {
        if (!TryParse(args, out var settings, out var parseError))
        {
            Write(parseError);
            Write(Usage);
            return 1;
        }

        var dataDirectory = settings.DataDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OVERSEER");
        var configPath = Path.Combine(dataDirectory, MonitorConfigStore.FileName);
        var configResult = MonitorConfigStore.Load(configPath);
        var codexHome = settings.CodexHome ?? configResult.Config.CodexHome ?? MonitorProfiles.ResolveCodexHome(null);
        var profiles = MonitorProfiles.Create(codexHome, configResult.Config.AdditionalRoots);
        var monitorOptions = MonitorConfigStore.ToOptions(configResult.Config);

        using var log = configResult.Config.Logging.Enabled
            ? LocalLog.TryCreate(Path.Combine(dataDirectory, "logs", "overseer.log"), configResult.Config.Logging.MaxBytes, configResult.Config.Logging.Level)
            : null;
        IMonitorLog monitorLog = log is not null ? log : NullMonitorLog.Instance;
        monitorLog.Write(MonitorLogLevel.Info, "monitor-debug-start", "duration=" + settings.Duration.TotalSeconds.ToString(CultureInfo.InvariantCulture));

        IRuntimeActivitySource activitySource = configResult.Config.RuntimeActivitySourceEnabled
            ? new WindowsSessionActivitySource()
            : NullRuntimeActivitySource.Instance;

        var projects = new ProjectResolver(configResult.Config.Project);
        await using var monitor = new CodexMonitor(monitorOptions, profiles, activitySource, monitorLog, TimeProvider.System, projects);

        Write("OVERSEER monitor debug (finite run; no window)");
        Write("console: " + (consoleAvailable ? "attached" : "unavailable"));
        Write("codex-home: " + Display(settings, codexHome));
        Write("data-directory: " + Display(settings, dataDirectory));
        Write("config: " + configResult.Status + " (" + configResult.ReasonCode + ") at " + Display(settings, configPath));
        Write("duration: " + settings.Duration.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "s   redact-labels: " + (settings.RedactLabels ? "on" : "off"));
        Write("profiles: " + profiles.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var profile in profiles)
        {
            Write("  - " + profile.Id + " sessions=" + Display(settings, profile.SessionsRoot) + " exists=" + Directory.Exists(profile.SessionsRoot));
        }

        Write(string.Empty);
        Write("--- capabilities ---");
        WriteCapabilities(monitor.Current.Capabilities);

        using var cancellation = new CancellationTokenSource();
        var loop = monitor.StartAsync(cancellation.Token);
        var startedAt = Stopwatch.StartNew();
        var everReadable = false;
        TimeSpan? lastReportAt = null;
        var cursorTop = 0;

        while (startedAt.Elapsed < settings.Duration)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var snapshot = monitor.Current;
            if (snapshot.Health.Level != MonitoringHealthLevel.Unavailable)
            {
                everReadable = true;
            }

            var due = lastReportAt is null || startedAt.Elapsed - lastReportAt.Value >= TimeSpan.FromSeconds(10);
            if (due || snapshot.Transitions.Count > 0 || cursorTop == 0)
            {
                Write(string.Empty);
                WriteStatus(settings, snapshot, startedAt.Elapsed);
                cursorTop = 1;
                lastReportAt = startedAt.Elapsed;
            }

            if (snapshot.Transitions.Count > 0)
            {
                WriteTransitions(settings, snapshot.Transitions);
            }
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Write("warning: monitor loop did not stop within 5s; disposal will still cancel it");
        }
        catch (OperationCanceledException)
        {
        }

        var final = monitor.Current;
        if (final.Health.Level != MonitoringHealthLevel.Unavailable)
        {
            everReadable = true;
        }

        Write(string.Empty);
        Write("--- final snapshot ---");
        WriteStatus(settings, final, startedAt.Elapsed);
        WriteObservation(final.Observation);
        WriteCapabilities(final.Capabilities);
        WriteLimitations(final);
        Write(string.Empty);
        Write("exit: " + (everReadable ? "0 (monitoring readable)" : "2 (essential monitoring unavailable throughout run)"));
        monitorLog.Write(MonitorLogLevel.Info, "monitor-debug-end", "readable=" + (everReadable ? "true" : "false"));
        return everReadable ? 0 : 2;
    }

    private static void WriteStatus(DebugSettings settings, MonitoringSnapshot snapshot, TimeSpan elapsed)
    {
        Write("[t=+" + elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s] health=" + snapshot.Health.Level +
              " reasons=" + snapshot.Health.ReasonsDisplay +
              " coverage=" + (snapshot.Health.CoverageComplete ? "complete" : "partial") +
              " fresh=" + (snapshot.Health.LastSuccessfulReadAt is { } last ? (snapshot.GeneratedAt - last).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "unknown"));
        Write(snapshot.Counts.ToSummary());
        Write(string.Empty);

        if (snapshot.Tasks.Count == 0)
        {
            Write("(no tasks)");
            return;
        }

        foreach (var task in snapshot.Tasks)
        {
            Write(Row(settings, task));
        }

        if (snapshot.Tasks.Count <= MaximumTasksPrintedInDetail)
        {
            foreach (var task in snapshot.Tasks)
            {
                Write(string.Empty);
                Write(Display(settings, "#" + task.Number.ToString(CultureInfo.InvariantCulture) + " " + task.DisplayLabel));
                Write(task.State.ToString().ToUpperInvariant());
                Write(task.Action.Label);
            }
        }
    }

    private static string Row(DebugSettings settings, AgentTask task)
    {
        var builder = new StringBuilder();
        builder.Append('#').Append(task.Number.ToString(CultureInfo.InvariantCulture)).Append(' ');
        builder.Append(settings.RedactLabels ? "[redacted]" : task.DisplayLabel.Length == 0 ? "(unnamed)" : task.DisplayLabel);
        builder.Append(" / ").Append(task.State.ToString().ToUpperInvariant());
        builder.Append(" / ").Append(task.Action.Label);
        builder.Append(" / surface=").Append(task.Surface.ToString().ToLowerInvariant());
        builder.Append(" / evidence=").Append(task.Evidence.ToString().ToLowerInvariant());
        builder.Append(" / files=").Append(task.SourceFileCount.ToString(CultureInfo.InvariantCulture));
        if (task.Evidence == EvidenceKind.None)
        {
            builder.Append(" / not-hydrated");
        }
        if (task.IsStale)
        {
            builder.Append(" / stale");
        }

        if (task.IsProvisional)
        {
            builder.Append(" / provisional-identity");
        }

        if (task.AttentionCode is { Length: > 0 } attention)
        {
            builder.Append(" / ").Append(attention);
        }

        if (task.CancellationObserved)
        {
            builder.Append(" / cancelled");
        }

        return builder.ToString();
    }

    private static void WriteTransitions(DebugSettings settings, IReadOnlyList<AttentionEvent> transitions)
    {
        foreach (var item in transitions)
        {
            var label = settings.RedactLabels || item.Number <= 0
                ? string.Empty
                : " " + item.Label;
            Write("  attention: " + item.Kind + " #" + item.Number.ToString(CultureInfo.InvariantCulture) + label + " (" + item.Code + ")");
        }
    }

    private static void WriteObservation(LiveObservationStats observation)
    {
        Write(string.Empty);
        Write("--- observation (actual counts from this run) ---");
        Write("files tracked=" + observation.FilesTracked.ToString(CultureInfo.InvariantCulture) +
              " hydrated=" + observation.FilesHydrated.ToString(CultureInfo.InvariantCulture) +
              " bytes-read=" + observation.BytesRead.ToString(CultureInfo.InvariantCulture));
        Write("records session_meta=" + observation.SessionMetaRecords.ToString(CultureInfo.InvariantCulture) +
              " task_started=" + observation.TaskStartedRecords.ToString(CultureInfo.InvariantCulture) +
              " task_complete=" + observation.TaskCompletedRecords.ToString(CultureInfo.InvariantCulture) +
              " turn_aborted=" + observation.TurnAbortedRecords.ToString(CultureInfo.InvariantCulture));
        Write("records item_completed=" + observation.ItemCompletedRecords.ToString(CultureInfo.InvariantCulture) +
              " tool_calls=" + observation.ToolCallRecords.ToString(CultureInfo.InvariantCulture) +
              " permission_requests=" + observation.PermissionRequestRecords.ToString(CultureInfo.InvariantCulture) +
              " input_requests=" + observation.InputRequestRecords.ToString(CultureInfo.InvariantCulture));
        Write("records malformed=" + observation.MalformedRecords.ToString(CultureInfo.InvariantCulture) +
              " oversize=" + observation.OversizeRecords.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteCapabilities(MonitorCapabilities capabilities)
    {
        WriteCapability("rollout-lifecycle", capabilities.LifecycleEvents);
        WriteCapability("structured-activity", capabilities.StructuredActivity);
        WriteCapability("permission-requests", capabilities.PermissionRequests);
        WriteCapability("input-requests", capabilities.InputRequests);
        WriteCapability("task-failure", capabilities.TaskFailureEvidence);
        WriteCapability("runtime-activity-source", capabilities.RuntimeActivitySource);
        WriteCapability("session-titles", capabilities.SessionTitles);
        WriteCapability("attention-live", capabilities.AttentionLiveVerification);
    }

    private static void WriteCapability(string name, CapabilityStatus status) =>
        Write("  " + name.PadRight(24) + " " + StatusText(status.Support).PadRight(14) + " " + status.Note);

    private static string StatusText(CapabilitySupport support) => support switch
    {
        CapabilitySupport.Supported => "supported",
        CapabilitySupport.Unsupported => "unsupported",
        CapabilitySupport.Unavailable => "unavailable",
        CapabilitySupport.NotExercised => "not-exercised",
        _ => "live-observed",
    };

    private static void WriteLimitations(MonitoringSnapshot snapshot)
    {
        Write(string.Empty);
        Write("--- limitations (as reported by this run) ---");
        Write("  approval/input attention: " + StatusText(snapshot.Capabilities.PermissionRequests.Support) +
              " — Codex persists lifecycle and item records, but approval and request_user_input events are transient inside the owning runtime.");
        Write("  live attention verification: " + StatusText(snapshot.Capabilities.AttentionLiveVerification.Support) +
              " — normalized attention contracts are covered by synthetic tests only.");
        Write("  runtime activity source: " + StatusText(snapshot.Capabilities.RuntimeActivitySource.Support) +
              " — timestamps locate candidates for freshness; they are not execution state.");
        Write("  stale lease: tasks without recent evidence keep their last-known state and are excluded from authoritative counts.");
    }

    private static string Display(DebugSettings settings, string value) =>
        !settings.RedactLabels || value.Length == 0 ? value : Leaf(value);

    private static string Leaf(string value)
    {
        var separator = value.LastIndexOfAny(new[] { '\\', '/' });
        return separator >= 0 && separator + 1 < value.Length ? "...\\" + value[(separator + 1)..] : value;
    }

    private static void Write(string line)
    {
        try
        {
            Console.Out.WriteLine(line);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool TryParse(IReadOnlyList<string> args, out DebugSettings settings, out string error)
    {
        settings = new DebugSettings();
        error = string.Empty;
        var duration = DefaultDurationSeconds;
        string? codexHome = null;
        string? dataDirectory = null;
        var redact = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--monitor-debug":
                    break;
                case "--redact-labels":
                    redact = true;
                    break;
                case "--duration":
                    if (index + 1 >= args.Count || !int.TryParse(args[++index], NumberStyles.Integer, CultureInfo.InvariantCulture, out duration))
                    {
                        error = "invalid value for --duration";
                        return false;
                    }

                    break;
                case "--codex-home":
                    if (index + 1 >= args.Count)
                    {
                        error = "missing value for --codex-home";
                        return false;
                    }

                    codexHome = args[++index];
                    break;
                case "--data-directory":
                    if (index + 1 >= args.Count)
                    {
                        error = "missing value for --data-directory";
                        return false;
                    }

                    dataDirectory = args[++index];
                    break;
                case "--help":
                case "-h":
                    error = string.Empty;
                    return false;
                default:
                    error = "unknown argument: " + argument;
                    return false;
            }
        }

        if (duration < 1 || duration > MaximumDurationSeconds)
        {
            error = "duration must be between 1 and " + MaximumDurationSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";
            return false;
        }

        settings = new DebugSettings
        {
            Duration = TimeSpan.FromSeconds(duration),
            CodexHome = codexHome,
            DataDirectory = dataDirectory,
            RedactLabels = redact,
        };
        return true;
    }

    private const string Usage = @"usage: TheOverseer.App --monitor-debug [--duration SECONDS] [--codex-home PATH] [--data-directory PATH] [--redact-labels]

  --monitor-debug      run the finite console monitor harness (no WPF window)
  --duration SECONDS   run length, default 60, maximum 3600
  --codex-home PATH    override CODEX_HOME / %USERPROFILE%\.codex
  --data-directory P   override %LOCALAPPDATA%\OVERSEER for config and logs
  --redact-labels      redact task labels and paths in console output

exit codes: 0 readable monitoring, 2 essential monitoring unavailable, 1 bad arguments";

    private sealed record DebugSettings
    {
        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(DefaultDurationSeconds);

        public string? CodexHome { get; init; }

        public string? DataDirectory { get; init; }

        public bool RedactLabels { get; init; }
    }
}
