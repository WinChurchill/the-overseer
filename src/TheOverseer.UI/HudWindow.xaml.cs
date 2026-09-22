using System.Windows;
using System.Windows.Input;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.UI;

/// <summary>
/// The single HUD window. It owns no monitor, no settings file and no platform
/// service: the App controller composes those and drives this window.
/// </summary>
public partial class HudWindow : Window
{
    private readonly DragGestureTracker _drag = new();

    public HudWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Raised after the user drags the window, so placement can be saved.</summary>
    public event EventHandler? PlacementChangedByUser;

    public HudViewModel? ViewModel => DataContext as HudViewModel;

    /// <summary>Applies the collapsed or expanded geometry for this window.</summary>
    public void ApplyGeometry(double width, double height, double left, double top)
    {
        Width = width;
        Height = height;
        Left = left;
        Top = top;
    }

    /// <summary>Updates the health marker brush without touching layout.</summary>
    public void SetHealthBrush(System.Windows.Media.Brush brush) => HealthDot.Fill = brush;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel is { IsExpanded: true } viewModel)
        {
            viewModel.IsExpanded = false;
            e.Handled = true;
        }
    }

    private void OnToggleExpandedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.IsExpanded = !viewModel.IsExpanded;
            e.Handled = true;
        }
    }

    private void OnDragRegionMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var position = e.GetPosition(this);
        _drag.Begin(position.X, position.Y);
    }

    private void OnDragRegionMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_drag.IsPressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (!_drag.Move(position.X, position.Y))
        {
            return;
        }

        try
        {
            DragMove();
            PlacementChangedByUser?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException)
        {
            // DragMove can reject a gesture that ended before the call; the
            // window simply stays where it is.
            _drag.Cancel();
        }
    }

    private void OnDragRegionMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        // A click inside the drag region intentionally does nothing: dragging
        // must never also toggle the list or open a task.
        _ = _drag.End();
    }
}
