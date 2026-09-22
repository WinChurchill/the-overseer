using TheOverseer.Core.Models;

namespace TheOverseer.Core.Voice;

public enum VoiceEventKind
{
    PermissionRequired,
    InputRequired,
    TaskError,
    TaskCompleted,
    AllTasksCompleted,
}

/// <summary>
/// One immutable notification candidate. Identity is semantic: kind, task key,
/// turn, observation time and code (or an aggregate episode id).
/// </summary>
public sealed record VoiceCandidate(
    VoiceEventKind Kind,
    TaskKey Key,
    int Number,
    string Alias,
    string TurnId,
    DateTimeOffset ObservedAt,
    string Code,
    bool IsAggregate,
    int TaskCount,
    string EpisodeId)
{
    public IReadOnlyList<VoiceCandidate> Members { get; init; } = Array.Empty<VoiceCandidate>();

    public string Identity => IsAggregate
        ? "agg:" + Kind + ":" + EpisodeId
        : Kind + ":" + Key + ":" + TurnId + ":" + ObservedAt.ToUnixTimeMilliseconds() + ":" + Code;
}

/// <summary>Validated WAV bytes plus a safe provenance code (never a path or URL).</summary>
public sealed record VoiceClip(byte[] Wav, string SourceCode, string DetailCode)
{
    public int ByteCount => Wav.Length;
}

/// <summary>Provider/protocol outcome codes surfaced to the HUD without private data.</summary>
public sealed record VoiceProviderStatus(string ProviderId, bool IsAvailable, string LastCode);

public interface IVoiceProvider
{
    string Id { get; }

    VoiceProviderStatus Status { get; }

    /// <summary>Creates a validated clip, or null when unavailable/failed.</summary>
    ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken);
}

public interface IVoicePlayer
{
    /// <summary>Plays one clip to completion (or cancellation). Serial by contract.</summary>
    ValueTask<bool> PlayAsync(VoiceClip clip, CancellationToken cancellationToken);

    /// <summary>Stops any current output immediately; safe to call from any thread.</summary>
    void Stop();
}

public sealed record VoiceManagerOptions(
    int QueueCapacity = 32,
    TimeSpan MinimumStartSpacing = default,
    TimeSpan CandidateTtl = default,
    int MaximumRecentOutcomes = 32)
{
    public static VoiceManagerOptions Default { get; } = new(
        QueueCapacity: 32,
        MinimumStartSpacing: TimeSpan.FromSeconds(1),
        CandidateTtl: TimeSpan.FromSeconds(30));
}

public sealed record VoiceStatus(
    bool IsMuted,
    string ProviderId,
    string LastOutcomeCode,
    int QueuedCount,
    int SpokenCount,
    int DroppedCount,
    int FailedCount,
    int FallbackCount,
    bool IsSpeaking,
    string Detail)
{
    public static VoiceStatus Initial { get; } = new(
        true, "pack", "idle", 0, 0, 0, 0, 0, false, "Voice is muted.");

    public string Summary => IsMuted ? "Muted" : ProviderId;
}

public sealed class NullVoicePlayer : IVoicePlayer
{
    public static NullVoicePlayer Instance { get; } = new();

    private NullVoicePlayer()
    {
    }

    public ValueTask<bool> PlayAsync(VoiceClip clip, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public void Stop()
    {
    }
}

/// <summary>Always-silent provider used when no pack or endpoint is available.</summary>
public sealed class SilentVoiceProvider : IVoiceProvider
{
    public static SilentVoiceProvider Instance { get; } = new();

    private SilentVoiceProvider()
    {
    }

    public string Id => "silent";

    public VoiceProviderStatus Status => new("silent", false, "unavailable");

    public ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken) =>
        ValueTask.FromResult<VoiceClip?>(null);
}

/// <summary>
/// Tries the primary provider once and, only when allowed, one local fallback.
/// No retries and no implicit remote fallback.
/// </summary>
public sealed class FallbackVoiceProvider : IVoiceProvider, IDisposable
{
    private readonly IVoiceProvider _primary;
    private readonly IVoiceProvider? _fallback;
    private readonly bool _allowFallback;
    private string _lastCode = "idle";
    private readonly Func<VoiceCandidate, bool>? _revalidate;

    public FallbackVoiceProvider(IVoiceProvider primary, IVoiceProvider? fallback, bool allowFallback, Func<VoiceCandidate, bool>? revalidate = null)
    {
        _revalidate = revalidate;
        _primary = primary;
        _fallback = fallback;
        _allowFallback = allowFallback;
    }

    public void Dispose()
    {
        (_primary as IDisposable)?.Dispose();
        (_fallback as IDisposable)?.Dispose();
    }

    public string Id => _primary.Id;

    public int FallbackCount { get; private set; }

    public VoiceProviderStatus Status => new(_primary.Id, _primary.Status.IsAvailable, _lastCode);

    public async ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken)
    {
        var clip = await _primary.TryCreateAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (clip is not null)
        {
            _lastCode = "primary";
            return clip;
        }

        _lastCode = _primary.Status.LastCode;
        if (!_allowFallback || _fallback is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_revalidate is not null && !_revalidate(candidate)) return null;
        var fallbackClip = await _fallback.TryCreateAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (fallbackClip is null)
        {
            _lastCode = "fallback-" + _fallback.Status.LastCode;
            return null;
        }

        FallbackCount++;
        _lastCode = "fallback";
        return fallbackClip with { DetailCode = "fallback" };
    }
}
