using System.Text.Json;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.Core.Configuration;

/// <summary>
/// System.Text.Json configuration with atomic writes and corrupt-file
/// preservation. Nothing here reads Codex configuration or credentials.
/// </summary>
public static class MonitorConfigStore
{
    public const string FileName = "config.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static ConfigLoadResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new ConfigLoadResult(MonitorConfig.Default, ConfigLoadStatus.Missing, "config-missing");
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64 * 1024)
                return new ConfigLoadResult(MonitorConfig.Default, ConfigLoadStatus.Failed, "config-too-large");
            using var reader = new StreamReader(stream);
            var buffer = new char[64 * 1024 + 1];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count == buffer.Length)
                return new ConfigLoadResult(MonitorConfig.Default, ConfigLoadStatus.Failed, "config-too-large");
            var text = new string(buffer, 0, count);
            reader.Dispose();
            if (string.IsNullOrWhiteSpace(text))
            {
                return new ConfigLoadResult(MonitorConfig.Default, ConfigLoadStatus.Missing, "config-empty");
            }

            var config = JsonSerializer.Deserialize<MonitorConfig>(text, SerializerOptions);
            if (config is null)
            {
                return new ConfigLoadResult(Preserve(path), ConfigLoadStatus.Recovered, "config-null");
            }

            var normalized = Normalize(config);
            return new ConfigLoadResult(normalized, ConfigLoadStatus.Loaded, "config-loaded");
        }
        catch (JsonException)
        {
            return new ConfigLoadResult(Preserve(path), ConfigLoadStatus.Recovered, "config-malformed");
        }
        catch (IOException)
        {
            return new ConfigLoadResult(MonitorConfig.Default, ConfigLoadStatus.Failed, "config-io-error");
        }
        catch (UnauthorizedAccessException)
        {
            return new ConfigLoadResult(MonitorConfig.Default, ConfigLoadStatus.Failed, "config-access-denied");
        }
    }

    public static void Save(string path, MonitorConfig config)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(config), SerializerOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public static MonitorConfig Normalize(MonitorConfig config) => config with
    {
        CodexHome = string.IsNullOrWhiteSpace(config.CodexHome) ? null : config.CodexHome.Trim(),
        AdditionalRoots = (config.AdditionalRoots ?? Array.Empty<string>())
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(static root => root.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray(),
        Limits = config.Limits ?? new MonitorLimits(),
        Logging = config.Logging ?? new LoggingSettings(),
        Hud = (config.Hud ?? new HudSettings()).Normalized(),
        Voice = (config.Voice ?? new VoiceSettings()).Normalized(),
        MaximumHarnessSources = Math.Clamp(config.MaximumHarnessSources, 1, TheOverseer.Core.Models.SourceBudget.MaximumSourceInstances),
        HarnessRoots = (config.HarnessRoots ?? Array.Empty<HarnessRootSetting>())
            .Select(HarnessRootSetting.Normalize)
            .Where(static setting => setting is not null)
            .Select(static setting => setting!)
            .Distinct()
            .Take(8)
            .ToArray(),
    };

    /// <summary>Applies configuration limits onto monitor options; bounds always win.</summary>
    public static MonitorOptions ToOptions(MonitorConfig config)
    {
        var normalized = Normalize(config);
        var options = MonitorOptions.Default with
        {
            MaxTrackedFiles = normalized.Limits.MaxTrackedFiles,
            PerFileBytesPerPass = normalized.Limits.PerFileBytesPerPass,
            GlobalBytesPerPass = normalized.Limits.GlobalBytesPerPass,
            StaleLease = TimeSpan.FromMinutes(normalized.Limits.StaleLeaseMinutes),
            DiscoveryEntriesPerSlice = normalized.Limits.DiscoveryEntriesPerSlice,
            MaxTitleCacheEntries = normalized.Limits.MaxTitleCacheEntries,
        };
        return options.Clamped();
    }

    private static MonitorConfig Preserve(string path)
    {
        try
        {
            var preserved = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(path, preserved, overwrite: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return MonitorConfig.Default;
    }
}
