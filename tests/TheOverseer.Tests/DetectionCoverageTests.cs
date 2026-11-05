using System.Text.Json;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Tests.Support;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.Tests;

public sealed class DetectionCoverageTests
{
    [Fact]
    public void OlderAsyncQuestionLosesCurrentWaitConfidenceWithoutInventingAnAnswer()
    {
        using var fixture = new Fixture();
        fixture.Monitor.Refresh();
        fixture.Input();
        Assert.True(Assert.Single(fixture.Monitor.Refresh().Tasks).NeedsHuman);
        fixture.Clock.AdvanceSeconds(16 * 60);
        fixture.Record("response_item", new { type = "function_call", name = "functions.exec_command", call_id = "fresh-work", arguments = "{\"cmd\":\"echo PRIVATE_WORK\"}" });
        var snapshot = fixture.Monitor.Refresh();
        var task = Assert.Single(snapshot.Tasks);
        Assert.True(task.HasCurrentState);
        Assert.Equal(AgentTaskState.Working, task.State);
        Assert.False(task.NeedsHuman);
        Assert.True(task.HasUnconfirmedInput);
        Assert.True(task.HasUnconfirmedAttention);
        Assert.Equal(CountValue.Partial(1), snapshot.Counts.Working);
        Assert.Equal(CountValue.Partial(0), snapshot.Counts.NeedsHuman);
        Assert.Contains("async-input-status-unconfirmed", snapshot.Health.Reasons);
        Assert.Empty(snapshot.Transitions);
        var viewModel = new HudViewModel(HudSettings.Default);
        viewModel.ApplySnapshot(snapshot);
        Assert.DoesNotContain("Question pending", Assert.Single(viewModel.Tasks).RowSubtitle, StringComparison.Ordinal);
        Assert.Contains("unconfirmed", Assert.Single(viewModel.Tasks).RowSubtitle, StringComparison.Ordinal);

        // Retaining the old ID matters: an exact late reply still resolves it.
        fixture.Reply();
        var answered = fixture.Monitor.Refresh();
        Assert.False(Assert.Single(answered.Tasks).HasUnconfirmedInput);
        Assert.Equal(CountValue.Known(0), answered.Counts.NeedsHuman);
        Assert.Empty(answered.Transitions);
    }

    [Fact]
    public void FreshAsyncQuestionRearmsAfterAnOlderQuestionBecomesUnconfirmed()
    {
        using var fixture = new Fixture();
        fixture.Monitor.Refresh();
        fixture.Input("old");
        fixture.Monitor.Refresh();
        fixture.Clock.AdvanceSeconds(16 * 60);
        fixture.Record("response_item", new { type = "function_call", name = "functions.exec_command", call_id = "fresh-work", arguments = "{}" });
        fixture.Monitor.Refresh();
        fixture.Input("new");
        var snapshot = fixture.Monitor.Refresh();
        Assert.True(Assert.Single(snapshot.Tasks).NeedsHuman);
        Assert.True(Assert.Single(snapshot.Tasks).HasUnconfirmedInput);
        Assert.Equal(CountValue.Partial(1), snapshot.Counts.NeedsHuman);
        Assert.Single(snapshot.Transitions, item => item.Kind == AttentionEventKind.InputRequired);
        fixture.Reply("new");
        var oldStillUnknown = fixture.Monitor.Refresh();
        Assert.False(Assert.Single(oldStillUnknown.Tasks).NeedsHuman);
        Assert.True(Assert.Single(oldStillUnknown.Tasks).HasUnconfirmedInput);
        Assert.Equal(CountValue.Partial(0), oldStillUnknown.Counts.NeedsHuman);
    }

