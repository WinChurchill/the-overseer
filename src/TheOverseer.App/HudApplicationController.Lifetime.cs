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
    // ---- timers, persistence ----------------------------------------------------

    private void OnAgeTick(object? sender, EventArgs e)
    {
        if (_shuttingDown)
        {
            return;
        }

        var now = _dependencies.TimeProvider.GetUtcNow();
        ViewModel.UpdateAges(now);
        RefreshActivity(now);
    }

    /// <summary>
    /// Timer-driven Plan 3 projection: flush completion aggregation and pending
    /// cooldowns, then project voice status onto the UI. Task history uses the strip source. Cheap, coalesced and
    /// on the UI thread only.
    /// </summary>
    public void RefreshActivity(DateTimeOffset now)
    {
        var notifications = _dependencies.Notifications;
        if (notifications is null)
        {
            return;
        }

        notifications.Pump();
        ViewModel.ApplyVoiceStatus(notifications.Status);
    }

    private void OnSaveTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        if (_stateDirty)
        {
            if (_saveTask.IsCompleted) _saveTask = SaveAsync();
            else _saveTimer.Start();
        }
    }

    private void ScheduleStateSave()
    {
        _stateDirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync()
    {
        _stateDirty = false;
        _state = CaptureState();
        var state = _state;
        // Capture immutable UI state plus the live voice settings at save time;
        // the older dependency snapshot must never overwrite them.
        var voice = (_dependencies.Notifications?.Settings ?? _voiceSettings).Normalized() with
        {
            Muted = ViewModel.IsMuted,
        };
        var config = _dependencies.Config with
        {
            Hud = new HudSettings
            {
                Theme = ViewModel.ThemeName,
                AlwaysOnTop = ViewModel.AlwaysOnTop,
                Opacity = ViewModel.Opacity,
                StartWithWindows = ViewModel.StartWithWindows,
                TaskView = ViewModel.TaskView,
                RecentHours = ViewModel.RecentHours,
                LightPosition = ViewModel.LightPosition,
                LightSize = ViewModel.LightSize,
                FocusPins = ViewModel.FocusPins,
            },
            Voice = voice,
            // Additive: source enablement is written with every other setting
            // preserved, never reset.
            DisabledHarnessSources = _disabledHarnessSources,
            ClaudeProjectsEnabled = _claudeProjectsEnabled,
        };
        var writer = _dependencies.Persistence ?? new FileHudPersistence();
        var result = await Task.Run(() => writer.Save(_dependencies.Paths, state, config));
        if (!result.StateSaved || !result.SettingsSaved)
        {
            Log(MonitorLogLevel.Warning, "hud-save-failed");
            ViewModel.NotifyStatus("Settings or position could not be saved.");
        }
    }

    private HudWindowState CaptureState()
    {
        if (_handle == IntPtr.Zero)
        {
            return _state;
        }

        if (_state.WindowView) return CaptureWindowViewState();
        var captured = _dependencies.Placement.CaptureState(_handle, ViewModel.IsExpanded, _dpiScale, _dependencies.TimeProvider.GetUtcNow());
        return _state with { IsExpanded = captured.IsExpanded, MonitorDeviceName = captured.MonitorDeviceName,
            OffsetXDips = captured.OffsetXDips, OffsetYDips = captured.OffsetYDips, DpiScale = captured.DpiScale, SavedAtUtc = captured.SavedAtUtc };
    }

    private void OnViewModelSettingsChanged()
    {
        if (_suppressSettings || _shuttingDown)
        {
            return;
        }

        ScheduleStateSave();
    }

    // ---- system events ----------------------------------------------------------

    private void SubscribeSystemEvents()
    {
        try
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            if (Application.Current is { } application)
            {
                application.SessionEnding += OnSessionEnding;
            }
        }
        catch (InvalidOperationException)
        {
            Log(MonitorLogLevel.Warning, "hud-system-events-unavailable");
        }
    }

    private void UnsubscribeSystemEvents()
    {
        try
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            if (Application.Current is { } application)
            {
                application.SessionEnding -= OnSessionEnding;
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Logoff/shutdown/restart. The request itself is never cancelled and the
    /// close intent switches away from hide-to-tray so Windows can proceed.
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => NotifySessionEnding();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => NotifyDisplaySettingsChanged();

    /// <summary>
    /// Display layout changed: keep the window reachable and ask the monitor to
    /// reconcile. Public so the lifecycle path is testable without a real
    /// display change; no task state is invented here.
    /// </summary>
    public void NotifyDisplaySettingsChanged() =>
        _dependencies.Dispatcher.Post(() =>
        {
            if (_shuttingDown)
            {
                return;
            }

            RecoverWindowViewIfNeeded();
            ApplyGeometry(show: _window?.IsVisible ?? false);
            _dependencies.Monitor.RequestReconcile();
        });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) =>
        NotifyPowerModeChanged(e.Mode);

    /// <summary>
    /// Power transitions. Suspend is handled inline on the events thread because
    /// a posted callback would not run before the machine sleeps; it discards
    /// cues that would otherwise play stale after wake. Resume re-runs the
    /// display repair and the reconcile on the UI thread. Both paths repeat the
    /// normal display-change handling, so a session that comes back with a
    /// different monitor set is repaired instead of assumed unchanged.
    /// </summary>
    public void NotifyPowerModeChanged(PowerModes mode)
    {
        switch (mode)
        {
            case PowerModes.Suspend:
                _dependencies.Notifications?.OnSuspend();
                Log(MonitorLogLevel.Info, "hud-suspend-voice-discarded");
                break;
            case PowerModes.Resume:
                _dependencies.Notifications?.OnResume();
                _dependencies.Dispatcher.Post(() =>
                {
                    if (_shuttingDown)
                    {
                        return;
                    }

                    RecoverWindowViewIfNeeded();
                    ApplyGeometry(show: _window?.IsVisible ?? false);
                    _dependencies.Monitor.RequestReconcile();
                    Log(MonitorLogLevel.Info, "hud-resume-reconcile");
                });
                break;
            default:
                break;
        }
    }

    // ---- shutdown ---------------------------------------------------------------

    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        StopForegroundGuard();
        _chrome?.Dispose();
        _chrome = null;
        _notificationSettings?.Close();
        _projectSources?.Close();
        _focusWindow?.Close();
        StopVoicePreview();
        _ageTimer.Stop();
        _saveTimer.Stop();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _dependencies.Monitor.SnapshotAvailable -= OnSnapshot;
        _pump.Stop();
        UnsubscribeSystemEvents();

        _lifetime.Cancel();
        var voiceShutdown = _dependencies.Notifications?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        // A slow disk must not strand Exit. There is at most one in-flight write;
        // if it times out, do not start another writer against the same files.
        await TryStepAsync("hud-shutdown-save", async () =>
        {
            await _saveTask.WaitAsync(TimeSpan.FromSeconds(3));
            _saveTask = SaveAsync();
            await _saveTask.WaitAsync(TimeSpan.FromSeconds(3));
        });
        await TryStepAsync("hud-shutdown-voice", () => voiceShutdown);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await _dependencies.Monitor.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Log(MonitorLogLevel.Warning, "hud-monitor-dispose-timeout");
        }
        catch (TimeoutException)
        {
            Log(MonitorLogLevel.Warning, "hud-monitor-dispose-timeout");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Log(MonitorLogLevel.Error, "hud-shutdown-monitor");
        }

        try
        {
            _showRequest?.Dispose();
            _showRequest = null;
            _tray?.Dispose();
            _tray = null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            Log(MonitorLogLevel.Error, "hud-shutdown-tray");
        }

        if (_window is not null)
        {
            try
            {
                _window.Closing -= OnWindowClosing;
                var window = _window;
                _window = null;
                window.Close();
            }
            catch (ObjectDisposedException)
            {
                Log(MonitorLogLevel.Warning, "hud-shutdown-window-disposed");
            }
            catch (InvalidOperationException)
            {
                // The window is already closing (for example the close request
                // that started this shutdown), which is not an error.
                Log(MonitorLogLevel.Info, "hud-shutdown-window-already-closing");
            }
        }

        Log(MonitorLogLevel.Info, "hud-shutdown-complete");
        _logQueue.Writer.TryComplete();
        try { await _logTask.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { /* A stalled log disk must not block Exit. */ }
        (_dependencies.Log as IDisposable)?.Dispose();
    }

    private void Log(MonitorLogLevel level, string code) => _logQueue.Writer.TryWrite((level, code));

    private async Task TryStepAsync(string code, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException or TimeoutException)
        {
            Log(MonitorLogLevel.Error, code);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(true);
        if (_disposed) return;
        _disposed = true;
        _ageTimer.Tick -= OnAgeTick;
        _saveTimer.Tick -= OnSaveTick;
        _lifetime.Dispose();
    }

    /// <summary>Finite status report used by the HUD smoke fixture.</summary>
    public string BuildStatusReport()
    {
        var window = _window;
        return string.Join(Environment.NewLine, new[]
        {
            "hud window: " + (window is null ? "none" : (window.IsVisible ? "visible" : "hidden") + " expanded=" + ViewModel.IsExpanded),
            "hud tray: " + (TrayAvailable ? "available" : "unavailable") + " failures=" + _trayFailures.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hud theme: " + ViewModel.ThemeName + " topmost=" + ViewModel.AlwaysOnTop + " opacity=" + ViewModel.OpacityPercentLabel +
                " dpi=" + _dpiScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            "hud placement: " + (ViewModel.PlacementLine.Length > 0 ? "resolved" : "none") +
                " expanded=" + ViewModel.IsExpanded + DescribeWindow(),
            "hud startup: " + _startupCode,
            "hud navigation: " + _navigationCode,
            "hud monitoring: " + ViewModel.MonitoringTitle + " · " + ViewModel.SummaryText,
            "hud rows: " + ViewModel.Tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    private string DescribeWindow()
    {
        if (_window is null)
        {
            return " window=none";
        }

        var wpfDpi = System.Windows.Media.VisualTreeHelper.GetDpi(_window).PixelsPerDip;
        var hwndDpi = _handle == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(_handle);
        _ = NativeMethods.GetWindowRect(_handle, out var rect);
        return " wpfDpi=" + wpfDpi.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
               " hwndDpi=" + hwndDpi.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " wpfSize=" + _window.ActualWidth.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x" +
               _window.ActualHeight.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
               " hwndRect=" + rect.Width.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" +
               rect.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
