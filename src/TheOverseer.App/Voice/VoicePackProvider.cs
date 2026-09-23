using System.IO;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice;

/// <summary>
/// Reads one validated voice pack from &lt;data-directory&gt;/voicepacks/&lt;packId&gt;.
/// Called by the background voice consumer; nothing is enumerated recursively
/// and reparse points are rejected along the complete file path.
/// </summary>
public sealed class VoicePackProvider : IVoiceProvider, IDisposable
{
    public const string PackFolderName = "voicepacks";
    public const string ManifestFileName = "manifest.json";

    private readonly string _packsRoot;
    private readonly Func<VoiceSettings> _settings;
    private readonly Dictionary<string, int> _roundRobin = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private VoicePackManifest? _manifest;
    private string _lastCode = "idle";

    public VoicePackProvider(string dataDirectory, Func<VoiceSettings> settings)
    {
        _packsRoot = Path.Combine(dataDirectory, PackFolderName);
        _settings = settings;
    }

    public string Id => "pack";

    public VoiceProviderStatus Status => new("pack", _manifest is not null, _lastCode);

    public string PacksRoot => _packsRoot;

    /// <summary>Loads and validates the manifest once; safe to call repeatedly.</summary>
    public async ValueTask<VoicePackManifest?> EnsureManifestAsync(CancellationToken cancellationToken)
    {
        if (_manifest is not null)
        {
            return _manifest;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_manifest is not null)
            {
                return _manifest;
            }

            var settings = _settings();
            var packId = VoicePackValidator.SafePackId(settings.PackId);
            if (packId is null)
            {
                _lastCode = "pack-id";
                return null;
            }

            var packRoot = Path.Combine(_packsRoot, packId);
            if (!VoicePackValidator.IsContained(_packsRoot, packRoot))
            {
                _lastCode = "pack-escape";
                return null;
            }

            var manifestPath = Path.Combine(packRoot, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                _lastCode = "pack-missing";
                return null;
            }

            if (HasReparsePoint(manifestPath))
            {
                _lastCode = "pack-reparse";
                return null;
            }

            var json = await Task.Run(() => ReadBoundedText(manifestPath, VoicePackValidator.MaximumManifestBytes), cancellationToken)
                .ConfigureAwait(false);
            if (json is null)
            {
                _lastCode = "pack-unreadable";
                return null;
            }

            var validation = VoicePackValidator.ParseManifest(json);
            if (!validation.IsValid || validation.Manifest is null)
            {
                _lastCode = validation.ReasonCode;
                return null;
            }

            if (!string.Equals(validation.Manifest.Id, packId, StringComparison.Ordinal))
            {
                _lastCode = "pack-id-mismatch";
                return null;
            }

            _manifest = validation.Manifest;
            _lastCode = "ok";
            return _manifest;
        }
        catch (IOException)
        {
            _lastCode = "pack-io";
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            _lastCode = "pack-denied";
            return null;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            return await CreateClipAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _lastCode = "clip-unreadable";
            return null;
        }
    }

    private async ValueTask<VoiceClip?> CreateClipAsync(VoiceCandidate candidate, CancellationToken cancellationToken)
    {
        var manifest = await EnsureManifestAsync(cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return null;
        }

        var eventKey = VoiceNotificationFormatter.EventCode(candidate.Kind);
        if (!manifest.Events.TryGetValue(eventKey, out var clips) || clips.Count == 0)
        {
            _lastCode = "clip-missing";
            return null;
        }

        var index = _roundRobin.TryGetValue(eventKey, out var next) ? next % clips.Count : 0;
        _roundRobin[eventKey] = index + 1;

        var packRoot = Path.Combine(_packsRoot, manifest.Id);
        var relative = clips[index];
        var full = Path.GetFullPath(Path.Combine(packRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!VoicePackValidator.IsContained(packRoot, full))
        {
            _lastCode = "clip-escape";
            return null;
        }

        // Existence is checked before the reparse test so a clip that is simply
        // gone reports "missing" instead of the conservative "reparse" verdict
        // that an uninspectable path would otherwise produce.
        if (!File.Exists(full))
        {
            _lastCode = "clip-missing";
            return null;
        }

        if (HasReparsePoint(full))
        {
            _lastCode = "clip-reparse";
            return null;
        }

        var bytes = await Task.Run(() => ReadBoundedBytes(full, VoicePackValidator.MaximumClipBytes), cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            _lastCode = "clip-unreadable";
            return null;
        }

        var wav = VoicePackValidator.ValidateWav(bytes);
        if (!wav.IsValid)
        {
            _lastCode = wav.ReasonCode;
            return null;
        }

        _lastCode = "ok";
        return new VoiceClip(bytes, "pack:" + manifest.Id, "pack");
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string? ReadBoundedText(string path, int maximumBytes)
    {
        if (HasReparsePoint(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[]? ReadBoundedBytes(string path, int maximumBytes)
    {
        if (HasReparsePoint(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes)
        {
            return null;
        }

        var buffer = new byte[stream.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var next = stream.Read(buffer, read, buffer.Length - read);
            if (next <= 0)
            {
                break;
            }

            read += next;
        }

        return read == buffer.Length ? buffer : null;
    }

    public void Dispose() => _loadGate.Dispose();
}
