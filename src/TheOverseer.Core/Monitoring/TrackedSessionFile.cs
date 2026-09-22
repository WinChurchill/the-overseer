using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.Sessions;

namespace TheOverseer.Core.Monitoring;

/// <summary>
/// Per-rollout-file tracking state. One file contributes evidence to exactly one
/// conversation, and several files may contribute to the same conversation
/// (rollover, continuation or client surface duplication).
/// </summary>
internal sealed class TrackedSessionFile
{
    public int FileId { get; init; }

    public required string ProfileId { get; init; }

    public required string Path { get; init; }

    public required string ConversationId { get; set; }

    public ClientSurface Surface { get; set; } = ClientSurface.Unknown;

    public string WorkspaceLeaf { get; set; } = string.Empty;

    public bool IdentityResolved { get; set; }

    public bool IdentityProvisional { get; set; }

    public IncrementalJsonlReader Reader { get; } = new();

    public bool Hydrated { get; set; }

    public bool HydrationLimited { get; set; }

    public long Length { get; set; }

    public DateTime LastWriteUtc { get; set; }

    public ulong Signature { get; set; }

    public DateTimeOffset LastSignatureCheckAt { get; set; }

    public int SourceOrder { get; set; }

    public int ConsecutiveFailures { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? FailureCode { get; set; }

    public bool ReadBlocked { get; set; }

    public DateTimeOffset LastProcessedAt { get; set; }

    public DateTimeOffset AdmittedAt { get; set; }

    public long BytesRead { get; set; }

    public TaskKey Key => new(ProfileId, ConversationId);
}
