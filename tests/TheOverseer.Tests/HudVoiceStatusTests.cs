using System.Windows;
using TheOverseer.App.Diagnostics;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

public sealed class HudVoiceStatusTests
{
    [Fact]
    public void ViewModelProjectsVoiceStatus()
    {
        var viewModel = new HudViewModel(new HudSettings());
        var status = VoiceStatus.Initial with { IsMuted = false, ProviderId = "pack", Detail = "Spoken 2.", SpokenCount = 2 };

        viewModel.ApplyVoiceStatus(status);

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
            viewModel.ApplyVoiceStatus(VoiceStatus.Initial with { IsMuted = false, Detail = "Voice ready." });
            viewModel.IsExpanded = true;

            var window = new HudWindow { DataContext = viewModel, ShowActivated = false };
            try
            {
                StaTestHost.ShowWindow(window);
                window.UpdateLayout();

                // The old dominant "count of working tasks" graph is gone from every
                // surface: the project/task strips are the only hour view.
                Assert.Null(window.FindName("ActivityChart"));
                Assert.Null(window.FindName("CollapsedActivityChart"));

                var mute = (System.Windows.Controls.Button)window.FindName("MuteButton")!;
                var icon = Assert.IsType<System.Windows.Controls.Grid>(mute.Content);
                var mutedSlash = Assert.IsType<System.Windows.Shapes.Path>(icon.Children[1]);
                Assert.Equal(Visibility.Collapsed, mutedSlash.Visibility);
                Assert.Contains("enabled", System.Windows.Automation.AutomationProperties.GetName(mute));

                viewModel.ToggleMuteCommand.Execute(null);
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, mutedSlash.Visibility);
                Assert.Contains("muted", System.Windows.Automation.AutomationProperties.GetName(mute));
            }
            finally
            {
                window.Close();
            }
        });
    }

}
