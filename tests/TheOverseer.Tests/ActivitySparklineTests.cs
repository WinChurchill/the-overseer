using System.Windows;
using System.Windows.Media;
using TheOverseer.App.Diagnostics;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;
using TheOverseer.UI;
using TheOverseer.UI.Controls;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

public sealed class ActivitySparklineTests
{
    private static ActivitySeries Series()
    {
        var history = new ActivityHistory();
        for (var index = 0; index < 60; index++)
        {
            history.Record(VoiceTestData.Snapshot(
                VoiceTestData.Start.AddSeconds(index),
                new[] { VoiceTestData.Task(state: AgentTaskState.Working) },
                working: CountValue.Known(index % 4)));
        }

        return history.Snapshot(VoiceTestData.Start.AddSeconds(60));
    }

    [Fact]
    public void RendersBoundedGeometryWithoutThrowing()
    {
        StaTestHost.Invoke(() =>
        {
            var series = Series();
            var control = new ActivitySparkline
            {
                Segments = ActivityChartGeometry.Build(series, 320, 30),
                LineBrush = Brushes.Orange,
                PartialBrush = Brushes.Gray,
                BaselineBrush = Brushes.DarkGray,
            };

            control.Measure(new Size(320, 30));
            control.Arrange(new Rect(0, 0, 320, 30));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 30, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(control);

            Assert.True(control.DrawnPointCount > 0);
            Assert.True(control.DrawnPointCount <= ActivityChartGeometry.DefaultMaximumColumns);
            Assert.True(control.DrawnSegmentCount >= 1);
        });
    }

    [Fact]
    public void EmptySeriesDrawsTheBaselineOnly()
    {
        StaTestHost.Invoke(() =>
        {
            var control = new ActivitySparkline
            {
                Segments = Array.Empty<ActivityChartSegment>(),
                LineBrush = Brushes.Orange,
                PartialBrush = Brushes.Gray,
                BaselineBrush = Brushes.DarkGray,
            };

            control.Measure(new Size(200, 30));
            control.Arrange(new Rect(0, 0, 200, 30));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(200, 30, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(control);

            Assert.Equal(0, control.DrawnSegmentCount);
            Assert.Equal(0, control.DrawnPointCount);
        });
    }

    [Fact]
    public void ViewModelProjectsActivityAndVoiceStatus()
    {
        var viewModel = new HudViewModel(new HudSettings());
        var series = Series();
        var status = VoiceStatus.Initial with { IsMuted = false, ProviderId = "pack", Detail = "Spoken 2.", SpokenCount = 2 };

        viewModel.ApplyActivity(series, status);

        Assert.True(viewModel.HasActivity);
        Assert.NotEmpty(viewModel.ActivitySegments);
        Assert.Equal(Math.Max(1, series.PeakWorking).ToString(), viewModel.ActivityAxisMaximum);
        Assert.NotEmpty(viewModel.ActivitySummaryLine);
        Assert.Equal("Spoken 2.", viewModel.VoiceStatusLine);
        Assert.False(viewModel.IsMuted);
        Assert.Equal("V", viewModel.MuteGlyph);
        Assert.True(viewModel.VoiceAvailable);
    }

    [Fact]
    public void MuteToggleRaisesSettingsChangedOnceAndUpdatesTheGlyph()
    {
        var viewModel = new HudViewModel(new HudSettings());
        var changes = 0;
        viewModel.SettingsChanged += (_, _) => changes++;

        Assert.True(viewModel.IsMuted);
        Assert.Equal("M", viewModel.MuteGlyph);
        Assert.Contains("muted", viewModel.MuteTooltip, StringComparison.OrdinalIgnoreCase);

        viewModel.ToggleMuteCommand.Execute(null);

        Assert.False(viewModel.IsMuted);
        Assert.Equal("V", viewModel.MuteGlyph);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void TheDefaultViewHasNoCompetingCountChartButKeepsTheMuteControl()
    {
        StaTestHost.Invoke(() =>
        {
            HudThemeManager.Apply(Application.Current, HudTheme.Dark);
            var viewModel = new HudViewModel(HudSettings.Default);
            viewModel.ApplyActivity(Series(), VoiceStatus.Initial with { IsMuted = false, Detail = "Voice ready." });
            viewModel.IsExpanded = true;

            var window = new HudWindow { DataContext = viewModel, ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();

                // The old dominant "count of working tasks" graph is gone from every
                // surface: the project/task strips are the only hour view.
                Assert.Null(window.FindName("ActivityChart"));
                Assert.Null(window.FindName("CollapsedActivityChart"));
                Assert.Empty(FindVisuals<ActivitySparkline>(window));

                var mute = (System.Windows.Controls.Button)window.FindName("MuteButton")!;
                Assert.Equal("V", mute.Content);

                viewModel.ToggleMuteCommand.Execute(null);
                window.UpdateLayout();
                Assert.Equal("M", ((System.Windows.Controls.Button)window.FindName("MuteButton")!).Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static IReadOnlyList<T> FindVisuals<T>(System.Windows.DependencyObject node) where T : System.Windows.DependencyObject
    {
        var found = new List<T>();
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, index);
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(FindVisuals<T>(child));
        }

        return found;
    }

    [Fact]
    public void ExistingChartUsesNewThemeBrushesAfterRendering()
    {
        StaTestHost.Invoke(() =>
        {
            var control = new ActivitySparkline
            {
                Width = 320, Height = 30,
                Segments = ActivityChartGeometry.Build(Series(), 320, 30),
                LineBrush = Brushes.Red,
            };
            byte[] Render()
            {
                control.Measure(new Size(320, 30));
                control.Arrange(new Rect(0, 0, 320, 30));
                control.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 30, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(control);
                var pixels = new byte[320 * 30 * 4];
                bitmap.CopyPixels(pixels, 320 * 4, 0);
                return pixels;
            }
            var before = Render();
            control.LineBrush = Brushes.Blue;
            Assert.False(before.SequenceEqual(Render()));
        });
    }
}
