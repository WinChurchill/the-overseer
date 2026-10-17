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

public sealed partial class HudApplicationController : IAsyncDisposable
{
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

}
