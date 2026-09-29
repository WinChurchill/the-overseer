using System.Drawing;
using System.Windows.Forms;

namespace TheOverseer.App.Desktop;

public enum TrayAvailability { Available, Unavailable }

/// <summary>Small restore/control surface. App supplies the same state as the header menu.</summary>
internal sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _artwork;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _visibilityItem;
    private readonly ToolStripMenuItem _topmostItem;
    private readonly ToolStripMenuItem _muteItem;
    private readonly ToolStripMenuItem _statusItem;
    private bool _disposed;

    private TrayService(NotifyIcon icon, ContextMenuStrip menu, ToolStripMenuItem visibility,
        ToolStripMenuItem topmost, ToolStripMenuItem mute, ToolStripMenuItem status)
    {
        _icon = icon;
        _artwork = icon.Icon!;
        _menu = menu;
        _visibilityItem = visibility;
        _topmostItem = topmost;
        _muteItem = mute;
        _statusItem = status;
    }

    public static (TrayService? Service, TrayAvailability Availability) TryCreate(TrayCallbacks callbacks)
    {
        ContextMenuStrip? menu = null;
        NotifyIcon? icon = null;
        Icon? artwork = null;
        try
        {
            menu = new ContextMenuStrip();
            ToolStripMenuItem Add(string label, Action action)
            {
                var item = new ToolStripMenuItem(label) { CheckOnClick = false };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
                return item;
            }
            var visibility = Add("Hide HUD", callbacks.ToggleVisibility);
            var topmost = Add("Always on top", callbacks.ToggleTopmost);
            var mute = Add("Mute voice", callbacks.ToggleMute);
            menu.Items.Add(new ToolStripSeparator());
            Add("Light & voice…", callbacks.OpenSettings);
            Add("Projects & sources…", callbacks.OpenSources);
            var status = new ToolStripMenuItem("Monitoring status") { Enabled = false };
            menu.Items.Add(status);
            Add("Exit", callbacks.Exit);

            artwork = AppIcon.CreateTrayIcon();
            icon = new NotifyIcon
            {
                Icon = artwork,
                Text = "OVERSEER",
                ContextMenuStrip = menu,
                Visible = true,
            };
            icon.DoubleClick += (_, _) => callbacks.ToggleVisibility();
            return (new TrayService(icon, menu, visibility, topmost, mute, status), TrayAvailability.Available);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            icon?.Dispose();
            artwork?.Dispose();
            menu?.Dispose();
            return (null, TrayAvailability.Unavailable);
        }
    }

    public void UpdateState(HudMenuSnapshot state, string statusLine)
    {
        if (_disposed) return;
        _visibilityItem.Text = state.WindowVisible ? "Hide HUD" : "Show HUD";
        _topmostItem.Checked = state.AlwaysOnTop;
        _muteItem.Checked = state.Muted;
        _statusItem.Text = statusLine.Length <= 120 ? statusLine : statusLine[..120];
        _icon.Text = "OVERSEER — " + (statusLine.Length <= 100 ? statusLine : statusLine[..100]);
    }

    /// <summary>Marshals a tray callback onto the UI thread; never throws to the shell.</summary>
    public static void Post(Action action, Action<Action> dispatcher)
    {
        try { dispatcher(action); }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _artwork.Dispose();
        _menu.Dispose();
    }
}

public sealed record TrayCallbacks(
    Action ToggleVisibility,
    Action ToggleTopmost,
    Action ToggleMute,
    Action Exit,
    Action OpenSettings,
    Action OpenSources);
