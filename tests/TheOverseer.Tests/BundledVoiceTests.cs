using System.Security.Cryptography;
using System.Text.Json;
using TheOverseer.App.Voice;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class BundledVoiceTests
{
    private static VoiceCandidate Candidate(VoiceEventKind kind) => new(kind, TaskKey.For("claude", "test", "task"),
        1, "", "turn", VoiceTestData.Start, "test", false, 1, "");

    [Fact]
    public async Task ShippedKokoroPackCoversTheExactCatalogWithValidRecordedAudio()
    {
        using var home = new SyntheticCodexHome();
        var settings = VoiceSettings.Default with { Provider = "pack", PackId = "kokoro-en" };
        using var provider = new VoicePackProvider(home.Root, () => settings);
        var manifest = await provider.EnsureManifestAsync(CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(VoiceEventCatalog.All.Count, manifest!.Events.Count);
        var source = Path.Combine(AppContext.BaseDirectory, "voicepacks", "kokoro-en", "source.json");
        using var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(source));
        foreach (var definition in VoiceEventCatalog.All)
        {
            var clip = await provider.TryCreateAsync(Candidate(definition.Kind), CancellationToken.None);
            Assert.NotNull(clip);
            Assert.Equal("pack:kokoro-en", clip!.SourceCode);
            Assert.True(VoicePackValidator.ValidateWav(clip.Wav).IsValid);
            var recorded = provenance.RootElement.GetProperty("clips").GetProperty(definition.Code);
            Assert.Equal(definition.Script, recorded.GetProperty("script").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(clip.Wav)).ToLowerInvariant(), recorded.GetProperty("sha256").GetString());
        }
        Assert.False(Directory.Exists(Path.Combine(home.Root, "voicepacks")));
    }

    [Fact]
    public async Task ChangingPackDoesNotReuseThePreviousManifestOrClipRoot()
    {
        using var home = new SyntheticCodexHome();
        void WritePack(string id, int frequency)
        {
            var directory = Path.Combine(home.Root, "voicepacks", id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
            {
                id, version = 1, events = new Dictionary<string, string[]> { ["input_required"] = new[] { id + ".wav" } },
            }));
            File.WriteAllBytes(Path.Combine(directory, id + ".wav"), VoicePackValidator.CreateTone(frequency, 0.05));
        }
        WritePack("first", 220); WritePack("second", 440);
        var settings = VoiceSettings.Default with { PackId = "first" };
        using var provider = new VoicePackProvider(home.Root, () => settings);
        Assert.Equal("pack:first", (await provider.TryCreateAsync(Candidate(VoiceEventKind.InputRequired), CancellationToken.None))!.SourceCode);
        settings = settings with { PackId = "second" };
        Assert.Equal("pack:second", (await provider.TryCreateAsync(Candidate(VoiceEventKind.InputRequired), CancellationToken.None))!.SourceCode);
        settings = settings with { PackId = "missing" };
        Assert.Null(await provider.TryCreateAsync(Candidate(VoiceEventKind.InputRequired), CancellationToken.None));
        Assert.False(provider.Status.IsAvailable);
    }

    [Fact]
    public async Task InvalidUserOverrideDoesNotSilentlyFallBackToBundledAudio()
    {
        using var home = new SyntheticCodexHome();
        var directory = Path.Combine(home.Root, "voicepacks", "kokoro-en");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), "invalid");
        using var provider = new VoicePackProvider(home.Root, () => VoiceSettings.Default with { PackId = "kokoro-en" });
        Assert.Null(await provider.TryCreateAsync(Candidate(VoiceEventKind.InputRequired), CancellationToken.None));
        Assert.Equal("manifest-json", provider.Status.LastCode);
    }
}
