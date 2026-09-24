using System.Globalization;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice.Speech;

/// <summary>
/// Offline baseline voice: the nine fixed catalog scripts are synthesized by the
/// installed Windows speech engine into validated WAV bytes and handed to the
/// existing playback path. It never speaks directly, never downloads a model and
/// never sends task text anywhere: the script text is a constant from the catalog.
/// </summary>
public sealed class WindowsSpeechVoiceProvider : IVoiceProvider, IDisposable
{
    private const int MaximumCacheEntries = 9;
    private readonly ISpeechSynthesisEngine _engine;
    private readonly Func<VoiceSettings> _settings;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, VoiceClip> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private string _lastCode = "idle";

    public WindowsSpeechVoiceProvider(ISpeechSynthesisEngine engine, Func<VoiceSettings> settings, TimeSpan? timeout = null)
    {
        _engine = engine;
        _settings = settings;
        _timeout = timeout ?? TimeSpan.FromSeconds(6);
    }

    public string Id => "speech";

    public VoiceProviderStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new VoiceProviderStatus(Id, _engine.IsAvailable && _cache.Count > 0 || _engine.IsAvailable, _engine.IsAvailable ? _lastCode : _engine.UnavailableCode);
            }
        }
    }

    public ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken)
    {
        if (!_engine.IsAvailable)
        {
            lock (_gate)
            {
                _lastCode = _engine.UnavailableCode;
            }

            return ValueTask.FromResult<VoiceClip?>(null);
        }

        var definition = VoiceEventCatalog.For(candidate.Kind);
        var settings = (_settings() ?? VoiceSettings.Default).Normalized();
        var cacheKey = definition.Code + ":" + settings.ProviderName;

        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                _lastCode = "speech-cached";
                return ValueTask.FromResult<VoiceClip?>(cached);
            }
        }

        var result = _engine.Synthesize(definition.Script, _timeout, cancellationToken);
        if (!result.IsValid)
        {
            lock (_gate)
            {
                _lastCode = result.Code;
            }

            return ValueTask.FromResult<VoiceClip?>(null);
        }

        var validation = VoicePackValidator.ValidateWav(result.Wav!);
        if (!validation.IsValid)
        {
            lock (_gate)
            {
                _lastCode = "speech-invalid-wav:" + validation.ReasonCode;
            }

            return ValueTask.FromResult<VoiceClip?>(null);
        }

        // Provenance is the catalog code only: no task labels, paths or ids.
        var clip = new VoiceClip(result.Wav!, "speech:" + definition.Code, definition.Code);
        lock (_gate)
        {
            if (_cache.Count >= MaximumCacheEntries)
            {
                _cache.Clear();
            }

            _cache[cacheKey] = clip;
            _lastCode = "speech-ok";
        }

        return ValueTask.FromResult<VoiceClip?>(clip);
    }

    /// <summary>Safe diagnostic summary: cache size and last code only.</summary>
    public string Describe() => "speech cache=" + _cache.Count.ToString(CultureInfo.InvariantCulture) + " code=" + _lastCode;

    public void Dispose()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }
}
