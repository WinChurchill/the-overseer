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
using TheOverseer.App.Monitoring;
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
    NotificationCoordinator? Notifications = null,
    bool? TrayAvailableOverride = null);

/// <summary>
/// Composition and lifetime owner for normal HUD mode: one monitor, one window,
/// one tray icon, coalesced snapshot delivery and serialized persistence.
/// </summary>
public sealed partial class HudApplicationController : IAsyncDisposable
{
    private static readonly HudSize ExpandedSize = new(360, 480);

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
    private ShowRequestSignal? _showRequest;
    private IntPtr _handle;
    private double _dpiScale = 1.0;
    private bool _applyingGeometry;
    private HudWindowState _state;
    private bool _shuttingDown;
    private bool _sessionEnding;
    private volatile bool _showPending;
    private bool _suppressSettings;
    private bool _stateDirty;
    private int _trayFailures;
    private string _startupCode = "not-read";
    private string _navigationCode = "none";
    private IReadOnlyList<string> _disabledHarnessSources = Array.Empty<string>();

    public HudApplicationController(HudControllerDependencies dependencies)
    {
        _dependencies = dependencies;
        _voiceSettings = dependencies.Config.Voice.Normalized();
        _disabledHarnessSources = dependencies.Config.DisabledHarnessSources;
        _logTask = Task.Run(async () =>
        {
            await foreach (var entry in _logQueue.Reader.ReadAllAsync())
                dependencies.Log.Write(entry.Level, entry.Code);
        });
        _state = (dependencies.WindowState ?? HudWindowState.Default).Normalized();
        ViewModel = new HudViewModel(dependencies.Config.Hud, OnActivateTask, OnCopyTaskId,
            dependencies.TimeProvider.GetUtcNow());
        if (dependencies.Notifications is { } notifications)
        {
            // Strips render producer-side observed activity; the UI only projects bins.
            ViewModel.UseActivitySource(new CoordinatorActivityStripSource(notifications, dependencies.TimeProvider));
        }
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

        // The show listener starts with the controller, before any window exists:
        // a second launch that arrives during startup is queued, not lost.
        _showRequest = ShowRequestSignal.TryListen(OnShowRequested, _dependencies.Paths.DataDirectory);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.SettingsChanged += (_, _) =>
        {
            OnViewModelSettingsChanged();
            RefreshTrayState();
        };
    }

    public HudViewModel ViewModel { get; }

    public HudMenuState MenuState { get; } = new();

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
        if (_showPending)
        {
            // A restore request arrived while the window was still being created.
            _showPending = false;
            RequestShow();
        }

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
        var foregroundBeforeShow = NativeMethods.GetForegroundWindow();
        _window = _dependencies.WindowFactory();
        _window.Icon = AppIcon.WindowImage;
        _window.DataContext = ViewModel;
        _window.WindowViewRequested += (_, _) => ToggleWindowView();
        _window.MaximizeRequested += (_, _) => ToggleMaximize();
        ConfigureWindowView();
        _window.SizeChanged += (_, _) => OnNativeBoundsChanged();
        _window.LocationChanged += (_, _) => OnNativeBoundsChanged();
        _window.StateChanged += (_, _) => OnNativeBoundsChanged();
        _window.PlacementChangedByUser += (_, _) =>
        {
            _dpiScale = _dependencies.Placement.GetDpiScale(_handle, null);
            if (_state.WindowView) { ScheduleStateSave(); return; }
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
        _chrome = new HudChromeAdapter(_window, PrepareNativeMaximize, BeginNativeSizeMove, EndNativeSizeMove);
        _dpiScale = _dependencies.Placement.GetDpiScale(_handle, null);

        // Position first, then show through WPF so IsVisible/layout are real.
        // ShowActivated=false keeps the HUD from taking focus.
        //
        // Windows can still make the window the foreground window when it is created
        // and positioned on a monitor other than the one holding the active window,
        // even when it is shown without activation and even while it stays hidden. A
        // HUD that owns the foreground silently swallows the user's typing, so the
        // window that had focus is remembered here and the guard below returns it.
        _activationGuard = new StartupActivationGuard(
            () => NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64(),
            style => NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, new IntPtr(style)),
            () => RestoreForegroundIfTaken(foregroundBeforeShow));
        ApplyGeometry(show: false);
        StartForegroundGuard();
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

