using System.Globalization;

namespace TheOverseer.Core.Models;

/// <summary>Evidence used to attach a task to its project or independent-chat group.</summary>
public enum ProjectResolution
{
    Unassigned = 0,
    WorkspaceRoot = 1,
    GitRoot = 2,
    ConfiguredRoot = 3,
    Explicit = 4,
    IndependentChat = 5,
}

/// <summary>
/// Opaque, stable project identifier. The value is a hash of the normalized
/// root, so it is stable across runs and safe to log; it is an identifier, not a
/// claim of anonymization.
/// </summary>
public readonly record struct ProjectId(string Value)
{
    public static ProjectId Unassigned { get; } = new("project:unassigned");

    public bool IsUnassigned => string.IsNullOrEmpty(Value) || string.Equals(Value, Unassigned.Value, StringComparison.Ordinal);

    public static ProjectId FromRoot(string normalizedRoot) => new("project:" + StableHash(normalizedRoot));

    public static ProjectId FromKey(string key)
    {
        var trimmed = (key ?? string.Empty).Trim();
        return trimmed.Length == 0 ? Unassigned : new ProjectId(trimmed.Length > 96 ? trimmed[..96] : trimmed);
    }

    /// <summary>FNV-1a over the normalized root; deterministic and dependency free.</summary>
    public static string StableHash(string value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var character in value ?? string.Empty)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    public override string ToString() => IsUnassigned ? "unassigned" : Value;
}

/// <summary>
/// One project row: a display name plus the local root that produced it. The
/// root is local metadata and must never reach logs, voice or exports.
/// </summary>
public sealed record ProjectDescriptor(
    ProjectId Id,
    string DisplayName,
    string Root,
    ProjectResolution Resolution,
    string? CheckoutLabel = null)
{
    public static ProjectDescriptor UnassignedProject { get; } = new(
        ProjectId.Unassigned,
        "Unassigned",
        string.Empty,
        ProjectResolution.Unassigned);

    public static ProjectDescriptor IndependentChats { get; } = new(
        ProjectId.FromKey("project:independent-chats"),
        "Independent chats",
        string.Empty,
        ProjectResolution.IndependentChat);

    public bool IsIndependentChats => Resolution == ProjectResolution.IndependentChat;

    public bool IsUnassigned => Resolution == ProjectResolution.Unassigned || Id.IsUnassigned;

    /// <summary>Safe project label for diagnostics: name and hash, never the root path.</summary>
    public string Describe() => DisplayName + " [" + Id + "]";
}

/// <summary>One task-to-project decision with the evidence that produced it.</summary>
public sealed record ProjectAssignment(ProjectDescriptor Project, ProjectResolution Resolution, string EvidenceCode)
{
    public static ProjectAssignment None { get; } = new(ProjectDescriptor.UnassignedProject, ProjectResolution.Unassigned, "unassigned");
}
