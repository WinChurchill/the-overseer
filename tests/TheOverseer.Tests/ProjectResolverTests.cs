using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.Tests;

public sealed class ProjectResolverTests
{
    private static ProjectResolverProbe Probe(
        IEnumerable<string>? directories = null,
        IReadOnlyDictionary<string, string>? files = null,
        Action<string>? onCheck = null,
        IEnumerable<string>? reparsePoints = null)
    {
        var directorySet = new HashSet<string>(directories ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var fileMap = files ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reparseSet = new HashSet<string>(reparsePoints ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return new ProjectResolverProbe(
            path =>
            {
                onCheck?.Invoke(path);
                return directorySet.Contains(path);
            },
            path =>
            {
                onCheck?.Invoke(path);
                return fileMap.ContainsKey(path);
            },
            (path, _) => fileMap.TryGetValue(path, out var value) ? value : null,
            path => reparseSet.Contains(path));
    }

    [Fact]
    public void StandaloneChatsShareANeutralGroupWithoutPoisoningOrdinaryWorkspaceCache()
    {
        var resolver = Resolver();
        var ordinary = resolver.Resolve(@"C:\work\first-prompt");
        var standalone = resolver.Resolve(@"C:\work\first-prompt", isStandaloneChat: true);
        var other = resolver.Resolve(@"C:\work\second-prompt", isStandaloneChat: true);
        Assert.Equal(ProjectDescriptor.IndependentChats, standalone.Project);
        Assert.Equal(standalone.Project.Id, other.Project.Id);
        Assert.Equal(ProjectResolution.IndependentChat, standalone.Resolution);
        Assert.Equal(ordinary, resolver.Resolve(@"C:\work\first-prompt"));
        Assert.NotEqual(ordinary.Project.Id, standalone.Project.Id);
    }

    [Fact]
    public void StandaloneHintNeverOverridesMappingsGitOrAnUnusableRoot()
    {
        var settings = new ProjectSettings
        {
            Mappings = new[] { new ProjectRootMapping { ProjectKey = "owner-project", Name = "Owner project", Root = @"C:\work\mapped" } },
        };
        var resolver = Resolver(settings, Probe(directories: new[] { @"c:\work\repo\.git" }));
        Assert.Equal(ProjectResolution.ConfiguredRoot, resolver.Resolve(@"C:\work\mapped", isStandaloneChat: true).Resolution);
        Assert.Equal(ProjectResolution.Explicit, resolver.Resolve(@"C:\work\elsewhere", "owner-project", true).Resolution);
        Assert.Equal(ProjectResolution.GitRoot, resolver.Resolve(@"C:\work\repo", isStandaloneChat: true).Resolution);
        Assert.True(resolver.Resolve("relative", isStandaloneChat: true).Project.IsUnassigned);
    }

    [Fact]
    public void PosixPathsPreserveCaseAndAreComparedCaseSensitively()
    {
        Assert.Equal("posix:/mnt/c/Work/App", ProjectResolver.Normalize("/mnt/c/Work/App/"));

        var upper = Resolver().Resolve("/mnt/c/Work/App");
        var lower = Resolver().Resolve("/mnt/c/work/app");

        Assert.NotEqual(upper.Project.Id, lower.Project.Id);
        Assert.Equal(ProjectResolution.WorkspaceRoot, upper.Resolution);
    }

    [Theory]
    [InlineData("C:repo")]
    [InlineData("C:")]
    [InlineData(@"\?\Volume{11111111-2222-3333-4444-555555555555}\")]
    [InlineData(@"\server")]
    [InlineData(@"\server\")]
    public void DriveRelativeDeviceAndIncompleteUncRootsAreRejected(string path)
    {
        var assignment = Resolver().Resolve(path);

        Assert.Equal(ProjectResolution.Unassigned, assignment.Resolution);
        Assert.True(assignment.Project.IsUnassigned);
    }

    [Fact]
    public void UncRootsKeepTheServerAndShareBoundary()
    {
        var settings = new ProjectSettings
        {
            Mappings = new[] { new ProjectRootMapping { ProjectKey = "share-app", Name = "share-app", Root = @"\\server\share\work\app" } },
        };
        var resolver = Resolver(settings);

        var inside = resolver.Resolve(@"\\server\share\work\app\src");
        var sibling = resolver.Resolve(@"\\server\share\work\app2\src");

        Assert.Equal(ProjectResolution.ConfiguredRoot, inside.Resolution);
        Assert.Equal("share-app", inside.Project.Id.Value);
        Assert.NotEqual(inside.Project.Id, sibling.Project.Id);
        Assert.StartsWith(@"\\server\share", ProjectResolver.Normalize(@"\\server\share\work\app"), StringComparison.Ordinal);
    }

    [Fact]
    public void DotDotNeverEscapesADriveOrUncRoot()
    {
        Assert.Equal(@"c:\app", ProjectResolver.Normalize(@"C:\work\..\..\..\app"));
        Assert.Equal(@"\\server\share\x", ProjectResolver.Normalize(@"\\server\share\..\..\x"));
        Assert.Equal(@"c:\work", ProjectResolver.Normalize(@"C:\work\..\..\..\..\work"));
    }

    [Fact]
    public void ReparsePointAncestorsStopTheGitWalkInsteadOfRedirectingIdentity()
    {
        var probe = Probe(
            directories: new[] { @"c:\real\.git" },
            reparsePoints: new[] { @"c:\link" });
        var resolver = Resolver(probe: probe);

        var throughLink = resolver.Resolve(@"C:\link\deep");
        var real = resolver.Resolve(@"C:\real\src");

        Assert.Equal(ProjectResolution.WorkspaceRoot, throughLink.Resolution);
        Assert.Equal(ProjectResolution.GitRoot, real.Resolution);
        Assert.NotEqual(real.Project.Id, throughLink.Project.Id);
    }

    [Fact]
    public void UncAndPosixRootsAreNeverProbedAsLocalPaths()
    {
        var checks = 0;
        var resolver = Resolver(probe: Probe(onCheck: _ => checks++));

        _ = resolver.Resolve(@"\\server\share\work\app");
        _ = resolver.Resolve("/mnt/c/work/app");

        Assert.Equal(0, checks);
    }

    private static ProjectResolver Resolver(
        ProjectSettings? settings = null,
        ProjectResolverProbe? probe = null) =>
        new(settings, probe ?? Probe());

    [Fact]
    public void CompleteSegmentsOnlySoSiblingNamesStayDistinct()
    {
        var settings = new ProjectSettings
        {
            Mappings = new[] { new ProjectRootMapping { ProjectKey = "app", Name = "app", Root = @"C:\work\app" } },
        };

        var inside = Resolver(settings).Resolve(@"C:\work\app\src");
        var sibling = Resolver(settings).Resolve(@"C:\work\app2\src");

        Assert.Equal(ProjectResolution.ConfiguredRoot, inside.Resolution);
        Assert.Equal("app", inside.Project.Id.Value);
        Assert.NotEqual(inside.Project.Id, sibling.Project.Id);
        Assert.Equal(ProjectResolution.WorkspaceRoot, sibling.Resolution);
    }

    [Fact]
    public void DuplicateLeafFoldersResolveToDifferentProjects()
    {
        var first = Resolver().Resolve(@"C:\one\repo");
        var second = Resolver().Resolve(@"C:\two\repo");

        Assert.NotEqual(first.Project.Id, second.Project.Id);
        Assert.Equal("repo", first.Project.DisplayName);
        Assert.Equal("repo", second.Project.DisplayName);
    }

    [Fact]
    public void CaseSeparatorsAndDotSegmentsNormalizeToTheSameProject()
    {
        var resolver = Resolver();
        var verbose = resolver.Resolve(@"C:/Work/App/./src/../");
        var compact = resolver.Resolve("c:\\work\\app");

        Assert.Equal(compact.Project.Id, verbose.Project.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..\\relative\\path")]
    [InlineData("relative")]
    [InlineData(@"\\.\pipe\x")]
    public void RelativeAndDevicePathsNeverResolveAgainstTheWorkingDirectory(string path)
    {
        var assignment = Resolver().Resolve(path);

        Assert.Equal(ProjectResolution.Unassigned, assignment.Resolution);
        Assert.True(assignment.Project.IsUnassigned);
    }

    [Fact]
    public void VerifiedGitRootWinsOverADeepWorkspaceFolder()
    {
        var probe = Probe(directories: new[] { @"c:\work\app\.git" });
        var assignment = Resolver(probe: probe).Resolve(@"C:\work\app\src\deep\folder");

        Assert.Equal(ProjectResolution.GitRoot, assignment.Resolution);
        Assert.Equal("git-root", assignment.EvidenceCode);
        Assert.Equal(@"c:\work\app", assignment.Project.Root);
        Assert.Equal("app", assignment.Project.DisplayName);
    }

    [Fact]
    public void LinkedWorktreeGroupsWithItsMainRepositoryAndKeepsACheckoutLabel()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"c:\work\feature-x\.git"] = "gitdir: c:/work/main/.git/worktrees/feature-x",
            [@"c:\work\main\.git\worktrees\feature-x\commondir"] = "../..",
        };
        var resolver = Resolver(probe: Probe(files: files));

        var worktree = resolver.Resolve(@"C:\work\feature-x");
        var main = resolver.Resolve(@"C:\work\main");

        Assert.Equal(ProjectResolution.GitRoot, worktree.Resolution);
        Assert.Equal("git-worktree", worktree.EvidenceCode);
        Assert.Equal(main.Project.Id, worktree.Project.Id);
        Assert.Equal(@"c:\work\main", worktree.Project.Root);
        Assert.Equal("feature-x", worktree.Project.CheckoutLabel);
    }

    [Fact]
    public void IndependentClonesOfTheSameRemoteStaySeparate()
    {
        var probe = Probe(directories: new[] { @"c:\clone-a\.git", @"c:\clone-b\.git" });
        var resolver = Resolver(probe: probe);

        var first = resolver.Resolve(@"C:\clone-a");
        var second = resolver.Resolve(@"C:\clone-b");

        Assert.NotEqual(first.Project.Id, second.Project.Id);
    }

    [Fact]
    public void ConfiguredMappingWinsOverGitEvidenceAndSupportsExplicitAssignment()
    {
        var settings = new ProjectSettings
        {
            Mappings = new[] { new ProjectRootMapping { ProjectKey = "proj-app", Name = "Product App", Root = @"C:\work\app" } },
        };
        var resolver = Resolver(settings, Probe(directories: new[] { @"c:\work\app\.git" }));

        var mapped = resolver.Resolve(@"C:\work\app\src");
        Assert.Equal(ProjectResolution.ConfiguredRoot, mapped.Resolution);
        Assert.Equal("proj-app", mapped.Project.Id.Value);
        Assert.Equal("Product App", mapped.Project.DisplayName);

        var explicitAssignment = resolver.Resolve(@"D:\scratch\anything", "proj-app");
        Assert.Equal(ProjectResolution.Explicit, explicitAssignment.Resolution);
        Assert.Equal("proj-app", explicitAssignment.Project.Id.Value);
    }

    [Fact]
    public void PosixRootsLiveInTheirOwnNamespaceUnlessAnAliasJoinsThem()
    {
        var settings = new ProjectSettings
        {
            Mappings = new[] { new ProjectRootMapping { ProjectKey = "app", Name = "app", Root = @"C:\work\app" } },
        };

        var separate = Resolver(settings).Resolve("/mnt/c/work/app");
        Assert.NotEqual("app", separate.Project.Id.Value);
        Assert.Equal(ProjectResolution.WorkspaceRoot, separate.Resolution);

        var joined = Resolver(settings with
        {
            Aliases = new[] { new ProjectAlias { Alias = "/mnt/c/work/app", Canonical = @"C:\work\app" } },
        }).Resolve("/mnt/c/work/app/src");

        Assert.Equal(ProjectResolution.ConfiguredRoot, joined.Resolution);
        Assert.Equal("app", joined.Project.Id.Value);
    }

    [Fact]
    public void AncestorChecksAndTheCacheAreBounded()
    {
        var checks = 0;
        var settings = new ProjectSettings { MaxAncestorChecks = 8, MaxCacheEntries = 16 };
        var resolver = Resolver(settings, Probe(onCheck: _ => checks++));

        var deep = @"C:\a\b\c\d\e\f\g\h\i\j\k\l\m\n";
        _ = resolver.Resolve(deep);
        Assert.True(checks <= 8 * 2, "probe calls were " + checks);

        for (var index = 0; index < 64; index++)
        {
            _ = resolver.Resolve(@"C:\roots\project-" + index);
        }

        Assert.True(resolver.CacheCount <= 16, "cache was " + resolver.CacheCount);
    }

    [Fact]
    public void ResolverSummaryNeverContainsAPath()
    {
        var resolver = Resolver(new ProjectSettings
        {
            Mappings = new[] { new ProjectRootMapping { ProjectKey = "app", Name = "app", Root = @"C:\secret\customer\app" } },
        });

        var assignment = resolver.Resolve(@"C:\secret\customer\app\src");
        var text = resolver.Describe() + " " + assignment.Project.Describe();

        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer", text, StringComparison.OrdinalIgnoreCase);
    }
}