    private System.Windows.Threading.DispatcherTimer? _foregroundGuard;
    private StartupActivationGuard? _activationGuard;

    /// <summary>
    /// Bounded startup guard: while the HUD is starting up it must never keep a focus
    /// it was not given. The check runs a few times over the first two seconds because
    /// the platform can activate the window a moment after it is created, and it stops
    /// for good as soon as the user interacts with the HUD or asks for it to be shown —
    /// an explicit request is intent, and the HUD keeps that activation.
    /// </summary>
    private void StartForegroundGuard()
    {
        if (_window is null)
        {
            StopForegroundGuard();
            return;
        }

        _window.PreviewMouseDown += OnStartupMouseInput;
        _window.PreviewKeyDown += OnStartupKeyInput;

        _foregroundGuard = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, _window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _foregroundGuard.Tick += (_, _) =>
        {
            if (_shuttingDown)
            {
                StopForegroundGuard();
                return;
            }

            _activationGuard?.Tick();
            if (_activationGuard?.IsActive != true) StopForegroundGuard();
        };
        _foregroundGuard.Start();
    }

    /// <summary>
    /// Removes the startup no-activate style once the HUD has settled, so ordinary
    /// interaction (clicking a control, Alt+Tab, keyboard access) behaves normally.
    /// </summary>
    private void StopForegroundGuard()
    {
        _foregroundGuard?.Stop();
        _foregroundGuard = null;
        _activationGuard?.Stop();
        _activationGuard = null;
        if (_window is not null)
        {
            _window.PreviewMouseDown -= OnStartupMouseInput;
            _window.PreviewKeyDown -= OnStartupKeyInput;
        }
    }

    private void OnStartupMouseInput(object sender, System.Windows.Input.MouseButtonEventArgs e) => StopForegroundGuard();
    private void OnStartupKeyInput(object sender, System.Windows.Input.KeyEventArgs e) => StopForegroundGuard();

    /// <summary>
    /// Returns focus to the window that had it before the HUD was created. It never
    /// runs once the user has interacted with the HUD, when the previous window is
    /// gone, or when the HUD does not actually hold the foreground.
    /// </summary>
    private void RestoreForegroundIfTaken(IntPtr foregroundBeforeShow)
    {
        if (_handle == IntPtr.Zero || foregroundBeforeShow == IntPtr.Zero || foregroundBeforeShow == _handle)
        {
            return;
        }

        if (NativeMethods.GetForegroundWindow() != _handle)
        {
            return;
        }

        if (!NativeMethods.IsWindow(foregroundBeforeShow))
        {
            return;
        }

        _ = NativeMethods.SetForegroundWindow(foregroundBeforeShow);
    }

