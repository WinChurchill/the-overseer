using TheOverseer.Core.Configuration;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Diagnostics-only placement seeding. Automated checks may run while the user is
/// working on the primary display, so they can ask for the window to start on a named
/// monitor. It affects one run's in-memory start state only: no setting is written and
/// the user's saved placement is never changed.
/// </summary>
internal static class HudPlacementOptions
{
    public static HudWindowState WithMonitor(HudWindowState state, string? monitorDevice) =>
        string.IsNullOrWhiteSpace(monitorDevice)
            ? state
            : state with { MonitorDeviceName = monitorDevice!.Trim(), WindowMonitor = monitorDevice.Trim() };

    // A diagnostic request must never silently fall back onto the display in use.
    internal static HudScreen? SelectMonitor(HudScreenLayout layout, string? requested, bool allowPrimary)
    {
        if (layout.IsEmpty) return null;
        var selected = string.IsNullOrWhiteSpace(requested)
            ? layout.Screens.Skip(1).FirstOrDefault() ?? (allowPrimary ? layout.Primary : null)
            : layout.Find(requested);
        return selected is not null && (allowPrimary || selected != layout.Primary) ? selected : null;
    }
}
