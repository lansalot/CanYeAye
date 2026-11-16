using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using WinCAN.Core.Sniffing;

namespace WinCAN.App.Controls;

/// <summary>Shared column geometry for the sniffer header and grid.</summary>
internal static class SnifferLayout
{
    public const double DeltaWidth = 110;
    public const double FrequencyWidth = 100;
    public const double IdWidth = 90;
    public const double ByteWidth = 46;

    /// <summary>Byte column width in bit view (eight larger bit cells).</summary>
    public const double BitsByteWidth = 74;

    public const double HeaderHeight = 26;
    public const double RowHeight = 22;
    public const double BitsRowHeight = 48;

    public static double ColumnWidth(bool showBits) => showBits ? BitsByteWidth : ByteWidth;

    public static double TotalWidth(int columns, bool showBits = false) =>
        DeltaWidth + FrequencyWidth + IdWidth +
        Math.Clamp(columns, 8, SnifferEntry.MaxBytes) * ColumnWidth(showBits);

    public static double ByteColumnX(int index, bool showBits = false) =>
        DeltaWidth + FrequencyWidth + IdWidth + index * ColumnWidth(showBits);

    public static void DrawColumnSeparators(
        DrawingContext context,
        int columns,
        double height,
        bool showBits = false)
    {
        var boundaries = new List<double>
        {
            DeltaWidth,
            DeltaWidth + FrequencyWidth,
            DeltaWidth + FrequencyWidth + IdWidth,
        };
        for (var i = 1; i <= columns; i++)
        {
            boundaries.Add(ByteColumnX(i, showBits));
        }

        foreach (var x in boundaries)
        {
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, height));
        }
    }

    public static IPen GridPen { get; } = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)), 1);
}

/// <summary>Fixed sniffer column header (Delta, Frequency, ID, 0..N).</summary>
public sealed class SnifferHeader : Control
{
    public static readonly StyledProperty<int> MaxBytesProperty =
        AvaloniaProperty.Register<SnifferHeader, int>(nameof(MaxBytes), 8);

    public static readonly StyledProperty<bool> ShowBitsProperty =
        AvaloniaProperty.Register<SnifferHeader, bool>(nameof(ShowBits));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<SnifferHeader>();

    private static readonly Typeface DefaultTypeface = new(FontFamily.Default);

    static SnifferHeader()
    {
        AffectsRender<SnifferHeader>(MaxBytesProperty, ShowBitsProperty, ForegroundProperty);
    }

    public int MaxBytes
    {
        get => GetValue(MaxBytesProperty);
        set => SetValue(MaxBytesProperty, value);
    }

    public bool ShowBits
    {
        get => GetValue(ShowBitsProperty);
        set => SetValue(ShowBitsProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Math.Clamp(MaxBytes, 8, SnifferEntry.MaxBytes);
        return new Size(SnifferLayout.TotalWidth(columns, ShowBits), SnifferLayout.HeaderHeight);
    }

    public override void Render(DrawingContext context)
    {
        var foreground = Foreground ?? Brushes.Gainsboro;
        var columns = Math.Clamp(MaxBytes, 8, SnifferEntry.MaxBytes);
        var height = SnifferLayout.HeaderHeight;

        DrawCentered(context, "Delta", 0, SnifferLayout.DeltaWidth, foreground);
        DrawCentered(context, "Frequency", SnifferLayout.DeltaWidth, SnifferLayout.FrequencyWidth, foreground);
        DrawCentered(
            context,
            "ID",
            SnifferLayout.DeltaWidth + SnifferLayout.FrequencyWidth,
            SnifferLayout.IdWidth,
            foreground);
        for (var i = 0; i < columns; i++)
        {
            DrawCentered(
                context,
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SnifferLayout.ByteColumnX(i, ShowBits),
                SnifferLayout.ColumnWidth(ShowBits),
                foreground);
        }

        SnifferLayout.DrawColumnSeparators(context, columns, height, ShowBits);
        context.DrawLine(
            SnifferLayout.GridPen,
            new Point(0, height - 0.5),
            new Point(SnifferLayout.TotalWidth(columns, ShowBits), height - 0.5));
    }

    private static void DrawCentered(DrawingContext context, string text, double x, double width, IBrush brush)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            DefaultTypeface,
            12,
            brush);
        context.DrawText(
            formatted,
            new Point(
                x + (width - formatted.Width) / 2,
                (SnifferLayout.HeaderHeight - formatted.Height) / 2));
    }
}
