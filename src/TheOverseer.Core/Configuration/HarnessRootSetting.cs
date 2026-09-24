namespace TheOverseer.Core.Configuration;

/// <summary>
/// One explicitly configured bridge root: the harness whose spool layout it holds
/// and the directory to scan. Only directories the user names are ever read, and
/// discovery stays bounded per root.
/// </summary>
public sealed record HarnessRootSetting
{
    public string HarnessId { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public static HarnessRootSetting? Normalize(HarnessRootSetting? setting)
    {
        if (setting is null) return null;
        var harness = TheOverseer.Core.Models.HarnessIds.Normalize(setting.HarnessId);
        var path = setting.Path?.Trim();
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512) return null;
        return new HarnessRootSetting { HarnessId = harness, Path = path };
    }

    public override string ToString() => HarnessId + ":" + Path;
}
