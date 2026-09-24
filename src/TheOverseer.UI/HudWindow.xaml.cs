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
        _lightResizeSize = Math.Clamp(_lightResizeSize + delta, 6, 128);
        vm.LightSize = (int)Math.Round(_lightResizeSize);
        e.Handled = true;
    }

    public HudWindow()
    {
        InitializeComponent();
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        PreviewKeyDown += OnPreviewKeyDown;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        StateChanged += (_, _) => UpdateMaximizeControl();
    }

    /// <summary>Raised after the user drags the window, so placement can be saved.</summary>
    public event EventHandler? PlacementChangedByUser;
    public event EventHandler? WindowViewRequested;
    public event EventHandler? MaximizeRequested;
    public bool IsWindowView { get; private set; }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        MaximizeRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (ContextMenu is { } menu) { menu.PlacementTarget = MenuButton; menu.IsOpen = true; }
        e.Handled = true;
    }

    public void UpdateMaximizeControl()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = maximized ? "Restore HUD size" : "Maximize HUD";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
    }

    public void SetMaximizeInteraction(bool over, bool pressed)
    {
        MaximizeButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
            pressed && over ? "AccentPrimaryBrush" : over ? "PanelOverlayBrush" : "BackgroundBaseBrush");
    }

    /// <summary>Client-space hit regions for the native chrome adapter.</summary>
    public int ChromeHitTest(Point point)
    {
        // WindowChrome owns the outer resize band. Controls/light never take it.
        if (WindowState != WindowState.Maximized &&
            (point.X < 6 || point.Y < 6 || point.X >= ActualWidth - 6 || point.Y >= ActualHeight - 6)) return 0;
        for (var node = InputHitTest(point) as DependencyObject; node is not null && node != this;
             node = node is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(node))
        {
            if (node == MaximizeButton) return 9; // HTMAXBUTTON: real Windows Snap hover target.
            if (node == HeaderControls || node == StatusLight || node is ButtonBase || node is System.Windows.Controls.Primitives.ScrollBar) return 1;
            if (node == DragRegion || node == ExpandedDragRegion) return 2;
        }
        return 1;
    }

    public void SetWindowView(bool enabled, bool preserveBounds = false)
    {
        IsWindowView = enabled;
        if (!preserveBounds) WindowState = WindowState.Normal;
        MinWidth = 280;
        MinHeight = enabled ? 200 : 0;
        ExpandButton.ToolTip = enabled ? "Collapse to compact HUD" : "Expand task list";
        System.Windows.Automation.AutomationProperties.SetName(ExpandButton, (string)ExpandButton.ToolTip);
        if (enabled && ViewModel is { } vm) vm.IsExpanded = true;
        UpdateResponsiveLayout();
    }

    public void UpdateResponsiveLayout()
    {
        var narrow = ActualWidth - (ViewModel?.LightExtraWidth ?? 0) < 260;
        BrandLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        // In a short native Snap region the complete content remains reachable.
        var shortWindow = IsWindowView && ActualHeight < 500;
        ContentScroll.VerticalScrollBarVisibility = shortWindow ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
        TaskScroll.Height = shortWindow ? 180 : double.NaN;
        SummaryRow.Height = new GridLength(IsWindowView ? 86 : 72);
        var verticalLight = ViewModel?.LightPosition is "left" or "right";
        StatusLight.MaxWidth = IsWindowView && verticalLight ? Math.Max(6, ActualWidth - 270) : double.PositiveInfinity;
        StatusLight.MaxHeight = IsWindowView && !verticalLight ? Math.Max(6, ActualHeight - 220) : double.PositiveInfinity;
    }

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
        if (!IsWindowView && e.Key == Key.Escape && ViewModel is { IsExpanded: true } viewModel)
        {
            viewModel.IsExpanded = false;
            e.Handled = true;
        }
    }

    private void OnToggleExpandedClick(object sender, RoutedEventArgs e)
    {
        if (WindowViewRequested is not null)
        {
            WindowViewRequested.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
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
            // The entire controls area, including gaps and the health indicator,
            // is reserved for interaction rather than moving the window.
            if (node == HeaderControls) return;
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
