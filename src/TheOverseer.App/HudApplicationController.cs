using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Threading.Channels;
using Microsoft.Win32;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
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
    bool TrayEnabled,
    IHudPersistence? Persistence = null,
    NotificationCoordinator? Notifications = null);

/// <summary>
/// Composition and lifetime owner for normal HUD mode: one monitor, one window,
/// one tray icon, coalesced snapshot delivery and serialized persistence.
/// </summary>
public sealed partial class HudApplicationController : IAsyncDisposable
{
    private static readonly HudSize ExpandedSize = new(360, 480);
    private const double ActivityChartWidth = 330;
    private const double ActivityChartHeight = 30;

    private readonly HudControllerDependencies _dependencies;
    private readonly SnapshotPump _pump;
    private Task _saveTask = Task.CompletedTask;
    private Task? _shutdownTask;
    private bool _disposed;
    private bool _monitorFailed;
    private readonly DispatcherTimer _ageTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<AgentTask> _lastTasks = new();
    private readonly Channel<(MonitorLogLevel Level, string Code)> _logQueue = Channel.CreateBounded<(MonitorLogLevel, string)>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _logTask;

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
        _voiceSettings = dependencies.Config.Voice.Normalized();
        _logTask = Task.Run(async () =>
        {
            await foreach (var entry in _logQueue.Reader.ReadAllAsync())
                dependencies.Log.Write(entry.Level, entry.Code);
        });
        _state = (dependencies.WindowState ?? HudWindowState.Default).Normalized();
        ViewModel = new HudViewModel(dependencies.Config.Hud, OnActivateTask, OnCopyTaskId,
            dependencies.TimeProvider.GetUtcNow());
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
        _suppressSettings = true;
        try
        {
            // The shared mute state starts from the persisted voice settings.
            ViewModel.IsMuted = dependencies.Notifications?.IsMuted ?? dependencies.Config.Voice.Muted;
        }
        finally
        {
            _suppressSettings = false;
        }

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
        _dependencies.Notifications?.Start();
        SubscribeMonitor();
        RefreshTrayState();
        RefreshActivity(_dependencies.TimeProvider.GetUtcNow());
        return Task.CompletedTask;
    }

    // ---- window -----------------------------------------------------------------

    private void CreateWindow()
    {
        _window = _dependencies.WindowFactory();
        _window.Icon = AppIcon.WindowImage;
        _window.DataContext = ViewModel;
        _window.PlacementChangedByUser += (_, _) =>
        {
            _dpiScale = _dependencies.Placement.GetDpiScale(_handle, null);
            _state = CaptureState();
            ApplyGeometry(show: false);
            ScheduleStateSave();
        };
        _window.DpiChanged += (_, _) => _window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_shuttingDown || _applyingGeometry) return;
            _dpiScale = _dependencies.Placement.GetDpiScale(_handle, null);
            _state = CaptureState();
            ApplyGeometry(show: false);
            ScheduleStateSave();
        }));
        _window.Closing += OnWindowClosing;
        CreateWindowMenu();

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
        size = new HudSize(size.Width + ViewModel.LightExtraWidth, size.Height + ViewModel.LightExtraHeight);
        if (ViewModel.IsExpanded) size = HudPlacementPolicy.BoundExpandedSize(target, size, _dpiScale, 8);

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
            if (!TrayAvailable)
            {
                ViewModel.NotifyStatus("Tray unavailable — HUD stays visible. Right-click the header for controls.");
                return;
            }

            _state = CaptureState();
            _window.Hide();
        }
        else
        {
            _window.Show();
            ApplyGeometry(show: false);
        }

        RefreshTrayState();
    }

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
                _ = task.Exception; // Observe faults without logging potentially private exception text.
                var code = task.IsCanceled ? "monitor-cancelled" : task.IsFaulted ? "monitor-faulted" : "monitor-stopped";
                _dependencies.Dispatcher.Post(() => OnMonitorFailed(code));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Producer callback. Plan 3 observation happens here, BEFORE the lossy UI
    /// pump, so every attention episode and clearance is seen even when the UI
    /// coalesces many snapshots into one render. Everything in this path is
    /// in-memory and bounded: no I/O, no HTTP, no audio and no Dispatcher work.
    /// </summary>
    private void OnSnapshot(MonitoringSnapshot snapshot)
    {
        _dependencies.Notifications?.OnSnapshot(snapshot);
        _pump.OnSnapshot(snapshot);
    }

    private void ApplySnapshot(MonitoringSnapshot snapshot)
    {
        if (_shuttingDown || _monitorFailed)
        {
            return;
        }

        RefreshActivity(snapshot.GeneratedAt);

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

        _monitorFailed = true;
        _pump.Stop();
        _lastTasks.Clear();
        ViewModel.NotifyMonitorFailure(code);
        _dependencies.Notifications?.OnMonitorFailure(code);
        Log(MonitorLogLevel.Error, "hud-monitor-" + code);
        RefreshTrayState();
    }

    // ---- settings, startup, navigation ------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HudViewModel.LightPosition) or nameof(HudViewModel.LightSize) && !_shuttingDown)
            ApplyGeometry(show: false);
        if (e.PropertyName is nameof(HudViewModel.Theme) && !_shuttingDown) ApplyTheme();
        if (e.PropertyName is nameof(HudViewModel.IsExpanded))
        {
            if (!_shuttingDown)
            {
                ApplyGeometry(show: _window?.IsVisible ?? false);
                ScheduleStateSave();
            }

            return;
        }

        if (e.PropertyName is nameof(HudViewModel.IsMuted) && !_shuttingDown)
        {
            if (ViewModel.IsMuted) StopVoicePreview();
            _voiceSettings = _voiceSettings with { Muted = ViewModel.IsMuted };
            _dependencies.Notifications?.SetMuted(ViewModel.IsMuted);
            RefreshTrayState();
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

    /// <summary>Shared mute used by the header, the tray and the header menu.</summary>
    public void ToggleMute()
    {
        ViewModel.IsMuted = !ViewModel.IsMuted;
        ViewModel.NotifyStatus(ViewModel.IsMuted
            ? "Voice muted. Current and queued notifications were stopped."
            : "Voice unmuted. Only new events will be announced.");
        RefreshTrayState();
    }

    public void ToggleTheme()
    {
        ViewModel.Theme = ViewModel.Theme == HudTheme.Light ? HudTheme.Dark : HudTheme.Light;
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
        Log(MonitorLogLevel.Info, "hud-navigate-" + result.Outcome.ToString().ToLowerInvariant());
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
            EnsureReachableWithoutTray();
            return;
        }

        var callbacks = new TrayCallbacks(
            ToggleVisibility,
            ToggleTheme,
            ToggleTopmost,
            SetOpacity,
            ToggleStartWithWindows,
            ToggleMute,
            ResetPosition,
            () => ExitRequested?.Invoke(this, EventArgs.Empty),
            OpenNotificationSettings);
        var (service, availability) = TrayService.TryCreate(callbacks);
        _tray = service;
        TrayAvailable = availability == TrayAvailability.Available;
        if (!TrayAvailable)
        {
            _trayFailures++;
            Log(MonitorLogLevel.Warning, "hud-tray-unavailable");
            ViewModel.NotifyStatus("Tray icon unavailable — use the HUD window to control it.");
            EnsureReachableWithoutTray();
        }
    }

    private void EnsureReachableWithoutTray()
    {
        if (_window is null) return;
        _window.Show();
        ApplyGeometry(show: false);
        ViewModel.NotifyStatus("Tray unavailable — right-click the header for controls.");
    }

    private void CreateWindowMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu { Cursor = System.Windows.Input.Cursors.Arrow };
        void Add(string label, Action action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Expand / collapse", () => ViewModel.IsExpanded = !ViewModel.IsExpanded);
        var lightMenu = new System.Windows.Controls.MenuItem { Header = "Status light" };
        foreach (var position in new[] { "right", "left", "top", "bottom", "off" })
        {
            var item = new System.Windows.Controls.MenuItem { Header = char.ToUpperInvariant(position[0]) + position[1..], IsCheckable = true };
            item.Click += (_, _) => ViewModel.LightPosition = position;
            lightMenu.SubmenuOpened += (_, _) => item.IsChecked = ViewModel.LightPosition == position;
            lightMenu.Items.Add(item);
        }
        lightMenu.Items.Add(new System.Windows.Controls.Separator());
        foreach (var size in new[] { 24, 32, 48, 64 })
        {
            var item = new System.Windows.Controls.MenuItem { Header = $"Width {size}", IsCheckable = true };
            item.Click += (_, _) => ViewModel.LightSize = size;
            lightMenu.SubmenuOpened += (_, _) => item.IsChecked = ViewModel.LightSize == size;
            lightMenu.Items.Add(item);
        }
        menu.Items.Add(lightMenu);
        Add("Light / dark", ToggleTheme);
        Add("Toggle always on top", ToggleTopmost);
        foreach (var opacity in new[] { 0.75, 0.85, 0.96, 1.0 })
        {
            var value = opacity;
            Add("Opacity " + (int)Math.Round(value * 100) + "%", () => SetOpacity(value));
        }
        Add("Mute / unmute voice", ToggleMute);
        Add("Light & voice…", OpenNotificationSettings);
        Add("Toggle start with Windows", ToggleStartWithWindows);
        Add("Reset position", ResetPosition);
        var monitoring = new System.Windows.Controls.MenuItem { Header = "Monitoring details" };
        foreach (var property in new[] { nameof(HudViewModel.MonitoringDisclosure), nameof(HudViewModel.AttentionDisclosure), nameof(HudViewModel.HealthLine) })
        {
            var detail = new System.Windows.Controls.MenuItem { IsEnabled = false };
            detail.SetBinding(System.Windows.Controls.HeaderedItemsControl.HeaderProperty,
                new System.Windows.Data.Binding(property) { Source = ViewModel });
            monitoring.Items.Add(detail);
        }
        menu.Items.Add(monitoring);
        Add("Exit", () => ExitRequested?.Invoke(this, EventArgs.Empty));
        _window!.ContextMenu = menu;
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
        if (!ViewModel.IsMuted)
        {
            status += " · voice on";
        }

        _tray.UpdateState(_window.IsVisible, ViewModel.Theme, ViewModel.AlwaysOnTop, ViewModel.StartWithWindows, ViewModel.IsMuted, status);
    }

    // ---- timers, persistence ----------------------------------------------------

    private void OnAgeTick(object? sender, EventArgs e)
    {
        if (_shuttingDown)
        {
            return;
        }

        var now = _dependencies.TimeProvider.GetUtcNow();
        ViewModel.UpdateAges(now);
        RefreshActivity(now);
    }

    /// <summary>
    /// Timer-driven Plan 3 projection: flush completion aggregation and pending
    /// cooldowns, then project history/voice onto the UI. Cheap, coalesced and
    /// on the UI thread only.
    /// </summary>
    public void RefreshActivity(DateTimeOffset now)
    {
        var notifications = _dependencies.Notifications;
        if (notifications is null)
        {
            return;
        }

        notifications.Pump();
        var series = notifications.History(now);
        ViewModel.ApplyActivity(series, notifications.Status, ActivityChartWidth, ActivityChartHeight);
    }

    private void OnSaveTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        if (_stateDirty)
        {
            if (_saveTask.IsCompleted) _saveTask = SaveAsync();
            else _saveTimer.Start();
        }
    }

    private void ScheduleStateSave()
    {
        _stateDirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync()
    {
        _stateDirty = false;
        _state = CaptureState();
        var state = _state;
        // Capture immutable UI state plus the live voice settings at save time;
        // the older dependency snapshot must never overwrite them.
        var voice = (_dependencies.Notifications?.Settings ?? _voiceSettings).Normalized() with
        {
            Muted = ViewModel.IsMuted,
        };
        var config = _dependencies.Config with
        {
            Hud = new HudSettings
            {
                Theme = ViewModel.ThemeName,
                AlwaysOnTop = ViewModel.AlwaysOnTop,
                Opacity = ViewModel.Opacity,
                StartWithWindows = ViewModel.StartWithWindows,
                TaskView = ViewModel.TaskView,
                RecentHours = ViewModel.RecentHours,
                LightPosition = ViewModel.LightPosition,
                LightSize = ViewModel.LightSize,
            },
            Voice = voice,
        };
        var writer = _dependencies.Persistence ?? new FileHudPersistence();
        var result = await Task.Run(() => writer.Save(_dependencies.Paths, state, config));
        if (!result.StateSaved || !result.SettingsSaved)
        {
            Log(MonitorLogLevel.Warning, "hud-save-failed");
            ViewModel.NotifyStatus("Settings or position could not be saved.");
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

    private void OnViewModelSettingsChanged()
    {
        if (_suppressSettings || _shuttingDown)
        {
            return;
        }

        ScheduleStateSave();
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
            Log(MonitorLogLevel.Warning, "hud-system-events-unavailable");
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

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => NotifyDisplaySettingsChanged();

    /// <summary>
    /// Display layout changed: keep the window reachable and ask the monitor to
    /// reconcile. Public so the lifecycle path is testable without a real
    /// display change; no task state is invented here.
    /// </summary>
    public void NotifyDisplaySettingsChanged() =>
        _dependencies.Dispatcher.Post(() =>
        {
            if (_shuttingDown)
            {
                return;
            }

            ApplyGeometry(show: _window?.IsVisible ?? false);
            _dependencies.Monitor.RequestReconcile();
        });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) =>
        NotifyPowerModeChanged(e.Mode);

    /// <summary>
    /// Power transitions. Suspend is handled inline on the events thread because
    /// a posted callback would not run before the machine sleeps; it discards
    /// cues that would otherwise play stale after wake. Resume re-runs the
    /// display repair and the reconcile on the UI thread. Both paths repeat the
    /// normal display-change handling, so a session that comes back with a
    /// different monitor set is repaired instead of assumed unchanged.
    /// </summary>
    public void NotifyPowerModeChanged(PowerModes mode)
    {
        switch (mode)
        {
            case PowerModes.Suspend:
                _dependencies.Notifications?.OnSuspend();
                Log(MonitorLogLevel.Info, "hud-suspend-voice-discarded");
                break;
            case PowerModes.Resume:
                _dependencies.Notifications?.OnResume();
                _dependencies.Dispatcher.Post(() =>
                {
                    if (_shuttingDown)
                    {
                        return;
                    }

                    ApplyGeometry(show: _window?.IsVisible ?? false);
                    _dependencies.Monitor.RequestReconcile();
                    Log(MonitorLogLevel.Info, "hud-resume-reconcile");
                });
                break;
            default:
                break;
        }
    }

    // ---- shutdown ---------------------------------------------------------------

    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _notificationSettings?.Close();
        StopVoicePreview();
        _ageTimer.Stop();
        _saveTimer.Stop();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _dependencies.Monitor.SnapshotAvailable -= OnSnapshot;
        _pump.Stop();
        UnsubscribeSystemEvents();

        _lifetime.Cancel();
        var voiceShutdown = _dependencies.Notifications?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        // A slow disk must not strand Exit. There is at most one in-flight write;
        // if it times out, do not start another writer against the same files.
        await TryStepAsync("hud-shutdown-save", async () =>
        {
            await _saveTask.WaitAsync(TimeSpan.FromSeconds(3));
            _saveTask = SaveAsync();
            await _saveTask.WaitAsync(TimeSpan.FromSeconds(3));
        });
        await TryStepAsync("hud-shutdown-voice", () => voiceShutdown);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await _dependencies.Monitor.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Log(MonitorLogLevel.Warning, "hud-monitor-dispose-timeout");
        }
        catch (TimeoutException)
        {
            Log(MonitorLogLevel.Warning, "hud-monitor-dispose-timeout");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Log(MonitorLogLevel.Error, "hud-shutdown-monitor");
        }

        try
        {
            _tray?.Dispose();
            _tray = null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            Log(MonitorLogLevel.Error, "hud-shutdown-tray");
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
                Log(MonitorLogLevel.Warning, "hud-shutdown-window-disposed");
            }
            catch (InvalidOperationException)
            {
                // The window is already closing (for example the close request
                // that started this shutdown), which is not an error.
                Log(MonitorLogLevel.Info, "hud-shutdown-window-already-closing");
            }
        }

        Log(MonitorLogLevel.Info, "hud-shutdown-complete");
        _logQueue.Writer.TryComplete();
        try { await _logTask.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { /* A stalled log disk must not block Exit. */ }
        (_dependencies.Log as IDisposable)?.Dispose();
    }

    private void Log(MonitorLogLevel level, string code) => _logQueue.Writer.TryWrite((level, code));

    private async Task TryStepAsync(string code, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException or TimeoutException)
        {
            Log(MonitorLogLevel.Error, code);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(true);
        if (_disposed) return;
        _disposed = true;
        _ageTimer.Tick -= OnAgeTick;
        _saveTimer.Tick -= OnSaveTick;
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
