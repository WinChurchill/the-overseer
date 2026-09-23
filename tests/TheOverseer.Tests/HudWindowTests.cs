using System.Windows;
using TheOverseer.App.Diagnostics;
using TheOverseer.Core.Configuration;
using TheOverseer.Tests.Support;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

public sealed class HudWindowTests
{
    [Fact]
    public void BothThemeDictionariesExposeTheSameKeys()
    {
        var keys = StaTestHost.Invoke(() =>
        {
            var light = HudThemeManager.Load(HudTheme.Light);
            var dark = HudThemeManager.Load(HudTheme.Dark);
            return (
                Light: light.Keys.Cast<object>().Select(key => key.ToString() ?? string.Empty).OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                Dark: dark.Keys.Cast<object>().Select(key => key.ToString() ?? string.Empty).OrderBy(key => key, StringComparer.Ordinal).ToArray());
        });

        Assert.NotEmpty(keys.Light);
        Assert.Equal(keys.Light, keys.Dark);
        Assert.Contains("BackgroundBaseBrush", keys.Light);
        Assert.Contains("AccentPrimaryBrush", keys.Light);
        Assert.Contains("HealthyBrush", keys.Light);
    }

    [Fact]
    public void ThemeSwitchKeepsOneWindowTreeAndExpansion()
    {
        StaTestHost.Invoke(() =>
        {
            HudThemeManager.Apply(Application.Current, HudTheme.Dark);
            var viewModel = new HudViewModel(HudSettings.Default);
            var window = new HudWindow { DataContext = viewModel, ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                viewModel.IsExpanded = true;
                window.UpdateLayout();

                var shell = (System.Windows.Controls.Border)window.FindName("Shell")!;
                var before = ((System.Windows.Media.SolidColorBrush)shell.Background).Color;
                HudThemeManager.Apply(Application.Current, HudTheme.Light);
                window.UpdateLayout();
                var after = ((System.Windows.Media.SolidColorBrush)shell.Background).Color;

                Assert.True(viewModel.IsExpanded, "theme change must not collapse the list");
                Assert.NotEqual(before, after);
                Assert.Equal("OVERSEER", window.Title);
                Assert.False(window.ShowActivated, "the HUD must not activate itself");
                Assert.Equal(viewModel.AlwaysOnTop, window.Topmost);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WindowBindsCollapsedSummaryAndRows()
    {
        StaTestHost.Invoke(() =>
        {
            HudThemeManager.Apply(Application.Current, HudTheme.Dark);
            var viewModel = new HudViewModel(HudSettings.Default);
            viewModel.ApplySnapshot(SyntheticHudScenarios.Mixed());
            var window = new HudWindow { DataContext = viewModel, ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();

                var drag = (FrameworkElement)window.FindName("DragRegion")!;
                var expandedPanel = (FrameworkElement)window.FindName("ExpandedPanel")!;
                Assert.Equal(Visibility.Collapsed, expandedPanel.Visibility);

                viewModel.IsExpanded = true;
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, expandedPanel.Visibility);
                Assert.True(drag.ActualWidth > 0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WholeSummaryCapturesDragButHeaderButtonsKeepTheirClicks()
    {
        StaTestHost.Invoke(() =>
        {
            HudThemeManager.Apply(Application.Current, HudTheme.Dark);
            var vm = new HudViewModel(HudSettings.Default);
            var window = new HudWindow { DataContext = vm };
            try
            {
                window.Show();
                window.UpdateLayout();
                var region = (System.Windows.Controls.Grid)window.FindName("DragRegion");
                var summary = (FrameworkElement)window.FindName("SummaryRegion");
                Assert.True(region.IsAncestorOf(summary));
                Assert.True(region.ActualHeight > 45);
                Assert.Equal(System.Windows.Input.Cursors.SizeAll, region.Cursor);
                Assert.Equal(System.Windows.Input.Cursors.Hand, ((FrameworkElement)window.FindName("MuteButton")).Cursor);
                Assert.Equal(System.Windows.Input.Cursors.Hand, ((FrameworkElement)window.FindName("ExpandButton")).Cursor);
                void MouseEvent(UIElement target, RoutedEvent routedEvent) => target.RaiseEvent(
                    new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = routedEvent });
                MouseEvent(summary, UIElement.PreviewMouseDownEvent);
                Assert.Same(region, System.Windows.Input.Mouse.Captured);
                MouseEvent(region, UIElement.PreviewMouseUpEvent);
                Assert.Null(System.Windows.Input.Mouse.Captured);
                Assert.False(vm.IsExpanded);
                MouseEvent((UIElement)window.FindName("MuteButton"), UIElement.PreviewMouseDownEvent);
                Assert.Null(System.Windows.Input.Mouse.Captured);
                MouseEvent((UIElement)window.FindName("StatusButton"), UIElement.PreviewMouseDownEvent);
                MouseEvent(region, UIElement.PreviewMouseUpEvent);
                Assert.True(vm.IsExpanded);
            }
            finally { System.Windows.Input.Mouse.Capture(null); window.Close(); }
        });
    }

    [Fact]
    public void TaskContextMenuCopiesOnlyTheSelectedIdentifierOnExplicitClick()
    {
        StaTestHost.Invoke(() =>
        {
            TaskRowViewModel? copied = null;
            var vm = new HudViewModel(HudSettings.Default, copyTaskId: row => copied = row);
            vm.ApplySnapshot(SyntheticHudScenarios.Mixed());
            vm.IsExpanded = true;
            var window = new HudWindow { DataContext = vm, Width = 360, Height = 392 };
            try
            {
                window.Show();
                window.UpdateLayout();
                var list = (System.Windows.Controls.ItemsControl)window.FindName("TaskList");
                var container = list.ItemContainerGenerator.ContainerFromIndex(0);
                var button = FindButton(container)!;
                var menu = button.ContextMenu;
                menu.PlacementTarget = button;
                menu.IsOpen = true;
                Assert.Null(copied);
                ((System.Windows.Controls.MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Assert.Same(vm.Tasks[0], copied);
                menu.IsOpen = false;
            }
            finally { window.Close(); }
        });
    }

    private static System.Windows.Controls.Button? FindButton(DependencyObject node)
    {
        if (node is System.Windows.Controls.Button button) return button;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var found = FindButton(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            if (found is not null) return found;
        }
        return null;
    }

    [Fact]
    public void DragThresholdSeparatesClickFromDrag()
    {
        var tracker = new DragGestureTracker();
        tracker.Begin(10, 10);
        Assert.False(tracker.Move(11, 11));
        Assert.True(tracker.End());

        tracker.Begin(10, 10);
        Assert.True(tracker.Move(20, 10));
        Assert.True(tracker.IsDragging);
        Assert.False(tracker.End());
    }
}
