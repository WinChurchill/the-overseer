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

        var codex = new SyntheticProducers.SyntheticCodexProducer(codexHome);
        var dsh = new SyntheticProducers.SyntheticBridgeProducer(dataDirectory, HarnessIds.Dsh, "dsh-check");
        var claude = new SyntheticProducers.SyntheticBridgeProducer(dataDirectory, HarnessIds.Claude, "claude-check");
        codex.WriteWorkingTask();
        dsh.Start("session-dsh-1", @"C:\synthetic\dsh-project");
        claude.Start("session-claude-1", @"C:\synthetic\claude-project");

        var monitorOptions = MonitorOptions.Default with
        {
            KnownFilePollInterval = TimeSpan.FromMilliseconds(500),
            ReconcileInterval = TimeSpan.FromSeconds(1),
            DebounceInterval = TimeSpan.FromMilliseconds(100),
            StaleLease = TimeSpan.FromMinutes(10),
            // Shorter bounds than the defaults, so one finite run can observe a removed
            // file being released and its reason clearing instead of the default
            // one-minute backoff hiding the behaviour behind a timeout.
            MissingFileRetention = TimeSpan.FromSeconds(12),
            FailureBackoffInitial = TimeSpan.FromSeconds(1),
            FailureBackoffMax = TimeSpan.FromSeconds(4),
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

            // The removed file must actually be released: until then it keeps its own
            // reason on the source and its task unobservable. A latched reason would
            // leave the HUD at limited coverage forever, so this waits for the release
            // instead of asserting a weaker condition.
            var released = await WaitAsync(() => !viewModel.HealthDetails.Any(static row =>
                row.Source.Contains("Codex", StringComparison.OrdinalIgnoreCase) &&
                (row.State.Contains("file-missing", StringComparison.OrdinalIgnoreCase) ||
                 row.State.Contains("stale-tasks", StringComparison.OrdinalIgnoreCase))), 45);
            Check("released-file-stops-degrading-the-source", released, Summarize(viewModel));
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
