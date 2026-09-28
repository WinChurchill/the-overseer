using TheOverseer.App.Diagnostics;
using TheOverseer.Core.Configuration;

namespace TheOverseer.Tests;

public sealed class DiagnosticPlacementTests
{
    private static readonly HudScreen Primary = new("primary", 0, 0, 1920, 1080, 1);
    private static readonly HudScreen Secondary = new("secondary", -1920, 0, 1920, 1080, 1);

    [Theory]
    [InlineData(null, false, "secondary")]
    [InlineData("secondary", false, "secondary")]
    [InlineData("missing", true, null)]
    [InlineData("primary", false, null)]
    [InlineData("primary", true, "primary")]
    public void ExplicitDisplayNeverFallsBack(string? requested, bool allowPrimary, string? expected)
    {
        var layout = new HudScreenLayout(new[] { Primary, Secondary });
        Assert.Equal(expected, HudPlacementOptions.SelectMonitor(layout, requested, allowPrimary)?.DeviceName);
    }

    [Fact]
    public void SingleDisplayDefersWithoutOptInAndEmptyLayoutAlwaysDefers()
    {
        var layout = new HudScreenLayout(new[] { Primary });
        Assert.Null(HudPlacementOptions.SelectMonitor(layout, null, false));
        Assert.Equal(Primary, HudPlacementOptions.SelectMonitor(layout, null, true));
        Assert.Null(HudPlacementOptions.SelectMonitor(HudScreenLayout.Empty, null, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlacementSeedCoversBothModesWithoutMutatingSavedState(bool windowView)
    {
        var saved = new HudWindowState { WindowView = windowView, MonitorDeviceName = "primary", WindowMonitor = "primary" };
        var seeded = HudPlacementOptions.WithMonitor(saved, "secondary");
        Assert.Equal("secondary", seeded.MonitorDeviceName);
        Assert.Equal("secondary", seeded.WindowMonitor);
        Assert.Equal("primary", saved.WindowMonitor);
        Assert.Equal(windowView, seeded.WindowView);
    }
}
