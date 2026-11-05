using TheOverseer.Core.Models;
using TheOverseer.App.Desktop;
using TheOverseer.UI;

namespace TheOverseer.App;

public sealed partial class HudApplicationController
{
    private FocusWindow? _focusWindow;

    public void OpenFocus()
    {
        if (_shuttingDown || _window is null) return;
        if (_focusWindow is { } open) { open.Activate(); return; }
        var window = new FocusWindow(ViewModel, _dependencies.TimeProvider.GetUtcNow)
        {
            Owner = _window, Icon = AppIcon.WindowImage,
        };
        _focusWindow = window;
        window.CopyPromptRequested += () =>
        {
            if (!_dependencies.Clipboard.TrySetText(FocusPinContract.Prompt))
                ViewModel.NotifyStatus("Could not copy the focus prompt.");
        };
        window.Closed += (_, _) => _focusWindow = null;
        var screen = _dependencies.Placement.CaptureLayout().Find(
            _state.WindowView ? _state.WindowMonitor : _state.MonitorDeviceName);
        if (screen is not null)
        {
            var scale = _dependencies.Placement.GetDpiScale(_handle, screen);
            window.Width = Math.Min(window.Width, screen.Width / scale);
            window.Height = Math.Min(window.Height, screen.Height / scale);
            window.MinWidth = Math.Min(window.MinWidth, window.Width);
            window.MinHeight = Math.Min(window.MinHeight, window.Height);
        }
        window.Show();
    }
}
