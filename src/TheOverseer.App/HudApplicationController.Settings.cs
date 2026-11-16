using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Threading.Channels;
using Microsoft.Win32;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.App;

public sealed partial class HudApplicationController : IAsyncDisposable
{
    // ---- settings, startup, navigation ------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HudViewModel.LightPosition) or nameof(HudViewModel.LightSize) && !_shuttingDown)
        {
            _window?.UpdateResponsiveLayout();
            ApplyGeometry(show: false);
        }
        if (e.PropertyName is nameof(HudViewModel.Theme) && !_shuttingDown) ApplyTheme();
        if (e.PropertyName is nameof(HudViewModel.IsExpanded))
        {
            if (_state.WindowView && !ViewModel.IsExpanded) { ViewModel.IsExpanded = true; return; }
            if (!_shuttingDown)
            {
                ApplyGeometry(show: _window?.IsVisible ?? false);
                ScheduleStateSave();
            }

            return;
        }

        if (e.PropertyName is nameof(HudViewModel.IsMuted) && !_shuttingDown)
        {
            if (ViewModel.IsMuted) StopVoicePreview();
            _voiceSettings = _voiceSettings with { Muted = ViewModel.IsMuted };
            _dependencies.Notifications?.SetMuted(ViewModel.IsMuted);
            RefreshTrayState();
            return;
        }

        if (e.PropertyName is nameof(HudViewModel.AlwaysOnTop) && !_shuttingDown)
        {
            RefreshWindowMenu();
            if (_state.WindowView) return;
            if (_window is not null) _window.Topmost = ViewModel.AlwaysOnTop;
            _ = NativeMethods.SetWindowPos(
                _handle,
                ViewModel.AlwaysOnTop ? NativeMethods.HwndTopmost : NativeMethods.HwndNotTopmost,
                0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }
    }

    private void ApplyTheme()
    {
        var application = Application.Current;
        if (application is not null)
        {
            HudThemeManager.Apply(application, ViewModel.Theme);
        }
    }

    private void SyncStartupState()
    {
        var result = _dependencies.Startup.Read();
        _startupCode = result.ReasonCode;
        _suppressSettings = true;
        try
        {
            ViewModel.StartWithWindows = result.State == StartupRegistrationState.Enabled;
        }
        finally
        {
            _suppressSettings = false;
        }

        if (result.State is StartupRegistrationState.StalePath or StartupRegistrationState.Unavailable)
        {
            ViewModel.NotifyStatus(result.State == StartupRegistrationState.StalePath
                ? "Startup entry points to an old location; toggle it to repair."
                : "Startup setting unavailable (registry access denied).");
        }
    }

    public void ToggleStartWithWindows()
    {
        var requested = !ViewModel.StartWithWindows;
        var result = _dependencies.Startup.Set(requested);
        _startupCode = result.ReasonCode;
        _suppressSettings = true;
        try
        {
            ViewModel.StartWithWindows = result.State == StartupRegistrationState.Enabled;
        }
        finally
        {
            _suppressSettings = false;
        }

        ViewModel.NotifyStatus(result.State switch
        {
            StartupRegistrationState.Enabled => "Start with Windows enabled.",
            StartupRegistrationState.Disabled => "Start with Windows disabled.",
            StartupRegistrationState.StalePath => "Startup entry is stale; try again.",
            _ => "Startup could not be changed (registry access denied).",
        });
        RefreshTrayState();
    }

    /// <summary>Shared mute used by the header, the tray and the header menu.</summary>
    public void ToggleMute()
    {
        ViewModel.IsMuted = !ViewModel.IsMuted;
        ViewModel.NotifyStatus(ViewModel.IsMuted
            ? "Voice muted. Current and queued notifications were stopped."
            : "Voice unmuted. Only new events will be announced.");
        RefreshTrayState();
    }

    public void ToggleTheme()
    {
        ViewModel.Theme = ViewModel.Theme == HudTheme.Light ? HudTheme.Dark : HudTheme.Light;
        RefreshTrayState();
    }

    public void ToggleTopmost()
    {
        if (_state.WindowView)
        {
            _state = _state with { WindowPinned = !_state.WindowPinned };
            _window!.Topmost = _state.WindowPinned;
            ScheduleStateSave();
            RefreshTrayState();
            return;
        }
        ViewModel.AlwaysOnTop = !ViewModel.AlwaysOnTop;
        RefreshTrayState();
    }

    public void SetOpacity(double value)
    {
        ViewModel.Opacity = value;
        RefreshTrayState();
    }

    private void OnActivateTask(TaskRowViewModel row)
    {
        var task = _lastTasks.Find(candidate => candidate.Key.Equals(row.Key)) ?? ViewModel.ReviewTaskFor(row.Key);
        if (task is null)
        {
            ViewModel.NotifyStatus("Task is no longer in view.");
            return;
        }

        var result = _dependencies.Navigator.Navigate(task, task.ProfileId);
        _navigationCode = result.Outcome.ToString();
        var routeUnsupported = result.Outcome is TaskNavigationOutcome.UnsupportedSurface
            or TaskNavigationOutcome.UnsupportedIdentity
            or TaskNavigationOutcome.UnsupportedProfile
            or TaskNavigationOutcome.InvalidIdentifier;
        if (routeUnsupported)
        {
            // No adapter route exists for this harness (or this task's identity),
            // so offer the honest fallback the row already advertises: copy the id.
            var copied = _dependencies.Clipboard.TrySetText(row.Key.ConversationId);
            ViewModel.NotifyStatus((copied ? "Task id copied. " : string.Empty) + result.UserMessage);
            Log(MonitorLogLevel.Info, "hud-navigate-" + result.Outcome.ToString().ToLowerInvariant());
            return;
        }

        // Opening a finished task counts as looking at it.
        if (result.Outcome == TaskNavigationOutcome.Requested && ViewModel.IsReviewItem(row.Key)) ViewModel.MarkReviewed(row.Key);
        ViewModel.NotifyStatus(result.UserMessage, transient: result.Outcome == TaskNavigationOutcome.Requested);
        Log(MonitorLogLevel.Info, "hud-navigate-" + result.Outcome.ToString().ToLowerInvariant());
    }

    private void OnCopyTaskId(TaskRowViewModel row)
    {
        var copied = _dependencies.Clipboard.TrySetText(row.Key.ConversationId);
        ViewModel.NotifyStatus(copied ? "Task id copied." : "Could not copy the task id.", transient: copied);
    }

}
