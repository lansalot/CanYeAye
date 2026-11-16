using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using WinCAN.App.ViewModels;

namespace WinCAN.App.Controls;

/// <summary>
/// Bit-change heatmap (re/frameinfowindow): one cell per payload bit, arranged as eight rows
/// (bytes) by eight columns (bit 7 on the left). The colour and the small percentage show how
/// often the bit changed compared with the previous frame.
/// </summary>
public sealed class FrameInfoHeatmap : Control
{
    public static readonly StyledProperty<FrameInfoHeatmapModel?> ModelProperty =
        AvaloniaProperty.Register<FrameInfoHeatmap, FrameInfoHeatmapModel?>(nameof(Model));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<FrameInfoHeatmap>();

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<FrameInfoHeatmap>();

    private const int Rows = 8;
    private const int Columns = 8;
    private const double HeaderHeight = 18;
    private const double RowLabelWidth = 24;

    private static readonly Typeface DefaultTypeface = new(FontFamily.Default);

    private static readonly (double Position, Color Color)[] HeatStops =
    [
        (0.00, Color.FromRgb(0x22, 0x26, 0x33)),
        (0.15, Color.FromRgb(0x2E, 0x4A, 0x8F)),
        (0.40, Color.FromRgb(0x7B, 0x3F, 0xA5)),
        (0.70, Color.FromRgb(0xD9, 0x6B, 0x22)),
        (1.00, Color.FromRgb(0xFF, 0xD2, 0x4A)),
    ];

    private static readonly IBrush MissingCellBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x24));
    private static readonly IPen MissingCellPen = new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x30, 0x39)), 1);
    private static readonly IPen CellPen = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)), 1);
    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x9A, 0xA0, 0xAC));

    static FrameInfoHeatmap()
    {
        AffectsRender<FrameInfoHeatmap>(ModelProperty, ForegroundProperty, BackgroundProperty);
    }

    public FrameInfoHeatmapModel? Model
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
        var width = double.IsInfinity(availableSize.Width) ? 420 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height;
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
        if (model is null || bounds.Width < 40 || bounds.Height < 40)
        {
            DrawText(context, "Select a frame ID", bounds, 12, foreground, TextAlignment.Center, true);
            return;
        }

        var cellWidth = Math.Max(1, (bounds.Width - RowLabelWidth) / Columns);
        var cellHeight = Math.Max(1, (bounds.Height - HeaderHeight) / Rows);
        var percentFontSize = Math.Clamp(Math.Min(cellWidth, cellHeight) * 0.22, 7, 10);

        // Column headers: bit 7 on the left, bit 0 on the right.
        for (var column = 0; column < Columns; column++)
        {
            var bit = Columns - 1 - column;
            var header = new Rect(
                RowLabelWidth + (column * cellWidth),
                0,
                cellWidth,
                HeaderHeight);
            DrawText(
                context,
                bit.ToString(CultureInfo.InvariantCulture),
                header,
                percentFontSize,
                foreground,
                TextAlignment.Center,
                true);
        }

        for (var row = 0; row < Rows; row++)
        {
            var rowTop = HeaderHeight + (row * cellHeight);
            var label = new Rect(0, rowTop, RowLabelWidth - 4, cellHeight);
            DrawText(
                context,
                row.ToString(CultureInfo.InvariantCulture),
                label,
                percentFontSize,
                foreground,
                TextAlignment.Right,
                true);

            var hasRow = row < model.ByteCount;
            for (var column = 0; column < Columns; column++)
            {
                var bit = Columns - 1 - column;
                var cell = new Rect(
                    RowLabelWidth + (column * cellWidth),
                    rowTop,
                    cellWidth,
                    cellHeight).Deflate(1.5);

                if (!hasRow)
                {
                    context.DrawRectangle(MissingCellBrush, MissingCellPen, cell, 3, 3);
                    continue;
                }

                var rate = model.Rates[(row * 8) + bit];
                var fill = new SolidColorBrush(HeatColour(rate));
                context.DrawRectangle(fill, CellPen, cell, 3, 3);

                var textBrush = IsDark(fill.Color) ? Brushes.White : Brushes.Black;

                // Bit index in the top-left corner, change rate below it, so the two never collide.
                var indexFont = Math.Clamp(cell.Height * 0.20, 7, 10);
                var indexRect = new Rect(cell.X + 5, cell.Y + 2, cell.Width - 10, indexFont + 3);
                DrawText(
                    context,
                    ((row * 8) + bit).ToString(CultureInfo.InvariantCulture),
                    indexRect,
                    indexFont,
                    textBrush,
                    TextAlignment.Left,
                    false);

                if (cell.Height >= 18)
                {
                    var percentRect = new Rect(
                        cell.X + 2,
                        indexRect.Bottom,
                        cell.Width - 6,
                        Math.Max(4, cell.Bottom - indexRect.Bottom - 2));
                    DrawText(
                        context,
                        (rate * 100.0).ToString("0.#", CultureInfo.InvariantCulture) + "%",
                        percentRect,
                        Math.Clamp(cell.Height * 0.32, 8, 15),
                        textBrush,
                        TextAlignment.Right,
                        true);
                }
            }
        }

        if (model.ByteCount == 0)
        {
            DrawText(
                context,
                "No payload bytes",
                new Rect(0, HeaderHeight, bounds.Width, bounds.Height - HeaderHeight),
                11,
                MutedTextBrush,
                TextAlignment.Center,
                true);
        }
        else if (model.ByteCount < Rows)
        {
            var footer = new Rect(RowLabelWidth, bounds.Bottom - 14, bounds.Width - RowLabelWidth - 4, 14);
            DrawText(
                context,
                $"Bytes {model.ByteCount}..7 have no data for this ID",
                footer,
                9,
                MutedTextBrush,
                TextAlignment.Right,
                false);
        }
    }

    private static Color HeatColour(double rate)
    {
        var value = Math.Clamp(rate, 0, 1);
        for (var i = 1; i < HeatStops.Length; i++)
        {
            if (value > HeatStops[i].Position)
            {
                continue;
            }

            var previous = HeatStops[i - 1];
            var next = HeatStops[i];
            var span = next.Position - previous.Position;
            var t = span <= 0 ? 0 : (value - previous.Position) / span;
            return Interpolate(previous.Color, next.Color, t);
        }

        return HeatStops[^1].Color;
    }

    private static Color Interpolate(Color from, Color to, double t) => Color.FromRgb(
        (byte)Math.Round(from.R + ((to.R - from.R) * t)),
        (byte)Math.Round(from.G + ((to.G - from.G) * t)),
        (byte)Math.Round(from.B + ((to.B - from.B) * t)));

    private static bool IsDark(Color colour) =>
        (0.299 * colour.R) + (0.587 * colour.G) + (0.114 * colour.B) < 145;

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
