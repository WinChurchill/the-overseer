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
    public void CustomChromeSeparatesResizeCaptionControlsAndLight()
    {
        StaTestHost.Invoke(() =>
        {
            var window = new HudWindow { DataContext = new HudViewModel(HudSettings.Default), Width = 520, Height = 640 };
            try
            {
                window.SetWindowView(true);
                window.Show();
                window.UpdateLayout();
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                Assert.NotNull(chrome);
                Assert.False(chrome.UseAeroCaptionButtons);
                Assert.Equal(0, chrome.CaptionHeight);
                Assert.Equal(0, window.ChromeHitTest(new Point(2, 100)));
                Assert.Equal(2, window.ChromeHitTest(new Point(100, 14)));
                Point Center(string name)
                {
                    var element = (FrameworkElement)window.FindName(name);
                    return element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), window);
                }
                Assert.Equal(9, window.ChromeHitTest(Center("MaximizeButton")));
                foreach (var name in new[] { "MenuButton", "ExpandButton", "StatusLight" })
                    Assert.Equal(1, window.ChromeHitTest(Center(name)));
                var clicks = 0;
                window.MaximizeRequested += (_, _) => clicks++;
                ((System.Windows.Controls.Button)window.FindName("MaximizeButton")).RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(1, clicks);
            }
            finally { window.Close(); }
        });
    }

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
                Assert.True(region.ActualHeight > 60);
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
                var controls = (FrameworkElement)window.FindName("HeaderControls");
                Assert.Equal(System.Windows.Input.Cursors.Arrow, controls.Cursor);
                Assert.True(controls.ForceCursor);
                MouseEvent(controls, UIElement.PreviewMouseDownEvent);
                Assert.Null(System.Windows.Input.Mouse.Captured);
                // The summary sections and the health control are real controls: they
                // keep their own clicks and never start a window drag.
                MouseEvent((UIElement)window.FindName("HealthDot"), UIElement.PreviewMouseDownEvent);
                Assert.Null(System.Windows.Input.Mouse.Captured);
                var health = (System.Windows.Controls.Button)window.FindName("HealthButton");
                Assert.False(vm.IsHealthOpen);
                // The button owns the toggle command; the drag assertion above proves
                // the click never reaches the window drag gesture.
                Assert.Same(vm.ToggleHealthCommand, health.Command);
                vm.ToggleHealthCommand.Execute(null);
                Assert.True(vm.IsHealthOpen);
                var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("HealthPopup");
                Assert.True(popup.IsOpen);
                vm.CloseHealthCommand.Execute(null);
                Assert.False(vm.IsHealthOpen);

                var attention = FindVisuals<System.Windows.Controls.Button>((FrameworkElement)window.FindName("SummaryRegion"))
                    .First(button => button.Command is not null && ReferenceEquals(button.Command, vm.ShowAttentionCommand));
                // Raising the click event directly does not run a WPF command, so the
                // command itself is invoked here; the drag assertion above proves the
                // button keeps its own mouse input.
                attention.Command.Execute(attention.CommandParameter);
                Assert.Equal("attention", vm.TaskFilter);
                Assert.True(vm.IsExpanded);
                Assert.True(vm.IsFilterAttention);
                vm.ShowAllTasksCommand.Execute(null);
                Assert.True(vm.IsFilterAll);
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
                var list = (System.Windows.Controls.ItemsControl)window.FindName("ProjectList");
                var button = FindVisuals<System.Windows.Controls.Button>(list)
                    .First(candidate => candidate.DataContext is TaskRowViewModel row && row.Key.Equals(vm.Tasks[0].Key));
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
    public void ProjectRowsRenderExpandableHeadersWithStripsAndLegend()
    {
        StaTestHost.Invoke(() =>
        {
            HudThemeManager.Apply(Application.Current, HudTheme.Dark);
            var viewModel = new HudViewModel(HudSettings.Default);
            viewModel.ApplySnapshot(SyntheticHudScenarios.Mixed());
            viewModel.IsExpanded = true;
            var window = new HudWindow { DataContext = viewModel, Width = 440, Height = 640 };
            try
            {
                window.Show();
                window.UpdateLayout();

                var list = (System.Windows.Controls.ItemsControl)window.FindName("ProjectList")!;
                Assert.Equal(viewModel.Projects.Count, list.Items.Count);
                Assert.True(viewModel.Projects.Count > 1, "the synthetic scenario must exercise several projects");

                // One expandable project row per project, each with an affordance and
                // a real task list underneath.
                var headers = FindVisuals<System.Windows.Controls.Button>(list)
                    .Where(button => button.CommandParameter is ProjectRowViewModel).ToArray();
                Assert.Equal(viewModel.Projects.Count, headers.Length);
                Assert.All(headers, header => Assert.Contains("project", header.ToolTip!.ToString()!, StringComparison.OrdinalIgnoreCase));

                // History is disclosed on demand, and the shared axis/legend belongs to
                // the history area: it appears with the first open disclosure.
                var historyArea = (FrameworkElement)window.FindName("HistoryArea")!;
                Assert.Equal(Visibility.Collapsed, historyArea.Visibility);
                viewModel.Projects[0].IsHistoryOpen = true;
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, historyArea.Visibility);
                var legend = (System.Windows.Controls.ItemsControl)window.FindName("ActivityLegend")!;
                Assert.Equal(viewModel.Legend.Count, legend.Items.Count);
                Assert.True(legend.ActualWidth > 0);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void CollapsingAProjectHidesOnlyItsTaskRows()
    {
        StaTestHost.Invoke(() =>
        {
            HudThemeManager.Apply(Application.Current, HudTheme.Dark);
            var viewModel = new HudViewModel(HudSettings.Default);
            viewModel.ApplySnapshot(SyntheticHudScenarios.Mixed());
            viewModel.IsExpanded = true;
            var window = new HudWindow { DataContext = viewModel, Width = 440, Height = 640 };
            try
            {
                window.Show();
                window.UpdateLayout();
                var list = (System.Windows.Controls.ItemsControl)window.FindName("ProjectList")!;
                var first = viewModel.Projects[0];
                var rows = FindVisuals<System.Windows.Controls.Button>(list)
                    .Where(button => button.DataContext is TaskRowViewModel).ToArray();
                var mine = rows.Where(button => ((TaskRowViewModel)button.DataContext).ProjectGroupKey == first.Key).ToArray();
                Assert.NotEmpty(mine);
                Assert.All(mine, button => Assert.True(button.IsVisible));

                // Settled projects start collapsed under the first-appearance defaults;
                // collapsing one project must not change any other project's state.
                var others = viewModel.Projects.Where(project => project.Key != first.Key && project.IsExpanded).ToArray();
                Assert.NotEmpty(others);
                viewModel.ToggleProject(first);
                window.UpdateLayout();

                Assert.All(mine, button => Assert.False(button.IsVisible));
                Assert.All(others, project => Assert.True(project.IsExpanded));
                Assert.Equal(viewModel.Projects.Sum(project => project.Tasks.Count), viewModel.Tasks.Count);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void CloseControlMatchesTrayAvailability()
    {
        StaTestHost.Invoke(() =>
        {
            var window = new HudWindow { DataContext = new HudViewModel(HudSettings.Default) };
            try
            {
                window.Show();
                window.UpdateLayout();
                var close = (System.Windows.Controls.Button)window.FindName("CloseButton")!;
                Assert.Equal("Hide to tray", close.ToolTip);
                Assert.Equal("Hide to tray", System.Windows.Automation.AutomationProperties.GetName(close));

                window.SetCloseBehavior(hideToTray: false);
                Assert.Equal("Close OVERSEER", close.ToolTip);
                Assert.Equal("Close OVERSEER", System.Windows.Automation.AutomationProperties.GetName(close));

                window.SetCloseBehavior(hideToTray: true);
                Assert.Equal("Hide to tray", close.ToolTip);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void HeaderStaysPinnedWhileTheExpandedContentScrolls()
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
                window.SetWindowView(true);
                window.Height = 240;
                window.UpdateLayout();

                var header = (FrameworkElement)window.FindName("DragRegion")!;
                var scroller = (System.Windows.Controls.ScrollViewer)window.FindName("ContentScroll")!;
                var panel = (FrameworkElement)window.FindName("ExpandedPanel")!;

                // The header is outside the scroller, so a short layout cannot scroll
                // the controls or the attention summary out of reach.
                Assert.False(scroller.IsAncestorOf(header));
                Assert.True(scroller.IsAncestorOf(panel));
                Assert.Equal(Visibility.Visible, header.Visibility);
                Assert.Equal(System.Windows.Controls.ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);
                Assert.True(header.ActualHeight > 0);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(240)]
    [InlineData(640)]
    public void ExpandedHudHasOneScrollSurfaceAndCanReachTheLastProject(double height)
    {
        StaTestHost.Invoke(() =>
        {
            var vm = new HudViewModel(HudSettings.Default) { IsExpanded = true };
            var fixture = SyntheticHudScenarios.Mixed();
            vm.ApplySnapshot(fixture with { Tasks = fixture.Tasks.SelectMany(task => Enumerable.Range(0, 6)
                .Select(index => task with { Key = task.Key with { ConversationId = task.ConversationId + "-" + index },
                    ConversationId = task.ConversationId + "-" + index })).ToArray() });
            var window = new HudWindow { DataContext = vm, Width = 440, Height = height, ShowActivated = false };
            try
            {
                window.Show();
                window.SetWindowView(true);
                window.Height = height;
                window.UpdateLayout();
                var scroller = (System.Windows.Controls.ScrollViewer)window.FindName("ContentScroll")!;
                var projectList = (System.Windows.Controls.ItemsControl)window.FindName("ProjectList")!;
                Assert.Empty(FindVisuals<System.Windows.Controls.ScrollViewer>(scroller));
                Assert.True(scroller.ScrollableHeight > 0);
                projectList.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent });
                window.UpdateLayout();
                Assert.True(scroller.VerticalOffset > 0);
                scroller.ScrollToEnd();
                window.UpdateLayout();
                Assert.Equal(scroller.ScrollableHeight, scroller.VerticalOffset, precision: 1);
                var lastRow = FindVisuals<System.Windows.Controls.Button>(projectList)
                    .Last(button => button.DataContext is TaskRowViewModel);
                var bottom = lastRow.TransformToAncestor(scroller).Transform(new Point(0, lastRow.ActualHeight));
                Assert.InRange(bottom.Y, 0, scroller.ActualHeight);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void BrandingCollapsesBeforeHeaderControlsAndNothingIsClipped()
    {
        StaTestHost.Invoke(() =>
        {
            HudThemeManager.Apply(Application.Current, HudTheme.Dark);
            var viewModel = new HudViewModel(HudSettings.Default with { LightPosition = "right", LightSize = 128 });
            var window = new HudWindow { DataContext = viewModel, Width = 320, Height = 220, ShowActivated = false };
            try
            {
                window.Show();
                window.SetWindowView(true);
                window.Width = 320;
                window.Height = 220;
                window.UpdateLayout();

                // Branding gives way first; every control stays real and reachable.
                var brand = (FrameworkElement)window.FindName("BrandLabel")!;
                Assert.Equal(Visibility.Collapsed, brand.Visibility);
                var controls = (System.Windows.Controls.WrapPanel)window.FindName("HeaderControls")!;
                Assert.Equal(Visibility.Visible, controls.Visibility);
                foreach (var name in new[] { "CloseButton", "MenuButton", "ExpandButton", "MaximizeButton", "MuteButton", "LightToggleButton" })
                {
                    var element = (FrameworkElement)window.FindName(name)!;
                    Assert.True(element.ActualWidth >= 24, name + " must stay at least 24 DIP wide");
                    Assert.True(element.ActualHeight >= 24, name + " must stay at least 24 DIP tall");
                    var corner = element.TranslatePoint(new Point(element.ActualWidth, element.ActualHeight), window);
                    Assert.InRange(corner.X, 0, window.ActualWidth + 0.5);
                    Assert.InRange(corner.Y, 0, window.ActualHeight + 0.5);
                }

                // The light grip keeps a usable size instead of being clipped away.
                var grip = (FrameworkElement)window.FindName("LightResizeGrip")!;
                Assert.True(grip.ActualWidth > 0 && grip.ActualHeight > 0);

                // Widening brings the branding back without touching the controls.
                window.Width = 640;
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, brand.Visibility);
                Assert.Equal(Visibility.Visible, controls.Visibility);
            }
            finally { window.Close(); }
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
