using System.Text.Json;
using TheOverseer.Core.Configuration;

namespace TheOverseer.Tests;

public sealed class WindowViewStateTests
{
    [Fact]
    public void LegacyStateKeepsHudPlacementAndDefaultsToFloating()
    {
        var state = JsonSerializer.Deserialize<HudWindowState>("""{"version":1,"isExpanded":true,"offsetXDips":75}""")!.Normalized();
        Assert.False(state.WindowView);
        Assert.True(state.IsExpanded);
        Assert.Equal(75, state.OffsetXDips);
        Assert.Equal(480, state.WindowWidth);
        Assert.False(state.WindowPinned);
    }

    [Fact]
    public void InvalidNativeBoundsCannotProduceAnUnreachableWindow()
    {
        var state = (HudWindowState.Default with { WindowWidth = double.NaN, WindowHeight = -1,
            WindowDpi = double.PositiveInfinity, WindowX = double.NaN }).Normalized();
        Assert.Equal(480, state.WindowWidth);
        Assert.Equal(200, state.WindowHeight);
        Assert.Equal(1, state.WindowDpi);
        Assert.Equal(0, state.WindowX);
    }
}
