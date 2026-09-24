using TheOverseer.Core.Configuration;

namespace TheOverseer.App.Desktop;

/// <summary>Injectable boundary around screen enumeration and HWND placement.</summary>
public interface IWindowPlacementService
{
    HudScreenLayout CaptureLayout();

    double GetDpiScale(IntPtr windowHandle, HudScreen? screen);

    /// <summary>
    /// Applies the clamped physical rectangle. Size comes from the live DPI and
    /// WPF keeps the matching DIP geometry, so per-monitor scaling cannot leave
    /// the two views of the window disagreeing.
    /// </summary>
    void Apply(IntPtr windowHandle, HudPhysicalPlacement placement, bool topmost, bool showWithoutActivation);

    HudWindowState CaptureState(IntPtr windowHandle, bool isExpanded, double dpiScale, DateTimeOffset now);
    HudWindowState? CaptureNormalBounds(IntPtr windowHandle, double dpiScale) => null;
}

/// <summary>
/// Windows implementation: Windows Forms supplies working-area rectangles in
/// physical pixels, WPF handles the window, and all coordinate conversion
/// happens here, never in the view model or XAML.
/// </summary>
internal sealed class WindowsWindowPlacementService : IWindowPlacementService
{
    public HudWindowState? CaptureNormalBounds(IntPtr windowHandle, double dpiScale)
    {
        var placement = new NativeMethods.WindowPlacement { Length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WindowPlacement>() };
        if (!NativeMethods.GetWindowPlacement(windowHandle, ref placement)) return null;
        var rect = placement.NormalPosition;
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Width, rect.Height));
        // WINDOWPLACEMENT normal positions use workspace coordinates for our
        // taskbar-visible window: offset back to the monitor's work-area origin.
        var scale = Math.Max(0.5, dpiScale);
        return new HudWindowState
        {
            WindowMonitor = screen.DeviceName,
            WindowX = (rect.Left - screen.Bounds.Left) / scale,
            WindowY = (rect.Top - screen.Bounds.Top) / scale,
            WindowWidth = rect.Width / scale, WindowHeight = rect.Height / scale,
            WindowDpi = scale,
        };
    }

    public HudScreenLayout CaptureLayout()
    {
        var screens = new List<HudScreen>();
        try
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens.OrderByDescending(screen => screen.Primary))
            {
                var bounds = screen.WorkingArea;
                screens.Add(new HudScreen(
                    screen.DeviceName,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    GetScaleForScreen()));
            }
        }
        catch (InvalidOperationException)
        {
            return HudScreenLayout.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return HudScreenLayout.Empty;
        }

        return new HudScreenLayout(screens);
    }

    public double GetDpiScale(IntPtr windowHandle, HudScreen? screen)
    {
        if (windowHandle != IntPtr.Zero)
        {
            var dpi = NativeMethods.GetDpiForWindow(windowHandle);
            if (dpi >= 96)
            {
                return dpi / 96.0;
            }
        }

        return screen?.DpiScale is > 0 ? screen.DpiScale : 1.0;
    }

    public void Apply(IntPtr windowHandle, HudPhysicalPlacement placement, bool topmost, bool showWithoutActivation)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var flags = NativeMethods.SwpNoActivate | NativeMethods.SwpNoSize;
        if (showWithoutActivation)
        {
            flags |= NativeMethods.SwpShowWindow;
        }

        // Position only: WPF owns the render/layout size in DIPs, so letting it
        // size the HWND is what guarantees the layout box and the window
        // rectangle always agree (and therefore that content is never clipped).
        _ = NativeMethods.SetWindowPos(
            windowHandle,
            topmost ? NativeMethods.HwndTopmost : NativeMethods.HwndNotTopmost,
            placement.X,
            placement.Y,
            0,
            0,
            flags);

        if (showWithoutActivation)
        {
            _ = NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowNoActivate);
        }
    }

    public HudWindowState CaptureState(IntPtr windowHandle, bool isExpanded, double dpiScale, DateTimeOffset now)
    {
        if (windowHandle == IntPtr.Zero || !NativeMethods.GetWindowRect(windowHandle, out var rect))
        {
            return new HudWindowState { IsExpanded = isExpanded, DpiScale = Math.Max(0.5, dpiScale) };
        }

        var layout = CaptureLayout();
        var screen = FindContaining(layout, rect);
        var scale = Math.Max(0.5, dpiScale);
        return new HudWindowState
        {
            IsExpanded = isExpanded,
            MonitorDeviceName = screen?.DeviceName ?? string.Empty,
            OffsetXDips = Math.Round((rect.Left - (screen?.X ?? 0)) / scale, 2),
            OffsetYDips = Math.Round((rect.Top - (screen?.Y ?? 0)) / scale, 2),
            DpiScale = scale,
            SavedAtUtc = now,
        };
    }

    public static HudScreen? FindContaining(HudScreenLayout layout, NativeMethods.Rect rect)
    {
        var centerX = rect.Left + (rect.Width / 2);
        var centerY = rect.Top + (rect.Height / 2);
        foreach (var screen in layout.Screens)
        {
            if (centerX >= screen.X && centerX < screen.Right && centerY >= screen.Y && centerY < screen.Bottom)
            {
                return screen;
            }
        }

        return layout.IsEmpty ? null : layout.Primary;
    }

    private static double GetScaleForScreen()
    {
        // Pre-HWND fallback only, not a claim about each monitor's actual DPI.
        // GetDpiForMonitor is unsupported on per-monitor-aware calling threads.
        // The controller creates its HWND before placing it and uses
        // GetDpiForWindow, then re-clamps after WPF's DpiChanged notification.
        var dpi = NativeMethods.GetDpiForSystem();
        return dpi >= 96 ? dpi / 96.0 : 1.0;
    }
}
