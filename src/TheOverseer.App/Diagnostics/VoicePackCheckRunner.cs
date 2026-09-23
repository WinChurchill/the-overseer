using System.Globalization;
using TheOverseer.App.Voice;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Finite, silent packaged-asset check: loads the configured pack through the
/// real provider and proves every known event resolves to a validated clip.
/// It plays nothing, starts no monitor, writes no settings and exits with a code.
/// </summary>
public static class VoicePackCheckRunner
{
    private static readonly VoiceEventKind[] Events =
    {
        VoiceEventKind.PermissionRequired,
        VoiceEventKind.InputRequired,
        VoiceEventKind.TaskError,
        VoiceEventKind.TaskCompleted,
        VoiceEventKind.AllTasksCompleted,
    };

    public static int Run(StartupArguments arguments)
    {
        var dataDirectory = DataDirectoryResolver.Resolve(arguments.DataDirectory);
        var configResult = MonitorConfigStore.Load(Path.Combine(dataDirectory, MonitorConfigStore.FileName));
        var settings = configResult.Config.Voice;
        Console.WriteLine("data directory: " + dataDirectory);
        Console.WriteLine(
            "config: " + configResult.ReasonCode +
            " provider: " + (settings.Provider ?? "(default)") +
            " pack: " + (string.IsNullOrWhiteSpace(settings.PackId) ? "(none)" : settings.PackId));

        if (!string.Equals(settings.Provider, "pack", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("FAIL provider must be 'pack' for a pack check");
            return 1;
        }

        using var provider = new VoicePackProvider(dataDirectory, () => settings);
        var manifest = provider.EnsureManifestAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (manifest is null)
        {
            Console.WriteLine("FAIL manifest " + provider.Status.LastCode);
            return 1;
        }
        var failures = 0;
        // Legacy five-event packs remain supported. Check any supplied extension events too.
        foreach (var kind in Events.Concat(VoiceEventCatalog.All.Where(item => item.Kind >= VoiceEventKind.MonitoringLost && manifest.Events.ContainsKey(item.Code)).Select(item => item.Kind)))
        {
            var candidate = new VoiceCandidate(
                kind,
                new TaskKey("codex", "pack-check"),
                1,
                "Task 1",
                "turn-1",
                DateTimeOffset.UtcNow,
                "pack-check",
                false,
                1,
                string.Empty);
            VoiceClip? clip = null;
            var valid = true;
            try
            {
                var code = VoiceNotificationFormatter.EventCode(kind);
                var count = manifest.Events.TryGetValue(code, out var variants) ? variants.Count : 1;
                // The production provider rotates variants. Inspect a full cycle,
                // including variants beyond the first which may be absent/corrupt.
                for (var index = 0; index < count; index++)
                {
                    clip = provider.TryCreateAsync(candidate, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    if (clip is null || !VoicePackValidator.ValidateWav(clip.Wav).IsValid)
                    {
                        valid = false;
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.WriteLine("FAIL " + VoiceNotificationFormatter.EventCode(kind) + " threw " + exception.GetType().Name);
                failures++;
                continue;
            }

            Console.WriteLine(
                (valid ? "ok   " : "FAIL ") + VoiceNotificationFormatter.EventCode(kind) +
                " clip-bytes=" + (clip?.ByteCount ?? 0).ToString(CultureInfo.InvariantCulture) +
                " code=" + provider.Status.LastCode);
            if (!valid)
            {
                failures++;
            }
        }

        Console.WriteLine(failures == 0 ? "voice pack check: passed" : "voice pack check: failed");
        return failures == 0 ? 0 : 1;
    }
}