    [Theory]
    [InlineData("additional_tools", false)]
    [InlineData("inter_agent_communication_metadata", false)]
    [InlineData("agent_message", false)]
    [InlineData("SubAgentActivity", false)]
    [InlineData("additional_tools", true)]
    [InlineData("inter_agent_communication_metadata", true)]
    [InlineData("agent_message", true)]
    [InlineData("SubAgentActivity", true)]
    public void AuxiliaryRecordsPreserveInputThroughMonitorAndHud(string shape, bool hydration)
    {
        using var fixture = new Fixture();
        if (!hydration) fixture.Monitor.Refresh();
        fixture.Auxiliary(shape);
        fixture.Input();
        fixture.Record("response_item", new { type = "function_call_output", call_id = "question", output = "PRIVATE_ACK" });
        fixture.Auxiliary(shape);
        var snapshot = fixture.Monitor.Refresh();
        var task = Assert.Single(snapshot.Tasks);
        Assert.Equal(AgentTaskState.Working, task.State);
        Assert.True(task.NeedsHuman);
        Assert.False(task.IsStale);
        Assert.Equal(CountValue.Known(1), snapshot.Counts.NeedsHuman);
        Assert.True(snapshot.Health.CoverageComplete);
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(snapshot);
        Assert.True(Assert.Single(vm.Tasks).NeedsHuman);
        Assert.Equal(1, Assert.Single(vm.Projects).NeedsHumanCount);
        Assert.Equal("Needs you", vm.StatusHeadline);
        if (hydration) Assert.Empty(snapshot.Transitions);
        else Assert.Single(snapshot.Transitions, edge => edge.Kind == AttentionEventKind.InputRequired);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(snapshot));

