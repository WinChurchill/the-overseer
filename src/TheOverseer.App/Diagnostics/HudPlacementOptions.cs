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
            : state with { MonitorDeviceName = monitorDevice!.Trim() };
}
