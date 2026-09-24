using System.IO;
using System.Net.Http;
using Microsoft.Win32;
using TheOverseer.App.Desktop;
using TheOverseer.App.Voice;
using TheOverseer.Core.Configuration;
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
        foreach (var row in rows)
            row.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(VoiceSetupRow.Enabled) || lifetime.IsCancellationRequested) return;
                var current = CurrentVoiceSettings;
                var events = current.Events.ToDictionary(pair => pair.Key, pair => pair.Value);
                events[row.Definition.Code] = row.Enabled;
                ApplyVoiceSettings(current with { Events = events });
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
                var current = CurrentVoiceSettings;
                var clips = current.Clips.ToDictionary(pair => pair.Key, pair => pair.Value);
                clips[row.Definition.Code] = name;
                ApplyVoiceSettings(current with { Clips = clips });
                await UpdateRecordingStatus(row, lifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            { row.Status = "Could not import. Choose a valid local WAV, at most 15 seconds / 4 MiB."; }
            finally { _importing = false; }
        };
        window.ClearRequested += async row =>
        {
            var current = CurrentVoiceSettings;
            var clips = current.Clips.Where(pair => pair.Key != row.Definition.Code).ToDictionary(pair => pair.Key, pair => pair.Value);
            ApplyVoiceSettings(current with { Clips = clips });
            await UpdateRecordingStatus(row, lifetime.Token);
        };
        window.PreviewRequested += async row => await PreviewRecording(row);
        window.StopRequested += StopVoicePreview;
        window.Closed += (_, _) =>
        {
            lifetime.Cancel();
            StopVoicePreview();
            _notificationSettings = null;
        };
        window.Loaded += async (_, _) =>
        {
            foreach (var row in rows)
            {
                if (lifetime.IsCancellationRequested) break;
                await UpdateRecordingStatus(row, lifetime.Token);
            }
        };
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

    private VoiceCandidate PreviewCandidate(VoiceEventKind kind) => new(kind, new TaskKey("preview", "preview"), 1, "Task 1", "preview",
        _dependencies.TimeProvider.GetUtcNow(), "preview", kind == VoiceEventKind.AllTasksCompleted, 1, "preview");

    private async Task UpdateRecordingStatus(VoiceSetupRow row, CancellationToken cancellationToken)
    {
        var settings = CurrentVoiceSettings;
        try
        {
            if (settings.Clips.TryGetValue(row.Definition.Code, out var name))
            {
                var clip = await Task.Run(() => VoiceRecordingStore.ReadAsync(_dependencies.Paths.DataDirectory, name, cancellationToken));
                row.Status = clip is null ? "Assigned recording missing or invalid — assign another WAV." : "Assigned recording ready.";
            }
            else if (settings.ResolvedProvider == VoiceProviderKind.Tts && settings.HasUsableEndpoint)
                row.Status = "TTS configured. Preview checks the service; no request sent on opening this panel.";
            else
            {
                using var pack = new VoicePackProvider(_dependencies.Paths.DataDirectory, () => settings);
                var clip = await Task.Run(async () => await pack.TryCreateAsync(PreviewCandidate(row.Definition.Kind), cancellationToken));
                row.Status = clip is null ? "No usable recording — assign a WAV." : "Ready from voice pack: " + settings.PackId;
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
        var settings = CurrentVoiceSettings;
        var pack = new VoicePackProvider(_dependencies.Paths.DataDirectory, () => settings);
        IVoiceProvider fallback = settings.ResolvedProvider == VoiceProviderKind.Tts && settings.HasUsableEndpoint
            ? new FallbackVoiceProvider(new TtsEndpointProvider(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan }, () => settings), pack, true, _ => true)
            : pack;
        using var provider = new AssignedVoiceProvider(_dependencies.Paths.DataDirectory, () => settings, fallback);
        try
        {
            row.Status = "Preparing preview…";
            var clip = await Task.Run(async () => await provider.TryCreateAsync(PreviewCandidate(row.Definition.Kind), cancellation.Token));
            if (clip is null) { row.Status = "No playable recording. Assign a WAV or check the configured provider."; return; }
            cancellation.Token.ThrowIfCancellationRequested();
            row.Status = "Playing preview…";
            var played = await _previewPlayer.PlayAsync(clip, cancellation.Token);
            row.Status = played ? "Preview finished." : "Audio device unavailable.";
        }
        catch (OperationCanceledException) { row.Status = "Preview stopped."; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        { row.Status = "Preview unavailable. Check the recording or audio device."; }
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
