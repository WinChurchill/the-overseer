using System.Windows;
using System.Windows.Media;
using TheOverseer.Core.History;

namespace TheOverseer.UI.Controls;

/// <summary>
/// Tiny dependency-free sparkline for the shared HUD layout. It draws the
/// pre-computed geometry from <see cref="ActivityChartGeometry"/>: a zero
/// baseline, one working-count line, a distinct dashed style for partial lower
/// bounds, and no line across unobserved gaps.
/// </summary>
public sealed class ActivitySparkline : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(IReadOnlyList<ActivityChartSegment>),
        typeof(ActivitySparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush),
        typeof(Brush),
        typeof(ActivitySparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PartialBrushProperty = DependencyProperty.Register(
        nameof(PartialBrush),
        typeof(Brush),
        typeof(ActivitySparkline),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineBrushProperty = DependencyProperty.Register(
        nameof(BaselineBrush),
        typeof(Brush),
        typeof(ActivitySparkline),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    private Pen? _linePen;
    private Pen? _partialPen;
    private Pen? _baselinePen;

    public IReadOnlyList<ActivityChartSegment>? Segments
    {
        get => (IReadOnlyList<ActivityChartSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush PartialBrush
    {
        get => (Brush)GetValue(PartialBrushProperty);
        set => SetValue(PartialBrushProperty, value);
    }

    public Brush BaselineBrush
    {
        get => (Brush)GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    /// <summary>Number of drawn segments; used by tests to prove bounded geometry.</summary>
    public int DrawnSegmentCount { get; private set; }

    /// <summary>Number of drawn points; used by tests to prove bounded geometry.</summary>
    public int DrawnPointCount { get; private set; }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        DrawnSegmentCount = 0;
        DrawnPointCount = 0;

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        context.DrawLine(BaselinePen(), new Point(0, height - 0.5), new Point(width, height - 0.5));

        var segments = Segments;
        if (segments is null || segments.Count == 0)
        {
            return;
        }

        foreach (var segment in segments)
        {
            if (segment.Points.Count == 0)
            {
                continue;
            }

            var pen = segment.Kind == ActivitySegmentKind.Partial ? PartialPen() : LinePen();
            if (segment.Points.Count == 1)
            {
                var point = segment.Points[0];
                context.DrawEllipse(pen.Brush, null, new Point(point.X, point.Y), 1.3, 1.3);
                DrawnPointCount++;
                DrawnSegmentCount++;
                continue;
            }

            var geometry = new StreamGeometry();
            using (var stream = geometry.Open())
            {
                stream.BeginFigure(new Point(segment.Points[0].X, segment.Points[0].Y), isFilled: false, isClosed: false);
                for (var index = 1; index < segment.Points.Count; index++)
                {
                    stream.LineTo(new Point(segment.Points[index].X, segment.Points[index].Y), isStroked: true, isSmoothJoin: true);
                }
            }

            geometry.Freeze();
            context.DrawGeometry(null, pen, geometry);
            DrawnSegmentCount++;
            DrawnPointCount += segment.Points.Count;
        }
    }

    private Pen LinePen() => _linePen ??= CreatePen(LineBrush, 1.4, null);

    private Pen PartialPen() => _partialPen ??= CreatePen(PartialBrush, 1.2, new DoubleCollection { 2, 2 });

    private Pen BaselinePen() => _baselinePen ??= CreatePen(BaselineBrush, 1, null, 0.6);

    private static Pen CreatePen(Brush brush, double thickness, DoubleCollection? dashes, double opacity = 1.0)
    {
        var pen = new Pen(brush, thickness);
        if (dashes is not null)
        {
            pen.DashStyle = new DashStyle(dashes, 0);
            pen.DashCap = PenLineCap.Flat;
        }

        if (opacity < 1.0)
        {
            pen.Brush = brush.Clone();
            pen.Brush.Opacity = opacity;
            pen.Brush.Freeze();
        }

        pen.Freeze();
        return pen;
    }
}
