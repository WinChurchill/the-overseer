using TheOverseer.Core.Configuration;

namespace TheOverseer.Tests;

public sealed class WindowPlacementTests
{
    private static readonly HudScreen Primary = new(@"\\.\DISPLAY1", 0, 0, 1920, 1040, 1.0);
    private static readonly HudScreen Left = new(@"\\.\DISPLAY2", -1920, -200, 1920, 1080, 1.0);
    private static readonly HudScreen Scaled = new(@"\\.\DISPLAY3", 1920, 0, 2560, 1400, 1.5);

    private static HudScreenLayout Layout(params HudScreen[] screens) => new(screens);

    [Fact]
    public void SavedOffsetRestoresInsideTheSameMonitor()
    {
        var state = new HudWindowState { MonitorDeviceName = Primary.DeviceName, OffsetXDips = 100, OffsetYDips = 40, DpiScale = 1.0 };
        var placement = HudPlacementPolicy.Resolve(state, Layout(Primary), HudSize.Collapsed, 1.0);

        Assert.Equal(100, placement.X);
        Assert.Equal(40, placement.Y);
        Assert.Equal(280, placement.Width);
        Assert.Equal(72, placement.Height);
        Assert.False(placement.Clamped);
        Assert.False(placement.FallbackMonitor);
    }

    [Fact]
    public void NegativeMonitorOriginsArePreserved()
    {
        var state = new HudWindowState { MonitorDeviceName = Left.DeviceName, OffsetXDips = 10, OffsetYDips = 10, DpiScale = 1.0 };
        var placement = HudPlacementPolicy.Resolve(state, Layout(Primary, Left), HudSize.Collapsed, 1.0);

        Assert.Equal(-1910, placement.X);
        Assert.Equal(-190, placement.Y);
        Assert.Equal(Left.DeviceName, placement.DeviceName);
    }

    [Fact]
    public void RemovedMonitorFallsBackToPrimaryAndClamps()
    {
        var state = new HudWindowState { MonitorDeviceName = @"\\.\DISPLAY9", OffsetXDips = 5000, OffsetYDips = 5000, DpiScale = 1.0 };
        var placement = HudPlacementPolicy.Resolve(state, Layout(Primary), HudSize.Collapsed, 1.0);

        Assert.True(placement.FallbackMonitor);
        Assert.True(placement.Clamped);
        Assert.Equal(Primary.DeviceName, placement.DeviceName);
        Assert.Equal(1920 - 280, placement.X);
        Assert.Equal(1040 - 72, placement.Y);
    }

    [Fact]
    public void HugeAndInvalidSavedCoordinatesAreBounded()
    {
        var state = new HudWindowState
        {
            MonitorDeviceName = Primary.DeviceName,
            OffsetXDips = double.PositiveInfinity,
            OffsetYDips = double.NaN,
            DpiScale = double.NaN,
        }.Normalized();

        Assert.Equal(0, state.OffsetXDips);
        Assert.Equal(0, state.OffsetYDips);
        Assert.Equal(1.0, state.DpiScale);

        var clamped = HudPlacementPolicy.Resolve(
            new HudWindowState { MonitorDeviceName = Primary.DeviceName, OffsetXDips = 999_999, OffsetYDips = -999_999, DpiScale = 1.0 },
            Layout(Primary),
            HudSize.Collapsed,
            1.0);
        Assert.True(clamped.Clamped);
        Assert.InRange(clamped.X, Primary.X, Primary.Right - clamped.Width);
        Assert.InRange(clamped.Y, Primary.Y, Primary.Bottom - clamped.Height);
    }

    [Fact]
    public void DpiChangeKeepsTheSamePhysicalSpot()
    {
        var state = new HudWindowState { MonitorDeviceName = Scaled.DeviceName, OffsetXDips = 200, OffsetYDips = 100, DpiScale = 1.5 };
        var placement = HudPlacementPolicy.Resolve(state, Layout(Scaled), HudSize.Collapsed, 1.5);

        Assert.Equal(1920 + 300, placement.X);
        Assert.Equal(150, placement.Y);
    }

