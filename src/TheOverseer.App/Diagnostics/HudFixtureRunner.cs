using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TheOverseer.App.Desktop;
using TheOverseer.Core.Configuration;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Explicit, isolated HUD diagnostics. Fixture mode renders synthetic previews;
/// smoke mode runs the whole production composition against a synthetic monitor
/// and exits cleanly. Neither registers startup, reads rollout files, contacts
/// tasks, nor replaces production input.
/// </summary>
internal static class HudFixtureRunner
{
    private const double CollapsedWidth = 280;
    private const double CollapsedHeight = 72;
    private const double ExpandedWidth = 360;
    private const double ExpandedHeight = 392;

    /// <summary>Schedules the preview render on the UI thread; returns immediately.</summary>
    public static void StartFixture(StartupArguments arguments, Dispatcher dispatcher)
    {
        var output = arguments.OutputDirectory;
        if (string.IsNullOrWhiteSpace(output))
        {
            output = Path.Combine(DataDirectoryResolver.Resolve(arguments.DataDirectory), "ui-fixture");
        }

        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var exit = 0;
            try
            {
                exit = RenderFixture(Path.GetFullPath(output));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.WriteLine("hud fixture failed: " + exception.GetType().Name);
                exit = 1;
            }
            finally
            {
                Application.Current?.Shutdown(exit);
            }
        }));
    }

    private static int RenderFixture(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var scenarios = SyntheticHudScenarios.Create();
        var settings = HudSettings.Default with { Opacity = 1.0, AlwaysOnTop = true };
        var viewModel = new HudViewModel(settings);
        HudThemeManager.Apply(Application.Current, HudTheme.Dark);
        var window = new HudWindow
        {
            DataContext = viewModel,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
        };

        window.Show();
        var rendered = new List<string>();
        try
        {
            foreach (var theme in new[] { HudTheme.Light, HudTheme.Dark })
            {
                HudThemeManager.Apply(Application.Current, theme);
                viewModel.ApplySnapshot(scenarios[0]);
                foreach (var expanded in new[] { false, true })
                {
                    viewModel.IsExpanded = expanded;
                    Resize(window, expanded);
                    window.UpdateLayout();
                    var path = Path.Combine(
                        outputDirectory,
                        theme.ToString().ToLowerInvariant() + "-" + (expanded ? "expanded" : "collapsed") + ".png");
                    Render(window, path);
                    rendered.Add(path);
                }
            }

            // Two extra diagnostic views: healthy-empty and unavailable never
            // look like a healthy zero or a frozen healthy summary.
            HudThemeManager.Apply(Application.Current, HudTheme.Dark);
            viewModel.IsExpanded = true;
            Resize(window, true);
            viewModel.ApplySnapshot(SyntheticHudScenarios.HealthyEmpty());
            window.UpdateLayout();
            var empty = Path.Combine(outputDirectory, "dark-empty.png");
            Render(window, empty);
            rendered.Add(empty);

            viewModel.ApplySnapshot(SyntheticHudScenarios.Unavailable());
            window.UpdateLayout();
            var unavailable = Path.Combine(outputDirectory, "dark-unavailable.png");
            Render(window, unavailable);
            rendered.Add(unavailable);
        }
        finally
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }

        Console.WriteLine("hud fixture rendered " + rendered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " views:");
        foreach (var path in rendered)
        {
            Console.WriteLine("  " + path);
        }

        return 0;
    }

    private static void Resize(Window window, bool expanded)
    {
        window.Width = expanded ? ExpandedWidth : CollapsedWidth;
        window.Height = expanded ? ExpandedHeight : CollapsedHeight;
    }

    private static void Render(Window window, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        const double scale = 2.0;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale),
            (int)Math.Ceiling(height * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// Live, reversible check of the owned current-user startup entry. It reads
    /// the original value, enables, verifies, disables, verifies, then restores
    /// the original state exactly. Only the OVERSEER value is ever touched.
    /// </summary>
    public static int RunStartupCheck()
    {
        var registry = new WindowsStartupRegistry();
        var registration = new StartupRegistration(registry, Environment.ProcessPath ?? string.Empty);
        var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WindowsStartupRegistry.RunKeyPath, writable: false);
        var original = runKey?.GetValue(StartupRegistration.ValueName) as string;
        runKey?.Dispose();

        Console.WriteLine("OVERSEER startup entry check (reversible, owned value only)");
        Console.WriteLine("executable: " + (Environment.ProcessPath ?? "<none>"));
        Console.WriteLine("usable path: " + StartupRegistration.IsUsableExecutablePath(Environment.ProcessPath));
        Console.WriteLine("original entry: " + (original is null ? "<absent>" : "\"" + original + "\""));
        Console.WriteLine("expected command: " + StartupRegistration.BuildCommand(Environment.ProcessPath ?? string.Empty));
        Console.WriteLine("initial read: " + registration.Read().State + " (" + registration.Read().ReasonCode + ")");

        var enable = registration.Set(true);
        Console.WriteLine("after enable: " + enable.State + " (" + enable.ReasonCode + ")");
        var readBack = registration.Read();
        Console.WriteLine("read back: " + readBack.State + " (" + readBack.ReasonCode + ")");

        var disable = registration.Set(false);
        Console.WriteLine("after disable: " + disable.State + " (" + disable.ReasonCode + ")");
        Console.WriteLine("read back: " + registration.Read().State);

        // Restore exactly what was there before this check.
        var restore = new StartupRegistration(new WindowsStartupRegistry(), Environment.ProcessPath ?? string.Empty);
        if (original is null)
        {
            _ = restore.Set(false);
            Console.WriteLine("restored: entry removed (was absent)");
        }
        else
        {
            var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(WindowsStartupRegistry.RunKeyPath, writable: true);
            key?.SetValue(StartupRegistration.ValueName, original, Microsoft.Win32.RegistryValueKind.String);
            key?.Dispose();
            Console.WriteLine("restored: original value written back");
        }

        var final = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WindowsStartupRegistry.RunKeyPath, writable: false);
        var finalValue = final?.GetValue(StartupRegistration.ValueName) as string;
        final?.Dispose();
        var matches = string.Equals(finalValue ?? string.Empty, original ?? string.Empty, StringComparison.Ordinal);
        Console.WriteLine("final state matches original: " + matches);
        Console.WriteLine("exit: " + (matches ? "0" : "1"));
        return matches ? 0 : 1;
    }

    /// <summary>Starts the finite integration smoke; completion shuts the app down.</summary>
    public static void StartSmoke(StartupArguments arguments, Dispatcher dispatcher)
    {
        var source = new SyntheticMonitorSource(SyntheticHudScenarios.Create());
        var dataDirectory = DataDirectoryResolver.Resolve(arguments.DataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var configPath = Path.Combine(dataDirectory, MonitorConfigStore.FileName);
        var statePath = Path.Combine(dataDirectory, HudStateStore.FileName);

        var dependencies = new HudControllerDependencies(
            source,
            new WpfUiDispatcher(dispatcher),
            new WindowsWindowPlacementService(),
            new FakeNavigator(),
            new FakeStartupRegistration(),
            new FakeClipboardWriter(),
            TheOverseer.Core.Diagnostics.NullMonitorLog.Instance,
            MonitorConfigStore.Load(configPath).Config,
            HudStateStore.Load(statePath).State,
            new HudControllerPaths(dataDirectory, configPath, statePath),
            () => new HudWindow(),
            TimeProvider.System,
            StartHidden: false,
            TrayEnabled: !arguments.NoTray);

        var controller = new HudApplicationController(dependencies);
        Console.WriteLine("OVERSEER HUD smoke (synthetic input, isolated data, finite run)");
        Console.WriteLine("data-directory: " + dataDirectory);
        Console.WriteLine("duration: " + arguments.Duration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s");

        var advance = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(3) };
        var scenario = 0;
        advance.Tick += (_, _) =>
        {
            scenario++;
            source.Push(scenario);
            controller.ViewModel.IsExpanded = scenario % 2 == 1;
            if (scenario == 2)
            {
                // Exercise a persisted display setting through the same path a
                // tray click uses.
                controller.ToggleTheme();
                controller.ViewModel.Opacity = 0.85;
            }
        };
        advance.Start();

        var finish = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = arguments.Duration };
        finish.Tick += async (_, _) =>
        {
            finish.Stop();
            advance.Stop();
            Console.WriteLine(controller.BuildStatusReport());
            await controller.ShutdownAsync().ConfigureAwait(true);
            Console.WriteLine("hud shutdown: clean");

            // Persistence is reported after shutdown has flushed it.
            var state = HudStateStore.Load(statePath);
            Console.WriteLine("hud persisted state: " + state.Status + " (" + state.ReasonCode + ") expanded=" + state.State.IsExpanded +
                              " monitor=" + (state.State.MonitorDeviceName.Length > 0 ? "saved" : "none"));
            var config = MonitorConfigStore.Load(configPath);
            Console.WriteLine("hud persisted config: " + config.Status + " (" + config.ReasonCode + ") theme=" + config.Config.Hud.ThemeName +
                              " opacity=" + config.Config.Hud.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                              " topmost=" + config.Config.Hud.AlwaysOnTop);
            Application.Current?.Shutdown(0);
        };
        finish.Start();

        _ = controller.StartAsync(CancellationToken.None);
    }

    private sealed class FakeNavigator : ITaskNavigator
    {
        public TaskNavigationResult Navigate(TheOverseer.Core.Models.AgentTask task, string profileId) =>
            new(TaskNavigationOutcome.Requested, "fixture: no real launch", null);
    }

    private sealed class FakeStartupRegistration : IStartupRegistration
    {
        public StartupRegistrationResult Read() => new(StartupRegistrationState.Disabled, "fixture");

        public StartupRegistrationResult Set(bool enabled) => new(StartupRegistrationState.Disabled, "fixture");
    }

    private sealed class FakeClipboardWriter : IClipboardWriter
    {
        public bool TrySetText(string text) => true;
    }
}