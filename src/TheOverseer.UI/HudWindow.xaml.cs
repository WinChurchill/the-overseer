using System.Windows;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.UI;

/// <summary>
/// The single HUD window. It owns no monitor, no settings file and no platform
/// service: the App controller composes those and drives this window.
/// </summary>
public partial class HudWindow : Window
{
    private readonly DragGestureTracker _drag = new();
    private bool _statusClick;
    private double _lightResizeSize;

    private void OnLightResizeStarted(object sender, DragStartedEventArgs e) => _lightResizeSize = ViewModel?.LightSize ?? 32;

    private void OnLightResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var delta = vm.LightPosition switch
        {
            "left" => -e.HorizontalChange, "right" => e.HorizontalChange,
            "top" => -e.VerticalChange, "bottom" => e.VerticalChange, _ => 0,
        };
        _lightResizeSize = Math.Clamp(_lightResizeSize + delta, 6, 64);
        vm.LightSize = (int)Math.Round(_lightResizeSize);
        e.Handled = true;
    }

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

    private void OnCopyTaskIdClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { DataContext: TaskRowViewModel row })
        {
            ViewModel?.CopyTaskIdCommand.Execute(row);
            e.Handled = true;
        }
    }

    private void OnDragRegionMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _statusClick = false;
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != sender;
             node = node is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase button)
            {
                if (button != StatusButton) return;
                _statusClick = true;
                break;
            }
        }
        var position = e.GetPosition(this);
        _drag.Begin(position.X, position.Y);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
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
            Mouse.Capture(null);
            DragMove();
            PlacementChangedByUser?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException)
        {
            // DragMove can reject a gesture that ended before the call; the
            // window simply stays where it is.
            _drag.Cancel();
        }
        finally
        {
            _drag.Cancel();
            _statusClick = false;
        }
    }

    private void OnDragRegionMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (!_drag.IsPressed) return;
        var clicked = _drag.End();
        Mouse.Capture(null);
        if (clicked && _statusClick) ViewModel?.ShowCurrentTasksCommand.Execute(null);
        _statusClick = false;
        e.Handled = true;
    }
}
