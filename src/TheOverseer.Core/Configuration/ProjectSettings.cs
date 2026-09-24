using System.Text.Json.Serialization;

namespace TheOverseer.Core.Configuration;

/// <summary>Explicit root-to-project mapping. Roots are local metadata only.</summary>
public sealed record ProjectRootMapping
{
    [JsonPropertyName("projectKey")]
    public string ProjectKey { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("root")]
    public string Root { get; init; } = string.Empty;
}

/// <summary>
/// Path alias. The alias path is rewritten to the canonical root before project
/// matching, which is how a WSL/POSIX checkout is joined to a Windows project.
/// </summary>
public sealed record ProjectAlias
{
    [JsonPropertyName("alias")]
    public string Alias { get; init; } = string.Empty;

    [JsonPropertyName("canonical")]
    public string Canonical { get; init; } = string.Empty;
}

/// <summary>
/// Additive project configuration. Absent settings keep every task resolvable by
/// repository/workspace evidence alone.
/// </summary>
public sealed record ProjectSettings
{
    [JsonPropertyName("mappings")]
    public IReadOnlyList<ProjectRootMapping> Mappings { get; init; } = Array.Empty<ProjectRootMapping>();

    [JsonPropertyName("aliases")]
    public IReadOnlyList<ProjectAlias> Aliases { get; init; } = Array.Empty<ProjectAlias>();

    /// <summary>Longest-ancestor checks per resolution. Bounded, never recursive.</summary>
    [JsonPropertyName("maxAncestorChecks")]
    public int MaxAncestorChecks { get; init; } = 32;

    [JsonPropertyName("maxCacheEntries")]
    public int MaxCacheEntries { get; init; } = 512;
}
