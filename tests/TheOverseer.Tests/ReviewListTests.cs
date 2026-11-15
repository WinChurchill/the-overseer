using System.Text.Json;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

/// <summary>The "To review" checklist: finished tasks stay until ticked, opened or resumed.</summary>
public sealed class ReviewListTests
{
    // Real time: saved settings prune ticked marks against the clock.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static AgentTask Finished(string id = "01a0e000-0000-0000-0000-000000000001", DateTimeOffset? at = null, AgentTaskState state = AgentTaskState.Done) =>
        VoiceTestData.Task(VoiceTestData.Key(id), state: state) with
        {
            CompletedAt = at ?? Now.AddMinutes(-5),
            StateSince = at ?? Now.AddMinutes(-5),
            LastActivityAt = at ?? Now.AddMinutes(-5),
            Label = "Fix login‮gnp.exe",
        };

    private static AgentTask Working(string id = "01a0e000-0000-0000-0000-000000000001", DateTimeOffset? since = null) =>
        VoiceTestData.Task(VoiceTestData.Key(id)) with { StateSince = since ?? Now, LastActivityAt = since ?? Now };

    [Fact]
    public void AFinishedTaskIsAddedOnceWithACleanLabel()
    {
        var state = ReviewList.Apply(ReviewListState.Empty, new[] { Finished() }, Now);
        var item = Assert.Single(state.Items);
        Assert.Equal("Fix logingnp.exe", item.Label);
        Assert.Same(state, ReviewList.Apply(state, new[] { Finished() }, Now));
    }

    [Fact]
    public void FailedTasksAreListedToo()
    {
        var state = ReviewList.Apply(ReviewListState.Empty, new[] { Finished(state: AgentTaskState.Error) }, Now);
        Assert.True(Assert.Single(state.Items).Failed);
        Assert.Equal(AgentTaskState.Error, state.Items[0].ToTask().State);
    }

    [Fact]
    public void ItemsStayAfterTheTaskLeavesTheSnapshot()
    {
        var state = ReviewList.Apply(ReviewListState.Empty, new[] { Finished() }, Now);
        state = ReviewList.Apply(state, Array.Empty<AgentTask>(), Now.AddDays(2));
        Assert.Single(state.Items);
    }

    [Fact]
    public void TickingRemovesTheItemAndTheSameFinishIsNotAddedBack()
    {
        var state = ReviewList.Apply(ReviewListState.Empty, new[] { Finished() }, Now);
        state = ReviewList.MarkReviewed(state, VoiceTestData.Key(), Now);
        Assert.Empty(state.Items);
        Assert.Empty(ReviewList.Apply(state, new[] { Finished() }, Now).Items);

        // A later finish of the same task is new work to look at.
        var again = ReviewList.Apply(state, new[] { Finished(at: Now.AddMinutes(10)) }, Now.AddMinutes(10));
        Assert.Single(again.Items);
    }

    [Fact]
    public void WorkingAgainRemovesTheEarlierFinish()
    {
        var state = ReviewList.Apply(ReviewListState.Empty, new[] { Finished() }, Now);
        state = ReviewList.Apply(state, new[] { Working(since: Now.AddMinutes(1)) }, Now.AddMinutes(1));
        Assert.Empty(state.Items);
    }

    [Fact]
    public void UnconfirmedProvisionalAndOldFinishesAreNotAdded()
    {
        var uncertain = Finished() with { IsStateUncertain = true };
        var provisional = Finished("01a0e000-0000-0000-0000-000000000002") with { IsProvisional = true };
        var old = Finished("01a0e000-0000-0000-0000-000000000003", at: Now - ReviewList.AdmissionWindow - TimeSpan.FromMinutes(1));
        Assert.Empty(ReviewList.Apply(ReviewListState.Empty, new[] { uncertain, provisional, old }, Now).Items);
    }

    [Fact]
    public void TheListIsBoundedAndKeepsTheNewest()
    {
        var tasks = Enumerable.Range(0, ReviewList.MaximumItems + 10)
            .Select(i => Finished($"01a0e000-0000-0000-0000-{i:D12}", Now.AddMinutes(-i))).ToArray();
        var state = ReviewList.Apply(ReviewListState.Empty, tasks, Now);
        Assert.Equal(ReviewList.MaximumItems, state.Items.Count);
        Assert.Equal(Now, state.Items[0].CompletedAt);
    }

    [Fact]
    public void HandEditedSettingsAreValidatedAndSurviveARoundTrip()
    {
        var bad = new ReviewItem("codex", "codex", "id\nwith control", 1, "x", "", ClientSurface.Desktop, Now);
        var good = new ReviewItem("claude", "cc-1", "11111111-2222-3333-4444-555555555555", 2, "Ship it", "repo", ClientSurface.Desktop, Now,
            DesktopSessionId: "claude://evil");
        var settings = new HudSettings { Review = new ReviewListState(new[] { bad, good }, Array.Empty<ReviewedMark>()) }.Normalized();
        var item = Assert.Single(settings.Review.Items);
        Assert.Null(item.DesktopSessionId);

        var json = JsonSerializer.Serialize(settings);
        var back = JsonSerializer.Deserialize<HudSettings>(json)!.Normalized();
        Assert.Equal(item.Key, Assert.Single(back.Review.Items).Key);
    }

    [Fact]
    public void TheHudListsTicksAndPersistsFinishedTasks()
    {
        var vm = new HudViewModel(HudSettings.Default);
        var saves = 0;
        vm.SettingsChanged += (_, _) => saves++;
        vm.ApplySnapshot(Snapshot(Finished()));
        Assert.Equal("1", vm.ReviewCountText);
        Assert.True(saves > 0);

        vm.ShowReviewCommand.Execute(null);
        Assert.True(vm.IsFilterReview);
        vm.ApplySnapshot(Snapshot());
        var row = Assert.Single(vm.Tasks);
        Assert.True(row.IsReviewItem);
        Assert.True(vm.ShowTickAll);

        vm.MarkReviewedCommand.Execute(row);
        Assert.Equal("0", vm.ReviewCountText);
        Assert.Empty(vm.Tasks);

        var reopened = new HudViewModel(HudSettings.Default with { Review = vm.Review });
        reopened.ApplySnapshot(Snapshot(Finished()));
        Assert.Equal("0", reopened.ReviewCountText);
    }

    private static MonitoringSnapshot Snapshot(params AgentTask[] tasks) =>
        VoiceTestData.Snapshot(Now, tasks);
}
