using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using CanYeAye.App.ViewModels;

namespace CanYeAye.App.Controls;

/// <summary>
/// Small bar chart used for the bit histogram and the interval histogram. Values are drawn as
/// bars (optionally on a logarithmic value axis, matching the reference's log tickers) with
/// labelled x-axis ticks supplied by the view model.
/// </summary>
public sealed class FrameInfoBarChart : Control
{
    public static readonly StyledProperty<FrameInfoChartModel?> ModelProperty =
        AvaloniaProperty.Register<FrameInfoBarChart, FrameInfoChartModel?>(nameof(Model));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<FrameInfoBarChart>();

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<FrameInfoBarChart>();

    private const double LeftAxisWidth = 42;
    private const double BottomAxisHeight = 20;
    private const double TopPadding = 8;
    private const double RightPadding = 8;

    private static readonly Typeface DefaultTypeface = new(FontFamily.Default);
    private static readonly IBrush BarBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0x9B, 0xFF));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0x80, 0x80, 0x80)), 1);
    private static readonly IPen AxisPen = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)), 1);

    static FrameInfoBarChart()
    {
        AffectsRender<FrameInfoBarChart>(ModelProperty, ForegroundProperty, BackgroundProperty);
    }

    public FrameInfoChartModel? Model
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
        var height = double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height;
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
        var hasAxisTitle = !string.IsNullOrEmpty(Model?.XAxisTitle);
        var bottomAxisHeight = hasAxisTitle ? BottomAxisHeight + 13 : BottomAxisHeight;
        var plot = new Rect(
            LeftAxisWidth,
            TopPadding,
            Math.Max(1, bounds.Width - LeftAxisWidth - RightPadding),
            Math.Max(1, bounds.Height - TopPadding - bottomAxisHeight));

        if (Model is not { Values.Count: > 0 } model)
        {
            DrawText(context, "No data", plot, 12, foreground, TextAlignment.Center, true);
            return;
        }

        var maximum = 0.0;
        foreach (var value in model.Values)
        {
            maximum = Math.Max(maximum, value);
        }

        if (maximum <= 0)
        {
            DrawText(
                context,
                string.IsNullOrEmpty(model.EmptyText) ? "No data" : model.EmptyText,
                plot,
                11,
                foreground,
                TextAlignment.Center,
                true);
            return;
        }

        double ToScale(double value) => model.Logarithmic ? Math.Log10(value + 1) : value;

        var maximumScale = Math.Max(ToScale(maximum), 1e-9);

        // Horizontal gridlines with value labels.
        const int gridLines = 4;
        for (var line = 0; line <= gridLines; line++)
        {
            var fraction = line / (double)gridLines;
            var y = plot.Bottom - (plot.Height * fraction);
            context.DrawLine(GridPen, new Point(plot.X, y), new Point(plot.Right, y));

            var value = model.Logarithmic
                ? Math.Pow(10, maximumScale * fraction) - 1
                : maximum * fraction;
            DrawText(
                context,
                FormatValue(value),
                new Rect(0, y - 8, LeftAxisWidth - 5, 16),
                9,
                foreground,
                TextAlignment.Right,
                true);
        }

        var barCount = model.Values.Count;
        var slot = plot.Width / barCount;
        var barWidth = Math.Max(1, slot * 0.78);

        for (var index = 0; index < barCount; index++)
        {
            var value = model.Values[index];
            var height = ToScale(value) / maximumScale * plot.Height;
            if (height <= 0)
            {
                continue;
            }

            var x = plot.X + (index * slot) + ((slot - barWidth) / 2);
            var rect = new Rect(x, plot.Bottom - height, barWidth, height);
            context.DrawRectangle(BarBrush, null, rect);
        }

        context.DrawLine(AxisPen, new Point(plot.X, plot.Y), new Point(plot.X, plot.Bottom));
        context.DrawLine(AxisPen, new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));

        foreach (var tick in model.Ticks)
        {
            if (tick.Index < 0 || tick.Index >= barCount)
            {
                continue;
            }

            var x = plot.X + ((tick.Index + 0.5) * slot);
            DrawText(
                context,
                tick.Text,
                new Rect(x - 34, plot.Bottom + 3, 68, 16),
                9,
                foreground,
                TextAlignment.Center,
                false);
        }

        if (hasAxisTitle)
        {
            DrawText(
                context,
                model.XAxisTitle,
                new Rect(plot.X, plot.Bottom + 18, plot.Width, 13),
                9,
                foreground,
                TextAlignment.Center,
                false);
        }
    }

    private static string FormatValue(double value)
    {
        if (value >= 1_000_000)
        {
            return (value / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (value >= 1_000)
        {
            return (value / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        }

        return value < 10
            ? value.ToString("0.#", CultureInfo.InvariantCulture)
            : Math.Round(value).ToString(CultureInfo.InvariantCulture);
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
