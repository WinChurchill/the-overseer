using System.Drawing;
using System.Windows.Forms;
using TheOverseer.Core.Configuration;

namespace TheOverseer.App.Desktop;

public enum TrayAvailability
{
    Available,
    Unavailable,
}

/// <summary>
/// Small tray surface. The framework NotifyIcon is created on the UI thread and
/// every callback is marshalled back through the WPF dispatcher by the
/// controller; nothing here touches the monitor.
/// </summary>
internal sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _artwork;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _visibilityItem;
    private readonly ToolStripMenuItem _themeItem;
    private readonly ToolStripMenuItem _topmostItem;
    private readonly ToolStripMenuItem _opacityItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _muteItem;
    private readonly ToolStripMenuItem _statusItem;
    private bool _disposed;

    private TrayService(NotifyIcon icon, ContextMenuStrip menu, ToolStripMenuItem visibility, ToolStripMenuItem theme,
        ToolStripMenuItem topmost, ToolStripMenuItem opacity, ToolStripMenuItem startup, ToolStripMenuItem mute, ToolStripMenuItem status)
    {
        _icon = icon;
        _artwork = icon.Icon!;
        _menu = menu;
        _visibilityItem = visibility;
        _themeItem = theme;
        _topmostItem = topmost;
        _opacityItem = opacity;
        _startupItem = startup;
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
            var visibility = new ToolStripMenuItem("Hide HUD");
            visibility.Click += (_, _) => callbacks.ToggleVisibility();
            var theme = new ToolStripMenuItem("Light mode");
            theme.Click += (_, _) => callbacks.ToggleTheme();
            var topmost = new ToolStripMenuItem("Always on top") { CheckOnClick = false };
            topmost.Click += (_, _) => callbacks.ToggleTopmost();
            var opacity = new ToolStripMenuItem("Opacity");
            foreach (var preset in new[] { 0.75, 0.85, 0.96, 1.0 })
            {
                var value = preset;
                var item = new ToolStripMenuItem(((int)Math.Round(value * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%");
                item.Click += (_, _) => callbacks.SetOpacity(value);
                opacity.DropDownItems.Add(item);
            }

            var startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
            startup.Click += (_, _) => callbacks.ToggleStartWithWindows();
            var mute = new ToolStripMenuItem("Mute voice") { CheckOnClick = false };
            mute.Click += (_, _) => callbacks.ToggleMute();
            var reset = new ToolStripMenuItem("Reset position");
            reset.Click += (_, _) => callbacks.ResetPosition();
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (_, _) => callbacks.Exit();
            var status = new ToolStripMenuItem("Monitoring status") { Enabled = false };

            menu.Items.Add(visibility);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(theme);
            menu.Items.Add(topmost);
            menu.Items.Add(opacity);
            menu.Items.Add(mute);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startup);
            menu.Items.Add(reset);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(status);
            menu.Items.Add(exit);

            artwork = AppIcon.CreateTrayIcon();
            icon = new NotifyIcon
            {
                Icon = artwork,
                Text = "OVERSEER",
                ContextMenuStrip = menu,
                Visible = true,
            };
            icon.DoubleClick += (_, _) => callbacks.ToggleVisibility();

            return (new TrayService(icon, menu, visibility, theme, topmost, opacity, startup, mute, status), TrayAvailability.Available);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            icon?.Dispose();
            artwork?.Dispose();
            menu?.Dispose();
            return (null, TrayAvailability.Unavailable);
        }
    }

    public void UpdateState(bool windowVisible, HudTheme theme, bool alwaysOnTop, bool startWithWindows, bool muted, string statusLine)
    {
        if (_disposed)
        {
            return;
        }

        _visibilityItem.Text = windowVisible ? "Hide HUD" : "Show HUD";
        _themeItem.Text = theme == HudTheme.Light ? "Dark mode" : "Light mode";
        _topmostItem.Checked = alwaysOnTop;
        _startupItem.Checked = startWithWindows;
        _muteItem.Checked = muted;
        _statusItem.Text = statusLine.Length <= 120 ? statusLine : statusLine[..120];
        _icon.Text = "OVERSEER — " + (statusLine.Length <= 100 ? statusLine : statusLine[..100]);
    }

    /// <summary>Marshals a tray callback onto the UI thread; never throws to the shell.</summary>
    public static void Post(Action action, Action<Action> dispatcher)
    {
        try
        {
            dispatcher(action);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _artwork.Dispose();
        _menu.Dispose();
    }
}

public sealed record TrayCallbacks(
    Action ToggleVisibility,
    Action ToggleTheme,
    Action ToggleTopmost,
    Action<double> SetOpacity,
    Action ToggleStartWithWindows,
    Action ToggleMute,
    Action ResetPosition,
    Action Exit);
