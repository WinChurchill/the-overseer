using System.Windows;
using System.Windows.Controls;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.UI;

/// <summary>Presentation only; App handles files, settings and playback.</summary>
public partial class NotificationSettingsWindow : Window
{
    private bool _suppressProviderChange;

    public NotificationSettingsWindow(HudViewModel hud, IReadOnlyList<VoiceSetupRow> rows)
    {
        InitializeComponent();
        DataContext = hud;
        Rows.ItemsSource = rows;
        // Show a provider before App supplies the stored one; this must not look like
        // a user change.
        _suppressProviderChange = true;
        try
        {
            ProviderBox.SelectedIndex = 0;
        }
        finally
        {
            _suppressProviderChange = false;
        }
    }

    public event Action<VoiceSetupRow>? AssignRequested;

    public event Action<VoiceSetupRow>? PreviewRequested;

    public event Action<VoiceSetupRow>? ClearRequested;

    public event Action? StopRequested;

    /// <summary>Raised with "pack", "speech" or "tts" when the user picks a provider.</summary>
    public event Action<string>? ProviderChangeRequested;

    /// <summary>Raised by the explicit Use baseline control.</summary>
    public event Action? BaselineRequested;

    /// <summary>Shows the stored provider without raising a change request.</summary>
    public void ShowProvider(string providerName)
    {
        _suppressProviderChange = true;
        try
        {
            foreach (var item in ProviderBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, providerName, StringComparison.OrdinalIgnoreCase))
                {
                    ProviderBox.SelectedItem = item;
                    return;
                }
            }

            ProviderBox.SelectedIndex = 0;
        }
        finally
        {
            _suppressProviderChange = false;
        }
    }

    /// <summary>Shows status text prepared by App; this window performs no probing itself.</summary>
    public void ShowStatus(string baselineStatus) => BaselineStatusText.Text = baselineStatus;

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProviderChange) return;
        if (ProviderBox.SelectedItem is ComboBoxItem { Tag: string provider }) ProviderChangeRequested?.Invoke(provider);
    }

    private void OnUseBaseline(object sender, RoutedEventArgs e) => BaselineRequested?.Invoke();
    private void OnAssign(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is VoiceSetupRow row) AssignRequested?.Invoke(row); }
    private void OnPreview(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is VoiceSetupRow row) PreviewRequested?.Invoke(row); }
    private void OnClear(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is VoiceSetupRow row) ClearRequested?.Invoke(row); }
    private void OnStop(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
