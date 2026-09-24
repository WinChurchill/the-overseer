using TheOverseer.App.Desktop;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class TaskNavigationTests
{
    private static AgentTask Task(
        string conversationId = "01a0d000-0000-0000-0000-000000000001",
        ClientSurface surface = ClientSurface.Desktop,
        bool provisional = false,
        AgentTaskState state = AgentTaskState.Working) => new(
        new TaskKey("codex", conversationId),
        1,
        conversationId,
        "codex",
        surface,
        "Synthetic",
        "turn-1",
        state,
        new AgentAction(AgentActionKind.RunningTests, EvidenceKind.Structured, "test-runner"),
        EvidenceKind.Structured,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        null,
        null,
        false,
        provisional,
        false,
        false,
        false,
        null,
        1);

    [Fact]
    public void BuildsTheExactEncodedDesktopRoute()
    {
        var result = CodexTaskNavigator.BuildUri(Task(), "codex");

        Assert.Equal(TaskNavigationOutcome.Requested, result.Outcome);
        Assert.Equal("codex://threads/01a0d000-0000-0000-0000-000000000001", result.Uri);
    }

    [Fact]
    public void NonCodexTaskCannotUseCodexRouteEvenWithMatchingProfileAndSurface()
    {
        var task = Task() with { Key = TaskKey.For(HarnessIds.Claude, "codex", "01a0d000-0000-0000-0000-000000000001") };
        Assert.Null(CodexTaskNavigator.BuildUri(task, "codex").Uri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../../etc/passwd")]
    [InlineData("id?query=1")]
    [InlineData("id#fragment")]
    [InlineData("id with space")]
    [InlineData("short")]
    public void UnsafeIdentifiersAreRejected(string id)
    {
        Assert.False(CodexTaskNavigator.IsSafeConversationId(id));
        var result = CodexTaskNavigator.BuildUri(Task(id), "codex");
        Assert.Equal(TaskNavigationOutcome.InvalidIdentifier, result.Outcome);
        Assert.Null(result.Uri);
    }

    [Fact]
    public void OverlongIdentifierIsRejected()
    {
        var id = new string('a', 65);
        Assert.False(CodexTaskNavigator.IsSafeConversationId(id));
    }

    [Fact]
    public void UnsupportedSurfacesProvisionalIdentityAndProfilesAreDistinct()
    {
        Assert.Equal(TaskNavigationOutcome.UnsupportedSurface, CodexTaskNavigator.BuildUri(Task(surface: ClientSurface.Cli), "codex").Outcome);
        Assert.Equal(TaskNavigationOutcome.UnsupportedSurface, CodexTaskNavigator.BuildUri(Task(surface: ClientSurface.VsCode), "codex").Outcome);
        Assert.Equal(TaskNavigationOutcome.UnsupportedIdentity, CodexTaskNavigator.BuildUri(Task(provisional: true), "codex").Outcome);
        Assert.Equal(TaskNavigationOutcome.UnsupportedProfile, CodexTaskNavigator.BuildUri(Task(), "codex-2").Outcome);
    }

    [Fact]
    public void MissingHandlerIsReportedAsUnsupportedWithHonestCopy()
    {
        var boundary = new FakeTaskLaunchBoundary { Succeed = false, ErrorCode = "no-handler" };
        var navigator = new CodexTaskNavigator(boundary);

        var result = navigator.Navigate(Task(), "codex");
        Assert.Equal(TaskNavigationOutcome.NoHandler, result.Outcome);
        Assert.Contains("manually", result.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, boundary.LaunchCount);
        Assert.Equal("codex://threads/01a0d000-0000-0000-0000-000000000001", boundary.LastUri!.AbsoluteUri);
    }

    [Fact]
    public void LaunchFailureIsDistinctFromSuccessRequested()
    {
        var failing = new CodexTaskNavigator(new FakeTaskLaunchBoundary { Succeed = false, ErrorCode = "io" });
        Assert.Equal(TaskNavigationOutcome.LaunchFailed, failing.Navigate(Task(), "codex").Outcome);

        var boundary = new FakeTaskLaunchBoundary();
        var succeeding = new CodexTaskNavigator(boundary);
        var result = succeeding.Navigate(Task(), "codex");
        Assert.Equal(TaskNavigationOutcome.Requested, result.Outcome);
        Assert.Equal(1, boundary.LaunchCount);
    }

    [Fact]
    public void UnsupportedTasksNeverReachTheLaunchBoundary()
    {
        var boundary = new FakeTaskLaunchBoundary();
        var navigator = new CodexTaskNavigator(boundary);

        _ = navigator.Navigate(Task(surface: ClientSurface.Cli), "codex");
        _ = navigator.Navigate(Task(provisional: true), "codex");
        _ = navigator.Navigate(Task(), "codex-2");

        Assert.Equal(0, boundary.LaunchCount);
    }
}
