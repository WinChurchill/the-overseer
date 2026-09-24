using System.Windows;
using TheOverseer.Core.Configuration;

namespace TheOverseer.App;

public sealed partial class HudApplicationController
{
    private bool _restoreWindowBounds = true;
    private bool _switchingView;

    public bool IsWindowView => _state.WindowView;

    private void ConfigureWindowView()
    {
        _window!.SetWindowView(_state.WindowView);
        _window.Topmost = _state.WindowView ? _state.WindowPinned : ViewModel.AlwaysOnTop;
    }

    public void ToggleWindowView()
    {
        if (_window is null || _shuttingDown) return;
        _state = CaptureState();
        _switchingView = true;
        try
        {
            _state = _state with { WindowView = !_state.WindowView };
            ConfigureWindowView();
            if (!_state.WindowView) ViewModel.IsExpanded = _state.IsExpanded;
            _restoreWindowBounds = true;
        }
        finally { _switchingView = false; }
        ApplyGeometry(false);
        ScheduleStateSave();
        RefreshTrayState();
    }

    private void OnNativeBoundsChanged()
    {
        if (_switchingView || _applyingGeometry || _shuttingDown || !_state.WindowView || _handle == IntPtr.Zero) return;
        // Windows/WPF own live resize, Snap, maximize and DPI transitions.
        // Saving must never feed those bounds back into a preset geometry pass.
        ScheduleStateSave();
    }

    private void RecoverWindowViewIfNeeded()
    {
        if (!_state.WindowView || _window is null) return;
        var layout = _dependencies.Placement.CaptureLayout();
        if (layout.IsEmpty) return;
        if (Desktop.NativeMethods.GetWindowRect(_handle, out var rect) && layout.Screens.Any(screen =>
            rect.Left >= screen.X - 8 && rect.Top >= screen.Y - 8 && rect.Right <= screen.Right + 8 && rect.Bottom <= screen.Bottom + 8)) return;
        _state = CaptureState();
        _restoreWindowBounds = true;
        var maximized = _state.WindowMaximized;
        _window.WindowState = WindowState.Normal;
        _state = _state with { WindowMaximized = maximized };
    }

    private HudWindowState CaptureWindowViewState()
    {
        if (_window is null || _switchingView || _window.WindowState == WindowState.Minimized) return _state;
        var scale = _dependencies.Placement.GetDpiScale(_handle, null);
        var normal = _dependencies.Placement.CaptureNormalBounds(_handle, scale);
        if (normal is not null)
            return (_state with { WindowMonitor = normal.WindowMonitor, WindowX = normal.WindowX, WindowY = normal.WindowY,
                WindowWidth = normal.WindowWidth, WindowHeight = normal.WindowHeight, WindowDpi = scale,
                WindowMaximized = _window.WindowState == WindowState.Maximized,
                SavedAtUtc = _dependencies.TimeProvider.GetUtcNow() }).Normalized();
        if (_window.WindowState == WindowState.Maximized) return _state with { WindowMaximized = true };
        var position = _dependencies.Placement.CaptureState(_handle, true, scale, _dependencies.TimeProvider.GetUtcNow());
        return (_state with
        {
            WindowMonitor = position.MonitorDeviceName, WindowX = position.OffsetXDips, WindowY = position.OffsetYDips,
            WindowDpi = scale, WindowWidth = _window.Width, WindowHeight = _window.Height,
            WindowMaximized = false, SavedAtUtc = position.SavedAtUtc,
        }).Normalized();
    }

    private void ApplyWindowViewGeometry(bool show)
    {
        if (_switchingView || _window is null) return;
        var layout = _dependencies.Placement.CaptureLayout();
        // On topology changes recover only a missing owner; ordinary updates
        // (including light changes) leave the native bounds entirely alone.
        if (!_restoreWindowBounds && layout.Find(_state.WindowMonitor) is not null) return;
        if (layout.IsEmpty) return;
        var screen = layout.Find(_state.WindowMonitor) ?? layout.Primary;
        var scale = _dependencies.Placement.GetDpiScale(_handle, screen);
        var width = Math.Min(_state.WindowWidth, screen.Width / scale);
        var height = Math.Min(_state.WindowHeight, screen.Height / scale);
        _window.MinWidth = Math.Min(280, width);
        _window.MinHeight = Math.Min(200, height);
        _window.Width = width;
        _window.Height = height;
        var position = new HudWindowState { MonitorDeviceName = screen.DeviceName,
            OffsetXDips = _state.WindowX, OffsetYDips = _state.WindowY, DpiScale = _state.WindowDpi };
        var resolved = HudPlacementPolicy.Resolve(position, layout, new HudSize(width, height), scale);
        _dependencies.Placement.Apply(_handle, resolved, _state.WindowPinned, show);
        _state = _state with { WindowMonitor = screen.DeviceName };
        _restoreWindowBounds = false;
        if (_state.WindowMaximized) _window.WindowState = WindowState.Maximized;
    }
}
