using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

public sealed partial class HudViewModel
{
    private IReadOnlyList<FocusPin> _focusPins = Array.Empty<FocusPin>();
    public IReadOnlyList<FocusPin> FocusPins => _focusPins;
    public bool HasFocusPins => _focusPins.Count > 0;

    public void SetFocusPins(IEnumerable<FocusPin> pins)
    {
        _focusPins = FocusPinContract.Normalize(pins);
        Raise(nameof(FocusPins));
        Raise(nameof(HasFocusPins));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
