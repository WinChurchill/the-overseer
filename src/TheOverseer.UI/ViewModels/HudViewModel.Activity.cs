using System.Windows.Input;
using TheOverseer.Core.History;
using TheOverseer.Core.Voice;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// Activity and voice projection for the HUD. Kept beside the main view model so
/// the snapshot projection stays readable; still free of any I/O.
/// </summary>
public sealed partial class HudViewModel
{
    private IReadOnlyList<ActivityChartSegment> _activitySegments = Array.Empty<ActivityChartSegment>();
    private string _activityTooltip = "No activity recorded in the last 60 minutes.";
    private string _activitySummary = "no samples yet";
    private string _voiceStatusLine = "Voice is muted.";
    private bool _isMuted = true;
    private bool _voiceAvailable;
    private bool _hasActivity;
    private string _activityAxisMaximum = "1";

    public string ActivityAxisMaximum { get => _activityAxisMaximum; private set => Set(ref _activityAxisMaximum, value); }

    /// <summary>Geometry for the shared sparkline (already bounded by the chart builder).</summary>
    public IReadOnlyList<ActivityChartSegment> ActivitySegments
    {
        get => _activitySegments;
        private set => Set(ref _activitySegments, value);
    }

    public string ActivityTooltip
    {
        get => _activityTooltip;
        private set => Set(ref _activityTooltip, value);
    }

    public string ActivitySummaryLine
    {
        get => _activitySummary;
        private set => Set(ref _activitySummary, value);
    }

    public bool HasActivity
    {
        get => _hasActivity;
        private set => Set(ref _hasActivity, value);
    }

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

    /// <summary>
    /// Applies the history projection. Called on the UI thread from the age
    /// timer, so the sparkline geometry is rebuilt at most once per tick.
    /// </summary>
    public void ApplyActivity(ActivitySeries series, VoiceStatus status, double chartWidth = 330, double chartHeight = 30)
    {
        ActivitySegments = ActivityChartGeometry.Build(series, chartWidth, chartHeight);
        ActivityTooltip = ActivityChartGeometry.Tooltip(series, series.To);
        HasActivity = !series.IsEmpty;
        ActivityAxisMaximum = Math.Max(1, series.PeakWorking).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ActivitySummaryLine = series.IsEmpty
            ? "Collecting activity…"
            : series.HasPartial ? "Dashed: minimum known"
                : series.HasGap || series.HasUnknown ? "Gaps: no observation" : "Count of working tasks";
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
