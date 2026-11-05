using System.Text.Json;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;
using TheOverseer.UI.ViewModels;
using TheOverseer.UI;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TheOverseer.Tests;

public sealed class FocusPinTests
{
    private static string Import(object[] items) => JsonSerializer.Serialize(new { version = 1, items });
    private static object Proposal(string objective = "Finish the evaluation") =>
        new { project = "Research", objective, nextStep = "Inspect the held-out result", reason = "Explicit user request", uncertain = true };

    [Fact]
    public void ImportIsOnlyAProposalAndRetainsUncertainty()
    {
        var model = new HudViewModel(HudSettings.Default);
        var before = model.WorkingCountText;
        var result = FocusPinContract.Parse(Import(new[] { Proposal() }));
        Assert.True(result.Accepted);
        Assert.True(Assert.Single(result.Items).Uncertain);
        Assert.Empty(model.FocusPins);
        Assert.Equal(before, model.WorkingCountText);
    }

    [Theory]
    [InlineData("{\"version\":2,\"items\":[]}")]
    [InlineData("{\"version\":1,\"version\":1,\"items\":[]}")]
    [InlineData("{\"version\":1,\"items\":[],\"command\":\"run\"}")]
    [InlineData("{\"version\":1,\"items\":null}")]
    [InlineData("{\"version\":\"1\",\"items\":[]}")]
    [InlineData("[]")]
    [InlineData("```json\n{}\n```")]
    public void MalformedOrAmbiguousImportsCannotModifyPins(string json) =>
        Assert.False(FocusPinContract.Parse(json).Accepted);

    [Fact]
    public void ImportBoundsApplyBeforeAdmission()
    {
        Assert.False(FocusPinContract.Parse(Import(Enumerable.Repeat(Proposal(), 4).ToArray())).Accepted);
        Assert.False(FocusPinContract.Parse(Import(new[] { Proposal(new string('x', 241)) })).Accepted);
        Assert.False(FocusPinContract.Parse(Import(new[] { Proposal("First\nSecond") })).Accepted);
        Assert.False(FocusPinContract.Parse(new string(' ', FocusPinContract.MaximumImportBytes + 1)).Accepted);
        Assert.True(FocusPinContract.Parse(Import(Array.Empty<object>())).Accepted);
    }

    [Fact]
    public void ConfirmedPinsPersistWithoutChangingMonitoringOrOtherPreferences()
    {
        using var home = new SyntheticCodexHome();
        var path = Path.Combine(home.Root, "config.json");
        var pin = new FocusPin(Guid.NewGuid().ToString("N"), "Research", "Finish evaluation", "Review results", "User priority", VoiceTestData.Start);
        var model = new HudViewModel(HudSettings.Default);
        var changes = 0;
        model.SettingsChanged += (_, _) => changes++;
        model.SetFocusPins(new[] { pin });
        Assert.Equal(1, changes);
        Assert.True(model.HasFocusPins);
        Assert.Empty(model.Tasks);
        var config = MonitorConfig.Default with { Hud = HudSettings.Default with { FocusPins = model.FocusPins, Theme = "light" } };
        MonitorConfigStore.Save(path, config);
        var restored = MonitorConfigStore.Load(path).Config;
        Assert.Equal(pin, Assert.Single(restored.Hud.FocusPins));
        Assert.Equal("light", restored.Hud.Theme);
        Assert.True(restored.Voice.Muted);
        model.SetFocusPins(Array.Empty<FocusPin>());
        Assert.False(model.HasFocusPins);
    }

    [Fact]
    public void PersistedPinsAreBoundedAndDuplicateIdsCannotCreateExtraRows()
    {
        var pin = new FocusPin(Guid.NewGuid().ToString("N"), "", "One", "", "", VoiceTestData.Start);
        var pins = new[] { pin, pin, pin with { Id = "bad" }, pin with { Objective = "" } }
            .Concat(Enumerable.Range(0, 10).Select(i => pin with { Id = Guid.NewGuid().ToString("N"), Objective = i.ToString() }));
        Assert.Equal(3, FocusPinContract.Normalize(pins).Count);
    }

    [Fact]
    public void FocusWindowRequiresConfirmationAndAllowsEditingTheProposal()
    {
        StaTestHost.Invoke(() =>
        {
            var model = new HudViewModel(HudSettings.Default);
            var window = new FocusWindow(model, () => VoiceTestData.Start);
            try
            {
                ((TextBox)window.FindName("Import")).Text = Import(new[] { Proposal() });
                // Exercise actual routed controls; no visible window or clipboard.
                var panel = (StackPanel)((ScrollViewer)((DockPanel)window.Content).Children[2]).Content;
                var preview = panel.Children.OfType<Button>().Single(button => Equals(button.Content, "Preview JSON"));
                preview.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Empty(model.FocusPins);
                Assert.Contains("uncertain", ((TextBlock)window.FindName("SuggestionNote")).Text, StringComparison.OrdinalIgnoreCase);
                ((TextBox)window.FindName("Objective")).Text = "User edited objective";
                var confirm = panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(button => Equals(button.Content, "Confirm and pin"));
                confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("User edited objective", Assert.Single(model.FocusPins).Objective);
            }
            finally { window.Close(); }
        });
    }
}