        fixture.Reply();
        var answered = fixture.Monitor.Refresh();
        Assert.False(Assert.Single(answered.Tasks).NeedsHuman);
        Assert.Equal(CountValue.Known(0), answered.Counts.NeedsHuman);
        Assert.Empty(answered.Transitions);
        vm.ApplySnapshot(answered);
        Assert.Equal("Working", vm.StatusHeadline);
    }

    [Fact]
    public void UnknownEvidenceQualifiesPendingInputInsteadOfHidingIt()
    {
        using var fixture = new Fixture();
        fixture.Monitor.Refresh();
        fixture.Input();
        fixture.Monitor.Refresh();
        fixture.Record("event_msg", new { type = "future_lifecycle_event", turn_id = "turn" });
        var snapshot = fixture.Monitor.Refresh();
        var task = Assert.Single(snapshot.Tasks);
        Assert.True(task.NeedsHuman);
        Assert.False(task.IsStale); // Coverage loss is not an expired activity lease.
        Assert.True(task.IsStateUncertain);
        Assert.False(task.HasCurrentState);
        Assert.False(snapshot.Health.CoverageComplete);
        Assert.Equal(CountValue.Partial(0), snapshot.Counts.NeedsHuman);
        Assert.Empty(snapshot.Transitions);
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(snapshot);
        Assert.Contains("Input status unconfirmed", Assert.Single(vm.Tasks).RowSubtitle);
        Assert.Equal("Attention unconfirmed", vm.StatusHeadline);
        Assert.Equal(0, Assert.Single(vm.Projects).NeedsHumanCount);
        fixture.Reply();
        var answered = fixture.Monitor.Refresh();
        Assert.False(Assert.Single(answered.Tasks).NeedsHuman);
        vm.ApplySnapshot(answered);
        Assert.DoesNotContain(vm.Tasks, row => row.NeedsHuman);
    }

    [Fact]
    public void ExpiredInputRemainsVisibleWithoutConfirmedCountsOrRepeatedAlert()
    {
        using var fixture = new Fixture();
        fixture.Monitor.Refresh();
        fixture.Input();
        fixture.Monitor.Refresh();
        fixture.Clock.Advance(TimeSpan.FromMinutes(16));
        var snapshot = fixture.Monitor.Refresh();
        Assert.True(Assert.Single(snapshot.Tasks).IsStale);
        Assert.Equal(CountValue.Partial(0), snapshot.Counts.NeedsHuman);
        Assert.Empty(snapshot.Transitions);
        var vm = new HudViewModel(HudSettings.Default);
        vm.ApplySnapshot(snapshot);
        Assert.Contains("Input status unconfirmed", Assert.Single(vm.Tasks).RowSubtitle);
        Assert.Equal("Attention unconfirmed", vm.StatusHeadline);
    }

    [Theory]
    [InlineData("additional_tools")]
    [InlineData("inter_agent_communication_metadata")]
    [InlineData("agent_message")]
    [InlineData("SubAgentActivity")]
    public void AuxiliaryRecordsCannotReopenCompletedParentWork(string shape)
    {
        using var fixture = new Fixture();
        fixture.Record("event_msg", new { type = "task_complete", turn_id = "turn" });
        fixture.Monitor.Refresh();
        fixture.Auxiliary(shape);
        var snapshot = fixture.Monitor.Refresh();
        Assert.Equal(AgentTaskState.Done, Assert.Single(snapshot.Tasks).State);
        Assert.Equal(CountValue.Known(0), snapshot.Counts.Working);
        Assert.Equal(CountValue.Known(0), snapshot.Counts.NeedsHuman);
        Assert.Empty(snapshot.Transitions);
    }

    [Fact]
    public void UnknownEvidenceCannotCreateConfirmedHistoryOrLiveAttention()
    {
        using var fixture = new Fixture();
        fixture.Monitor.Refresh();
        fixture.Record("future_lifecycle", new { type = "PRIVATE_UNKNOWN" });
        fixture.Input();
        var snapshot = fixture.Monitor.Refresh();
        var task = Assert.Single(snapshot.Tasks);
        Assert.False(task.HasCurrentState);
        Assert.Equal(TheOverseer.Core.History.ActivityCategory.Unknown,
            TheOverseer.Core.History.TaskActivityRecorder.Classify(task).Category);
        Assert.Empty(snapshot.Transitions);
        Assert.Contains(HealthReasons.UnknownSchema, snapshot.Health.Reasons);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public void CodexRequestEvictionCannotTurnUnansweredInputIntoConfirmedZero()
    {
        using var fixture = new Fixture();
        fixture.Monitor.Refresh();
        for (var index = 0; index < 65; index++) fixture.Input("question-" + index);
        fixture.Monitor.Refresh();
        for (var index = 1; index < 65; index++) fixture.Reply("question-" + index);
        var snapshot = fixture.Monitor.Refresh();
        var task = Assert.Single(snapshot.Tasks);
        Assert.True(task.HasUnconfirmedAttention); // question-0 exceeded the retained ID set.
        Assert.Equal(CountValue.Partial(0), snapshot.Counts.NeedsHuman);
        Assert.False(snapshot.Health.CoverageComplete);
        Assert.Empty(snapshot.Transitions);
        fixture.Record("event_msg", new { type = "task_complete", turn_id = "turn" });
        var ended = fixture.Monitor.Refresh();
        Assert.False(Assert.Single(ended.Tasks).HasAttention);
        Assert.True(ended.Health.CoverageComplete);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Id = "01a0c700-0000-7000-8000-000000000111";
        private readonly SyntheticCodexHome _home = new();
        private readonly string _path;
        public ManualTimeProvider Clock { get; } = new(DateTimeOffset.FromUnixTimeSeconds(Records.BaseSeconds));
        public CodexMonitor Monitor { get; }

        public Fixture()
        {
            _path = _home.WriteRollout(Id, new[] { Records.SessionMeta(Id), Records.TaskStarted("turn") });
            File.SetLastWriteTimeUtc(_path, Clock.GetUtcNow().UtcDateTime);
            Monitor = new CodexMonitor(MonitorOptions.Default, new[]
            {
                new SessionProfile("codex", "Codex", _home.Root, _home.SessionsRoot, _home.IndexPath, _home.StateDatabasePath),
            }, timeProvider: Clock);
        }

        public void Record(string type, object payload)
        {
            Clock.AdvanceSeconds(1);
            SyntheticCodexHome.Append(_path, new[] { JsonSerializer.Serialize(new { type, timestamp = Clock.GetUtcNow(), payload }) });
            File.SetLastWriteTimeUtc(_path, Clock.GetUtcNow().UtcDateTime);
        }

        public void Input(string callId = "question") => Record("response_item", new
        {
            type = "function_call", name = "request_user_input_async", call_id = callId,
            arguments = "{\"questions\":[{\"title\":\"PRIVATE_QUESTION\"}]}",
        });

        public void Reply(string callId = "question") => Record("response_item", new
        {
            type = "message", role = "user", content = new[] { new
            {
                type = "input_text", text = "<send_user_message_question_reply>" +
                    JsonSerializer.Serialize(new[] { new { questionItemId = JsonSerializer.Serialize(new object[] { "request_user_input_async", callId, 0 }), answer = "PRIVATE_ANSWER" } }) +
                    "</send_user_message_question_reply>",
            } },
        });

        public void Auxiliary(string shape)
        {
            switch (shape)
            {
                case "inter_agent_communication_metadata": Record(shape, new { trigger_turn = false }); break;
                case "SubAgentActivity": Record("event_msg", new { type = "item_completed", turn_id = "turn", item = new { type = shape, kind = "completed", content = "PRIVATE_CHILD" } }); break;
                default: Record("response_item", new { type = shape, id = "metadata", content = "PRIVATE_METADATA" }); break;
            }
        }

        public void Dispose() { Monitor.Dispose(); _home.Dispose(); }
    }
}
