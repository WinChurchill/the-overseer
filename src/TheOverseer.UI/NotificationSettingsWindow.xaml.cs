using System.Windows;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.UI;

/// <summary>Presentation only; App handles files, settings and playback.</summary>
public partial class NotificationSettingsWindow : Window
{
    public NotificationSettingsWindow(HudViewModel hud, IReadOnlyList<VoiceSetupRow> rows)
    {
        InitializeComponent();
        DataContext = hud;
        Rows.ItemsSource = rows;
    }
    public event Action<VoiceSetupRow>? AssignRequested;
    public event Action<VoiceSetupRow>? PreviewRequested;
    public event Action<VoiceSetupRow>? ClearRequested;
    public event Action? StopRequested;
    private void OnAssign(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is VoiceSetupRow row) AssignRequested?.Invoke(row); }
    private void OnPreview(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is VoiceSetupRow row) PreviewRequested?.Invoke(row); }
    private void OnClear(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is VoiceSetupRow row) ClearRequested?.Invoke(row); }
    private void OnStop(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
