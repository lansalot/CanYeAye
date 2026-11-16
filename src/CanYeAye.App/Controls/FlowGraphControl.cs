using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using CanYeAye.App.ViewModels;

namespace CanYeAye.App.Controls;

/// <summary>
/// Flow-view byte graph (re/flowviewwindow): every enabled payload byte plotted over the
/// frame-number or timestamp axis, windowed around the current frame. The window scrolls as
/// the flow is stepped and zooms with the chart-range slider.
/// </summary>
public sealed class FlowGraphControl : Control
{
    public static readonly StyledProperty<FlowGraphModel?> ModelProperty =
        AvaloniaProperty.Register<FlowGraphControl, FlowGraphModel?>(nameof(Model));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<FlowGraphControl>();

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<FlowGraphControl>();

    private const double LeftAxisWidth = 34;
    private const double BottomAxisHeight = 20;
    private const double TopPadding = 10;
    private const double RightPadding = 12;

    private static readonly double[] ValueGrid = [0, 64, 128, 192, 255];

    private static readonly Typeface DefaultTypeface = new(FontFamily.Default);
    private static readonly IPen GridPen =
        new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0x80, 0x80, 0x80)), 1);
    private static readonly IPen AxisPen =
        new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)), 1);
    private static readonly IPen MarkerPen =
        new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0x4F, 0xC3, 0xF7)), 1, new DashStyle([3, 3], 0));

    static FlowGraphControl()
    {
        AffectsRender<FlowGraphControl>(ModelProperty, ForegroundProperty, BackgroundProperty);
    }

    public FlowGraphModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 220 : availableSize.Height;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
        {
            context.FillRectangle(background, bounds);
        }

        var foreground = Foreground ?? Brushes.Gainsboro;
        var model = Model;
        var plot = new Rect(
            LeftAxisWidth,
            TopPadding,
            Math.Max(1, bounds.Width - LeftAxisWidth - RightPadding),
            Math.Max(1, bounds.Height - TopPadding - BottomAxisHeight));

        if (model is null || !model.HasFrames)
        {
            DrawText(context, "No frames", plot, 12, foreground, TextAlignment.Center, true);
            return;
        }

        var useTime = model.UseTime && model.Times.Count > model.End;
        double X(int index) => useTime && index < model.Times.Count ? model.Times[index] : index;

        var xMin = X(model.Start);
        var xMax = X(model.End);
        if (xMax <= xMin)
        {
            xMax = xMin + (useTime ? 0.001 : 1);
        }

        const double yMin = 0;
        const double yMax = 255;
        double ToPixelX(double x) => plot.X + (x - xMin) / (xMax - xMin) * plot.Width;
        double ToPixelY(double y) => plot.Bottom - (y - yMin) / (yMax - yMin) * plot.Height;

        // Horizontal value gridlines and labels.
        foreach (var value in ValueGrid)
        {
            var y = ToPixelY(value);
            context.DrawLine(GridPen, new Point(plot.X, y), new Point(plot.Right, y));
            DrawText(
                context,
                ((int)value).ToString(CultureInfo.InvariantCulture),
                new Rect(0, y - 8, LeftAxisWidth - 4, 16),
                9,
                foreground,
                TextAlignment.Right,
                true);
        }

        // Vertical time/frame gridlines.
        const int xTicks = 4;
        for (var tick = 0; tick <= xTicks; tick++)
        {
            var value = xMin + (xMax - xMin) * tick / xTicks;
            var x = ToPixelX(value);
            context.DrawLine(GridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
            DrawText(
                context,
                FormatAxisValue(value, useTime, xMax - xMin),
                new Rect(x - 40, plot.Bottom + 3, 80, 16),
                9,
                foreground,
                TextAlignment.Center,
                false);
        }

        context.DrawLine(AxisPen, new Point(plot.X, plot.Y), new Point(plot.X, plot.Bottom));
        context.DrawLine(AxisPen, new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));

        if (model.Series.Count == 0)
        {
            DrawText(
                context,
                "No graphs selected - tick Graph? for a byte",
                plot,
                12,
                foreground,
                TextAlignment.Center,
                true);
            return;
        }

        // One polyline per enabled byte.
        foreach (var series in model.Series)
        {
            var values = series.Values;
            if (values.Count <= model.Start)
            {
                continue;
            }

            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                var started = false;
                var end = Math.Min(model.End, values.Count - 1);
                var step = Math.Max(1, (end - model.Start) / Math.Max(1, (int)plot.Width * 2));
                for (var i = model.Start; i <= end; i += step)
                {
                    var point = new Point(ToPixelX(X(i)), ToPixelY(values[i]));
                    if (!started)
                    {
                        path.BeginFigure(point, isFilled: false);
                        started = true;
                    }
                    else
                    {
                        path.LineTo(point);
                    }
                }

                if (!started)
                {
                    continue;
                }

                // Always include the final sample so the line reaches the window edge.
                if (end > model.Start)
                {
                    path.LineTo(new Point(ToPixelX(X(end)), ToPixelY(values[end])));
                }
            }

            context.DrawGeometry(null, new Pen(series.Brush, 1.5), geometry);
        }

        // Current frame marker with a dot per byte value.
        var currentX = ToPixelX(X(model.Current));
        if (currentX >= plot.X - 1 && currentX <= plot.Right + 1)
        {
            context.DrawLine(MarkerPen, new Point(currentX, plot.Y), new Point(currentX, plot.Bottom));

            var plusPen = new Pen(MarkerPen.Brush, 2);
            context.DrawLine(plusPen, new Point(currentX - 4, plot.Y + 4), new Point(currentX + 4, plot.Y + 4));
            context.DrawLine(plusPen, new Point(currentX, plot.Y), new Point(currentX, plot.Y + 8));

            foreach (var series in model.Series)
            {
                if (model.Current >= 0 && model.Current < series.Values.Count)
                {
                    var y = ToPixelY(series.Values[model.Current]);
                    context.DrawEllipse(series.Brush, null, new Point(currentX, y), 3, 3);
                }
            }
        }
    }

    private static string FormatAxisValue(double value, bool useTime, double span)
    {
        if (!useTime)
        {
            return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        }

        var decimals = span < 0.01 ? 4 : span < 1 ? 3 : 2;
        return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        Rect rect,
        double fontSize,
        IBrush brush,
        TextAlignment alignment,
        bool centerVertically)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            DefaultTypeface,
            fontSize,
            brush);
        var x = alignment switch
        {
            TextAlignment.Right => rect.Right - formatted.Width,
            TextAlignment.Center => rect.X + (rect.Width - formatted.Width) / 2,
            _ => rect.X,
        };
        var y = centerVertically ? rect.Y + (rect.Height - formatted.Height) / 2 : rect.Y;
        context.DrawText(formatted, new Point(x, y));
    }
}
