namespace TheOverseer.Core.Configuration;

/// <summary>One physical monitor working area as reported by the platform.</summary>
public sealed record HudScreen(string DeviceName, int X, int Y, int Width, int Height, double DpiScale)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsUsable => Width > 0 && Height > 0;
}

public sealed record HudScreenLayout(IReadOnlyList<HudScreen> Screens)
{
    public static HudScreenLayout Empty { get; } = new(Array.Empty<HudScreen>());

    public bool IsEmpty => Screens.Count == 0;

    public HudScreen? Find(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        foreach (var screen in Screens)
        {
            if (string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return screen;
            }
        }

        return null;
    }

    public HudScreen Primary => Screens[0];
}

/// <summary>Window size in display independent pixels.</summary>
public sealed record HudSize(double Width, double Height)
{
    public static HudSize Collapsed { get; } = new(280, 72);

    public static HudSize Expanded { get; } = new(360, 320);
}

public sealed record HudPhysicalPlacement(string DeviceName, int X, int Y, int Width, int Height, bool Clamped, bool FallbackMonitor)
{
    public HudWindowState ToState(double dpiScale, bool isExpanded, HudScreen screen, DateTimeOffset savedAtUtc) => new()
    {
        IsExpanded = isExpanded,
        MonitorDeviceName = screen.DeviceName,
        OffsetXDips = Math.Round((X - screen.X) / Math.Max(0.5, dpiScale), 2),
        OffsetYDips = Math.Round((Y - screen.Y) / Math.Max(0.5, dpiScale), 2),
        DpiScale = dpiScale,
        SavedAtUtc = savedAtUtc,
    };
}

/// <summary>
/// Framework independent placement policy: no WPF, no Win32, no screen
/// enumeration. Converts saved DIP offsets back to physical pixels with the DPI
/// that was in effect when they were saved, then clamps the window fully into a
/// reachable working area so a removed monitor or resolution change cannot
/// strand it off screen.
/// </summary>
public static class HudPlacementPolicy
{
    public static HudPhysicalPlacement Resolve(
        HudWindowState state,
        HudScreenLayout layout,
        HudSize sizeDips,
        double fallbackDpiScale)
    {
        var normalized = (state ?? HudWindowState.Default).Normalized();
        var scale = normalized.DpiScale > 0 ? normalized.DpiScale : Math.Max(0.5, fallbackDpiScale);
        var width = Math.Max(1, (int)Math.Round(sizeDips.Width * Math.Max(0.5, fallbackDpiScale)));
        var height = Math.Max(1, (int)Math.Round(sizeDips.Height * Math.Max(0.5, fallbackDpiScale)));

        if (layout is null || layout.IsEmpty)
        {
            return new HudPhysicalPlacement(string.Empty, 0, 0, width, height, true, true);
        }

        var screen = layout.Find(normalized.MonitorDeviceName);
        var fallback = screen is null;
        screen ??= layout.Primary;

        var x = screen.X + (int)Math.Round(normalized.OffsetXDips * scale);
        var y = screen.Y + (int)Math.Round(normalized.OffsetYDips * scale);

        var maximumX = screen.Right - width;
        var maximumY = screen.Bottom - height;
        var clampedX = maximumX <= screen.X ? screen.X : Math.Clamp(x, screen.X, maximumX);
        var clampedY = maximumY <= screen.Y ? screen.Y : Math.Clamp(y, screen.Y, maximumY);

        return new HudPhysicalPlacement(
            screen.DeviceName,
            clampedX,
            clampedY,
            width,
            height,
            clampedX != x || clampedY != y,
            fallback);
    }

    /// <summary>
    /// Clamps an already measured physical rectangle into a reachable working
    /// area. Used when the platform decides the real window size, so clamping
    /// always reflects the true rectangle rather than an assumed one.
    /// </summary>
    public static HudPhysicalPlacement Clamp(
        HudScreen screen,
        int x,
        int y,
        int width,
        int height,
        bool fallbackMonitor,
        string? requestedDeviceName)
    {
        var maximumX = screen.Right - Math.Max(1, width);
        var maximumY = screen.Bottom - Math.Max(1, height);
        var clampedX = maximumX <= screen.X ? screen.X : Math.Min(Math.Max(x, screen.X), maximumX);
        var clampedY = maximumY <= screen.Y ? screen.Y : Math.Min(Math.Max(y, screen.Y), maximumY);
        return new HudPhysicalPlacement(
            screen.DeviceName,
            clampedX,
            clampedY,
            Math.Max(1, width),
            Math.Max(1, height),
            clampedX != x || clampedY != y,
            fallbackMonitor || !string.Equals(screen.DeviceName, requestedDeviceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Physical size of an expanded window bounded to the working area so a
    /// concise list never grows past the screen.
    /// </summary>
    public static HudSize BoundExpandedSize(HudScreen? screen, HudSize requested, double dpiScale, int maximumVisibleRows)
    {
        if (screen is null || !screen.IsUsable)
        {
            return requested;
        }

        var scale = Math.Max(0.5, dpiScale);
        var maximumWidth = (screen.Width - 16) / scale;
        var maximumHeight = (screen.Height - 16) / scale;
        var width = Math.Max(HudSize.Collapsed.Width, Math.Min(requested.Width, maximumWidth));
        var height = Math.Max(HudSize.Collapsed.Height, Math.Min(requested.Height, maximumHeight));
        _ = maximumVisibleRows;
        return new HudSize(Math.Round(width), Math.Round(height));
    }
}
