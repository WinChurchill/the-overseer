using System.ComponentModel;
using System.Runtime.CompilerServices;
using TheOverseer.Core.Voice;

namespace TheOverseer.UI.ViewModels;

public sealed class VoiceSetupRow(VoiceEventDefinition definition, bool enabled) : INotifyPropertyChanged
{
    private bool _enabled = enabled;
    private string _status = "Checking recording…";
    public VoiceEventDefinition Definition { get; } = definition;
    public bool Enabled { get => _enabled; set { if (_enabled == value) return; _enabled = value; Changed(); } }
    public string Status { get => _status; set { _status = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
