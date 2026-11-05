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
    // ---- tray -------------------------------------------------------------------

    private void CreateTray()
    {
        if (_dependencies.TrayAvailableOverride is { } trayOverride)
        {
            // Test seam: close-to-tray behaviour is verified without a real icon.
            TrayAvailable = trayOverride;
            if (trayOverride)
            {
                _window?.SetCloseBehavior(hideToTray: true);
                RefreshWindowMenu();
            }
            else
            {
                EnsureReachableWithoutTray();
            }

            return;
        }

        if (!_dependencies.TrayEnabled)
        {
            TrayAvailable = false;
            EnsureReachableWithoutTray();
            return;
        }

        var callbacks = new TrayCallbacks(
            ToggleVisibility, ToggleTopmost, ToggleMute,
            () => ExitRequested?.Invoke(this, EventArgs.Empty),
            OpenNotificationSettings, OpenProjectSources);
        var (service, availability) = TrayService.TryCreate(callbacks);
        _tray = service;
        TrayAvailable = availability == TrayAvailability.Available;
        if (!TrayAvailable)
        {
            _trayFailures++;
            Log(MonitorLogLevel.Warning, "hud-tray-unavailable");
            ViewModel.NotifyStatus("Tray icon unavailable — use the HUD window to control it.");
            EnsureReachableWithoutTray();
        }

        _window!.SetCloseBehavior(TrayAvailable);
        RefreshWindowMenu();
    }

    private void EnsureReachableWithoutTray()
    {
        if (_window is null) return;
        _window.Show();
        _window.SetCloseBehavior(hideToTray: false);
        RefreshWindowMenu();
        ApplyGeometry(show: false);
        ViewModel.NotifyStatus("Tray unavailable — right-click the header for controls.");
    }

    /// <summary>Daily actions; appearance and startup options live in settings.</summary>
    private void CreateWindowMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu { Cursor = System.Windows.Input.Cursors.Arrow, ForceCursor = true };
        System.Windows.Controls.MenuItem Add(string label, Action action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        var pin = Add("Always on top", ToggleTopmost);
        pin.IsCheckable = true;
        pin.SetBinding(System.Windows.Controls.MenuItem.IsCheckedProperty,
            new System.Windows.Data.Binding("Snapshot.AlwaysOnTop") { Source = MenuState, Mode = System.Windows.Data.BindingMode.OneWay });
        Add("Expand all projects", () => ViewModel.SetAllProjectsExpanded(true));
        Add("Collapse all projects", () => ViewModel.SetAllProjectsExpanded(false));
        Add("Compact / expanded HUD", ToggleWindowView);
        Add("Light & voice…", OpenNotificationSettings);
        Add("Projects & sources…", OpenProjectSources);
        Add("Focus…", OpenFocus);
        var hide = Add("Hide to tray", HideToTray);
        hide.SetBinding(System.Windows.Controls.MenuItem.IsEnabledProperty,
            new System.Windows.Data.Binding("Snapshot.CanHideToTray") { Source = MenuState });
        Add("Exit", () => ExitRequested?.Invoke(this, EventArgs.Empty));
        _window!.ContextMenu = menu;
        RefreshWindowMenu();
    }

    private void RefreshWindowMenu() => MenuState.Update(new HudMenuSnapshot(
        _window?.IsVisible ?? false,
        _state.WindowView ? _state.WindowPinned : ViewModel.AlwaysOnTop,
        ViewModel.StartWithWindows, ViewModel.IsMuted, CanHideToTray));

    // ---- projects and sources ---------------------------------------------------

    private ProjectSourcesWindow? _projectSources;

    /// <summary>Open project/source settings window, if any. Never creates one.</summary>
    public ProjectSourcesWindow? ProjectSources => _projectSources;

    /// <summary>
    /// Bounded project/source settings surface, reachable from the header menu. It
    /// reuses the existing observation data and the existing config path; a source
    /// list change is additive and applies when the monitor is next composed.
    /// </summary>
    public void OpenProjectSources()
    {
        if (_shuttingDown || _window is null)
        {
            return;
        }

        if (_projectSources is { } open)
        {
            open.Activate();
            return;
        }

        var model = BuildProjectSources();
        var window = new ProjectSourcesWindow(model, _claudeProjectsEnabled) { Owner = _window, Icon = AppIcon.WindowImage };
        window.ClaudeProjectsChanged += enabled =>
        {
            _claudeProjectsEnabled = enabled;
            if (enabled) _disabledHarnessSources = _disabledHarnessSources.Where(item => item != "claude/projects-desktop").ToArray();
            ScheduleStateSave();
            ViewModel.NotifyStatus("Claude Projects observation saved — applies after OVERSEER restarts.");
        };
        _projectSources = window;
        model.Changed += (_, _) =>
        {
            _disabledHarnessSources = model.DisabledHarnessSources;
            ScheduleStateSave();
            ViewModel.NotifyStatus("Source enablement saved — it applies after OVERSEER restarts.");
        };
        window.Closed += (_, _) =>
        {
            _projectSources = null;
            if (model.DisabledHarnessSources.Count != _disabledHarnessSources.Count ||
                !model.DisabledHarnessSources.SequenceEqual(_disabledHarnessSources, StringComparer.Ordinal))
            {
                _disabledHarnessSources = model.DisabledHarnessSources;
                ScheduleStateSave();
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

    /// <summary>
    /// Rows for the settings surface. Sources come from the tasks the monitor already
    /// published plus the persisted disabled list, so a disabled source stays visible
    /// and can be re-enabled; project buckets come from the same grouping the HUD
    /// renders, including Unassigned.
    /// </summary>
    private ProjectSourcesViewModel BuildProjectSources()
    {
        var sources = new List<ProjectSourceRow>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var task in _lastTasks)
        {
            var key = HarnessIds.Normalize(task.Key.HarnessId) + "/" + task.Key.SourceInstanceId;
            if (!counts.TryGetValue(key, out var count))
            {
                order.Add(key);
            }

            counts[key] = count + 1;
        }

        foreach (var entry in _disabledHarnessSources)
        {
            if (entry.Length > 0 && !counts.ContainsKey(entry))
            {
                counts[entry] = 0;
                order.Add(entry);
            }
        }

        foreach (var key in order)
        {
            // This source has its own opt-in checkbox, including when no task
            // has yet been observed. Avoid two controls with conflicting state.
            if (key == "claude/projects-desktop") continue;
            var separator = key.IndexOf('/');
            var harness = separator < 0 ? key : key[..separator];
            var instance = separator < 0 ? string.Empty : key[(separator + 1)..];
            var observed = counts[key];
            var disabled = _disabledHarnessSources.Contains(key, StringComparer.Ordinal);
            var detail = observed == 0
                ? "No task observed in this session."
                : observed.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                  (observed == 1 ? " task observed" : " tasks observed") + " in this session.";
            sources.Add(new ProjectSourceRow(harness, instance, detail, !disabled));
        }

        var projects = new List<ProjectBucketRow>();
        foreach (var project in ViewModel.Projects)
        {
            var detail = project.Tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                (project.Tasks.Count == 1 ? " task · " : " tasks · ") + project.SummaryText;
            projects.Add(new ProjectBucketRow(project.Name, detail));
        }

        return new ProjectSourcesViewModel(
            sources.OrderBy(static source => source.Key, StringComparer.Ordinal),
            projects,
            "Saved with your other settings; a new source list is applied when the monitor is next composed.");
    }

    private void RefreshTrayState()
    {
        RefreshWindowMenu();
        if (_tray is null || _window is null)
        {
            return;
        }

        var status = ViewModel.MonitoringUnavailable
            ? "monitor unavailable"
            : ViewModel.SummaryText;
        if (!ViewModel.IsMuted)
        {
            status += " · voice on";
        }

        _tray.UpdateState(MenuState.Snapshot, status);
    }

}
