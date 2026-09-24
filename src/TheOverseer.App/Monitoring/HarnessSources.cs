using System.IO;
using TheOverseer.Core.Models;

namespace TheOverseer.App.Monitoring;

/// <summary>
/// Discovers installed bridge sources. A source exists only when its spool
/// directory exists, so an integration that was never installed is absent rather
/// than a disabled phantom; configuration can still disable a discovered source.
/// </summary>
public static class HarnessSources
{
    public static IReadOnlyList<SourceInstance> Discover(string dataDirectory, IReadOnlyList<string>? disabled = null)
    {
        var sources = new List<SourceInstance>();
        foreach (var harnessId in new[] { HarnessIds.Dsh, HarnessIds.Claude })
        {
            var harnessRoot = Path.Combine(dataDirectory, "bridges", harnessId);
            if (!Directory.Exists(harnessRoot))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(harnessRoot))
            {
                var instanceId = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 64)
                {
                    continue;
                }

                var key = harnessId + "/" + instanceId;
                var isDisabled = disabled?.Any(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase)) == true;
                sources.Add(new SourceInstance(harnessId, instanceId, HarnessIds.DisplayName(harnessId) + " (" + instanceId + ")", !isDisabled));
            }
        }

        return sources;
    }
}
