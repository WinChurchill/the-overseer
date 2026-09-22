using System.Runtime.InteropServices;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Voice;

/// <summary>
/// One asynchronous native WAV playback. Start and Stop share a lock so a
/// cancellation cannot run before a queued PlaySync and accidentally revive it.
/// Memory stays pinned until native playback has stopped. No default beep.
/// </summary>
public sealed class WavPlaybackService : IVoicePlayer
{
    private readonly object _gate = new();
    private int _generation;
    private bool _playing;
    private bool _deviceFailure;

    public bool DeviceUnavailable => _deviceFailure;

    public async ValueTask<bool> PlayAsync(VoiceClip clip, CancellationToken cancellationToken)
    {
        int generation;
        lock (_gate) generation = _generation;
        cancellationToken.ThrowIfCancellationRequested();
        var validation = VoicePackValidator.ValidateWav(clip.Wav);
        if (!validation.IsValid) return false;

        var pinned = GCHandle.Alloc(clip.Wav, GCHandleType.Pinned);
        using var registration = cancellationToken.Register(Stop);
        var started = false;
        try
        {
            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested || generation != _generation) return false;
                // SND_ASYNC | SND_NODEFAULT | SND_MEMORY: starts before releasing
                // the lock; Stop cannot be lost ahead of a deferred start.
                started = PlaySound(pinned.AddrOfPinnedObject(), IntPtr.Zero, 0x0001 | 0x0002 | 0x0004);
                _playing = started;
                _deviceFailure = !started;
            }
            if (!started) return false;
            await Task.Delay(validation.Duration, cancellationToken).ConfigureAwait(false);
            lock (_gate) return generation == _generation && !cancellationToken.IsCancellationRequested;
        }
        finally
        {
            lock (_gate)
            {
                if (started && generation == _generation) Stop();
                pinned.Free();
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _generation++;
            if (_playing) PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            _playing = false;
        }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
