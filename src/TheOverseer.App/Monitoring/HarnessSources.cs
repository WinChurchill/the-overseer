using System.IO;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;

namespace TheOverseer.App.Monitoring;

/// <summary>
/// Bounds for source discovery. Discovery reads directories, so it is capped both
/// per root and in total: an installed harness with thousands of spool directories
/// must not turn a startup pass into an unbounded scan.
/// </summary>
public sealed record HarnessDiscoveryOptions(
    int MaximumSources = SourceBudget.MaximumSourceInstances,
    int MaximumEntriesPerRoot = 512,
    IReadOnlyList<HarnessRootSetting>? ConfiguredRoots = null);

/// <summary>Discovered sources plus whether the bounded scan hit its cap.</summary>
public sealed record HarnessDiscoveryResult(IReadOnlyList<SourceInstance> Sources, bool Capped, int RootsScanned);

/// <summary>
/// Discovers installed bridge sources. A source exists only when its spool
/// directory exists, so an integration that was never installed is absent rather
/// than a disabled phantom; configuration can still disable a discovered source or
/// add an explicit root.
/// </summary>
public static class HarnessSources
{
    // Match each shipped producer's default; they currently use different roots.
    public static string SpoolDirectory(string dataDirectory, SourceInstance source) =>
        Path.Combine(dataDirectory, source.HarnessId == HarnessIds.Claude ? "spool" : "bridges",
            source.HarnessId, source.SourceInstanceId);

    /// <summary>Default root for one harness under the data directory.</summary>
    public static string DefaultRoot(string dataDirectory, string harnessId) =>
        Path.Combine(dataDirectory, harnessId == HarnessIds.Claude ? "spool" : "bridges", harnessId);

    public static IReadOnlyList<SourceInstance> Discover(
        string dataDirectory,
        IReadOnlyList<string>? disabled = null,
        HarnessDiscoveryOptions? options = null) =>
        DiscoverDetailed(dataDirectory, disabled, options).Sources;

    public static HarnessDiscoveryResult DiscoverDetailed(
        string dataDirectory,
        IReadOnlyList<string>? disabled = null,
        HarnessDiscoveryOptions? options = null)
    {
        var settings = options ?? new HarnessDiscoveryOptions();
        var maximum = Math.Clamp(settings.MaximumSources, 1, SourceBudget.MaximumSourceInstances);
        var perRoot = Math.Clamp(settings.MaximumEntriesPerRoot, 1, 8192);
        var roots = new List<(string HarnessId, string Root)>
        {
            (HarnessIds.Dsh, DefaultRoot(dataDirectory, HarnessIds.Dsh)),
            (HarnessIds.Claude, DefaultRoot(dataDirectory, HarnessIds.Claude)),
        };
        foreach (var configured in settings.ConfiguredRoots ?? Array.Empty<HarnessRootSetting>())
        {
            if (string.IsNullOrWhiteSpace(configured?.Path)) continue;
            roots.Add((HarnessIds.Normalize(configured.HarnessId), configured.Path));
        }

        var sources = new List<SourceInstance>();
        var capped = false;
        var scanned = 0;
        foreach (var (harnessId, root) in roots)
        {
            if (sources.Count >= maximum)
            {
                capped = true;
                break;
            }

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            scanned++;
            List<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(root)
                    .Take(perRoot + 1)
                    .ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (directories.Count > perRoot)
            {
                // More entries than the scan allows: keep the deterministic prefix
                // and report that discovery was bounded rather than reading forever.
                directories = directories.Take(perRoot).ToList();
                capped = true;
            }

            foreach (var directory in directories.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                var instanceId = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 64)
                {
                    continue;
                }

                var key = harnessId + "/" + instanceId;
                var isDisabled = disabled?.Any(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase)) == true;
                sources.Add(new SourceInstance(harnessId, instanceId, HarnessIds.DisplayName(harnessId) + " (" + instanceId + ")", !isDisabled));
                if (sources.Count >= maximum)
                {
                    capped = true;
                    break;
                }
            }
        }

        return new HarnessDiscoveryResult(sources, capped, scanned);
    }
}
