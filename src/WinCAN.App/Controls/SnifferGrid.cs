using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using WinCAN.Core.Sniffing;

namespace WinCAN.App.Controls;

/// <summary>
/// Custom-drawn sniffer rows (re/sniffer): Delta, Frequency, ID and one column per payload
/// byte. Bytes that changed flash green/red, notched bits are ignored, bytes fade when
/// inactive, and "View Bits" renders each byte as eight bit cells.
/// </summary>
public sealed class SnifferGrid : Control
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<SnifferGrid, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<int> MaxBytesProperty =
        AvaloniaProperty.Register<SnifferGrid, int>(nameof(MaxBytes), 8);

    public static readonly StyledProperty<bool> ShowBitsProperty =
        AvaloniaProperty.Register<SnifferGrid, bool>(nameof(ShowBits));

    public static readonly StyledProperty<bool> FadeInactiveProperty =
        AvaloniaProperty.Register<SnifferGrid, bool>(nameof(FadeInactive));

    /// <summary>Bumped by the view model on every refresh; invalidates the drawing.</summary>
    public static readonly StyledProperty<int> RefreshTokenProperty =
        AvaloniaProperty.Register<SnifferGrid, int>(nameof(RefreshToken));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<SnifferGrid>();

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<SnifferGrid>();

    private static readonly IBrush IncreaseBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x2E, 0xA0, 0x43));
    private static readonly IBrush DecreaseBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xC0, 0x39, 0x2B));
    private static readonly IBrush NotchBrush = new SolidColorBrush(Color.FromArgb(0x88, 0x80, 0x80, 0x80));
    private static readonly IBrush StaleIdBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A));
    private static readonly IBrush BitBorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80));
    private static readonly IPen BitPen = new Pen(BitBorderBrush, 1);
    private static readonly Typeface DefaultTypeface = new(FontFamily.Default);

    private INotifyCollectionChanged? _observed;

    static SnifferGrid()
    {
        AffectsRender<SnifferGrid>(
            MaxBytesProperty,
            ShowBitsProperty,
            FadeInactiveProperty,
            RefreshTokenProperty,
            ForegroundProperty,
            BackgroundProperty);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
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

    public bool FadeInactive
    {
        get => GetValue(FadeInactiveProperty);
        set => SetValue(FadeInactiveProperty, value);
    }

    public int RefreshToken
    {
        get => GetValue(RefreshTokenProperty);
        set => SetValue(RefreshTokenProperty, value);
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            if (_observed is not null)
            {
                _observed.CollectionChanged -= OnItemsChanged;
            }

            _observed = change.NewValue as INotifyCollectionChanged;
            if (_observed is not null)
            {
                _observed.CollectionChanged += OnItemsChanged;
            }

            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Math.Clamp(MaxBytes, 8, SnifferEntry.MaxBytes);
        var rows = ItemsSource?.Cast<object?>().Count() ?? 0;
        var rowHeight = ShowBits ? SnifferLayout.BitsRowHeight : SnifferLayout.RowHeight;
        return new Size(SnifferLayout.TotalWidth(columns), rows * rowHeight);
    }

    public override void Render(DrawingContext context)
    {
        var width = Math.Max(Bounds.Width, 1);
        var height = Math.Max(Bounds.Height, 1);
        if (Background is { } background)
        {
            context.FillRectangle(background, new Rect(0, 0, width, height));
        }

        var foreground = Foreground ?? Brushes.Gainsboro;
        var columns = Math.Clamp(MaxBytes, 8, SnifferEntry.MaxBytes);
        var rowHeight = ShowBits ? SnifferLayout.BitsRowHeight : SnifferLayout.RowHeight;
        var rows = ItemsSource?.Cast<SnifferEntry>().ToList() ?? [];
        var now = Environment.TickCount64;
        for (var i = 0; i < rows.Count; i++)
        {
            DrawRow(context, rows[i], foreground, columns, rowHeight, now, i);
        }
    }

    private void DrawRow(
        DrawingContext context,
        SnifferEntry entry,
        IBrush foreground,
        int columns,
        double rowHeight,
        long now,
        int rowIndex)
    {
        var y = rowIndex * rowHeight;
        var delta = entry.DeltaMicroseconds;
        var deltaText = (delta / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture);
        var frequencyText = delta <= 0
            ? "0 hz"
            : $"{Math.Round(1_000_000.0 / delta)} hz";

        DrawCentered(context, deltaText, 0, SnifferLayout.DeltaWidth, y, rowHeight, 12, foreground);
        DrawCentered(
            context,
            frequencyText,
            SnifferLayout.DeltaWidth,
            SnifferLayout.FrequencyWidth,
            y,
            rowHeight,
            12,
            foreground);

        var idBrush = entry.ElapsedMs(now) > 4000 ? StaleIdBrush : foreground;
        DrawCentered(
            context,
            "0x" + entry.Id.ToString("X5"),
            SnifferLayout.DeltaWidth + SnifferLayout.FrequencyWidth,
            SnifferLayout.IdWidth,
            y,
            rowHeight,
            12,
            idBrush);

        for (var i = 0; i < columns; i++)
        {
            var value = entry.Data(i);
            var x = SnifferLayout.ByteColumnX(i);
            if (value < 0)
            {
                continue;
            }

            var fade = FadeInactive ? entry.FadeFactor(i, entry.CurrentSequence, 200) : 0;
            if (ShowBits)
            {
                DrawByteBits(context, entry, i, value, x, y, fade, foreground);
            }
            else
            {
                var change = entry.ChangeFor(i);
                if (change != SnifferByteChange.None)
                {
                    var brush = change == SnifferByteChange.Increased ? IncreaseBrush : DecreaseBrush;
                    context.FillRectangle(
                        brush,
                        new Rect(x, y + 1, SnifferLayout.ByteWidth, rowHeight - 2));
                }

                using (context.PushOpacity(1 - fade))
                {
                    DrawCentered(
                        context,
                        value.ToString("X2", CultureInfo.InvariantCulture),
                        x,
                        SnifferLayout.ByteWidth,
                        y,
                        rowHeight,
                        12,
                        foreground);
                }
            }
        }
    }

    private void DrawByteBits(
        DrawingContext context,
        SnifferEntry entry,
        int index,
        int value,
        double x,
        double y,
        double fade,
        IBrush foreground)
    {
        var previous = entry.LastData(index);
        var notch = entry.NotchMask(index);
        var size = Math.Min(10, (SnifferLayout.ByteWidth - 8) / 8);
        var startX = x + (SnifferLayout.ByteWidth - size * 8) / 2;
        var top = y + 3;
        for (var bit = 0; bit < 8; bit++)
        {
            var mask = 1 << (7 - bit);
            var isSet = (value & mask) != 0;
            var changed = previous >= 0 && ((value ^ previous) & mask) != 0;
            IBrush? fill = null;
            if ((notch & mask) != 0)
            {
                fill = NotchBrush;
            }
            else if (changed)
            {
                fill = isSet ? IncreaseBrush : DecreaseBrush;
            }
            else if (isSet)
            {
                fill = foreground;
            }

            var rect = new Rect(startX + bit * size, top, size - 1, size - 1);
            if (fill is not null)
            {
                using (context.PushOpacity(fill == foreground ? 0.75 * (1 - fade) : 1 - fade))
                {
                    context.FillRectangle(fill, rect);
                }
            }
            else
            {
                context.DrawRectangle(null, BitPen, rect);
            }
        }

        using (context.PushOpacity(1 - fade * 0.6))
        {
            DrawCentered(
                context,
                value.ToString("X2", CultureInfo.InvariantCulture),
                x,
                SnifferLayout.ByteWidth,
                y + size + 2,
                SnifferLayout.BitsRowHeight - size - 2,
                10,
                foreground);
        }
    }

    private static void DrawCentered(
        DrawingContext context,
        string text,
        double x,
        double width,
        double y,
        double height,
        double fontSize,
        IBrush brush)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            DefaultTypeface,
            fontSize,
            brush);
        context.DrawText(
            formatted,
            new Point(x + (width - formatted.Width) / 2, y + (height - formatted.Height) / 2));
    }
}
