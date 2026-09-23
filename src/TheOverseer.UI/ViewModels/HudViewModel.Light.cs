using System.Windows.Controls;
using TheOverseer.Core.Configuration;

namespace TheOverseer.UI.ViewModels;

public sealed partial class HudViewModel
{
    private string _lightPosition = "right";
    private int _lightSize = 32;
    public string LightPosition
    {
        get => _lightPosition;
        set { if (Set(ref _lightPosition, HudSettings.NormalizeLightPosition(value))) LightChanged(); }
    }
    public int LightSize
    {
        get => _lightSize;
        set { if (Set(ref _lightSize, Math.Clamp(value, 6, 64))) LightChanged(); }
    }
    public bool LightVisible => LightPosition != "off";
    public Dock LightDock => LightPosition switch { "bottom" => Dock.Bottom, "left" => Dock.Left, "right" => Dock.Right, _ => Dock.Top };
    public double LightWidth => LightDock is Dock.Left or Dock.Right ? LightSize : double.NaN;
    public double LightHeight => LightDock is Dock.Top or Dock.Bottom ? LightSize : double.NaN;
    public System.Windows.Input.Cursor LightResizeCursor => LightDock is Dock.Left or Dock.Right
        ? System.Windows.Input.Cursors.SizeWE : System.Windows.Input.Cursors.SizeNS;
    public double LightExtraWidth => LightVisible && LightDock is Dock.Left or Dock.Right ? LightSize : 0;
    public double LightExtraHeight => LightVisible && LightDock is Dock.Top or Dock.Bottom ? LightSize : 0;
    private void LightChanged()
    {
        foreach (var name in new[] { nameof(LightVisible), nameof(LightDock), nameof(LightWidth), nameof(LightHeight), nameof(LightExtraWidth), nameof(LightExtraHeight), nameof(LightResizeCursor) }) Raise(name);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
