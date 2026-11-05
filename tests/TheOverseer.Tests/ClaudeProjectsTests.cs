using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class ClaudeProjectsTests
{
    private static readonly DateTimeOffset Start = VoiceTestData.Start;
    private static readonly SourceInstance Source = new(HarnessIds.Claude, "projects-desktop", "Claude Projects");
    private static ClaudeProjectObservation Project(string id, string state = "Working", string scope = "account-a", DateTimeOffset? at = null) =>
        new(scope, id, "Project", at ?? Start, new[] { new ClaudeProjectWorker("same-worker", state) }, true);
    private static ClaudeProjectRead Read(params ClaudeProjectObservation[] projects) => new(projects, "ok");

    [Fact]
    public void EqualWorkerIdsInDifferentProjectsAndScopesNeverAlias()
    {
        var tracker = new ClaudeProjectTracker(Source);
        var result = tracker.Apply(Read(Project("p1"), Project("p2"), Project("p1", scope: "account-b")), Start, 32);
        Assert.Equal(3, result.Tasks.Select(task => task.Key).Distinct().Count());
        Assert.Equal(3, result.Tasks.Select(task => task.AssignedProject.Id).Distinct().Count());
        Assert.All(result.Tasks, task => Assert.Empty(task.WorkspaceRoot));
        Assert.False(result.CoverageComplete);
    }

    [Fact]
    public void InitialAndRepeatedAttentionIsSilentButObservedChangesProduceOneEdge()
    {
        var tracker = new ClaudeProjectTracker(Source);
        Assert.Empty(tracker.Apply(Read(Project("p1")), Start, 32).Transitions);
        var waiting = tracker.Apply(Read(Project("p1", "Waiting on you", at: Start.AddSeconds(1))), Start.AddSeconds(1), 32);
        Assert.Equal(AgentTaskState.NeedsInput, Assert.Single(waiting.Tasks).State);
        Assert.Equal(AttentionEventKind.InputRequired, Assert.Single(waiting.Transitions).Kind);
        Assert.Empty(tracker.Apply(Read(Project("p1", "Waiting on you", at: Start.AddSeconds(2))), Start.AddSeconds(2), 32).Transitions);
        Assert.Empty(tracker.Apply(Read(Project("p1", "Working", at: Start.AddSeconds(3))), Start.AddSeconds(3), 32).Transitions);
        var cold = new ClaudeProjectTracker(Source);
        Assert.Empty(cold.Apply(Read(Project("p1", "Waiting on you")), Start, 32).Transitions);
    }

    [Fact]
    public void MissingPaneRetainsQualifiedAttentionAndReturnDoesNotReplayIt()
    {
        var tracker = new ClaudeProjectTracker(Source);
        tracker.Apply(Read(Project("p1", "Waiting on you")), Start, 32);
        var missing = tracker.Apply(new(Array.Empty<ClaudeProjectObservation>(), "projects-overview-not-visible"), Start.AddSeconds(1), 32);
        var task = Assert.Single(missing.Tasks);
        Assert.True(task.HasUnconfirmedAttention);
        Assert.False(task.HasCurrentState);
        Assert.Equal(MonitoringHealthLevel.Unavailable, missing.Health);
        Assert.Empty(tracker.Apply(Read(Project("p1", "Waiting on you", at: Start.AddSeconds(2))), Start.AddSeconds(2), 32).Transitions);
    }

    [Theory]
    [InlineData("Idle", AgentTaskState.Idle, false)]
    [InlineData("Resolved", AgentTaskState.Idle, false)]
    [InlineData("Ready for review", AgentTaskState.NeedsInput, false)]
    [InlineData("Landing", AgentTaskState.Working, false)]
    [InlineData("Unexpected", AgentTaskState.Working, true)]
    public void StateGroupsDoNotInventSuccessfulCompletion(string group, AgentTaskState expected, bool uncertain)
    {
        var tracker = new ClaudeProjectTracker(Source);
        tracker.Apply(Read(Project("p1")), Start, 32);
        var result = tracker.Apply(Read(Project("p1", group, at: Start.AddSeconds(1))), Start.AddSeconds(1), 32);
        var task = Assert.Single(result.Tasks);
        Assert.Equal(expected, task.State);
        Assert.Equal(uncertain, task.IsStateUncertain);
        Assert.Null(task.CompletedAt);
        Assert.DoesNotContain(result.Transitions, edge => edge.Kind is AttentionEventKind.TaskCompleted or AttentionEventKind.AllTasksCompleted);
    }

    [Fact]
    public void DisconnectedWorkerCannotEraseAnUnresolvedRequest()
    {
        var tracker = new ClaudeProjectTracker(Source);
        tracker.Apply(Read(Project("p1", "Waiting on you")), Start, 32);
        var project = Project("p1", at: Start.AddSeconds(1)) with { Workers = new[] { new ClaudeProjectWorker("same-worker", "Idle", true) } };
        var result = tracker.Apply(Read(project), Start.AddSeconds(1), 32);
        Assert.True(Assert.Single(result.Tasks).HasUnconfirmedAttention);
    }

    [Fact]
    public void FutureAndOldObservationsCannotPoisonTheWatermarkOrFreshness()
    {
        var tracker = new ClaudeProjectTracker(Source);
        tracker.Apply(Read(Project("p1")), Start, 32);
        var future = tracker.Apply(Read(Project("p1", "Waiting on you", at: Start.AddHours(1))), Start.AddSeconds(1), 32);
        Assert.Equal(Start, future.EvidenceAt);
        Assert.True(Assert.Single(future.Tasks).IsStateUncertain);
        var valid = tracker.Apply(Read(Project("p1", "Waiting on you", at: Start.AddSeconds(2))), Start.AddSeconds(2), 32);
        Assert.True(Assert.Single(valid.Tasks).HasCurrentState);
        var old = tracker.Apply(Read(Project("p1", "Working")), Start.AddSeconds(3), 32);
        Assert.Equal(AgentTaskState.NeedsInput, Assert.Single(old.Tasks).State);
        Assert.Equal(Start.AddSeconds(2), old.EvidenceAt);
    }

    [Fact]
    public void DuplicateAndCappedWorkersRemainUnconfirmedAndBounded()
    {
        var tracker = new ClaudeProjectTracker(Source);
        var project = Project("p1") with { Workers = new[] { new ClaudeProjectWorker("same-worker", "Working"), new ClaudeProjectWorker("same-worker", "Waiting on you") } };
        Assert.True(Assert.Single(tracker.Apply(Read(project), Start, 1).Tasks).IsStateUncertain);
        var capped = tracker.Apply(Read(Project("p2"), Project("p3")), Start, 1);
        Assert.Single(capped.Tasks);
        Assert.Contains("projects-session-limit", capped.HealthReasons);
    }

    [Fact]
    public void AcceptedPartialReadStillRejectsAnOlderFollowingSnapshot()
    {
        var tracker = new ClaudeProjectTracker(Source);
        tracker.Apply(Read(Project("p1")), Start, 32);
        tracker.Apply(new(new[] { Project("p1", "Waiting on you", at: Start.AddSeconds(2)) }, "projects-read-budget"), Start.AddSeconds(2), 32);
        var old = tracker.Apply(Read(Project("p1", "Working", at: Start.AddSeconds(1))), Start.AddSeconds(3), 32);
        Assert.Equal(AgentTaskState.NeedsInput, Assert.Single(old.Tasks).State);
        Assert.True(Assert.Single(old.Tasks).IsStateUncertain);
        Assert.Equal(Start.AddSeconds(2), old.EvidenceAt);
    }

    private sealed class Node(ClaudeOverviewNodeKind kind, string name = "", string address = "", params Node[] children) : IClaudeOverviewNode
    {
        public ClaudeOverviewNodeKind Kind => kind;
        public bool Private { get; init; }
        public bool PrivateContent { get; init; }
        public string Name => Private || PrivateContent || kind is ClaudeOverviewNodeKind.Link or ClaudeOverviewNodeKind.Other ? throw new InvalidOperationException("Private name read") : name;
        public string Address => Private ? throw new InvalidOperationException("Private value read") : address;
        public IEnumerable<IClaudeOverviewNode> Children => Private || PrivateContent ? throw new InvalidOperationException("Private descendants read") : children;
        public void Dispose() { }
    }

    [Fact]
    public void OverviewExtractionPrunesMessagesEditsAggregateLabelsAndSnippetsBeforeReadingThem()
    {
        const string root = "https://claude.ai/epitaxy/project/chan_test";
        var overview = new Node(ClaudeOverviewNodeKind.Pane, "Overview", "",
            new(ClaudeOverviewNodeKind.Heading, "Waiting on you · 2"),
            new(ClaudeOverviewNodeKind.Link, "private title", root + "?thread=cmsg_one", new Node(ClaudeOverviewNodeKind.Other, "private snippet")),
            new(ClaudeOverviewNodeKind.Link, "private title", root + "?thread=cmsg_two"),
            new(ClaudeOverviewNodeKind.Heading, "Idle · 1"),
            new(ClaudeOverviewNodeKind.Link, "private title", root + "?thread=cmsg_three", new Node(ClaudeOverviewNodeKind.Image, "Disconnected")),
            new(ClaudeOverviewNodeKind.Heading, "Resolved · 1"));
        var document = new Node(ClaudeOverviewNodeKind.Document, "Project - Claude Code", root,
            new Node(ClaudeOverviewNodeKind.Feed) { Private = true },
            new Node(ClaudeOverviewNodeKind.Edit) { Private = true }, overview);
        var result = new ClaudeOverviewParser("account", Start, 64 * 1024, 32).Read(new[] { document });
        var project = Assert.Single(result.Projects);
        Assert.Equal(3, project.Workers.Count);
        Assert.False(project.MembershipComplete);
        Assert.True(project.Workers[2].Disconnected);
        Assert.Equal("Project", project.ProjectName);
    }

    [Theory]
    [InlineData("https://claude.ai/projects")]
    [InlineData("https://claude.ai/code/projects/browse")]
    public void ProjectsDirectoryIsNotWorkerEvidenceAndItsContentIsNeverRead(string address)
    {
        var directory = new Node(ClaudeOverviewNodeKind.Document, address: address) { PrivateContent = true };
        var result = new ClaudeOverviewParser("account", Start, 64 * 1024, 32).Read(new[] { directory });
        Assert.Empty(result.Projects);
        Assert.Equal("projects-directory-has-no-worker-state", result.Reason);
    }

    private static Node OverviewDocument(string id)
    {
        var address = "https://claude.ai/epitaxy/project/chan_" + id;
        return new(ClaudeOverviewNodeKind.Document, "Project - Claude Code", address,
            new Node(ClaudeOverviewNodeKind.Pane, "Overview", "",
                new Node(ClaudeOverviewNodeKind.Heading, "Working · 1"),
                new Node(ClaudeOverviewNodeKind.Link, address: address + "?thread=cmsg_worker")));
    }

    [Fact]
    public void RepeatedLinksToOneWorkerDoNotConsumeTheSessionBudgetOrInflateMembership()
    {
        const string address = "https://claude.ai/epitaxy/project/chan_test";
        var document = new Node(ClaudeOverviewNodeKind.Document, "Project", address,
            new Node(ClaudeOverviewNodeKind.Pane, "Overview", "",
                new Node(ClaudeOverviewNodeKind.Heading, "Idle · 2"),
                new Node(ClaudeOverviewNodeKind.Link, address: address + "?thread=cmsg_one"),
                new Node(ClaudeOverviewNodeKind.Link, address: address + "?thread=cmsg_one"),
                new Node(ClaudeOverviewNodeKind.Link, address: address + "?thread=cmsg_two"),
                new Node(ClaudeOverviewNodeKind.Link, address: address + "?thread=cmsg_two")));
        var read = new ClaudeOverviewParser("account", Start, 64 * 1024, 2).Read(new[] { document });
        var project = Assert.Single(read.Projects);
        Assert.Equal("ok", read.Reason);
        Assert.Equal(2, project.Workers.Count);
        Assert.True(project.MembershipComplete);
        Assert.All(new ClaudeProjectTracker(Source).Apply(read, Start, 2).Tasks,
            task => Assert.True(task.HasCurrentState));
    }

    [Fact]
    public void IdenticalWorkersAcrossLoadedViewsRemainCurrentAndProduceOneAttentionEdge()
    {
        var tracker = new ClaudeProjectTracker(Source);
        var working = Project("p1");
        Assert.Empty(tracker.Apply(Read(working, working), Start, 1).Transitions);
        var waiting = Project("p1", "Waiting on you", at: Start.AddSeconds(1));
        var result = tracker.Apply(Read(waiting, waiting), Start.AddSeconds(1), 1);
        Assert.True(Assert.Single(result.Tasks).HasCurrentState);
        Assert.True(Assert.Single(result.Tasks).NeedsHuman);
        Assert.Equal(AttentionEventKind.InputRequired, Assert.Single(result.Transitions).Kind);
        Assert.DoesNotContain("projects-duplicate-observation", result.HealthReasons);
    }

    [Theory]
    [InlineData("Working", true, 1)]
    [InlineData("Working", true, 8)]
    [InlineData("Waiting on you", false, 1)]
    [InlineData("Waiting on you", false, 8)]
    public void ConflictingLinksAreRetainedAsUnconfirmedWithoutAnAttentionEdge(string group, bool disconnected, int maximumSessions)
    {
        const string address = "https://claude.ai/epitaxy/project/chan_test";
        var document = new Node(ClaudeOverviewNodeKind.Document, "Project", address,
            new Node(ClaudeOverviewNodeKind.Pane, "Overview", "",
                new Node(ClaudeOverviewNodeKind.Heading, "Working · 1"),
                new Node(ClaudeOverviewNodeKind.Link, address: address + "?thread=cmsg_worker"),
                new Node(ClaudeOverviewNodeKind.Heading, group + " · 1"),
                new Node(ClaudeOverviewNodeKind.Link, "private title", address + "?thread=cmsg_worker",
                    new Node(ClaudeOverviewNodeKind.Image, disconnected ? "Disconnected" : "status"))));
        var read = new ClaudeOverviewParser("account", Start, 64 * 1024, maximumSessions).Read(new[] { document });
        Assert.Single(Assert.Single(read.Projects).Workers);
        var result = new ClaudeProjectTracker(Source).Apply(read, Start, maximumSessions);
        Assert.True(Assert.Single(result.Tasks).IsStateUncertain);
        Assert.Contains("projects-conflicting-worker", result.HealthReasons);
        Assert.Empty(result.Transitions);
    }

    [Fact]
    public void MultipleLoadedWindowsShareOneSessionBudgetAndKeepProjectIdentity()
    {
        var result = new ClaudeOverviewParser("account", Start, 64 * 1024, 1)
            .Read(new[] { OverviewDocument("one"), OverviewDocument("two") });
        Assert.Equal(2, result.Projects.Count);
        Assert.Equal("chan_one", result.Projects[0].ProjectId);
        Assert.Equal("chan_two", result.Projects[1].ProjectId);
        Assert.Single(result.Projects.SelectMany(project => project.Workers));
        Assert.Equal("projects-session-limit", result.Reason);
        Assert.All(result.Projects, project => Assert.False(project.MembershipComplete));
    }

    [Fact]
    public void MultipleRootsShareByteBudgetAndNeverTraverseTheFifthWindow()
    {
        var roots = Enumerable.Range(0, 4).Select(index => OverviewDocument("p" + index))
            .Append(new Node(ClaudeOverviewNodeKind.Document) { Private = true });
        var bounded = new ClaudeOverviewParser("account", Start, 64 * 1024, 32).Read(roots);
        Assert.Equal(4, bounded.Projects.Count);
        Assert.Equal("ok", bounded.Reason);
        var limited = new ClaudeOverviewParser("account", Start, 1024, 32).Read(roots);
        Assert.Equal("projects-read-budget", limited.Reason);
        Assert.True(limited.Projects.Count < bounded.Projects.Count);
        Assert.All(limited.Projects, project => Assert.False(project.MembershipComplete));
    }

    [Fact]
    public void DirectoryAlongsideLoadedOverviewDoesNotEraseWorkerEvidence()
    {
        var directory = new Node(ClaudeOverviewNodeKind.Document, address: "https://claude.ai/projects") { PrivateContent = true };
        var result = new ClaudeOverviewParser("account", Start, 64 * 1024, 32)
            .Read(new[] { directory, OverviewDocument("one") });
        Assert.Single(Assert.Single(result.Projects).Workers);
        Assert.Equal("ok", result.Reason);
    }

    [Theory]
    [InlineData("https://evil.example/epitaxy/project/chan_test?thread=cmsg_one")]
    [InlineData("http://claude.ai/epitaxy/project/chan_test")]
    [InlineData("https://claude.ai/epitaxy/project/chan_test?thread=cmsg_one&thread=cmsg_two")]
    [InlineData("https://claude.ai/epitaxy/project/chan_test/other")]
    [InlineData("https://claude.ai/epitaxy/project/chan_test?thread=cmsg_%6fne")]
    public void AmbiguousOrForeignIdentitiesAreRejected(string address) =>
        Assert.False(ClaudeOverviewParser.TryThreadAddress(address, out _, out _));

    [Fact]
    public async Task BlockedReaderHasOneWorkerAndDoesNotHoldShutdownOrQueueMoreReads()
    {
        using var gate = new ManualResetEventSlim();
        var calls = 0;
        var adapter = new ClaudeProjectsMonitorAdapter((_, _) =>
        {
            Interlocked.Increment(ref calls);
            gate.Wait(TimeSpan.FromSeconds(10));
            return Read(Project("p1"));
        }, readTimeout: TimeSpan.FromMilliseconds(40));
        try
        {
            var timeout = await adapter.PollAsync();
            Assert.Contains("projects-reader-timeout", timeout.HealthReasons);
            for (var i = 0; i < 8; i++) await adapter.PollAsync();
            Assert.Equal(1, calls);
            var stop = adapter.DisposeAsync().AsTask();
            await stop.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { gate.Set(); await adapter.DisposeAsync(); }
    }

    [Fact]
    public void ObserverOptInSurvivesConfigNormalizationAndDefaultsOff()
    {
        Assert.False(MonitorConfig.Default.ClaudeProjectsEnabled);
        Assert.True(MonitorConfigStore.Normalize(MonitorConfig.Default with { ClaudeProjectsEnabled = true }).ClaudeProjectsEnabled);
    }

    [Fact]
    public void DedicatedTitleIsReadWithoutAggregateLinkNamesOrPrivateSnippets()
    {
        const string address = "https://claude.ai/epitaxy/project/chan_titles";
        var document = new Node(ClaudeOverviewNodeKind.Document, "Project", address,
            new Node(ClaudeOverviewNodeKind.Feed) { Private = true },
            new Node(ClaudeOverviewNodeKind.Pane, "Overview", "",
                new Node(ClaudeOverviewNodeKind.Heading, "Idle · 1"),
                new Node(ClaudeOverviewNodeKind.Link, "", address + "?thread=cmsg_worker",
                    new Node(ClaudeOverviewNodeKind.WorkerTitle, "  Review the synthetic model  "),
                    new Node(ClaudeOverviewNodeKind.Other, "PRIVATE_SNIPPET") { PrivateContent = true },
                    new Node(ClaudeOverviewNodeKind.Group, "", "",
                        new Node(ClaudeOverviewNodeKind.Image, "Disconnected"),
                        new Node(ClaudeOverviewNodeKind.Edit) { Private = true },
                        new Node(ClaudeOverviewNodeKind.WorkerTitle) { Private = true }))));
        var read = new ClaudeOverviewParser("account", Start, 64 * 1024, 1).Read(new[] { document });
        var worker = Assert.Single(Assert.Single(read.Projects).Workers);
        Assert.Equal("Review the synthetic model", worker.Title);
        Assert.True(worker.Disconnected);
        Assert.DoesNotContain("PRIVATE_", System.Text.Json.JsonSerializer.Serialize(read));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("card", false)]
    [InlineData("not-df-link-card", false)]
    [InlineData("cds-text-link df-link-card text-primary", true)]
    public void OnlyPrimaryWorkerLinksExposeDirectTitleText(string classes, bool expected)
    {
        var primary = ClaudeProjectsDesktopReader.IsPrimaryWorkerLink(classes);
        Assert.Equal(expected, primary);
        Assert.Equal(expected, ClaudeProjectsDesktopReader.IsTitleText(50020, "description", primary));
        Assert.False(ClaudeProjectsDesktopReader.IsTitleText(50020, "description", false));
        Assert.False(ClaudeProjectsDesktopReader.IsTitleText(50020, "paragraph", primary));
        Assert.False(ClaudeProjectsDesktopReader.IsTitleText(50004, "description", primary));
    }

    [Theory]
    [InlineData("Working")]
    [InlineData("Waiting on you")]
    public void RenameKeepsIdentityStateTimingNumberAndNotificationQuiet(string group)
    {
        var tracker = new ClaudeProjectTracker(Source);
        var original = Project("p1", group) with { Workers = new[] { new ClaudeProjectWorker("worker", group) { Title = "First title" } } };
        var before = Assert.Single(tracker.Apply(Read(original), Start, 32).Tasks);
        var renamed = original with { ObservedAt = Start.AddSeconds(1), Workers = new[] { original.Workers[0] with { Title = "Renamed title" } } };
        var result = tracker.Apply(Read(renamed), Start.AddSeconds(1), 32);
        var after = Assert.Single(result.Tasks);
        Assert.Equal("Renamed title", after.Label);
        Assert.Equal(before with { Label = "Renamed title" }, after);
        Assert.Empty(result.Transitions);
        Assert.True(after.HasCurrentState);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nname")]
    [InlineData("spoof\u202ename")]
    public void UnusableTitlesKeepTheLastGoodLabelWithoutChangingState(string title)
    {
        var tracker = new ClaudeProjectTracker(Source);
        var project = Project("p1") with { Workers = new[] { new ClaudeProjectWorker("worker-opaque", "Working") { Title = "Good title" } } };
        tracker.Apply(Read(project), Start, 32);
        var changed = project with { ObservedAt = Start.AddSeconds(1), Workers = new[] { project.Workers[0] with { Title = title } } };
        var result = tracker.Apply(Read(changed), Start.AddSeconds(1), 32);
        Assert.Equal("Good title", Assert.Single(result.Tasks).Label);
        Assert.True(Assert.Single(result.Tasks).HasCurrentState);
        Assert.Empty(result.Transitions);
        var cold = new ClaudeProjectTracker(Source).Apply(Read(changed), Start.AddSeconds(1), 32);
        Assert.Equal("r-opaque", Assert.Single(cold.Tasks).Label);
    }

    [Fact]
    public void LongTitlesAreBoundedAndMissingViewsKeepTheLabel()
    {
        var tracker = new ClaudeProjectTracker(Source);
        var project = Project("p1") with { Workers = new[] { new ClaudeProjectWorker("worker", "Working") { Title = new string('a', 158) + "😀" + new string('b', 20) } } };
        var before = Assert.Single(tracker.Apply(Read(project), Start, 32).Tasks);
        Assert.True(before.Label.Length <= 160);
        Assert.EndsWith("…", before.Label);
        Assert.DoesNotContain('\uFFFD', System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(before.Label)));
        var missing = Assert.Single(tracker.Apply(Read(), Start.AddSeconds(1), 32).Tasks);
        Assert.Equal(before.Label, missing.Label);
        Assert.False(missing.HasCurrentState);
    }

    [Fact]
    public void TitlesNeverMergeDistinctWorkersAndMirroredRenamesDoNotConflict()
    {
        var tracker = new ClaudeProjectTracker(Source);
        var project = Project("p1") with { Workers = new[] {
            new ClaudeProjectWorker("one", "Working") { Title = "Same title" },
            new ClaudeProjectWorker("two", "Working") { Title = "Same title" } } };
        var mirror = project with { Workers = new[] { project.Workers[0] with { Title = "Renamed elsewhere" } } };
        var result = tracker.Apply(Read(project, mirror), Start, 32);
        Assert.Equal(2, result.Tasks.Count);
        Assert.All(result.Tasks, task => Assert.True(task.HasCurrentState));
        Assert.Empty(result.Transitions);
    }

    [Fact]
    public void ArtifactDuplicatesDoNotEraseThePrimaryTitleOrConsumeAnotherSession()
    {
        const string address = "https://claude.ai/epitaxy/project/chan_titles";
        var document = new Node(ClaudeOverviewNodeKind.Document, "Project", address,
            new Node(ClaudeOverviewNodeKind.Pane, "Overview", "",
                new Node(ClaudeOverviewNodeKind.Heading, "Idle · 1"),
                new Node(ClaudeOverviewNodeKind.Link, address: address + "?thread=cmsg_worker"),
                new Node(ClaudeOverviewNodeKind.Link, "", address + "?thread=cmsg_worker",
                    new Node(ClaudeOverviewNodeKind.WorkerTitle, "Primary worker title"))));
        var read = new ClaudeOverviewParser("account", Start, 64 * 1024, 1).Read(new[] { document });
        var worker = Assert.Single(Assert.Single(read.Projects).Workers);
        Assert.Equal("Primary worker title", worker.Title);
        Assert.Equal("ok", read.Reason);
        Assert.True(Assert.Single(read.Projects).MembershipComplete);
    }

    [Fact]
    public void AmbiguousTitleNodesFallBackWithoutReadingOtherNames()
    {
        const string address = "https://claude.ai/epitaxy/project/chan_titles";
        var document = new Node(ClaudeOverviewNodeKind.Document, "Project", address,
            new Node(ClaudeOverviewNodeKind.Pane, "Overview", "",
                new Node(ClaudeOverviewNodeKind.Heading, "Idle · 1"),
                new Node(ClaudeOverviewNodeKind.Link, "", address + "?thread=cmsg_worker",
                    new Node(ClaudeOverviewNodeKind.WorkerTitle, "First"), new Node(ClaudeOverviewNodeKind.WorkerTitle, "Second"))));
        var read = new ClaudeOverviewParser("account", Start, 64 * 1024, 1).Read(new[] { document });
        Assert.Empty(Assert.Single(Assert.Single(read.Projects).Workers).Title);
        Assert.True(Assert.Single(new ClaudeProjectTracker(Source).Apply(read, Start, 1).Tasks).HasCurrentState);
    }

    [Fact]
    public void TitleBytesRespectTheSharedReadBudget()
    {
        const string address = "https://claude.ai/epitaxy/project/chan_titles";
        var document = new Node(ClaudeOverviewNodeKind.Document, "Project", address,
            new Node(ClaudeOverviewNodeKind.Pane, "Overview", "",
                new Node(ClaudeOverviewNodeKind.Heading, "Idle · 1"),
                new Node(ClaudeOverviewNodeKind.Link, "", address + "?thread=cmsg_worker",
                    new Node(ClaudeOverviewNodeKind.WorkerTitle, new string('語', 900)))));
        var read = new ClaudeOverviewParser("account", Start, 2048, 1).Read(new[] { document });
        Assert.Equal("projects-read-budget", read.Reason);
        Assert.Empty(Assert.Single(Assert.Single(read.Projects).Workers).Title);
        Assert.False(Assert.Single(read.Projects).MembershipComplete);
    }

    [Fact]
    public void WorkerMetadataStopsAtTheExistingSixteenChildLimit()
    {
        const string address = "https://claude.ai/epitaxy/project/chan_titles";
        var children = Enumerable.Range(0, 16).Select(_ => new Node(ClaudeOverviewNodeKind.Other) { PrivateContent = true })
            .Append(new Node(ClaudeOverviewNodeKind.WorkerTitle) { Private = true }).ToArray();
        var document = new Node(ClaudeOverviewNodeKind.Document, "Project", address,
            new Node(ClaudeOverviewNodeKind.Pane, "Overview", "",
                new Node(ClaudeOverviewNodeKind.Heading, "Idle · 1"),
                new Node(ClaudeOverviewNodeKind.Link, "", address + "?thread=cmsg_worker", children)));
        var read = new ClaudeOverviewParser("account", Start, 64 * 1024, 1).Read(new[] { document });
        Assert.Empty(Assert.Single(Assert.Single(read.Projects).Workers).Title);
        Assert.Equal("ok", read.Reason);
    }
}
