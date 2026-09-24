using System.IO;
using System.Net.Http;
using Microsoft.Win32;
using TheOverseer.App.Desktop;
using TheOverseer.App.Voice;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.App;

public sealed partial class HudApplicationController
{
    private VoiceSettings _voiceSettings;
    private NotificationSettingsWindow? _notificationSettings;
    private CancellationTokenSource? _previewCancellation;
    private readonly WavPlaybackService _previewPlayer = new();
    private bool _importing;

    public void OpenNotificationSettings()
    {
        if (_shuttingDown) return;
        if (_notificationSettings is { } existing) { existing.Activate(); return; }
        var settings = CurrentVoiceSettings;
        var rows = VoiceEventCatalog.All.Select(item => new VoiceSetupRow(item, settings.IsEnabled(item.Kind))).ToArray();
        var window = new NotificationSettingsWindow(ViewModel, rows) { Owner = _window, Icon = AppIcon.WindowImage };
        _notificationSettings = window;
        var lifetime = new CancellationTokenSource();

        void Change(Func<VoiceSettings, VoiceSettings> update)
        {
            if (lifetime.IsCancellationRequested) return;
            ApplyVoiceSettings(update(CurrentVoiceSettings));
        }

        // Status refresh is fire-and-forget so a UI event handler can never surface
        // an exception; the refresh itself reports failures as text.
        void Refresh() => _ = RefreshVoiceStatus(window, rows, lifetime.Token);

        foreach (var row in rows)
            row.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(VoiceSetupRow.Enabled) || lifetime.IsCancellationRequested) return;
                var events = CurrentVoiceSettings.Events.ToDictionary(pair => pair.Key, pair => pair.Value);
                events[row.Definition.Code] = row.Enabled;
                Change(current => current with { Events = events });
            };
        // Selecting a provider stores only the preference: playback picks it up on the
        // next cue, preview builds it on demand, and nothing is spoken by this change.
        window.ProviderChangeRequested += provider =>
        {
            Change(current => current with { Provider = provider });
            Refresh();
        };
        window.BaselineRequested += () =>
        {
            Change(current => current with { Provider = VoiceSettings.SpeechProviderName });
            Refresh();
        };
        window.AssignRequested += async row =>
        {
            if (_importing) return;
            var picker = new OpenFileDialog { Title = "Assign a voice recording", Filter = "WAV audio (*.wav)|*.wav", CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(window) != true) return;
            _importing = true;
            try
            {
                var name = await Task.Run(() => VoiceRecordingStore.ImportAsync(_dependencies.Paths.DataDirectory, picker.FileName, lifetime.Token));
                if (lifetime.IsCancellationRequested) return;
                Change(current => current with { Clips = WithClip(current.Clips, row.Definition.Code, name) });
                await RefreshVoiceStatus(window, rows, lifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            { row.Status = "Could not import. Choose a valid local WAV, at most 15 seconds / 4 MiB."; }
            finally { _importing = false; }
        };
        window.ClearRequested += row =>
        {
            Change(current => current with { Clips = current.Clips.Where(pair => pair.Key != row.Definition.Code).ToDictionary(pair => pair.Key, pair => pair.Value) });
            Refresh();
        };
        window.PreviewRequested += async row => await PreviewRecording(row);
        window.StopRequested += StopVoicePreview;
        window.Closed += (_, _) =>
        {
            lifetime.Cancel();
            StopVoicePreview();
            _notificationSettings = null;
        };
        window.Loaded += (_, _) => Refresh();
        var screens = _dependencies.Placement.CaptureLayout();
        var screen = screens.Find(_state.WindowView ? _state.WindowMonitor : _state.MonitorDeviceName) ?? (screens.IsEmpty ? null : screens.Primary);
        if (screen is not null)
        {
            var scale = _dependencies.Placement.GetDpiScale(_handle, screen);
            window.MinWidth = Math.Min(window.MinWidth, screen.Width / scale);
            window.MinHeight = Math.Min(window.MinHeight, screen.Height / scale);
            window.Width = Math.Min(window.Width, screen.Width / scale);
            window.Height = Math.Min(window.Height, screen.Height / scale);
        }

        window.ShowProvider(settings.ProviderName);
        window.Show();
    }

    private VoiceSettings CurrentVoiceSettings => _dependencies.Notifications?.Settings ?? _voiceSettings;

    private void ApplyVoiceSettings(VoiceSettings settings)
    {
        StopVoicePreview();
        _voiceSettings = settings.Normalized();
        _dependencies.Notifications?.UpdateSettings(_voiceSettings);
        ScheduleStateSave();
    }

    private static IReadOnlyDictionary<string, string> WithClip(IReadOnlyDictionary<string, string> clips, string code, string name)
    {
        var updated = clips.ToDictionary(pair => pair.Key, pair => pair.Value);
        updated[code] = name;
        return updated;
    }

    /// <summary>
    /// Refreshes the panel from bounded local state only: the installed baseline's
    /// availability probe and the selected provider's own status (plus a local pack
    /// clip probe). Nothing is synthesized, no request is sent and no audio plays.
    /// </summary>
    private async Task RefreshVoiceStatus(NotificationSettingsWindow window, IReadOnlyList<VoiceSetupRow> rows, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested) return;
            var baseline = await Task.Run(() => VoiceProviderFactory.DescribeSpeechBaseline(), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            window.ShowStatus(VoiceSetupStatus.Baseline(baseline));
            using var provider = VoiceProviderFactory.Create(_dependencies.Paths.DataDirectory, () => CurrentVoiceSettings);
            foreach (var row in rows)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await UpdateRecordingStatus(row, provider, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            Log(MonitorLogLevel.Warning, "voice-status-refresh-failed");
            window.ShowStatus("Windows speech baseline (offline): status unavailable.");
        }
    }

    private async Task UpdateRecordingStatus(VoiceSetupRow row, IVoiceProvider selectedProvider, CancellationToken cancellationToken)
    {
        var settings = CurrentVoiceSettings;
        try
        {
            if (settings.Clips.TryGetValue(row.Definition.Code, out var name))
            {
                var clip = await Task.Run(() => VoiceRecordingStore.ReadAsync(_dependencies.Paths.DataDirectory, name, cancellationToken));
                row.Status = clip is null ? "Assigned recording missing or invalid — assign another WAV." : "Assigned recording ready.";
                return;
            }

            switch (settings.ResolvedProvider)
            {
                case VoiceProviderKind.Speech:
                case VoiceProviderKind.Tts:
                    // Status only, read off the UI thread: the baseline is never
                    // synthesized and the endpoint is never contacted merely to open
                    // this panel.
                    var status = await Task.Run(() => selectedProvider.Status, cancellationToken);
                    row.Status = VoiceSetupStatus.ProviderRow(settings, status);
                    return;
                default:
                    // Pack: a bounded local read proves the clip exists without playing it.
                    var probe = await Task.Run(async () => await selectedProvider.TryCreateAsync(
                        VoicePreviewService.Candidate(row.Definition.Kind, _dependencies.TimeProvider), cancellationToken));
                    row.Status = VoiceSetupStatus.PackRow(settings.PackId, probe is not null);
                    return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { row.Status = "Recording unavailable — assign a valid WAV."; }
    }

    private async Task PreviewRecording(VoiceSetupRow row)
    {
        StopVoicePreview();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _dependencies.Notifications?.SetPreviewing(true);
        // Exactly the provider graph normal playback uses: assigned recording first,
        // then the selected provider. A failing selected provider is never replaced.
        var preview = new VoicePreviewService(
            _dependencies.Paths.DataDirectory,
            () => CurrentVoiceSettings,
            _previewPlayer,
            timeProvider: _dependencies.TimeProvider);
        try
        {
            row.Status = "Preparing preview…";
            var outcome = await Task.Run(() => preview.PreviewAsync(row.Definition.Kind, cancellation.Token));
            if (!outcome.ClipCreated)
            {
                row.Status = "No playable recording (" + outcome.Code + "). Assign a WAV or choose another provider.";
                return;
            }

            row.Status = outcome.Played ? "Preview finished." : "Audio device unavailable (" + outcome.Code + ").";
        }
        catch (OperationCanceledException) { row.Status = "Preview stopped."; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        { row.Status = "Preview unavailable. Check the recording or audio device."; }
        catch (Exception)
        {
            Log(MonitorLogLevel.Warning, "voice-preview-failed");
            row.Status = "Preview unavailable. Check the recording or audio device.";
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
                _dependencies.Notifications?.SetPreviewing(false);
            }
            cancellation.Dispose();
        }
    }

    private void StopVoicePreview()
    {
        if (_previewCancellation is not { } cancellation) return;
        _previewCancellation = null;
        cancellation.Cancel();
        _previewPlayer.Stop();
        _dependencies.Notifications?.SetPreviewing(false);
    }
}