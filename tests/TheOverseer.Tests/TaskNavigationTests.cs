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
    public void UnsupportedTaskNeverReachesEitherWindowsActivationRoute()
    {
        var shell = new FakeTaskLaunchBoundary();
        var packaged = new FakeTaskLaunchBoundary();
        var navigator = new CodexTaskNavigator(new ShellTaskLaunchBoundary(shell, packaged));
        var result = navigator.Navigate(Task(surface: ClientSurface.Cli), "codex");
        Assert.Equal(TaskNavigationOutcome.UnsupportedSurface, result.Outcome);
        Assert.Equal(0, shell.LaunchCount);
        Assert.Equal(0, packaged.LaunchCount);
    }

    [Theory]
    [InlineData("https://threads/00000000-0000-0000-0000-000000000000")]
    [InlineData("codex://settings/00000000-0000-0000-0000-000000000000")]
    [InlineData("codex://user@threads/00000000-0000-0000-0000-000000000000")]
    [InlineData("codex://threads:123/00000000-0000-0000-0000-000000000000")]
    [InlineData("codex://threads//00000000-0000-0000-0000-000000000000")]
    [InlineData("codex://threads/00000000-0000-0000-0000-000000000000?command=x")]
    [InlineData("codex://threads/00000000-0000-0000-0000-000000000000#x")]
    public void PackagedActivationRejectsNonTaskRoutesBeforeCallingWindows(string value)
    {
        var boundary = new PackagedCodexTaskLaunchBoundary();
        Assert.False(boundary.TryLaunch(new Uri(value), out var error));
        Assert.Equal("invalid-uri", error);
    }

    [Fact]
    public void StoreActivationIsUsedOnlyWhenShellHasNoAssociation()
    {
        var shell = new FakeTaskLaunchBoundary { Succeed = false, ErrorCode = "no-handler" };
        var packaged = new FakeTaskLaunchBoundary();
        var navigator = new CodexTaskNavigator(new ShellTaskLaunchBoundary(shell, packaged));
        var result = navigator.Navigate(Task(), "codex");
        Assert.Equal(TaskNavigationOutcome.Requested, result.Outcome);
        Assert.Equal(1, shell.LaunchCount);
        Assert.Equal(1, packaged.LaunchCount);
        Assert.Equal(shell.LastUri, packaged.LastUri);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "shell-launch-failed")]
    [InlineData(false, "handler-unavailable")]
    public void StoreActivationDoesNotOverrideShellSuccessOrOtherFailures(bool success, string? error)
    {
        var shell = new FakeTaskLaunchBoundary { Succeed = success, ErrorCode = error };
        var packaged = new FakeTaskLaunchBoundary();
        var navigator = new CodexTaskNavigator(new ShellTaskLaunchBoundary(shell, packaged));
        Assert.Equal(success ? TaskNavigationOutcome.Requested : TaskNavigationOutcome.LaunchFailed,
            navigator.Navigate(Task(), "codex").Outcome);
        Assert.Equal(0, packaged.LaunchCount);
    }

    [Theory]
    [InlineData("no-handler", TaskNavigationOutcome.NoHandler)]
    [InlineData("packaged-activation-failed", TaskNavigationOutcome.LaunchFailed)]
    public void StoreActivationFailureRemainsVisible(string error, TaskNavigationOutcome expected)
    {
        var navigator = new CodexTaskNavigator(new ShellTaskLaunchBoundary(
            new FakeTaskLaunchBoundary { Succeed = false, ErrorCode = "no-handler" },
            new FakeTaskLaunchBoundary { Succeed = false, ErrorCode = error }));
        Assert.Equal(expected, navigator.Navigate(Task(), "codex").Outcome);
    }

    private sealed class ThrowingTaskLaunchBoundary(Exception exception) : ITaskLaunchBoundary
    {
        public bool TryLaunch(Uri uri, out string? errorCode) => throw exception;
    }

    // Marshal.ThrowExceptionForHR raises these for E_INVALIDARG, E_NOINTERFACE and
    // access denied; none is a COMException, and a task click has no global handler.
    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    public void ThrowingStoreActivationBecomesAVisibleFailedLaunch(Type exceptionType)
    {
        var navigator = new CodexTaskNavigator(new ShellTaskLaunchBoundary(
            new FakeTaskLaunchBoundary { Succeed = false, ErrorCode = "no-handler" },
            new ThrowingTaskLaunchBoundary((Exception)Activator.CreateInstance(exceptionType)!)));

        var result = navigator.Navigate(Task(), "codex");

        Assert.Equal(TaskNavigationOutcome.LaunchFailed, result.Outcome);
        Assert.Contains("manually", result.UserMessage);
    }

    [Fact]
    public void ThrowingShellLaunchBecomesAVisibleFailedLaunchWithoutTryingStore()
    {
        var packaged = new FakeTaskLaunchBoundary();
        var navigator = new CodexTaskNavigator(new ShellTaskLaunchBoundary(
            new ThrowingTaskLaunchBoundary(new InvalidOperationException()), packaged));

        Assert.Equal(TaskNavigationOutcome.LaunchFailed, navigator.Navigate(Task(), "codex").Outcome);
        Assert.Equal(0, packaged.LaunchCount);
    }

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

    [Fact]
    public void ClaudeDesktopChatOpensItsOwnSessionLink()
    {
        var task = Task() with
        {
            Key = TaskKey.For(HarnessIds.Claude, "cc-test", "11111111-2222-3333-4444-555555555555"),
            DesktopSessionId = "local_0818420e-ff5b-4789-a3be-05bc084bc1c6",
        };
        var result = CodexTaskNavigator.BuildUri(task, "codex");
        Assert.Equal(TaskNavigationOutcome.Requested, result.Outcome);
        Assert.Equal("claude://claude.ai/epitaxy/local_0818420e-ff5b-4789-a3be-05bc084bc1c6", result.Uri);
        Assert.True(TheOverseer.UI.ViewModels.HudViewModel.CanNavigate(task));

        var launcher = new FakeTaskLaunchBoundary();
        Assert.Equal(TaskNavigationOutcome.Requested, new CodexTaskNavigator(launcher).Navigate(task, "codex").Outcome);
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("local_")]
    [InlineData("local_a/b")]
    [InlineData("local_abc?x=1")]
    [InlineData("claude://evil")]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    public void ClaudeChatsWithoutAValidDesktopIdCannotBeOpened(string? id)
    {
        var task = Task() with { Key = TaskKey.For(HarnessIds.Claude, "cc-test", "11111111-2222-3333-4444-555555555555"), DesktopSessionId = id };
        Assert.Null(CodexTaskNavigator.BuildUri(task, "codex").Uri);
        Assert.False(TheOverseer.UI.ViewModels.HudViewModel.CanNavigate(task));
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