    /// <summary>Recomputes size/position from state and the live monitor layout.</summary>
    private void ApplyGeometry(bool show)
    {
        if (_window is null || _handle == IntPtr.Zero || _applyingGeometry || _nativeSizeMove)
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
        if (_state.WindowView) { ApplyWindowViewGeometry(show); return; }
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
        _automaticHudSize = size;
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

        // ×, Alt+F4, the system menu and WM_CLOSE all mean "hide to tray" while a
        // working tray exists, so an accidental close never strands the session.
        // Explicit Exit, session ending and the no-tray case still run the
        // unconditional orderly shutdown.
        e.Cancel = true;
        if (ShouldHideInsteadOfExit())
        {
            HideToTray();
            return;
        }

        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True while the HUD can hide and still be restored.</summary>
    public bool CanHideToTray => TrayAvailable && _window is not null && !_shuttingDown && !_sessionEnding;

    private bool ShouldHideInsteadOfExit() => CanHideToTray;

    /// <summary>Hides the window and keeps the session, monitor and tray running.</summary>
    public void HideToTray()
    {
        if (_window is null || _shuttingDown)
        {
            return;
        }

        if (!TrayAvailable)
        {
            ViewModel.NotifyStatus("Tray unavailable — HUD stays visible. Right-click the header for controls.");
            return;
        }

        _window.Hide();
        _state = CaptureState();
        ScheduleStateSave();
        ViewModel.NotifyStatus("Hidden to tray. Use the tray icon or the header menu to show it again.");
    }

    /// <summary>
    /// Restore request from another launch. The window may not exist yet during
    /// startup, so the request is remembered and honoured as soon as it does.
    /// </summary>
    private void OnShowRequested()
    {
        if (_shuttingDown)
        {
            return;
        }

        if (_window is null)
        {
            _showPending = true;
            return;
        }

        RequestShow();
    }

    /// <summary>
    /// Restores the existing window and session. Used by the tray, the header menu
    /// and the second-launch signal; never creates a second window.
    /// </summary>
    public void RequestShow()
    {
        _dependencies.Dispatcher.Post(() =>
        {
            if (_shuttingDown || _window is null)
            {
                return;
            }

            StopForegroundGuard();
            if (!_window.IsVisible)
            {
                _window.Show();
            }

            ApplyGeometry(show: true);
            _window.Activate();
        });
    }

    /// <summary>
    /// Windows is ending the session (logoff, shutdown, restart). Hiding to tray
    /// would block it, so the close path switches to the orderly shutdown and the
    /// session-ending request itself is never cancelled.
    /// </summary>
    public void NotifySessionEnding() => _sessionEnding = true;

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
            StopForegroundGuard();
            _window.Show();
            ApplyGeometry(show: false);
        }

