using System.Windows.Controls;
using TheOverseer.Core.Configuration;

namespace TheOverseer.UI.ViewModels;

public sealed partial class HudViewModel
{
    private string _lightPosition = "right";
    private int _lightSize = 32;
    private string _lastLightPosition = "right";
    private System.Windows.Input.ICommand? _toggleLightCommand;
    public System.Windows.Input.ICommand ToggleLightCommand => _toggleLightCommand ??= new RelayCommand(() =>
    {
        if (LightVisible) { _lastLightPosition = LightPosition; LightPosition = "off"; }
        else LightPosition = _lastLightPosition;
    });
    public string LightToggleText => LightVisible ? "LIGHT ON" : "LIGHT OFF";
    public string LightToggleTooltip => LightVisible ? "Hide status light" : "Show status light";
    public string LightPosition
    {
        get => _lightPosition;
        set { if (Set(ref _lightPosition, HudSettings.NormalizeLightPosition(value))) LightChanged(); }
    }
    public int LightSize
    {
        get => _lightSize;
        set { if (Set(ref _lightSize, Math.Clamp(value, 6, 128))) LightChanged(); }
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
        foreach (var name in new[] { nameof(LightVisible), nameof(LightDock), nameof(LightWidth), nameof(LightHeight), nameof(LightExtraWidth), nameof(LightExtraHeight), nameof(LightResizeCursor), nameof(LightToggleText), nameof(LightToggleTooltip) }) Raise(name);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
