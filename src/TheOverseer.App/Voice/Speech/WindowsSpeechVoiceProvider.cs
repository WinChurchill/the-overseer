using System.Globalization;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice.Speech;

/// <summary>
/// Offline baseline voice: the nine fixed catalog scripts are synthesized by the
/// installed Windows speech engine into validated WAV bytes and handed to the
/// existing playback path. It never speaks directly, never downloads a model and
/// never sends task text anywhere: the script text is a constant from the catalog.
/// Failed and timed-out attempts are never cached, and the bounded cache only ever
/// holds validated audio for those nine scripts.
/// </summary>
public sealed class WindowsSpeechVoiceProvider : IVoiceProvider, IDisposable
{
    private const int MaximumCacheEntries = 9;

    /// <summary>
    /// Cache identity: the explicit English voice selection, the stream format and
    /// the validation rules. Changing any of them must change the key, so stale
    /// audio can never be replayed as if it were current.
    /// </summary>
    private const string FormatVersion = "sapi-en-409-pcm16-v1";

    private readonly ISpeechSynthesisEngine _engine;
    private readonly Func<VoiceSettings> _settings;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, VoiceClip> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();
    private string _lastCode = "idle";
    private bool _disposed;

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
                if (_disposed)
                {
                    return new VoiceProviderStatus(Id, false, "speech-disposed");
                }

                // Availability comes from the engine probe alone: a cached clip is
                // never a substitute for an installed English voice, and a failed
                // attempt is reported with the engine's own code.
                var available = _engine.IsAvailable;
                return new VoiceProviderStatus(Id, available, available ? _lastCode : EngineCode());
            }
        }
    }

    /// <summary>Safe diagnostic summary: cache size and last code only.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            return "speech cache=" + _cache.Count.ToString(CultureInfo.InvariantCulture) + " code=" + _lastCode;
        }
    }

    public ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.FromResult<VoiceClip?>(null);
            }
        }

        if (!_engine.IsAvailable)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _lastCode = EngineCode();
                }
            }

            return ValueTask.FromResult<VoiceClip?>(null);
        }

        var definition = VoiceEventCatalog.For(candidate.Kind);
        var cacheKey = _engine.Id + "|" + FormatVersion + "|" + definition.Script;
        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                _lastCode = "speech-cached";
                return ValueTask.FromResult<VoiceClip?>(cached);
            }
        }

        // The user's configured voice timeout bounds the engine, but the provider
        // never waits longer than its own hard cap.
        var settings = (_settings() ?? VoiceSettings.Default).Normalized();
        var timeout = settings.Timeout < _timeout ? settings.Timeout : _timeout;
        var result = _engine.Synthesize(definition.Script, timeout, cancellationToken);
        if (!result.IsValid)
        {
            // Failures and timeouts are reported, never cached: the next request
            // retries instead of being poisoned by a transient hang.
            lock (_gate)
            {
                if (!_disposed)
                {
                    _lastCode = string.IsNullOrWhiteSpace(result.Code) ? "speech-failed" : result.Code;
                }
            }

            return ValueTask.FromResult<VoiceClip?>(null);
        }

        var validation = VoicePackValidator.ValidateWav(result.Wav!);
        if (!validation.IsValid)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _lastCode = "speech-invalid-wav:" + validation.ReasonCode;
                }
            }

            return ValueTask.FromResult<VoiceClip?>(null);
        }

        // Provenance is the catalog code only: no task labels, paths or ids.
        var clip = new VoiceClip(result.Wav!, "speech:" + definition.Code, definition.Code);
        lock (_gate)
        {
            if (!_disposed)
            {
                if (_cache.Count >= MaximumCacheEntries && _order.Count > 0)
                {
                    _cache.Remove(_order.Dequeue());
                }

                if (!_cache.ContainsKey(cacheKey))
                {
                    _order.Enqueue(cacheKey);
                }

                _cache[cacheKey] = clip;
                _lastCode = "speech-ok";
            }
        }

        return ValueTask.FromResult<VoiceClip?>(clip);
    }

    /// <summary>
    /// Safe with in-flight work: a synthesis that finishes after disposal is still
    /// returned to its caller but never populates the cache, and no later request
    /// reaches the engine.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _cache.Clear();
            _order.Clear();
            _lastCode = "speech-disposed";
        }
    }

    private string EngineCode()
    {
        var code = _engine.UnavailableCode;
        return string.IsNullOrWhiteSpace(code) ? "speech-unavailable" : code;
    }
}
