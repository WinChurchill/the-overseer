using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using TheOverseer.App.Desktop;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Production-path candidate check. It invents producer files - a Codex rollout
/// home and two bridge spools - and drives them through the real readers, the real
/// composite source and the real controller, asserting the state that actually
/// reaches the HUD view model. The synthetic smoke only proves the window starts;
/// this proves live transitions, honest failure and recovery.
///
/// Nothing here touches a real rollout, credential, harness profile or settings
/// file: every path is created under the requested output directory and every label
/// is synthetic.
/// </summary>
internal static class HudIntegrationCheckRunner
{
    private const string ProducerVersion = "overseer-integration-check/1.0.0";

    private sealed record CheckResult(string Name, bool Passed, string Detail);

    public static async Task<int> RunAsync(StartupArguments arguments, Dispatcher dispatcher)
    {
        var outputRoot = string.IsNullOrWhiteSpace(arguments.OutputDirectory)
            ? Path.Combine(DataDirectoryResolver.Resolve(arguments.DataDirectory), "integration-check")
            : Path.GetFullPath(arguments.OutputDirectory!);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var root = Path.Combine(outputRoot, "run-" + stamp + "-" + Guid.NewGuid().ToString("N")[..8]);
        var dataDirectory = Path.Combine(root, "data");
        var codexHome = Path.Combine(root, "codex-home");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(codexHome);

        var checks = new List<CheckResult>();
        void Check(string name, bool passed, string detail)
        {
            checks.Add(new CheckResult(name, passed, detail));
            Console.WriteLine("check " + (passed ? "PASS " : "FAIL ") + name + " - " + detail);
        }

        Console.WriteLine("OVERSEER production-path integration check (invented producers, isolated data)");
        Console.WriteLine("root: " + root);
        Console.WriteLine("placement monitor: " + (string.IsNullOrWhiteSpace(arguments.PlacementMonitor) ? "(saved or primary)" : arguments.PlacementMonitor));

        var codex = new SyntheticCodexProducer(codexHome);
        var dsh = new SyntheticBridgeProducer(dataDirectory, HarnessIds.Dsh, "dsh-check");
        var claude = new SyntheticBridgeProducer(dataDirectory, HarnessIds.Claude, "claude-check");
        codex.WriteWorkingTask();
        dsh.Start("session-dsh-1", @"C:\synthetic\dsh-project");
        claude.Start("session-claude-1", @"C:\synthetic\claude-project");

        var monitorOptions = MonitorOptions.Default with
        {
            KnownFilePollInterval = TimeSpan.FromMilliseconds(500),
            ReconcileInterval = TimeSpan.FromSeconds(1),
            DebounceInterval = TimeSpan.FromMilliseconds(100),
            StaleLease = TimeSpan.FromMinutes(10),
            // A shorter retention than the 60-second default, so one finite run can
            // observe a removed file being released and the source recovering.
            MissingFileRetention = TimeSpan.FromSeconds(12),
        };

        var projects = new ProjectResolver(new ProjectSettings());
        var monitor = new CodexMonitor(
            monitorOptions,
            MonitorProfiles.Create(codexHome),
            NullRuntimeActivitySource.Instance,
            NullMonitorLog.Instance,
            TimeProvider.System,
            projects);
        var discovery = HarnessSources.DiscoverDetailed(dataDirectory);
        var adapters = new List<IAgentMonitorAdapter>
        {
            new CodexMonitorAdapter(new CodexMonitorSource(monitor), new SourceInstance(HarnessIds.Codex, "codex", "Codex")),
        };
        foreach (var source in discovery.Sources)
        {
            adapters.Add(new BridgeMonitorAdapter(source, dataDirectory, projects, pollInterval: TimeSpan.FromMilliseconds(500)));
        }

        Check("discovery-finds-both-bridges", discovery.Sources.Count == 2,
            "sources=" + string.Join(',', discovery.Sources.Select(static source => source.Describe())));

        var composite = new CompositeMonitorSource(adapters, NullMonitorLog.Instance, TimeProvider.System, new SourceBudget(
            MonitorOptions.Default.GlobalBytesPerPass, Math.Max(SourceBudget.MinimumSessions, 64 * 16), "integration-check"), discovery.Capped);

        var configPath = Path.Combine(dataDirectory, MonitorConfigStore.FileName);
        var statePath = Path.Combine(dataDirectory, HudStateStore.FileName);
        var dependencies = new HudControllerDependencies(
            composite,
            new WpfUiDispatcher(dispatcher),
            new WindowsWindowPlacementService(),
            new IntegrationNavigator(),
            new IntegrationStartup(),
            new IntegrationClipboard(),
            NullMonitorLog.Instance,
            MonitorConfigStore.Load(configPath).Config,
            HudPlacementOptions.WithMonitor(HudStateStore.Load(statePath).State, arguments.PlacementMonitor),
            new HudControllerPaths(dataDirectory, configPath, statePath),
            () => new HudWindow { ShowActivated = false },
            TimeProvider.System,
            StartHidden: false,
            TrayEnabled: false);

        var controller = new HudApplicationController(dependencies);
        var viewModel = controller.ViewModel;
        try
        {
            await controller.StartAsync(CancellationToken.None).ConfigureAwait(true);

            var working = await WaitAsync(() => viewModel.Tasks.Any(static row =>
                row.TaskState == AgentTaskState.Working && row.Key.HarnessId == HarnessIds.Codex), 30);
            Check("codex-working-task-applied", working, Summarize(viewModel));

            var bridged = await WaitAsync(() => viewModel.Tasks.Any(row =>
                row.Key.HarnessId == HarnessIds.Dsh &&
                row.ProjectGroupLabel.Contains("dsh-project", StringComparison.OrdinalIgnoreCase)), 30);
            Check("bridge-task-applied", bridged, Summarize(viewModel));

            dsh.ApprovalAsked("approval-1");
            var attention = await WaitAsync(() => viewModel.Tasks.Any(static row =>
                row.Key.HarnessId == HarnessIds.Dsh && row.NeedsHuman), 30);
            Check("bridge-attention-applied", attention, Summarize(viewModel));

            dsh.ApprovalDecided("approval-1");
            var answered = await WaitAsync(() => viewModel.Tasks.Any(static row =>
                row.Key.HarnessId == HarnessIds.Dsh && !row.NeedsHuman && row.TaskState == AgentTaskState.Working), 30);
            Check("bridge-answer-clears-attention", answered, Summarize(viewModel));

            codex.RequestAsyncInput("call-async-1");
            var asyncAttention = await WaitAsync(() => viewModel.Tasks.Any(static row =>
                row.Key.HarnessId == HarnessIds.Codex && row.NeedsHuman && row.TaskState == AgentTaskState.Working), 30);
            Check("async-attention-while-working", asyncAttention, Summarize(viewModel));

            // An unrelated tool output cannot answer a persisted async request: the
            // turn boundary is the observable resolution. This is asserted rather than
            // assumed, because clearing it in the UI alone would be a lie.
            codex.AnswerAsyncInput("call-async-1");
            var stillWaiting = !await WaitAsync(() => viewModel.Tasks.Any(static row =>
                row.Key.HarnessId == HarnessIds.Codex && !row.NeedsHuman), 6);
            Check("async-attention-survives-unrelated-output", stillWaiting, Summarize(viewModel));

            codex.Complete();
            var completed = await WaitAsync(() => viewModel.Tasks.Any(static row =>
                row.Key.HarnessId == HarnessIds.Codex && row.TaskState == AgentTaskState.Done && !row.NeedsHuman), 30);
            Check("codex-completion-applied-and-clears-attention", completed, Summarize(viewModel));

            // Malformed producer data degrades the source honestly: the peer keeps
            // publishing, the failure is disclosed per source, and there is no
            // monitor-stopped latch.
            codex.BreakFile();
            var reported = await WaitAsync(() => viewModel.HealthDetails.Any(static row =>
                row.Source.Contains("Codex", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(row.State, "reporting", StringComparison.Ordinal)), 45);
            codex.RemoveFile();
            var peerSurvived = viewModel.Tasks.Any(static row => row.Key.HarnessId == HarnessIds.Dsh);
            Check("codex-degradation-is-disclosed", reported, Summarize(viewModel));
            Check("peer-source-survives-failure", peerSurvived, Summarize(viewModel));

            dsh.Start("session-dsh-2", @"C:\synthetic\dsh-project-two");
            var lateArrival = await WaitAsync(() => viewModel.Tasks.Any(row =>
                row.Key.HarnessId == HarnessIds.Dsh &&
                row.ProjectGroupLabel.Contains("dsh-project-two", StringComparison.OrdinalIgnoreCase)), 30);
            Check("no-monitor-stopped-latch",
                lateArrival && !viewModel.StatusMessage.Contains("Monitor stopped", StringComparison.OrdinalIgnoreCase),
                Summarize(viewModel));

            // Recovery: the producer writes a clean rollout again and the task is
            // observed fresh without restarting the HUD.
            codex.RestoreFile();
            var recovered = await WaitAsync(() => viewModel.Tasks.Any(static row =>
                row.Key.HarnessId == HarnessIds.Codex && row.TaskState == AgentTaskState.Working), 60);
            Check("codex-recovers-after-clean-data", recovered, Summarize(viewModel));

            // Freshness advances means the pipeline keeps delivering, not that the
            // process stayed alive: the applied-snapshot counter must grow while the
            // producers keep publishing.
            var before = viewModel.AppliedSnapshots;
            dsh.Heartbeat();
            await Task.Delay(3000).ConfigureAwait(true);
            var after = viewModel.AppliedSnapshots;
            Check("freshness-advances", viewModel.HasAppliedSnapshot && after > before,
                "applied " + before.ToString(CultureInfo.InvariantCulture) + " -> " + after.ToString(CultureInfo.InvariantCulture) +
                " " + Summarize(viewModel));

            // Does a reason recorded while a tracked file was unreadable clear once the
            // source is healthy again? A latched reason would leave the HUD at limited
            // coverage forever, so this is asserted rather than assumed.
            await Task.Delay(8000).ConfigureAwait(true);
            var clearedHealth = !viewModel.HealthDetails.Any(static row =>
                row.Source.Contains("Codex", StringComparison.OrdinalIgnoreCase) &&
                row.State.Contains("read-failure", StringComparison.OrdinalIgnoreCase));
            Check("source-health-clears-after-recovery", clearedHealth, Summarize(viewModel));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Check("integration-check-run", false, exception.GetType().Name + ": " + exception.Message);
        }
        finally
        {
            try
            {
                await controller.ShutdownAsync().ConfigureAwait(true);
            }
            catch (InvalidOperationException)
            {
            }

            await controller.DisposeAsync().ConfigureAwait(true);
        }

        var failed = checks.Count(static check => !check.Passed);
        var report = new
        {
            product = "OVERSEER",
            check = "production-path-integration",
            root,
            generatedAtUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            passed = checks.Count - failed,
            failed,
            checks,
        };
        Directory.CreateDirectory(outputRoot);
        var reportPath = Path.Combine(root, "integration-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Console.WriteLine("integration checks: " + (checks.Count - failed) + " passed, " + failed + " failed");
        Console.WriteLine("integration report: " + reportPath);
        return failed == 0 ? 0 : 1;
    }

    private static async Task<bool> WaitAsync(Func<bool> predicate, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(200).ConfigureAwait(true);
        }

        return predicate();
    }

    /// <summary>Redacted state summary: counts, states and codes only, never a label.</summary>
    private static string Summarize(HudViewModel viewModel)
    {
        var codex = viewModel.Tasks.FirstOrDefault(static row => row.Key.HarnessId == HarnessIds.Codex);
        var details = string.Join(',', viewModel.HealthDetails.Select(static row => row.Source + ":" + row.State));
        return "rows=" + viewModel.Tasks.Count.ToString(CultureInfo.InvariantCulture) +
               " working=" + viewModel.WorkingCountText + " needsYou=" + viewModel.NeedsHumanCountText +
               " health=" + viewModel.HealthLabel +
               " codex=" + (codex is null ? "absent" : codex.StateText + (codex.IsStale ? "/stale" : "/fresh")) +
               " details=[" + details + "]" +
               " status=" + (viewModel.StatusMessage.Length == 0 ? "-" : "set");
    }

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// One invented Codex rollout. Only structural fields the parser may read are
    /// written, and the labels are synthetic.
    /// </summary>
    private sealed class SyntheticCodexProducer
    {
        private const string ConversationId = "01a0c900-0000-0000-0000-0000000000c9";
        private string _path;

        public SyntheticCodexProducer(string codexHome)
        {
            var sessions = Path.Combine(codexHome, "sessions", "2026", "01", "02");
            Directory.CreateDirectory(sessions);
            _path = Path.Combine(sessions, "rollout-2026-01-02T03-04-05-" + ConversationId + ".jsonl");
            var now = DateTimeOffset.UtcNow;
            File.WriteAllText(_path, JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new
                {
                    id = ConversationId,
                    session_id = ConversationId,
                    timestamp = Iso(now),
                    cwd = @"C:\synthetic\codex-project",
                    originator = "Codex Desktop",
                    source = "vscode",
                    history_mode = "paginated",
                    cli_version = "0.155.1",
                    model_provider = "openai",
                    thread_source = "user",
                },
            }) + "\n", Utf8NoBom);
        }

