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
    internal static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPlacement
    {
        internal int Length;
        internal int Flags;
        internal int ShowCommand;
        internal System.Drawing.Point MinPosition;
        internal System.Drawing.Point MaxPosition;
        internal Rect NormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

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
