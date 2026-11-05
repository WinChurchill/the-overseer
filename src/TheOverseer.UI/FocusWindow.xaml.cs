using System.Windows;
using TheOverseer.Core.Models;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.UI;

/// <summary>Edits user intent only. App supplies persistence and clipboard access.</summary>
public partial class FocusWindow : Window
{
    private readonly HudViewModel _hud;
    private readonly Func<DateTimeOffset> _now;
    private IReadOnlyList<FocusProposal> _suggestions = Array.Empty<FocusProposal>();
    private int _index;
    public event Action? CopyPromptRequested;

    public FocusWindow(HudViewModel hud, Func<DateTimeOffset> now)
    {
        InitializeComponent();
        _hud = hud;
        _now = now;
        DataContext = hud;
    }

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        var result = FocusPinContract.Parse(Import.Text);
        Status.Text = result.Message;
        _suggestions = result.Accepted ? result.Items : Array.Empty<FocusProposal>();
        _index = 0;
        if (_suggestions.Count > 0) ShowSuggestion();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_hud.FocusPins.Count >= FocusPinContract.MaximumPins)
        {
            Status.Text = "Three pins are already saved. Remove one before adding another.";
            return;
        }
        var pin = new FocusPin(Guid.NewGuid().ToString("N"), Project.Text.Trim(), Objective.Text.Trim(),
            NextStep.Text.Trim(), Reason.Text.Trim(), _now());
        var valid = FocusPinContract.Normalize(new[] { pin });
        if (valid.Count == 0) { Status.Text = "Enter a short objective; keep each field on one line."; return; }
        _hud.SetFocusPins(_hud.FocusPins.Append(pin));
        Status.Text = "Focus pin saved.";
        Project.Clear(); Objective.Clear(); NextStep.Clear(); Reason.Clear();
        SuggestionNote.Text = string.Empty;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FocusPin pin })
            _hud.SetFocusPins(_hud.FocusPins.Where(item => item.Id != pin.Id));
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_suggestions.Count == 0) return;
        _index = (_index + 1) % _suggestions.Count;
        ShowSuggestion();
    }

    private void ShowSuggestion()
    {
        var item = _suggestions[_index];
        Project.Text = item.Project; Objective.Text = item.Objective;
        NextStep.Text = item.NextStep; Reason.Text = item.Reason;
        SuggestionNote.Text = $"Suggestion {_index + 1} of {_suggestions.Count}. " +
            (item.Uncertain ? "The agent marked this uncertain. Review before confirming." : "Review before confirming.");
    }

    private void OnCopyPrompt(object sender, RoutedEventArgs e) => CopyPromptRequested?.Invoke();
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