    [Fact]
    public void MixedDpiScreensKeepTheirOwnScaleForOffsetsAndSize()
    {
        // Two monitors with different scales: the saved screen decides the
        // offset conversion and the live window DPI decides the physical size.
        var onPrimary = HudPlacementPolicy.Resolve(
            new HudWindowState { MonitorDeviceName = Primary.DeviceName, OffsetXDips = 100, OffsetYDips = 40, DpiScale = 1.0 },
            Layout(Primary, Scaled),
            HudSize.Collapsed,
            1.0);
        Assert.Equal(100, onPrimary.X);
        Assert.Equal(40, onPrimary.Y);
        Assert.Equal(280, onPrimary.Width);
        Assert.Equal(72, onPrimary.Height);

        var onScaled = HudPlacementPolicy.Resolve(
            new HudWindowState { MonitorDeviceName = Scaled.DeviceName, OffsetXDips = 100, OffsetYDips = 40, DpiScale = 1.5 },
            Layout(Primary, Scaled),
            HudSize.Collapsed,
            1.5);
        Assert.Equal(1920 + 150, onScaled.X);
        Assert.Equal(60, onScaled.Y);
        Assert.Equal(420, onScaled.Width);
        Assert.Equal(108, onScaled.Height);
        Assert.False(onScaled.FallbackMonitor);
    }

    [Fact]
    public void MixedDpiLayoutClampsAHugeOffsetIntoTheOwnersWorkArea()
    {
        var scaled = new HudScreen(@"\.DISPLAY5", 1920, 0, 2560, 1400, 1.5);
        var state = new HudWindowState
        {
            MonitorDeviceName = scaled.DeviceName,
            OffsetXDips = 5000,
            OffsetYDips = 5000,
            DpiScale = 1.5,
        };

        var placement = HudPlacementPolicy.Resolve(state, Layout(Primary, scaled), HudSize.Collapsed, 1.5);

        Assert.Equal(scaled.DeviceName, placement.DeviceName);
        Assert.True(placement.Clamped);
        Assert.False(placement.FallbackMonitor);
        Assert.InRange(placement.X, scaled.X, scaled.Right - placement.Width);
        Assert.InRange(placement.Y, scaled.Y, scaled.Bottom - placement.Height);
    }

    [Fact]
    public void RemovedHighDpiScreenFallsBackToPrimaryWithTheLiveScale()
    {
        // The saved 150% monitor is gone: the window must land inside the primary
        // work area using the live DPI, not the saved scale.
        var state = new HudWindowState
        {
            MonitorDeviceName = @"\.DISPLAY9",
            OffsetXDips = 400,
            OffsetYDips = 300,
            DpiScale = 1.5,
        };

        var placement = HudPlacementPolicy.Resolve(state, Layout(Primary), HudSize.Collapsed, 1.0);

        Assert.True(placement.FallbackMonitor);
        Assert.Equal(Primary.DeviceName, placement.DeviceName);
        Assert.Equal(280, placement.Width);
        Assert.InRange(placement.X, Primary.X, Primary.Right - placement.Width);
        Assert.InRange(placement.Y, Primary.Y, Primary.Bottom - placement.Height);
    }

    [Fact]
    public void ExpandedSizeIsBoundedToTheWorkArea()
    {
        var small = new HudScreen(@"\\.\DISPLAY4", 0, 0, 400, 300, 1.0);
        var bounded = HudPlacementPolicy.BoundExpandedSize(small, new HudSize(360, 392), 1.0, 8);

        Assert.True(bounded.Height <= 300);
        Assert.True(bounded.Width <= 400);

        var large = HudPlacementPolicy.BoundExpandedSize(Primary, new HudSize(360, 392), 1.0, 8);
        Assert.Equal(360, large.Width);
        Assert.Equal(392, large.Height);
    }

    [Fact]
    public void ExpandedWindowIsClampedInsideTheWorkingArea()
    {
        var state = new HudWindowState { MonitorDeviceName = Primary.DeviceName, OffsetXDips = 1000, OffsetYDips = 700, DpiScale = 1.0 };
        var placement = HudPlacementPolicy.Resolve(state, Layout(Primary), new HudSize(360, 392), 1.0);

        Assert.Equal(1000, placement.X);
        Assert.Equal(648, placement.Y);
        Assert.True(placement.Clamped);
    }

    [Fact]
    public void EmptyLayoutStillProducesAReachablePlacement()
    {
        var placement = HudPlacementPolicy.Resolve(HudWindowState.Default, HudScreenLayout.Empty, HudSize.Collapsed, 1.0);
        Assert.True(placement.FallbackMonitor);
        Assert.Equal(0, placement.X);
        Assert.Equal(0, placement.Y);
    }

    [Fact]
    public void StateRoundTripKeepsOffsetsRelativeToTheMonitor()
    {
        var placement = new HudPhysicalPlacement(Primary.DeviceName, 432, 120, 280, 72, false, false);
        var state = placement.ToState(1.0, isExpanded: true, Primary, DateTimeOffset.UnixEpoch);

        Assert.Equal(432, state.OffsetXDips);
        Assert.Equal(120, state.OffsetYDips);
        Assert.True(state.IsExpanded);
        Assert.Equal(Primary.DeviceName, state.MonitorDeviceName);
    }
}
