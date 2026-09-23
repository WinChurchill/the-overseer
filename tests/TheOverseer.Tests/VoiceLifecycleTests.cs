using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class VoiceLifecycleTests
{
    private static readonly DateTimeOffset Start = VoiceTestData.Start;
    private static VoiceSettings Settings => new VoiceSettings { Muted = false }.Normalized();
    private static VoiceSettings WithOptional => Settings with
    {
        Events = VoiceEventCatalog.All.ToDictionary(item => item.Code, _ => true),
    };

    [Fact]
    public void CatalogContainsNineUniqueEventsAndOnlyTwoOptIns()
    {
        Assert.Equal(9, VoiceEventCatalog.All.Count);
        Assert.Equal(9, VoiceEventCatalog.All.Select(item => item.Code).Distinct().Count());
        Assert.Equal(new[] { VoiceEventKind.StillWaiting, VoiceEventKind.SystemOnline }, VoiceEventCatalog.All.Where(item => !Settings.IsEnabled(item.Kind)).Select(item => item.Kind));
    }

    [Fact]
    public void StartupWaitsForAvailableSnapshotAndNeverRepeats()
    {
        var policy = new VoiceLifecyclePolicy();
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start, level: MonitoringHealthLevel.Unavailable), WithOptional, true));
        Assert.Equal(VoiceEventKind.SystemOnline, Assert.Single(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(1)), WithOptional, true)).Kind);
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(2)), WithOptional, true));
    }

    [Fact]
    public void MutedStartupNeverReplaysOnUnmute()
    {
        var policy = new VoiceLifecyclePolicy();
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start), WithOptional, false));
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(1)), WithOptional, true));
    }

    [Fact]
    public void FirstRecoveryDoesNotAlsoSpeakStartupGreeting()
    {
        var policy = new VoiceLifecyclePolicy();
        policy.Observe(VoiceTestData.Snapshot(Start, level: MonitoringHealthLevel.Unavailable), WithOptional, true);
        var loss = Assert.Single(policy.Pump(Start.AddSeconds(10), WithOptional, true));
        policy.Played(loss);
        Assert.Equal(VoiceEventKind.MonitoringRestored,
            Assert.Single(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(11)), WithOptional, true)).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreRequiresSuccessfulLossPlayback(bool played)
    {
        var policy = new VoiceLifecyclePolicy();
        policy.Observe(VoiceTestData.Snapshot(Start), Settings, true);
        policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(1), level: MonitoringHealthLevel.Unavailable), Settings, true);
        Assert.Empty(policy.Pump(Start.AddSeconds(10), Settings, true));
        var loss = Assert.Single(policy.Pump(Start.AddSeconds(11), Settings, true));
        Assert.Equal(VoiceEventKind.MonitoringLost, loss.Kind);
        Assert.Empty(policy.Pump(Start.AddSeconds(12), Settings, true));
        if (played) policy.Played(loss);
        var restored = policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(13)), Settings, true);
        Assert.Equal(played ? 1 : 0, restored.Count);
        Assert.False(policy.IsRelevant(loss, Start.AddSeconds(13)));
        if (played) Assert.Equal(VoiceEventKind.MonitoringRestored, restored[0].Kind);
    }

    [Fact]
    public void BriefOutageAndMutedOutageStaySilent()
    {
        var policy = new VoiceLifecyclePolicy();
        policy.Observe(VoiceTestData.Snapshot(Start), Settings, true);
        policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(1), level: MonitoringHealthLevel.Unavailable), Settings, true);
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(4)), Settings, true));
        policy.Fail(Start.AddSeconds(5));
        Assert.Empty(policy.Pump(Start.AddSeconds(16), Settings, false));
        Assert.Empty(policy.Pump(Start.AddSeconds(17), Settings, true));
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start.AddSeconds(18)), Settings, true));
    }

    private static MonitoringSnapshot Request(DateTimeOffset time, bool fresh = false, bool stale = false, bool cancelled = false) =>
        VoiceTestData.Snapshot(time,
            new[] { VoiceTestData.Task(state: AgentTaskState.NeedsApproval, stale: stale, cancelled: cancelled) },
            fresh ? new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, observedAt: time) } : null);

    [Fact]
    public void ReminderIsOnceAfterFiveMinutesAndClearsWithRequest()
    {
        var policy = new VoiceLifecyclePolicy();
        policy.Observe(VoiceTestData.Snapshot(Start), WithOptional, true);
        policy.Observe(Request(Start.AddSeconds(1), fresh: true), WithOptional, true);
        Assert.Empty(policy.Observe(Request(Start.AddMinutes(5)), WithOptional, true));
        var reminder = Assert.Single(policy.Observe(Request(Start.AddMinutes(5).AddSeconds(1)), WithOptional, true));
        Assert.Equal(VoiceEventKind.StillWaiting, reminder.Kind);
        Assert.True(policy.IsRelevant(reminder, reminder.ObservedAt));
        Assert.Empty(policy.Observe(Request(Start.AddMinutes(10)), WithOptional, true));
        policy.Observe(VoiceTestData.Snapshot(Start.AddMinutes(10).AddSeconds(1)), WithOptional, true);
        Assert.False(policy.IsRelevant(reminder, Start.AddMinutes(10).AddSeconds(1)));
    }

    [Fact]
    public void OldOrUnobservedRequestsNeverProduceReminders()
    {
        var policy = new VoiceLifecyclePolicy();
        policy.Observe(Request(Start), WithOptional, true);
        Assert.Empty(policy.Observe(Request(Start.AddMinutes(6)), WithOptional, true));
        policy.Observe(VoiceTestData.Snapshot(Start.AddMinutes(7)), WithOptional, true);
        policy.Observe(Request(Start.AddMinutes(8)), WithOptional, true);
        Assert.Empty(policy.Observe(Request(Start.AddMinutes(14)), WithOptional, true));
    }

    [Fact]
    public void StaleCancelledOrMutedRequestsCannotRemindLater()
    {
        foreach (var mode in new[] { "stale", "cancelled", "muted" })
        {
            var policy = new VoiceLifecyclePolicy();
            policy.Observe(VoiceTestData.Snapshot(Start), WithOptional, true);
            policy.Observe(Request(Start.AddSeconds(1), fresh: true), WithOptional, true);
            Assert.Empty(policy.Observe(Request(Start.AddMinutes(3), stale: mode == "stale", cancelled: mode == "cancelled"), WithOptional, mode != "muted"));
            Assert.Empty(policy.Observe(Request(Start.AddMinutes(6)), WithOptional, true));
        }
    }

    [Fact]
    public void UserCancellationIsNotAnErrorAnnouncement()
    {
        var policy = new VoiceEventPolicy();
        Assert.Empty(policy.Observe(VoiceTestData.Snapshot(Start,
            new[] { VoiceTestData.Task(state: AgentTaskState.Error, cancelled: true) },
            new[] { VoiceTestData.Event(AttentionEventKind.TaskError, observedAt: Start) })));
    }
}
