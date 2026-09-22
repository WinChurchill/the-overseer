using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.App;

public interface IStartupRegistration
{
    StartupRegistrationResult Read();

    StartupRegistrationResult Set(bool enabled);
}

public interface ITaskNavigator
{
    TaskNavigationResult Navigate(AgentTask task, string profileId);
}

public interface IClipboardWriter
{
    bool TrySetText(string text);
}

public sealed class WpfClipboardWriter : IClipboardWriter
{
    public bool TrySetText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}

internal sealed class StartupRegistrationAdapter : IStartupRegistration
{
    private readonly StartupRegistration _registration;

    public StartupRegistrationAdapter(StartupRegistration registration) => _registration = registration;

    public StartupRegistrationResult Read() => _registration.Read();

    public StartupRegistrationResult Set(bool enabled) => _registration.Set(enabled);
}

internal sealed class TaskNavigatorAdapter : ITaskNavigator
{
    private readonly CodexTaskNavigator _navigator;

    public TaskNavigatorAdapter(CodexTaskNavigator navigator) => _navigator = navigator;

    public TaskNavigationResult Navigate(AgentTask task, string profileId) => _navigator.Navigate(task, profileId);
}

public sealed record HudControllerPaths(string DataDirectory, string ConfigPath, string StatePath);

public sealed record HudControllerDependencies(
    IMonitorSource Monitor,
    IUiDispatcher Dispatcher,
    IWindowPlacementService Placement,
    ITaskNavigator Navigator,
    IStartupRegistration Startup,
    IClipboardWriter Clipboard,
    IMonitorLog Log,
    MonitorConfig Config,
    HudWindowState WindowState,
    HudControllerPaths Paths,
    Func<HudWindow> WindowFactory,
    TimeProvider TimeProvider,
    bool StartHidden,
    bool TrayEnabled);

/// <summary>
/// Composition and lifetime owner for normal HUD mode: one monitor, one window,
/// one tray icon, coalesced snapshot delivery and serialized persistence.
/// </summary>
public sealed class HudApplicationController : IAsyncDisposable
{
    private static readonly HudSize ExpandedSize = new(360, 392);

    private readonly HudControllerDependencies _dependencies;
    private readonly SnapshotPump _pump;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly DispatcherTimer _ageTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<AgentTask> _lastTasks = new();

    private HudWindow? _window;
    private TrayService? _tray;
    private IntPtr _handle;
    private double _dpiScale = 1.0;
    private bool _applyingGeometry;
    private HudWindowState _state;
    private bool _shuttingDown;
    private bool _suppressSettings;
    private bool _stateDirty;
    private int _trayFailures;
    private string _startupCode = "not-read";
    private string _navigationCode = "none";

    public HudApplicationController(HudControllerDependencies dependencies)
    {
        _dependencies = dependencies;
        _state = (dependencies.WindowState ?? HudWindowState.Default).Normalized();
        ViewModel = new HudViewModel(dependencies.Config.Hud, OnActivateTask, OnCopyTaskId);
        _pump = new SnapshotPump(dependencies.Dispatcher, ApplySnapshot);
        _ageTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _ageTimer.Tick += OnAgeTick;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _saveTimer.Tick += OnSaveTick;
        ViewModel.IsExpanded = _state.IsExpanded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.SettingsChanged += (_, _) =>
        {
            OnViewModelSettingsChanged();
            RefreshTrayState();
        };
    }

    public HudViewModel ViewModel { get; }

    public HudWindow? Window => _window;

    public bool TrayAvailable { get; private set; }

    public string StartupCode => _startupCode;

    public string NavigationCode => _navigationCode;

    public bool IsShuttingDown => _shuttingDown;

