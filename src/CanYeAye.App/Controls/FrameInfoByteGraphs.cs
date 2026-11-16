using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using CanYeAye.App.ViewModels;

namespace CanYeAye.App.Controls;

/// <summary>
/// The eight per-byte value graphs at the bottom of the Detailed Frame Information window: a
/// 2x4 grid, one mini chart per payload byte, each plotting the byte value against the frame
/// index of the selected ID.
/// </summary>
public sealed class FrameInfoByteGraphs : Control
{
    public static readonly StyledProperty<FrameInfoByteGraphsModel?> ModelProperty =
        AvaloniaProperty.Register<FrameInfoByteGraphs, FrameInfoByteGraphsModel?>(nameof(Model));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<FrameInfoByteGraphs>();

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<FrameInfoByteGraphs>();

    private const int Columns = 4;
    private const int Rows = 2;
    private const double CellGap = 6;

    private static readonly Typeface DefaultTypeface = new(FontFamily.Default);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x1E, 0x80, 0x80, 0x80)), 1);
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.FromArgb(0x44, 0x80, 0x80, 0x80)), 1);

    static FrameInfoByteGraphs()
    {
        AffectsRender<FrameInfoByteGraphs>(ModelProperty, ForegroundProperty, BackgroundProperty);
    }

    public FrameInfoByteGraphsModel? Model
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
        var width = double.IsInfinity(availableSize.Width) ? 560 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height;
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
        if (model is null || model.Series.Count == 0)
        {
            DrawText(context, "No byte graphs", bounds, 12, foreground, TextAlignment.Center, true);
            return;
        }

        var cellWidth = (bounds.Width - (CellGap * (Columns - 1))) / Columns;
        var cellHeight = (bounds.Height - (CellGap * (Rows - 1))) / Rows;
        if (cellWidth < 30 || cellHeight < 30)
        {
            return;
        }

        for (var index = 0; index < model.Series.Count && index < Columns * Rows; index++)
        {
            var column = index % Columns;
            var row = index / Columns;
            var cell = new Rect(
                column * (cellWidth + CellGap),
                row * (cellHeight + CellGap),
                cellWidth,
                cellHeight);

            DrawGraph(context, model, index, cell, foreground);
        }
    }

    private static void DrawGraph(
        DrawingContext context,
        FrameInfoByteGraphsModel model,
        int byteIndex,
        Rect cell,
        IBrush foreground)
    {
        context.DrawRectangle(null, FramePen, cell, 3, 3);

        var title = new Rect(cell.X + 6, cell.Y + 2, cell.Width - 12, 12);
        DrawText(context, $"Byte {byteIndex}", title, 10, foreground, TextAlignment.Left, false);

        var plot = new Rect(
            cell.X + 24,
            cell.Y + 24,
            Math.Max(1, cell.Width - 30),
            Math.Max(1, cell.Height - 42));

        context.DrawLine(GridPen, new Point(plot.X, plot.Y), new Point(plot.Right, plot.Y));
        context.DrawLine(GridPen, new Point(plot.X, plot.Y + (plot.Height / 2)), new Point(plot.Right, plot.Y + (plot.Height / 2)));
        context.DrawLine(GridPen, new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));
        context.DrawLine(FramePen, new Point(plot.X, plot.Y), new Point(plot.X, plot.Bottom));

        DrawText(context, "255", new Rect(cell.X, plot.Y - 6, 21, 12), 8, foreground, TextAlignment.Right, false);
        DrawText(context, "128", new Rect(cell.X, plot.Y + (plot.Height / 2) - 6, 21, 12), 8, foreground, TextAlignment.Right, false);
        DrawText(context, "0", new Rect(cell.X, plot.Bottom - 6, 21, 12), 8, foreground, TextAlignment.Right, false);

        var values = model.Series[byteIndex];
        if (values.Count == 0)
        {
            return;
        }

        var brush = model.Brushes.Count > 0
            ? model.Brushes[byteIndex % model.Brushes.Count]
            : foreground;

        var frameCount = Math.Max(values.Count, model.FrameCount);
        var xSpan = Math.Max(1, frameCount - 1);
        double ToPixelX(int frame) => plot.X + ((frame / (double)xSpan) * plot.Width);
        double ToPixelY(double value) => plot.Bottom - (Math.Clamp(value, 0, 255) / 255.0 * plot.Height);

        var step = Math.Max(1, values.Count / Math.Max(1, (int)(plot.Width * 2)));
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            var started = false;
            for (var frame = 0; frame < values.Count; frame += step)
            {
                var point = new Point(ToPixelX(frame), ToPixelY(values[frame]));
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

            if (started && (values.Count - 1) % step != 0)
            {
                path.LineTo(new Point(ToPixelX(values.Count - 1), ToPixelY(values[^1])));
            }
        }

        context.DrawGeometry(null, new Pen(brush, 1.4), geometry);

        DrawText(
            context,
            "0",
            new Rect(plot.X, plot.Bottom + 1, 20, 12),
            8,
            foreground,
            TextAlignment.Left,
            false);
        DrawText(
            context,
            (frameCount - 1).ToString(CultureInfo.InvariantCulture),
            new Rect(plot.Right - 60, plot.Bottom + 1, 60, 12),
            8,
            foreground,
            TextAlignment.Right,
            false);
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
            TextAlignment.Center => rect.X + ((rect.Width - formatted.Width) / 2),
            _ => rect.X,
        };
        var y = centerVertically ? rect.Y + ((rect.Height - formatted.Height) / 2) : rect.Y;
        context.DrawText(formatted, new Point(x, y));
    }
}
