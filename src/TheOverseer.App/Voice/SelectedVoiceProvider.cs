using System.Net.Http;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice;

/// <summary>
/// Dispatches every request to the provider the current settings select. The kind
/// is decided per request, so changing the preference in settings takes effect
/// immediately, and it is decided only once per request: a failing selected
/// provider reports unavailable instead of quietly speaking with another provider.
/// At most three backing providers exist and this instance disposes all of them.
/// </summary>
public sealed class SelectedVoiceProvider : IVoiceProvider, IDisposable
{
    private readonly string _dataDirectory;
    private readonly Func<VoiceSettings> _settings;
    private readonly VoiceProviderBuilders _builders;
    private readonly Dictionary<VoiceProviderKind, IVoiceProvider> _providers = new();
    private readonly object _gate = new();
    private bool _disposed;

    public SelectedVoiceProvider(string dataDirectory, Func<VoiceSettings> settings, VoiceProviderBuilders? builders = null)
    {
        _dataDirectory = dataDirectory;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _builders = builders ?? VoiceProviderBuilders.Default;
    }

    public string Id => Current().ProviderName;

    public VoiceProviderStatus Status
    {
        get
        {
            var settings = Current();
            lock (_gate)
            {
                if (_disposed)
                {
                    return new VoiceProviderStatus(settings.ProviderName, false, "provider-disposed");
                }

                return ProviderFor(settings.ResolvedProvider).Status;
            }
        }
    }

    public ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken)
    {
        var settings = Current();
        IVoiceProvider provider;
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.FromResult<VoiceClip?>(null);
            }

            provider = ProviderFor(settings.ResolvedProvider);
        }

        return provider.TryCreateAsync(candidate, cancellationToken);
    }

    public void Dispose()
    {
        IVoiceProvider[] providers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            providers = _providers.Values.ToArray();
            _providers.Clear();
        }

        foreach (var provider in providers)
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    private VoiceSettings Current() => (_settings() ?? VoiceSettings.Default).Normalized();

    private IVoiceProvider ProviderFor(VoiceProviderKind kind)
    {
        if (_providers.TryGetValue(kind, out var existing))
        {
            return existing;
        }

        var defaults = VoiceProviderBuilders.Default;
        var created = kind switch
        {
            VoiceProviderKind.Tts => (_builders.Tts ?? defaults.Tts!)(_dataDirectory, _settings, VoiceProviderFactory.CreateTtsClient()),
            VoiceProviderKind.Speech => (_builders.Speech ?? defaults.Speech!)(_dataDirectory, _settings),
            _ => (_builders.Pack ?? defaults.Pack!)(_dataDirectory, _settings),
        };

        _providers[kind] = created;
        return created;
    }
}
