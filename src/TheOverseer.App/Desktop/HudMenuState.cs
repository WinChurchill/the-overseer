using System.ComponentModel;

namespace TheOverseer.App.Desktop;

/// <summary>The controller's actual state, shared by the header and tray menus.</summary>
public sealed record HudMenuSnapshot(
    bool WindowVisible,
    bool AlwaysOnTop,
    bool StartWithWindows,
    bool Muted,
    bool CanHideToTray);

public sealed class HudMenuState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public HudMenuSnapshot Snapshot { get; private set; } = new(false, false, false, true, false);

    public void Update(HudMenuSnapshot snapshot)
    {
        if (Snapshot == snapshot) return;
        Snapshot = snapshot;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Snapshot)));
    }
}
