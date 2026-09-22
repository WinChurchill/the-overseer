using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.State;

namespace TheOverseer.Tests;

public sealed class CurrentActionClassifierTests
{
    private static AgentAction Classify(
        AgentTaskState state,
        bool approval = false,
        bool blockingInput = false,
        IReadOnlyList<ActiveToolView>? tools = null,
        AgentActionKind? lastAction = null,
        ToolCallCategory lastCategory = ToolCallCategory.None,
        bool hasActivity = false,
        bool cancelled = false) => CurrentActionClassifier.Instance.Classify(new ClassifierContext(
        state,
        approval,
        blockingInput,
        tools ?? Array.Empty<ActiveToolView>(),
        lastAction,
        hasActivity ? "detail" : null,
        lastCategory,
        hasActivity,
        cancelled));

    [Fact]
    public void TerminalStatesOverrideOldToolLabels()
    {
        var tools = new[] { new ActiveToolView(ToolCallCategory.CommandExecution, AgentActionKind.RunningCommand, "tool-exec", 5) };
        Assert.Equal(AgentActionKind.Completed, Classify(AgentTaskState.Done, tools: tools, lastAction: AgentActionKind.RunningCommand, hasActivity: true).Kind);
        Assert.Equal(AgentActionKind.Error, Classify(AgentTaskState.Error).Kind);
    }

    [Fact]
    public void WaitingStatesUseStructuredRequests()
    {
        Assert.Equal(AgentActionKind.WaitingForApproval, Classify(AgentTaskState.NeedsApproval, approval: true).Kind);
        Assert.Equal(AgentActionKind.WaitingForInput, Classify(AgentTaskState.NeedsInput, blockingInput: true).Kind);
    }

    [Fact]
    public void ApprovalWinsOverInputForDisplay()
    {
        var action = Classify(AgentTaskState.NeedsApproval, approval: true, blockingInput: true);
        Assert.Equal(AgentActionKind.WaitingForApproval, action.Kind);
    }

    [Fact]
    public void ConcurrentToolsResolveDeterministically()
    {
        var tools = new[]
        {
            new ActiveToolView(ToolCallCategory.CommandExecution, AgentActionKind.RunningCommand, "a", 1),
            new ActiveToolView(ToolCallCategory.FileChange, AgentActionKind.EditingFiles, "b", 2),
            new ActiveToolView(ToolCallCategory.CommandExecution, AgentActionKind.RunningTests, "c", 3),
        };

        Assert.Equal(AgentActionKind.RunningTests, Classify(AgentTaskState.Working, tools: tools).Kind);

        var withoutTests = new[] { tools[0], tools[1] };
        Assert.Equal(AgentActionKind.RunningCommand, Classify(AgentTaskState.Working, tools: withoutTests).Kind);

        var same = new[]
        {
            new ActiveToolView(ToolCallCategory.FileChange, AgentActionKind.EditingFiles, "old", 1),
            new ActiveToolView(ToolCallCategory.FileChange, AgentActionKind.EditingFiles, "new", 9),
        };
        var action = Classify(AgentTaskState.Working, tools: same);
        Assert.Equal("new", action.DetailCode);
    }

    [Fact]
    public void RecentActivityIsUsedBeforeTurnFallback()
    {
        var action = Classify(AgentTaskState.Working, lastAction: AgentActionKind.Searching, lastCategory: ToolCallCategory.Extension, hasActivity: true);
        Assert.Equal(AgentActionKind.Searching, action.Kind);
        Assert.Equal(EvidenceKind.Structured, action.Evidence);
    }

    [Fact]
    public void WorkingWithoutActivityFallsBackToThinking()
    {
        var action = Classify(AgentTaskState.Working);
        Assert.Equal(AgentActionKind.Thinking, action.Kind);
        Assert.Equal(EvidenceKind.Inferred, action.Evidence);
    }

    [Fact]
    public void IdleAndUnknownStayIdle()
    {
        Assert.Equal(AgentActionKind.Idle, Classify(AgentTaskState.Idle).Kind);
        Assert.Equal("cancelled", Classify(AgentTaskState.Idle, cancelled: true).DetailCode);
    }

    [Fact]
    public void SettledIdleStateDoesNotKeepAnOldWorkLabel()
    {
        // A cancelled or finished turn must not keep showing the last tool label.
        var action = Classify(
            AgentTaskState.Idle,
            tools: new[] { new ActiveToolView(ToolCallCategory.CommandExecution, AgentActionKind.RunningTests, "test-runner", 3) },
            lastAction: AgentActionKind.RunningTests,
            lastCategory: ToolCallCategory.CommandExecution,
            hasActivity: true,
            cancelled: true);

        Assert.Equal(AgentActionKind.Idle, action.Kind);
        Assert.Equal("cancelled", action.DetailCode);
    }

    [Fact]
    public void EveryCanonicalLabelIsStable()
    {
        Assert.Equal("Waiting for approval", AgentActionKind.WaitingForApproval.ToLabel());
        Assert.Equal("Running tests", AgentActionKind.RunningTests.ToLabel());
        Assert.Equal("Reading files", AgentActionKind.ReadingFiles.ToLabel());
        Assert.Equal("Editing files", AgentActionKind.EditingFiles.ToLabel());
        Assert.Equal("Running command", AgentActionKind.RunningCommand.ToLabel());
        Assert.Equal("Waiting for input", AgentActionKind.WaitingForInput.ToLabel());
        Assert.Equal("Completed", AgentActionKind.Completed.ToLabel());
        Assert.Equal("Idle", AgentActionKind.Idle.ToLabel());
        Assert.Equal("Error", AgentActionKind.Error.ToLabel());
        Assert.Equal("Thinking", AgentActionKind.Thinking.ToLabel());
        Assert.Equal("Searching", AgentActionKind.Searching.ToLabel());
    }
}
