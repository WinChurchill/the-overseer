using System.Windows.Input;
using TheOverseer.Core.Voice;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// Voice status projection for the HUD. Kept beside the main view model so
/// the snapshot projection stays readable; still free of any I/O.
/// </summary>
public sealed partial class HudViewModel
{
    private string _voiceStatusLine = "Voice is muted.";
    private bool _isMuted = true;
    private bool _voiceAvailable;

    public string VoiceStatusLine => _voiceStatusLine;

    /// <summary>
    /// True when voice is enabled and the last attempted notification did not speak.
    /// A failure must be discoverable without opening the voice settings window, and it
    /// must not become a permanent sentence when nothing is wrong.
    /// </summary>
    public bool VoiceFailed =>
        !IsMuted && VoiceStatusLine.StartsWith("Voice unavailable", StringComparison.Ordinal);

    /// <summary>The failure text, or empty when voice is fine or muted.</summary>
    public string VoiceFailureLine => VoiceFailed ? VoiceStatusLine : string.Empty;

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (Set(ref _isMuted, value))
            {
                Raise(nameof(MuteGlyph));
                Raise(nameof(MuteTooltip));
                Raise(nameof(VoiceStatusLine));
                Raise(nameof(VoiceFailed));
                Raise(nameof(VoiceFailureLine));
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Compact state glyph for the header mute control.</summary>
    public string MuteGlyph => _isMuted ? "M" : "V";

    /// <summary>Header/tray mute toggle. Persistence is the controller's job.</summary>
    public ICommand ToggleMuteCommand => _toggleMuteCommand ??= new RelayCommand(() => IsMuted = !IsMuted);

    private ICommand? _toggleMuteCommand;

    public string MuteTooltip => _isMuted
        ? "Voice is muted — click to enable notifications"
        : "Voice notifications are enabled — click to mute";

    /// <summary>True when a voice provider reported itself usable.</summary>
    public bool VoiceAvailable
    {
        get => _voiceAvailable;
        private set => Set(ref _voiceAvailable, value);
    }

    /// <summary>Projects voice status without rebuilding unused count-chart geometry.</summary>
    public void ApplyVoiceStatus(VoiceStatus status)
    {
        // Set both inputs before notifying dependent bindings: neither an old mute
        // flag nor an old detail should be observed during this single update.
        _voiceStatusLine = status.Detail;
        IsMuted = status.IsMuted;
        Raise(nameof(VoiceStatusLine));
        Raise(nameof(VoiceFailed));
        Raise(nameof(VoiceFailureLine));
        VoiceAvailable = !status.IsMuted && status.ProviderId != "silent";
    }
}
