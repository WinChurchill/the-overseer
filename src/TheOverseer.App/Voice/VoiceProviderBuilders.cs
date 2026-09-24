using System.Net.Http;
using TheOverseer.App.Voice.Speech;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice;

/// <summary>
/// Builders for the three selectable providers. Production uses
/// <see cref="Default"/>; tests inject recording fakes to prove which provider a
/// stored preference selects without touching files, the speech engine or the
/// network. A member left <see langword="null"/> falls back to the real provider.
/// </summary>
public sealed record VoiceProviderBuilders(
    Func<string, Func<VoiceSettings>, IVoiceProvider>? Pack = null,
    Func<string, Func<VoiceSettings>, IVoiceProvider>? Speech = null,
    Func<string, Func<VoiceSettings>, HttpClient, IVoiceProvider>? Tts = null)
{
    /// <summary>The local pack, the offline Windows baseline and the explicit endpoint.</summary>
    public static VoiceProviderBuilders Default { get; } = new(
        Pack: static (dataDirectory, settings) => new VoicePackProvider(dataDirectory, settings),
        Speech: static (_, settings) => new WindowsSpeechVoiceProvider(new SapiSpeechSynthesisEngine(), settings),
        Tts: static (_, settings, client) => new TtsEndpointProvider(client, settings));
}
