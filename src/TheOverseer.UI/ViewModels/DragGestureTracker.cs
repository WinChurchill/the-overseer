namespace TheOverseer.UI.ViewModels;

/// <summary>
/// Distinguishes a click from a drag on the HUD header. Pure logic so the
/// threshold behaviour is unit-testable without a real window.
/// </summary>
public sealed class DragGestureTracker
{
    public const double DefaultThreshold = 4.0;

    private double _originX;
    private double _originY;
    private bool _pressed;
    private bool _dragging;

    public bool IsDragging => _dragging;

    public bool IsPressed => _pressed;

    public void Begin(double x, double y)
    {
        _originX = x;
        _originY = y;
        _pressed = true;
        _dragging = false;
    }

    /// <summary>Returns true when this movement crossed the drag threshold.</summary>
    public bool Move(double x, double y, double threshold = DefaultThreshold)
    {
        if (!_pressed)
        {
            return false;
        }

        if (!_dragging)
        {
            var dx = Math.Abs(x - _originX);
            var dy = Math.Abs(y - _originY);
            if (dx >= threshold || dy >= threshold)
            {
                _dragging = true;
            }
        }

        return _dragging;
    }

    /// <summary>Returns true when the gesture was a click, not a drag.</summary>
    public bool End()
    {
        var wasClick = _pressed && !_dragging;
        _pressed = false;
        _dragging = false;
        return wasClick;
    }

    public void Cancel()
    {
        _pressed = false;
        _dragging = false;
    }
}
