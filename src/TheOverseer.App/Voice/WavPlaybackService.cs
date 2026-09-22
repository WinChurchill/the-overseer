using System.IO;
using System.Media;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice;

/// <summary>
/// Serial, cancellable WAV playback through the framework sound player. Playback
/// runs off the UI thread, Stop() takes effect immediately from any thread, and
/// no shell command or system beep is ever used as a fallback.
/// </summary>
public sealed class WavPlaybackService : IVoicePlayer
{
    private readonly object _gate = new();
    private SoundPlayer? _current;
    private bool _deviceFailure;

    /// <summary>True when the last attempt failed because no audio device was usable.</summary>
    public bool DeviceUnavailable => _deviceFailure;

    public async ValueTask<bool> PlayAsync(VoiceClip clip, CancellationToken cancellationToken)
    {
        if (clip.ByteCount == 0)
        {
            return false;
        }

        SoundPlayer player;
        try
        {
            player = new SoundPlayer(new MemoryStream(clip.Wav, writable: false));
            player.Load();
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException or NotSupportedException)
        {
            _deviceFailure = true;
            return false;
        }

        lock (_gate)
        {
            _current = player;
        }

        using var registration = cancellationToken.Register(static state =>
        {
            try
            {
                ((SoundPlayer)state!).Stop();
            }
            catch (InvalidOperationException)
            {
            }
        }, player);

        try
        {
            await Task.Run(player.PlaySync, CancellationToken.None).ConfigureAwait(false);
            return !cancellationToken.IsCancellationRequested;
        }
        catch (InvalidOperationException)
        {
            _deviceFailure = true;
            return false;
        }
        catch (TimeoutException)
        {
            _deviceFailure = true;
            return false;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, player))
                {
                    _current = null;
                }
            }

            player.Dispose();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try
            {
                _current?.Stop();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
