using System.Windows;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.UI;

/// <summary>
/// Bounded project/source settings surface. It owns no discovery, no file access and
/// no persistence: the App controller supplies the rows and saves the change through
/// the existing settings path.
/// </summary>
public partial class ProjectSourcesWindow : Window
{
    public ProjectSourcesWindow(ProjectSourcesViewModel model, bool claudeProjectsEnabled = false)
    {
        InitializeComponent();
        DataContext = model;
        ViewModel = model;
        ClaudeProjectsBox.IsChecked = claudeProjectsEnabled;
    }

    public ProjectSourcesViewModel ViewModel { get; }
    public event Action<bool>? ClaudeProjectsChanged;

    private void OnClaudeProjects(object sender, RoutedEventArgs e) =>
        ClaudeProjectsChanged?.Invoke(ClaudeProjectsBox.IsChecked == true);

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
        e.Handled = true;
    }
}