        public void WriteWorkingTask()
        {
            var now = DateTimeOffset.UtcNow;
            Append(JsonSerializer.Serialize(new
            {
                type = "event_msg",
                payload = new
                {
                    type = "task_started",
                    turn_id = "turn-1",
                    started_at = now.ToUnixTimeSeconds(),
                    collaboration_mode_kind = "default",
                },
            }));
        }

        /// <summary>
        /// The asynchronous input tool call the desktop client persists: attention
        /// arrives while the turn itself keeps working.
        /// </summary>
        public void RequestAsyncInput(string callId) => Append(ResponseItem("function_call", callId, "request_user_input_async"));

        public void AnswerAsyncInput(string callId) => Append(ResponseItem("function_call_output", callId, null));

        /// <summary>
        /// Replaces the rollout with unreadable content. A real truncated or partially
        /// written rollout is exactly this shape, and the monitor must report it
        /// instead of keeping the last good state silently.
        /// </summary>
        public void BreakFile() => File.WriteAllText(_path, "{\"type\": \"event_msg\", \"payload\": {", Utf8NoBom);

        /// <summary>Removes the rollout: the session is gone from the source's view.</summary>
        public void RemoveFile()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }

        /// <summary>
        /// Writes a clean rollout under a new file name - what a producer does when it
        /// rotates - so the source can be observed fresh without depending on how a
        /// reader resumes a file that was rewritten underneath it.
        /// </summary>
        public void RestoreFile()
        {
            var now = DateTimeOffset.UtcNow;
            var suffix = now.ToString("HHmmss", CultureInfo.InvariantCulture);
            _path = Path.Combine(Path.GetDirectoryName(_path)!, "rollout-" + suffix + "-" + ConversationId + "_recovered.jsonl");
            var lines = new List<string>
            {
                JsonSerializer.Serialize(new
                {
                    type = "session_meta",
                    payload = new
                    {
                        id = ConversationId,
                        session_id = ConversationId,
                        timestamp = Iso(now),
                        cwd = @"C:\synthetic\codex-project",
                        originator = "Codex Desktop",
                        source = "vscode",
                        history_mode = "paginated",
                        cli_version = "0.155.1",
                        model_provider = "openai",
                        thread_source = "user",
                    },
                }),
                JsonSerializer.Serialize(new
                {
                    type = "event_msg",
                    payload = new
                    {
                        type = "task_started",
                        turn_id = "turn-2",
                        started_at = now.ToUnixTimeSeconds(),
                        collaboration_mode_kind = "default",
                    },
                }),
            };
            File.WriteAllLines(_path, lines, Utf8NoBom);
        }

        public void Complete()
        {
            var now = DateTimeOffset.UtcNow;
            Append(JsonSerializer.Serialize(new
            {
                type = "event_msg",
                payload = new
                {
                    type = "task_complete",
                    turn_id = "turn-1",
                    last_agent_message = "synthetic",
                    started_at = now.AddSeconds(-2).ToUnixTimeSeconds(),
                    completed_at = now.ToUnixTimeSeconds(),
                    duration_ms = 2000,
                },
            }));
        }

        private static string ResponseItem(string type, string callId, string? name) =>
            JsonSerializer.Serialize(new
            {
                type = "response_item",
                timestamp = Iso(DateTimeOffset.UtcNow),
                payload = new
                {
                    type,
                    name,
                    call_id = callId,
                    arguments = type == "function_call" ? "synthetic structural arguments" : null,
                    output = type == "function_call_output" ? "synthetic response accepted" : null,
                },
            });

        private void Append(string line)
        {
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.WriteLine(line);
        }
    }

    /// <summary>
    /// One invented bridge producer. It publishes the same two documents as the
    /// shipped observers - an append-only <c>events.jsonl</c> and an atomically
    /// replaced <c>current.json</c> - so the consumer's resynchronization path is
    /// exercised rather than bypassed.
    /// </summary>
    private sealed class SyntheticBridgeProducer
    {
        private sealed class SessionState
        {
            public string ProjectRoot = string.Empty;
            public string OpenTurnId = string.Empty;
            public string PendingApprovalRequestId = string.Empty;
            public string Category = "idle";
            public long Sequence;
            public long Records;
            public DateTimeOffset CreatedAtUtc;
            public DateTimeOffset FirstSeenUtc;
            public DateTimeOffset LastObservedUtc;
        }

        private readonly string _directory;
        private readonly string _harnessId;
        private readonly string _instanceId;
        private readonly string _epoch;
        private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

        public SyntheticBridgeProducer(string dataDirectory, string harnessId, string instanceId)
        {
            _harnessId = HarnessIds.Normalize(harnessId);
            _instanceId = instanceId;
            _epoch = instanceId + "-epoch-1";
            var root = Path.Combine(dataDirectory, _harnessId == HarnessIds.Claude ? "spool" : "bridges", _harnessId);
            _directory = Path.Combine(root, instanceId);
            Directory.CreateDirectory(_directory);
            File.WriteAllText(EventsPath, string.Empty, Utf8NoBom);
            WriteHealth();
        }

        private string EventsPath => Path.Combine(_directory, "events.jsonl");

        private string CurrentPath => Path.Combine(_directory, "current.json");

        private string HealthPath => Path.Combine(_directory, "health.json");

        public void Start(string sessionId, string projectRoot)
        {
            var now = DateTimeOffset.UtcNow;
            _sessions[sessionId] = new SessionState
            {
                ProjectRoot = projectRoot,
                Category = "idle",
                CreatedAtUtc = now,
                FirstSeenUtc = now,
                LastObservedUtc = now,
            };
            Publish(sessionId, "session/start");
            Publish(sessionId, "turn/start", turnId: "turn-1");
        }

        /// <summary>A host heartbeat: keeps the source fresh without changing task state.</summary>
        public void Heartbeat() => Publish(FirstSession(), "heartbeat");

        public void ApprovalAsked(string requestId) =>
            Publish(FirstSession(), "approval/asked", requestId: requestId, turnId: "turn-1", category: "waiting");

        public void ApprovalDecided(string requestId) =>
            Publish(FirstSession(), "approval/decided", requestId: requestId, turnId: "turn-1", outcome: "ok", category: "working");

        private string FirstSession() => _sessions.Keys.OrderBy(static key => key, StringComparer.Ordinal).First();

        private void Publish(string sessionId, string kind, string? turnId = null, string? requestId = null,
            string? outcome = null, string? category = null)
        {
            var state = _sessions[sessionId];
            var now = DateTimeOffset.UtcNow;
            state.Sequence++;
            state.Records++;
            state.LastObservedUtc = now;
            if (category is not null)
            {
                state.Category = category;
            }

            if (!string.IsNullOrEmpty(turnId))
            {
                state.OpenTurnId = turnId!;
            }

            if (kind == "turn/end")
            {
                state.OpenTurnId = string.Empty;
            }

            if (kind == "approval/asked")
            {
                state.PendingApprovalRequestId = requestId ?? string.Empty;
            }

            if (kind == "approval/decided")
            {
                state.PendingApprovalRequestId = string.Empty;
            }

            var record = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                harnessId = _harnessId,
                sourceInstanceId = _instanceId,
                sourceEpoch = _epoch,
                sessionId,
                sequence = state.Sequence,
                eventId = _harnessId + ":" + sessionId + ":" + state.Sequence.ToString(CultureInfo.InvariantCulture),
                observedAtUtc = Iso(now),
                eventAtUtc = Iso(now),
                kind,
                turnId,
                callId = (string?)null,
                requestId,
                projectRoot = state.ProjectRoot,
                category = state.Category,
                evidence = "explicit",
                outcome,
                hydration = false,
                producerVersion = ProducerVersion,
            });
            using (var stream = new FileStream(EventsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.WriteLine(record);
            }

            WriteCurrent();
            WriteHealth();
        }

        private void WriteCurrent()
        {
            var now = DateTimeOffset.UtcNow;
            var document = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                harnessId = _harnessId,
                sourceInstanceId = _instanceId,
                sourceEpoch = _epoch,
                producerVersion = ProducerVersion,
                updatedAtUtc = Iso(now),
                evictedSessions = 0,
                sessions = _sessions.Select(pair => new
                {
                    sessionId = pair.Key,
                    parentSessionId = (string?)null,
                    projectRoot = pair.Value.ProjectRoot,
                    createdAtUtc = Iso(pair.Value.CreatedAtUtc),
                    firstSeenUtc = Iso(pair.Value.FirstSeenUtc),
                    lastObservedUtc = Iso(pair.Value.LastObservedUtc),
                    openTurnId = pair.Value.OpenTurnId.Length == 0 ? null : pair.Value.OpenTurnId,
                    planMode = false,
                    pendingApprovalRequestId = pair.Value.PendingApprovalRequestId.Length == 0 ? null : pair.Value.PendingApprovalRequestId,
                    category = pair.Value.Category,
                    disposed = false,
                    lastSequence = pair.Value.Sequence,
                    lastEventId = _harnessId + ":" + pair.Key + ":" + pair.Value.Sequence.ToString(CultureInfo.InvariantCulture),
                    records = pair.Value.Records,
                }).ToArray(),
            });
            // The shipped producers publish atomically; the consumer may read at any
            // moment, so a torn document must never be observable.
            var pending = CurrentPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllText(pending, document, Utf8NoBom);
            File.Move(pending, CurrentPath, overwrite: true);
        }

        private void WriteHealth()
        {
            var now = DateTimeOffset.UtcNow;
            var document = JsonSerializer.Serialize(new
            {
                version = 1,
                producerVersion = ProducerVersion,
                harnessId = _harnessId,
                sourceInstanceId = _instanceId,
                sourceEpoch = _epoch,
                startedAtUtc = Iso(now),
                lastWrite = Iso(now),
                queueDepth = 0,
                dropped = 0,
                unavailable = false,
                counters = new
                {
                    enqueued = _sessions.Values.Sum(static state => state.Records),
                    written = _sessions.Values.Sum(static state => state.Records),
                    dropped = 0,
                    droppedOldest = 0,
                    droppedOversize = 0,
                    droppedError = 0,
                    writeErrors = 0,
                },
                lastError = (string?)null,
            });
            File.WriteAllText(HealthPath, document, Utf8NoBom);
        }
    }

    private sealed class IntegrationNavigator : ITaskNavigator
    {
        public TaskNavigationResult Navigate(AgentTask task, string profileId) =>
            new(TaskNavigationOutcome.Requested, "integration-check: no real launch", null);
    }

    private sealed class IntegrationStartup : IStartupRegistration
    {
        public StartupRegistrationResult Read() => new(StartupRegistrationState.Disabled, "integration-check");

        public StartupRegistrationResult Set(bool enabled) => new(StartupRegistrationState.Disabled, "integration-check");
    }

    private sealed class IntegrationClipboard : IClipboardWriter
    {
        public bool TrySetText(string text) => true;
    }
}
