using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.Tests;

public sealed class ProjectResolverTests
{
    private static ProjectResolverProbe Probe(
        IEnumerable<string>? directories = null,
        IReadOnlyDictionary<string, string>? files = null,
        Action<string>? onCheck = null)
    {
        var directorySet = new HashSet<string>(directories ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var fileMap = files ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            (path, _) => fileMap.TryGetValue(path, out var value) ? value : null);
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
