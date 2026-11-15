using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

/// <summary>Sessions joined midway still show in Now while they visibly work.</summary>
public sealed class NowViewTests
{
    private static readonly DateTimeOffset Now = VoiceTestData.Start.AddHours(1);

    private static AgentTask JoinedMidway(DateTimeOffset lastActivity) =>
        VoiceTestData.Task(VoiceTestData.Key()) with { IsStateUncertain = true, LastActivityAt = lastActivity, StateSince = lastActivity };

    [Fact]
    public void AnUnconfirmedTaskWithRecentActivityShowsInNowWithItsMark()
    {
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { JoinedMidway(Now.AddSeconds(-30)) }));
        var row = Assert.Single(vm.Tasks);
        Assert.Contains("state unconfirmed", row.MarksText, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnconfirmedTaskLeavesNowOnceItGoesQuiet()
    {
        var vm = new HudViewModel(HudSettings.Default);
        var quiet = Now - HudViewModel.UnconfirmedWorkWindow - TimeSpan.FromSeconds(1);
        vm.ApplySnapshot(VoiceTestData.Snapshot(Now, new[] { JoinedMidway(quiet) }));
        Assert.Empty(vm.Tasks);
    }
}
