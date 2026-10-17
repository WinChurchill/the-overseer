using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using TheOverseer.App.Desktop;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.UI;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Hidden, finite endurance check of the production composition and readers.
/// Invented producers and owned paths only; this does not qualify real host/UI use.
/// Resource budgets are regression gates, not new product guarantees.
/// </summary>
internal static class HudEnduranceCheckRunner
{
    private sealed record Sample(double Seconds, long PrivateBytes, int Handles, int Threads,
        double CpuSeconds, long DiskBytes, long AppliedSnapshots, double LatencyMs);
    private sealed record Check(string Name, bool Passed);

    public static async Task<int> RunAsync(StartupArguments arguments, Dispatcher dispatcher)
    {
        // Never open a window or interpret an explicit caller path as an owned data directory.
        if (!arguments.StartInTray || !arguments.NoTray || string.IsNullOrWhiteSpace(arguments.OutputDirectory))
            return 2;
        var root = Path.Combine(Path.GetFullPath(arguments.OutputDirectory), "endurance-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var home = Path.Combine(root, "invented-codex");
        Directory.CreateDirectory(data);
        var config = MonitorConfig.Default with { RuntimeActivitySourceEnabled = false };
        File.WriteAllText(Path.Combine(data, MonitorConfigStore.FileName), JsonSerializer.Serialize(config));
        var codex = new SyntheticProducers.SyntheticCodexProducer(home);
        var dsh = new SyntheticProducers.SyntheticBridgeProducer(data, HarnessIds.Dsh, "endurance-dsh");
        var claude = new SyntheticProducers.SyntheticBridgeProducer(data, HarnessIds.Claude, "endurance-claude");
        codex.WriteWorkingTask();
        dsh.Start("dsh-synthetic", @"C:\synthetic\dsh-project");
        claude.Start("claude-synthetic", @"C:\synthetic\claude-project");
        var prepared = await HudComposition.ComposeAsync(arguments.WithPaths(home, data), dispatcher,
            new HudCompositionOverrides
            {
                WindowFactory = () => new HudWindow { ShowActivated = false },
                TrayAvailableOverride = true,
                Startup = new InertStartup(), Navigator = new InertNavigator(), Clipboard = new InertClipboard(),
                BudgetLabel = "endurance-check",
            });
        var controller = prepared.Controller;
        var vm = controller.ViewModel;
        var checks = new List<Check>();
        var samples = new List<Sample>();
        // Published before starting: defaults bound reads to 4MiB/pass and history
        // to one hour. These conservative process regression ceilings supplement them.
        var budgets = new { warmupSeconds = 60, privateMiB = 384, growthMiB = 96,
            handleGrowth = 128, threadGrowth = 16, averageOneCoreCpuPercent = 25,
            diskMiB = 64, transitionSeconds = 15, maximumRows = 3 };
        File.WriteAllText(Path.Combine(root, "budgets.json"), JsonSerializer.Serialize(budgets));
        Console.WriteLine("endurance evidence: " + root);
        using var process = Process.GetCurrentProcess();
        var timer = Stopwatch.StartNew();
        var transitions = 0;
        try
        {
            await controller.StartAsync(CancellationToken.None);
            checks.Add(new("three-sources-applied", await WaitAsync(() => vm.Tasks.Count == 3)));
            checks.Add(new("hidden-window", controller.Window is { IsVisible: false }));
            checks.Add(new("working-projects-auto-expand", vm.Projects.Count > 0 && vm.Projects.All(p => p.IsExpanded)));
            var end = DateTime.UtcNow.Add(arguments.Duration);
            while (DateTime.UtcNow < end)
            {
                var call = "endurance-" + transitions;
                var step = Stopwatch.StartNew();
                await Task.Run(() => { codex.RequestAsyncInput(call); dsh.ApprovalAsked(call); claude.ApprovalAsked(call); });
                if (!await WaitAsync(() => vm.Tasks.Count == 3 && vm.Tasks.All(t => t.NeedsHuman)))
                    throw new InvalidOperationException("attention transition timed out");
                await Task.Run(() => { codex.AnswerAsyncInput(call, 0); dsh.ApprovalDecided(call); claude.ApprovalDecided(call); });
                if (!await WaitAsync(() => vm.Tasks.Count == 3 && vm.Tasks.All(t => !t.NeedsHuman)))
                    throw new InvalidOperationException("answer transition timed out");
                transitions++;
                process.Refresh();
                var disk = await Task.Run(() => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length));
                samples.Add(new(timer.Elapsed.TotalSeconds, process.PrivateMemorySize64, process.HandleCount,
                    process.Threads.Count, process.TotalProcessorTime.TotalSeconds, disk, vm.AppliedSnapshots, step.Elapsed.TotalMilliseconds));
                if (transitions % 6 == 0)
                {
                    File.WriteAllText(Path.Combine(root, "progress.json"), JsonSerializer.Serialize(new
                    { elapsedSeconds = timer.Elapsed.TotalSeconds, transitions, latest = samples[^1] }));
                    Console.WriteLine($"endurance {timer.Elapsed.TotalSeconds:F0}s: {transitions} cycles, {vm.AppliedSnapshots} snapshots");
                }
                await Task.Delay(TimeSpan.FromSeconds(4));
            }
            var measured = samples.Where(s => s.Seconds >= budgets.warmupSeconds).ToArray();
            checks.Add(new("duration", timer.Elapsed >= arguments.Duration));
            checks.Add(new("post-warmup-samples", measured.Length >= 2));
            if (measured.Length >= 2)
            {
                var first = measured[0]; var last = measured[^1];
                checks.Add(new("private-memory-ceiling", measured.Max(s => s.PrivateBytes) <= budgets.privateMiB * 1024L * 1024));
                checks.Add(new("private-memory-growth", last.PrivateBytes - first.PrivateBytes <= budgets.growthMiB * 1024L * 1024));
                checks.Add(new("handle-growth", last.Handles - first.Handles <= budgets.handleGrowth));
                checks.Add(new("thread-growth", last.Threads - first.Threads <= budgets.threadGrowth));
                checks.Add(new("average-cpu", 100 * (last.CpuSeconds - first.CpuSeconds) / (last.Seconds - first.Seconds) <= budgets.averageOneCoreCpuPercent));
                checks.Add(new("disk-budget", measured.Max(s => s.DiskBytes) <= budgets.diskMiB * 1024L * 1024));
                checks.Add(new("bounded-latency", measured.Max(s => s.LatencyMs) <= 2 * budgets.transitionSeconds * 1000));
                checks.Add(new("snapshots-continue", last.AppliedSnapshots > first.AppliedSnapshots));
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            checks.Add(new("execution-" + error.GetType().Name, false));
            Console.WriteLine("endurance failure: " + error.Message);
        }
        finally
        {
            await controller.DisposeAsync();
            checks.Add(new("state-flushed", File.Exists(Path.Combine(data, HudStateStore.FileName))));
        }
        var failed = checks.Count(c => !c.Passed);
        File.WriteAllText(Path.Combine(root, "endurance-report.json"), JsonSerializer.Serialize(new
        {
            provenance = "synthetic-producers-real-hidden-composition", elapsedSeconds = timer.Elapsed.TotalSeconds,
            requestedSeconds = arguments.Duration.TotalSeconds, transitions, budgets,
            passed = checks.Count - failed, failed, checks, samples,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }

    private static async Task<bool> WaitAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline) { if (predicate()) return true; await Task.Delay(100); }
        return predicate();
    }
    private sealed class InertStartup : IStartupRegistration
    {
        public StartupRegistrationResult Read() => new(StartupRegistrationState.Disabled, "endurance-check");
        public StartupRegistrationResult Set(bool enabled) => Read();
    }
    private sealed class InertNavigator : ITaskNavigator
    {
        public TaskNavigationResult Navigate(AgentTask task, string profileId) => new(TaskNavigationOutcome.Requested, "endurance-check: no real launch", null);
    }
    private sealed class InertClipboard : IClipboardWriter { public bool TrySetText(string text) => false; }
}
