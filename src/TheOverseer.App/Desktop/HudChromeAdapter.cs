using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TheOverseer.UI;

namespace TheOverseer.App.Desktop;

/// <summary>Bridges our drawn header to Windows caption, Snap and resize behavior.</summary>
internal sealed class HudChromeAdapter : IDisposable
{
    private readonly HudWindow _window;
    private readonly HwndSource _source;
    private readonly Action _prepareMaximize;
    private readonly Action _beginResize;
    private readonly Action _placementChanged;
    private bool _pressed;

    public HudChromeAdapter(HudWindow window, Action prepareMaximize, Action beginResize, Action placementChanged)
    {
        _window = window;
        _prepareMaximize = prepareMaximize;
        _beginResize = beginResize;
        _placementChanged = placementChanged;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
        _source.AddHook(Hook);
    }

    private Point ScreenPoint(IntPtr packed) => new(unchecked((short)(packed.ToInt64() & 0xffff)), unchecked((short)((packed.ToInt64() >> 16) & 0xffff)));
    private bool IsMaximize(Point screen) => _window.ChromeHitTest(_window.PointFromScreen(screen)) == 9;

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case 0x84: // WM_NCHITTEST. Leave only the resize border to WindowChrome.
                var hit = _window.ChromeHitTest(_window.PointFromScreen(ScreenPoint(lParam)));
                if (hit != 0) { handled = true; return new IntPtr(hit); }
                break;
            case 0x112: // WM_SYSCOMMAND: keyboard/system-menu maximize must also expand.
                if ((wParam.ToInt64() & 0xfff0) == 0xf030) _prepareMaximize();
                break;
            case 0x231: // WM_ENTERSIZEMOVE: defer compact promotion until the gesture commits.
                _beginResize();
                break;
            case 0x232: // WM_EXITSIZEMOVE
                _placementChanged();
                break;
            case 0xa0: // WM_NCMOUSEMOVE. Let DefWindowProc see it for Snap hover.
                _window.SetMaximizeInteraction(wParam.ToInt64() == 9, _pressed);
                var track = new TrackMouse { Size = Marshal.SizeOf<TrackMouse>(), Flags = 0x12, Window = hwnd }; // LEAVE | NONCLIENT
                TrackMouseEvent(ref track);
                break;
            case 0x2a2: // WM_NCMOUSELEAVE
                if (!_pressed) _window.SetMaximizeInteraction(false, false);
                break;
            case 0xa1: // WM_NCLBUTTONDOWN. One owner for pointer activation.
            case 0xa3:
                if (wParam.ToInt64() != 9) break;
                _pressed = true;
                SetCapture(hwnd);
                _window.SetMaximizeInteraction(true, true);
                handled = true;
                break;
            case 0x200: // WM_MOUSEMOVE while our non-client press has capture.
                if (!_pressed) break;
                GetCursorPos(out var move);
                _window.SetMaximizeInteraction(IsMaximize(new Point(move.X, move.Y)), true);
                handled = true;
                break;
            case 0x202: // WM_LBUTTONUP after capture.
            case 0xa2:
                if (!_pressed) break;
                GetCursorPos(out var up);
                var activate = IsMaximize(new Point(up.X, up.Y));
                CancelPress();
                if (activate)
                {
                    if (_window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(_window);
                    else { _prepareMaximize(); SystemCommands.MaximizeWindow(_window); }
                }
                handled = true;
                break;
            case 0x1f: // WM_CANCELMODE
            case 0x215: // WM_CAPTURECHANGED: cancel without triggering a click.
                if (_pressed) CancelPress();
                break;
        }
        return IntPtr.Zero;
    }

    private void CancelPress()
    {
        _pressed = false;
        if (GetCapture() == _source.Handle) ReleaseCapture();
        _window.SetMaximizeInteraction(false, false);
    }

    public void Dispose()
    {
        CancelPress();
        _source.RemoveHook(Hook);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouse { public int Size; public int Flags; public IntPtr Window; public int HoverTime; }
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouse track);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point point);
}