        RefreshTrayState();
    }

    public void ResetPosition()
    {
        if (_state.WindowView)
        {
            _state = _state with { WindowMonitor = string.Empty, WindowX = 40, WindowY = 40, WindowWidth = 480, WindowHeight = 720, WindowMaximized = false };
            _restoreWindowBounds = true;
            _window!.WindowState = WindowState.Normal;
            ApplyGeometry(show: false);
            ScheduleStateSave();
            return;
        }
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
            monitorTask = ObserveMonitorAsync();
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

    private async Task ObserveMonitorAsync()
    {
        await _dependencies.Monitor.StartAsync(_lifetime.Token).ConfigureAwait(false);
        await _dependencies.Monitor.Completion.ConfigureAwait(false);
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
        {
            _window?.UpdateResponsiveLayout();
            ApplyGeometry(show: false);
        }
        if (e.PropertyName is nameof(HudViewModel.Theme) && !_shuttingDown) ApplyTheme();
        if (e.PropertyName is nameof(HudViewModel.IsExpanded))
        {
            if (_state.WindowView && !ViewModel.IsExpanded) { ViewModel.IsExpanded = true; return; }
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
            RefreshWindowMenu();
            if (_state.WindowView) return;
            if (_window is not null) _window.Topmost = ViewModel.AlwaysOnTop;
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
        if (_state.WindowView)
        {
            _state = _state with { WindowPinned = !_state.WindowPinned };
            _window!.Topmost = _state.WindowPinned;
            ScheduleStateSave();
            RefreshTrayState();
            return;
        }
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
        var routeUnsupported = result.Outcome is TaskNavigationOutcome.UnsupportedSurface
            or TaskNavigationOutcome.UnsupportedIdentity
            or TaskNavigationOutcome.UnsupportedProfile
            or TaskNavigationOutcome.InvalidIdentifier;
        if (routeUnsupported)
        {
            // No adapter route exists for this harness (or this task's identity),
            // so offer the honest fallback the row already advertises: copy the id.
            var copied = _dependencies.Clipboard.TrySetText(row.Key.ConversationId);
            ViewModel.NotifyStatus((copied ? "Task id copied. " : string.Empty) + result.UserMessage);
            Log(MonitorLogLevel.Info, "hud-navigate-" + result.Outcome.ToString().ToLowerInvariant());
            return;
        }

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
        if (_dependencies.TrayAvailableOverride is { } trayOverride)
        {
            // Test seam: close-to-tray behaviour is verified without a real icon.
            TrayAvailable = trayOverride;
            if (trayOverride)
            {
                _window?.SetCloseBehavior(hideToTray: true);
                RefreshWindowMenu();
            }
            else
            {
                EnsureReachableWithoutTray();
            }

            return;
        }

        if (!_dependencies.TrayEnabled)
        {
            TrayAvailable = false;
            EnsureReachableWithoutTray();
            return;
        }

        var callbacks = new TrayCallbacks(
            ToggleVisibility, ToggleTopmost, ToggleMute,
            () => ExitRequested?.Invoke(this, EventArgs.Empty),
            OpenNotificationSettings, OpenProjectSources);
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

        _window!.SetCloseBehavior(TrayAvailable);
        RefreshWindowMenu();
    }

    private void EnsureReachableWithoutTray()
    {
        if (_window is null) return;
        _window.Show();
        _window.SetCloseBehavior(hideToTray: false);
        RefreshWindowMenu();
        ApplyGeometry(show: false);
        ViewModel.NotifyStatus("Tray unavailable — right-click the header for controls.");
    }

    /// <summary>Eight daily actions; appearance and startup options live in settings.</summary>
    private void CreateWindowMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu { Cursor = System.Windows.Input.Cursors.Arrow, ForceCursor = true };
        System.Windows.Controls.MenuItem Add(string label, Action action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        var pin = Add("Always on top", ToggleTopmost);
        pin.IsCheckable = true;
        pin.SetBinding(System.Windows.Controls.MenuItem.IsCheckedProperty,
            new System.Windows.Data.Binding("Snapshot.AlwaysOnTop") { Source = MenuState, Mode = System.Windows.Data.BindingMode.OneWay });
        Add("Expand all projects", () => ViewModel.SetAllProjectsExpanded(true));
        Add("Collapse all projects", () => ViewModel.SetAllProjectsExpanded(false));
        Add("Compact / expanded HUD", ToggleWindowView);
        Add("Light & voice…", OpenNotificationSettings);
        Add("Projects & sources…", OpenProjectSources);
        var hide = Add("Hide to tray", HideToTray);
        hide.SetBinding(System.Windows.Controls.MenuItem.IsEnabledProperty,
            new System.Windows.Data.Binding("Snapshot.CanHideToTray") { Source = MenuState });
        Add("Exit", () => ExitRequested?.Invoke(this, EventArgs.Empty));
        _window!.ContextMenu = menu;
        RefreshWindowMenu();
    }

    private void RefreshWindowMenu() => MenuState.Update(new HudMenuSnapshot(
        _window?.IsVisible ?? false,
        _state.WindowView ? _state.WindowPinned : ViewModel.AlwaysOnTop,
        ViewModel.StartWithWindows, ViewModel.IsMuted, CanHideToTray));

    // ---- projects and sources ---------------------------------------------------

    private ProjectSourcesWindow? _projectSources;

    /// <summary>Open project/source settings window, if any. Never creates one.</summary>
    public ProjectSourcesWindow? ProjectSources => _projectSources;

    /// <summary>
    /// Bounded project/source settings surface, reachable from the header menu. It
    /// reuses the existing observation data and the existing config path; a source
    /// list change is additive and applies when the monitor is next composed.
    /// </summary>
    public void OpenProjectSources()
    {
        if (_shuttingDown || _window is null)
        {
            return;
        }

        if (_projectSources is { } open)
        {
            open.Activate();
            return;
        }

        var model = BuildProjectSources();
        var window = new ProjectSourcesWindow(model) { Owner = _window, Icon = AppIcon.WindowImage };
        _projectSources = window;
        model.Changed += (_, _) =>
        {
            _disabledHarnessSources = model.DisabledHarnessSources;
            ScheduleStateSave();
            ViewModel.NotifyStatus("Source enablement saved — it applies after OVERSEER restarts.");
        };
        window.Closed += (_, _) =>
        {
            _projectSources = null;
            if (model.DisabledHarnessSources.Count != _disabledHarnessSources.Count ||
                !model.DisabledHarnessSources.SequenceEqual(_disabledHarnessSources, StringComparer.Ordinal))
            {
                _disabledHarnessSources = model.DisabledHarnessSources;
                ScheduleStateSave();
            }
        };
        var screens = _dependencies.Placement.CaptureLayout();
        var screen = screens.Find(_state.WindowView ? _state.WindowMonitor : _state.MonitorDeviceName) ?? (screens.IsEmpty ? null : screens.Primary);
        if (screen is not null)
        {
            var scale = _dependencies.Placement.GetDpiScale(_handle, screen);
            window.MinWidth = Math.Min(window.MinWidth, screen.Width / scale);
            window.MinHeight = Math.Min(window.MinHeight, screen.Height / scale);
            window.Width = Math.Min(window.Width, screen.Width / scale);
            window.Height = Math.Min(window.Height, screen.Height / scale);
        }

        window.Show();
    }

    /// <summary>
    /// Rows for the settings surface. Sources come from the tasks the monitor already
    /// published plus the persisted disabled list, so a disabled source stays visible
    /// and can be re-enabled; project buckets come from the same grouping the HUD
    /// renders, including Unassigned.
    /// </summary>
    private ProjectSourcesViewModel BuildProjectSources()
    {
        var sources = new List<ProjectSourceRow>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var task in _lastTasks)
        {
            var key = HarnessIds.Normalize(task.Key.HarnessId) + "/" + task.Key.SourceInstanceId;
            if (!counts.TryGetValue(key, out var count))
            {
                order.Add(key);
            }

            counts[key] = count + 1;
        }

        foreach (var entry in _disabledHarnessSources)
        {
            if (entry.Length > 0 && !counts.ContainsKey(entry))
            {
                counts[entry] = 0;
                order.Add(entry);
            }
        }

        foreach (var key in order)
        {
            var separator = key.IndexOf('/');
            var harness = separator < 0 ? key : key[..separator];
            var instance = separator < 0 ? string.Empty : key[(separator + 1)..];
            var observed = counts[key];
            var disabled = _disabledHarnessSources.Contains(key, StringComparer.Ordinal);
            var detail = observed == 0
                ? "No task observed in this session."
                : observed.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                  (observed == 1 ? " task observed" : " tasks observed") + " in this session.";
            sources.Add(new ProjectSourceRow(harness, instance, detail, !disabled));
        }

        var projects = new List<ProjectBucketRow>();
        foreach (var project in ViewModel.Projects)
        {
            var detail = project.Tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                (project.Tasks.Count == 1 ? " task · " : " tasks · ") + project.SummaryText;
            projects.Add(new ProjectBucketRow(project.Name, detail));
        }

        return new ProjectSourcesViewModel(
            sources.OrderBy(static source => source.Key, StringComparer.Ordinal),
            projects,
            "Saved with your other settings; a new source list is applied when the monitor is next composed.");
    }

    private void RefreshTrayState()
    {
        RefreshWindowMenu();
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

        _tray.UpdateState(MenuState.Snapshot, status);
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
    /// cooldowns, then project voice status onto the UI. Task history uses the strip source. Cheap, coalesced and
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
        ViewModel.ApplyVoiceStatus(notifications.Status);
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
            // Additive: source enablement is written with every other setting
            // preserved, never reset.
            DisabledHarnessSources = _disabledHarnessSources,
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

        if (_state.WindowView) return CaptureWindowViewState();
        var captured = _dependencies.Placement.CaptureState(_handle, ViewModel.IsExpanded, _dpiScale, _dependencies.TimeProvider.GetUtcNow());
        return _state with { IsExpanded = captured.IsExpanded, MonitorDeviceName = captured.MonitorDeviceName,
            OffsetXDips = captured.OffsetXDips, OffsetYDips = captured.OffsetYDips, DpiScale = captured.DpiScale, SavedAtUtc = captured.SavedAtUtc };
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
            if (Application.Current is { } application)
            {
                application.SessionEnding += OnSessionEnding;
            }
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
            if (Application.Current is { } application)
            {
                application.SessionEnding -= OnSessionEnding;
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Logoff/shutdown/restart. The request itself is never cancelled and the
    /// close intent switches away from hide-to-tray so Windows can proceed.
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => NotifySessionEnding();

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

            RecoverWindowViewIfNeeded();
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

                    RecoverWindowViewIfNeeded();
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
        StopForegroundGuard();
        _chrome?.Dispose();
        _chrome = null;
        _notificationSettings?.Close();
        _projectSources?.Close();
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
            _showRequest?.Dispose();
            _showRequest = null;
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
