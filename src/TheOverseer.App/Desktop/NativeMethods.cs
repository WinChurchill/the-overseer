using System.Runtime.InteropServices;

namespace TheOverseer.App.Desktop;

/// <summary>
/// Small, explicit Win32 surface used by the HUD. Only what the plan needs:
/// no-activate display, placement, DPI, monitors and close requests.
/// </summary>
internal static class NativeMethods
{
    internal const int SwShowNoActivate = 4;
    internal const int SwHide = 0;
    internal const uint WmClose = 0x0010;
    internal const int GwlExStyle = -20;
    internal const int WsExNoActivate = 0x08000000;
    internal const int WsExToolWindow = 0x00000080;

    internal static readonly IntPtr HwndTopmost = new(-1);
    internal static readonly IntPtr HwndNotTopmost = new(-2);
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>MONITOR_DEFAULTTONEAREST: never fail, return the closest monitor.</summary>
    internal const uint MonitorDefaultToNearest = 2;

    /// <summary>MDT_EFFECTIVE_DPI: the DPI the shell actually scales this monitor by.</summary>
    private const int MdtEffectiveDpi = 0;

    /// <summary>
    /// Per-monitor scale for a physical point. Mixed-DPI desktops report a
    /// different scale per screen, so screen enumeration must ask about the
    /// monitor that owns the point instead of assuming the system baseline.
    /// Returns 0 when the query is unavailable so callers can fall back.
    /// </summary>
    internal static uint GetMonitorDpi(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new Point { X = x, Y = y }, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return 0;
            }

            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out var dpiY) != 0)
            {
                return 0;
            }

            return dpiX != 0 ? dpiX : dpiY;
        }
        catch (DllNotFoundException)
        {
            // Pre-8.1 shells have no shcore.dll; the system DPI is the fallback.
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly int Width => Right - Left;

        internal readonly int Height => Bottom - Top;
    }
}
