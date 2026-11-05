using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TheOverseer.Core.Configuration;
using TheOverseer.Tests.Support;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

// Popups and drag assertions share native mouse capture. Keep both classes in
// this nonparallel collection so dispatcher pumping cannot interleave their tests.
[CollectionDefinition("Recent popup", DisableParallelization = true)]
public sealed class RecentPopupCollection { }

[Collection("Recent popup")]
public sealed class RecentSelectorTests
{
    [Fact]
    public void FacePopupAndSelectedItemFollowBothPalettesAndSelectionUpdatesRecentHours()
    {
        StaTestHost.Invoke(() =>
        {
            var vm = new HudViewModel(HudSettings.Default with { TaskView = "recent" }) { IsExpanded = true };
            var window = new HudWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000, Width = 360, Height = 480 };
            try
            {
                var palette = HudThemeManager.Load(HudTheme.Dark);
                window.Resources.MergedDictionaries.Add(palette);
                StaTestHost.ShowWindow(window);
                window.UpdateLayout();
                window.Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                var selector = (ComboBox)window.FindName("RecentHoursBox");
                selector.Visibility = Visibility.Visible;
                selector.ApplyTemplate();
                selector.Measure(new Size(100, 40));
                selector.Arrange(new Rect(0, 0, 100, 40));
                var popup = (Popup)selector.Template.FindName("PART_Popup", selector);
                var surface = (Border)selector.Template.FindName("PopupSurface", selector);
                Assert.NotNull(popup);
                Assert.True(selector.Focusable);
                Assert.False(selector.IsEditable);
                Assert.Equal("Recent time window in hours", System.Windows.Automation.AutomationProperties.GetName(selector));
                selector.IsDropDownOpen = true;
                window.Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                Assert.True(popup.IsOpen);
                var item = (ComboBoxItem)selector.ItemContainerGenerator.ContainerFromItem(2);
                Assert.NotNull(item);
                Color Colour(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
                void Check(ResourceDictionary colours)
                {
                    Assert.Equal(Colour((Brush)colours["PanelSurfaceBrush"]), Colour(selector.Background));
                    Assert.Equal(Colour((Brush)colours["ForegroundBrush"]), Colour(selector.Foreground));
                    Assert.Equal(Colour((Brush)colours["PanelSurfaceBrush"]), Colour(surface.Background));
                    Assert.Equal(Colour((Brush)colours["AccentPrimaryBrush"]), Colour(item.Background));
                    Assert.Equal(Colour((Brush)colours["BackgroundBaseBrush"]), Colour(item.Foreground));
                }
                Check(palette);
                palette = HudThemeManager.Load(HudTheme.Light);
                window.Resources.MergedDictionaries[0] = palette;
                Check(palette);
                Assert.Equal(5, selector.Items.Count);
                selector.SetCurrentValue(ComboBox.SelectedItemProperty, 8);
                window.Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                Assert.Equal(8, selector.SelectedItem);
                Assert.Equal(8, vm.RecentHours);
                vm.RecentHours = 2;
                selector.GetBindingExpression(ComboBox.SelectedItemProperty)!.UpdateTarget();
                Assert.Equal(2, selector.SelectedItem);
            }
            finally
            {
                ((ComboBox)window.FindName("RecentHoursBox")).IsDropDownOpen = false;
                window.Close();
            }
        });
    }
}