    public event EventHandler? ExitRequested;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        ApplyTheme();
        CreateWindow();
        CreateTray();
        SyncStartupState();
        SubscribeSystemEvents();
        _ageTimer.Start();
        SubscribeMonitor();
        RefreshTrayState();
        return Task.CompletedTask;
    }

    // ---- window -----------------------------------------------------------------

    private void CreateWindow()
    {
        _window = _dependencies.WindowFactory();
        _window.DataContext = ViewModel;
        _window.PlacementChangedByUser += (_, _) => ScheduleStateSave();
        _window.Closing += OnWindowClosing;

        var helper = new WindowInteropHelper(_window);
        _handle = helper.EnsureHandle();
        _dpiScale = _dependencies.Placement.GetDpiScale(_handle, null);

        // Position first, then show through WPF so IsVisible/layout are real.
        // ShowActivated=false keeps the HUD from taking focus.
        ApplyGeometry(show: false);
        if (!_dependencies.StartHidden)
        {
            _window.Show();
            _window.UpdateLayout();
    
            // The window now lives on its real monitor, so the DPI read here is
            // authoritative: re-clamp and re-position with it.
            ApplyGeometry(show: false);
    
            // One deferred pass after the first render: WPF's device transform can
            // settle a moment later, and this corrects size and position without
            // ever activating the window.
            _window.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    if (!_shuttingDown && _window is { IsVisible: true })
                    {
                        ApplyGeometry(show: false);
                    }
                }));
        }
    }

    /// <summary>Recomputes size/position from state and the live monitor layout.</summary>
    private void ApplyGeometry(bool show)
    {
        if (_window is null || _handle == IntPtr.Zero || _applyingGeometry)
        {
            return;
        }

        _applyingGeometry = true;
        try
        {
            ApplyGeometryCore(show);
        }
        finally
        {
            _applyingGeometry = false;
        }
    }

    private void ApplyGeometryCore(bool show)
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        var layout = _dependencies.Placement.CaptureLayout();
        var target = ResolveTargetScreen(layout);
        _dpiScale = _dependencies.Placement.GetDpiScale(_handle, target);

        var size = ViewModel.IsExpanded
            ? HudPlacementPolicy.BoundExpandedSize(target, ExpandedSize, _dpiScale, 8)
            : HudSize.Collapsed;

        // WPF owns the size (DIPs); the placement policy only decides where the
        // resulting window goes and keeps it reachable.
        window.Width = size.Width;
        window.Height = size.Height;
        window.UpdateLayout();

        var proposed = HudPlacementPolicy.Resolve(_state, layout, size, _dpiScale);
        if (target is not null && NativeMethods.GetWindowRect(_handle, out var rect) && rect.Width > 0)
        {
            proposed = HudPlacementPolicy.Clamp(target, proposed.X, proposed.Y, rect.Width, rect.Height, proposed.FallbackMonitor, _state.MonitorDeviceName);
        }

        _dependencies.Placement.Apply(_handle, proposed, ViewModel.AlwaysOnTop, show);
        _state = _state with { MonitorDeviceName = proposed.DeviceName };
        ViewModel.NotifyPlacement(proposed.DeviceName, proposed.Clamped || proposed.FallbackMonitor);
    }

    private HudScreen? ResolveTargetScreen(HudScreenLayout layout)
    {
        if (layout.IsEmpty)
        {
            return null;
        }

        return layout.Find(_state.MonitorDeviceName) ?? layout.Primary;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shuttingDown)
        {
            return;
        }

        // Route every close request (system menu, Alt+F4, WM_CLOSE) through the
        // same orderly shutdown instead of letting the window vanish first.
        e.Cancel = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleVisibility()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible)
        {
            _window.Hide();
        }
        else
        {
            _window.Show();
            _dependencies.Placement.Apply(
                _handle,
                HudPlacementPolicy.Resolve(_state, _dependencies.Placement.CaptureLayout(), CurrentSize(), _dpiScale),
                ViewModel.AlwaysOnTop,
                showWithoutActivation: false);
        }

        RefreshTrayState();
    }

    private HudSize CurrentSize() => ViewModel.IsExpanded ? ExpandedSize : HudSize.Collapsed;

    public void ResetPosition()
    {
        _state = new HudWindowState { IsExpanded = ViewModel.IsExpanded };
        ApplyGeometry(show: _window?.IsVisible ?? false);
        ScheduleStateSave();
        ViewModel.NotifyStatus("Position reset.");
    }

    // ---- monitor ----------------------------------------------------------------

    private void SubscribeMonitor()
    {
        _dependencies.Monitor.SnapshotAvailable += OnSnapshot;
        Task monitorTask;
        try
        {
            monitorTask = _dependencies.Monitor.StartAsync(_lifetime.Token);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            OnMonitorFailed("monitor-start");
            return;
        }

        _ = monitorTask.ContinueWith(
            task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    var code = task.IsCanceled ? "monitor-cancelled" : "monitor-faulted";
                    _dependencies.Dispatcher.Post(() => OnMonitorFailed(code));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnSnapshot(MonitoringSnapshot snapshot) => _pump.OnSnapshot(snapshot);

    private void ApplySnapshot(MonitoringSnapshot snapshot)
    {
        if (_shuttingDown)
        {
            return;
        }

        ViewModel.ApplySnapshot(snapshot);
        _lastTasks.Clear();
        _lastTasks.AddRange(snapshot.Tasks);
        RefreshTrayState();
    }

    private void OnMonitorFailed(string code)
    {
        if (_shuttingDown)
        {
            return;
        }

        ViewModel.NotifyMonitorFailure(code);
        _dependencies.Log.Write(MonitorLogLevel.Error, "hud-monitor-" + code);
        RefreshTrayState();
    }

    // ---- settings, startup, navigation ------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HudViewModel.IsExpanded))
        {
            if (!_shuttingDown)
            {
                ApplyGeometry(show: _window?.IsVisible ?? false);
                ScheduleStateSave();
            }

            return;
        }

        if (e.PropertyName is nameof(HudViewModel.AlwaysOnTop) && !_shuttingDown)
        {
            _ = NativeMethods.SetWindowPos(
                _handle,
                ViewModel.AlwaysOnTop ? NativeMethods.HwndTopmost : NativeMethods.HwndNotTopmost,
                0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }
    }

    private void ApplyTheme()
    {
        var application = Application.Current;
        if (application is not null)
        {
            HudThemeManager.Apply(application, ViewModel.Theme);
        }
    }

    private void SyncStartupState()
    {
        var result = _dependencies.Startup.Read();
        _startupCode = result.ReasonCode;
        _suppressSettings = true;
        try
        {
            ViewModel.StartWithWindows = result.State == StartupRegistrationState.Enabled;
        }
        finally
        {
            _suppressSettings = false;
        }

        if (result.State is StartupRegistrationState.StalePath or StartupRegistrationState.Unavailable)
        {
            ViewModel.NotifyStatus(result.State == StartupRegistrationState.StalePath
                ? "Startup entry points to an old location; toggle it to repair."
                : "Startup setting unavailable (registry access denied).");
        }
    }

    public void ToggleStartWithWindows()
    {
        var requested = !ViewModel.StartWithWindows;
        var result = _dependencies.Startup.Set(requested);
        _startupCode = result.ReasonCode;
        _suppressSettings = true;
        try
        {
            ViewModel.StartWithWindows = result.State == StartupRegistrationState.Enabled;
        }
        finally
        {
            _suppressSettings = false;
        }

        ViewModel.NotifyStatus(result.State switch
        {
            StartupRegistrationState.Enabled => "Start with Windows enabled.",
            StartupRegistrationState.Disabled => "Start with Windows disabled.",
            StartupRegistrationState.StalePath => "Startup entry is stale; try again.",
            _ => "Startup could not be changed (registry access denied).",
        });
        RefreshTrayState();
    }

    public void ToggleTheme()
    {
        ViewModel.Theme = ViewModel.Theme == HudTheme.Light ? HudTheme.Dark : HudTheme.Light;
        ApplyTheme();
        RefreshTrayState();
    }

    public void ToggleTopmost()
    {
        ViewModel.AlwaysOnTop = !ViewModel.AlwaysOnTop;
        RefreshTrayState();
    }

    public void SetOpacity(double value)
    {
        ViewModel.Opacity = value;
        RefreshTrayState();
    }

    private void OnActivateTask(TaskRowViewModel row)
    {
        var task = _lastTasks.Find(candidate => candidate.Key.Equals(row.Key));
        if (task is null)
        {
            ViewModel.NotifyStatus("Task is no longer in view.");
            return;
        }

        var result = _dependencies.Navigator.Navigate(task, task.ProfileId);
        _navigationCode = result.Outcome.ToString();
        ViewModel.NotifyStatus(result.UserMessage);
        _dependencies.Log.Write(MonitorLogLevel.Info, "hud-navigate-" + result.Outcome.ToString().ToLowerInvariant());
    }

    private void OnCopyTaskId(TaskRowViewModel row)
    {
        var copied = _dependencies.Clipboard.TrySetText(row.Key.ConversationId);
        ViewModel.NotifyStatus(copied ? "Task id copied." : "Could not copy the task id.");
    }

    // ---- tray -------------------------------------------------------------------

    private void CreateTray()
    {
        if (!_dependencies.TrayEnabled)
        {
            TrayAvailable = false;
            return;
        }

        var callbacks = new TrayCallbacks(
            ToggleVisibility,
            ToggleTheme,
            ToggleTopmost,
            SetOpacity,
            ToggleStartWithWindows,
            ResetPosition,
            () => ExitRequested?.Invoke(this, EventArgs.Empty));
        var (service, availability) = TrayService.TryCreate(callbacks);
        _tray = service;
        TrayAvailable = availability == TrayAvailability.Available;
        if (!TrayAvailable)
        {
            _trayFailures++;
            _dependencies.Log.Write(MonitorLogLevel.Warning, "hud-tray-unavailable");
            ViewModel.NotifyStatus("Tray icon unavailable — use the HUD window to control it.");
        }
    }

    private void RefreshTrayState()
    {
        if (_tray is null || _window is null)
        {
            return;
        }

        var status = ViewModel.MonitoringUnavailable
            ? "monitor unavailable"
            : ViewModel.SummaryText;
        _tray.UpdateState(_window.IsVisible, ViewModel.Theme, ViewModel.AlwaysOnTop, ViewModel.StartWithWindows, status);
    }

    // ---- timers, persistence ----------------------------------------------------

    private void OnAgeTick(object? sender, EventArgs e)
    {
        if (!_shuttingDown)
        {
            ViewModel.UpdateAges(_dependencies.TimeProvider.GetUtcNow());
        }
    }

    private void OnSaveTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        if (_stateDirty)
        {
            _ = SaveStateAsync();
        }
    }

    private void ScheduleStateSave()
    {
        _stateDirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveStateAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(true);
        try
        {
            _stateDirty = false;
            _state = CaptureState();
            if (!HudStateStore.TrySave(_dependencies.Paths.StatePath, _state))
            {
                _dependencies.Log.Write(MonitorLogLevel.Warning, "hud-state-save-failed");
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private HudWindowState CaptureState()
    {
        if (_handle == IntPtr.Zero)
        {
            return _state;
        }

        return _dependencies.Placement.CaptureState(_handle, ViewModel.IsExpanded, _dpiScale, _dependencies.TimeProvider.GetUtcNow());
    }

    private async Task SaveSettingsAsync()
    {
        if (_suppressSettings)
        {
            return;
        }

        await _saveGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var config = _dependencies.Config with
            {
                Hud = new HudSettings
                {
                    Theme = ViewModel.ThemeName,
                    AlwaysOnTop = ViewModel.AlwaysOnTop,
                    Opacity = ViewModel.Opacity,
                    StartWithWindows = ViewModel.StartWithWindows,
                },
            };
            MonitorConfigStore.Save(_dependencies.Paths.ConfigPath, config);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _dependencies.Log.Write(MonitorLogLevel.Warning, "hud-config-save-failed");
            ViewModel.NotifyStatus("Settings could not be saved.");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void OnViewModelSettingsChanged()
    {
        if (_suppressSettings || _shuttingDown)
        {
            return;
        }

        _ = SaveSettingsAsync();
    }

    // ---- system events ----------------------------------------------------------

    private void SubscribeSystemEvents()
    {
        try
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (InvalidOperationException)
        {
            _dependencies.Log.Write(MonitorLogLevel.Warning, "hud-system-events-unavailable");
        }
    }

    private void UnsubscribeSystemEvents()
    {
        try
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dependencies.Dispatcher.Post(() =>
        {
            if (_shuttingDown)
            {
                return;
            }

            // Display layout changed: keep the window reachable and ask the
            // monitor to reconcile; no task state is invented here.
            ApplyGeometry(show: _window?.IsVisible ?? false);
            _dependencies.Monitor.RequestReconcile();
        });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _dependencies.Dispatcher.Post(() =>
        {
            if (_shuttingDown)
            {
                return;
            }

            ApplyGeometry(show: _window?.IsVisible ?? false);
            _dependencies.Monitor.RequestReconcile();
        });
    }

    // ---- shutdown ---------------------------------------------------------------

    public async Task ShutdownAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _ageTimer.Stop();
        _saveTimer.Stop();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _dependencies.Monitor.SnapshotAvailable -= OnSnapshot;
        _pump.Stop();
        UnsubscribeSystemEvents();

        await TryStepAsync("hud-shutdown-state", SaveStateAsync).ConfigureAwait(true);
        await TryStepAsync("hud-shutdown-settings", SaveSettingsAsync).ConfigureAwait(true);

        _lifetime.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _dependencies.Monitor.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _dependencies.Log.Write(MonitorLogLevel.Warning, "hud-monitor-dispose-timeout");
        }
        catch (TimeoutException)
        {
            _dependencies.Log.Write(MonitorLogLevel.Warning, "hud-monitor-dispose-timeout");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _dependencies.Log.Write(MonitorLogLevel.Error, "hud-shutdown-monitor");
        }

        try
        {
            _tray?.Dispose();
            _tray = null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            _dependencies.Log.Write(MonitorLogLevel.Error, "hud-shutdown-tray");
        }

        if (_window is not null)
        {
            try
            {
                _window.Closing -= OnWindowClosing;
                var window = _window;
                _window = null;
                window.Close();
            }
            catch (ObjectDisposedException)
            {
                _dependencies.Log.Write(MonitorLogLevel.Warning, "hud-shutdown-window-disposed");
            }
            catch (InvalidOperationException)
            {
                // The window is already closing (for example the close request
                // that started this shutdown), which is not an error.
                _dependencies.Log.Write(MonitorLogLevel.Info, "hud-shutdown-window-already-closing");
            }
        }

        _dependencies.Log.Write(MonitorLogLevel.Info, "hud-shutdown-complete");
    }

    private async Task TryStepAsync(string code, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
        {
            _dependencies.Log.Write(MonitorLogLevel.Error, code);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _ageTimer.Tick -= OnAgeTick;
        _saveTimer.Tick -= OnSaveTick;
        _saveGate.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>Finite status report used by the HUD smoke fixture.</summary>
    public string BuildStatusReport()
    {
        var window = _window;
        return string.Join(Environment.NewLine, new[]
        {
            "hud window: " + (window is null ? "none" : (window.IsVisible ? "visible" : "hidden") + " expanded=" + ViewModel.IsExpanded),
            "hud tray: " + (TrayAvailable ? "available" : "unavailable") + " failures=" + _trayFailures.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hud theme: " + ViewModel.ThemeName + " topmost=" + ViewModel.AlwaysOnTop + " opacity=" + ViewModel.OpacityPercentLabel +
                " dpi=" + _dpiScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            "hud placement: " + (ViewModel.PlacementLine.Length > 0 ? "resolved" : "none") +
                " expanded=" + ViewModel.IsExpanded + DescribeWindow(),
            "hud startup: " + _startupCode,
            "hud navigation: " + _navigationCode,
            "hud monitoring: " + ViewModel.MonitoringTitle + " · " + ViewModel.SummaryText,
            "hud rows: " + ViewModel.Tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    private string DescribeWindow()
    {
        if (_window is null)
        {
            return " window=none";
        }

        var wpfDpi = System.Windows.Media.VisualTreeHelper.GetDpi(_window).PixelsPerDip;
        var hwndDpi = _handle == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(_handle);
        _ = NativeMethods.GetWindowRect(_handle, out var rect);
        return " wpfDpi=" + wpfDpi.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
               " hwndDpi=" + hwndDpi.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " wpfSize=" + _window.ActualWidth.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x" +
               _window.ActualHeight.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
               " hwndRect=" + rect.Width.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" +
               rect.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

}