using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WinCAN.App.ViewModels;
using WinCAN.Core.Analysis;

namespace WinCAN.App.Controls;

/// <summary>
/// Flow-view payload grid (re/flowviewwindow + CANDataGrid): bytes run top to bottom and the
/// eight bits of each byte left to right (bit 7 first). The grid fills the available panel
/// width (cells are wide bars, no per-cell border). A set bit is black, a clear bit white;
/// bits that just changed flash green (0 -> 1) or red (1 -> 0) before settling.
/// Clicking a cell marks that bit as a playback trigger.
/// </summary>
public sealed class FlowBitGrid : Control
{
    private const double RowLabelWidth = 26;
    private const double HeaderHeight = 16;

    public static readonly StyledProperty<FlowBitGridModel?> ModelProperty =
        AvaloniaProperty.Register<FlowBitGrid, FlowBitGridModel?>(nameof(Model));

    /// <summary>
    /// Bumped when the trigger set changes, so the grid repaints without restarting the
    /// change flash (replacing <see cref="Model"/> would).
    /// </summary>
    public static readonly StyledProperty<int> TriggerVersionProperty =
        AvaloniaProperty.Register<FlowBitGrid, int>(nameof(TriggerVersion));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<FlowBitGrid>();

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<FlowBitGrid>();

    private static readonly IBrush SteadyOnBrush = Brushes.Black;
    private static readonly IBrush SteadyOffBrush = Brushes.White;
    private static readonly IBrush ChangedOnBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
    private static readonly IBrush ChangedOffBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
    private static readonly IBrush TriggerTextBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly Typeface DefaultTypeface = new(FontFamily.Default);

    private readonly bool[] _flash = new bool[FlowViewSession.MaxBytes * 8];
    private readonly long[] _flashUntil = new long[FlowViewSession.MaxBytes * 8];
    private readonly DispatcherTimer _flashTimer;

    private int _columns;
    private int _rows;
    private int _bytesPerRow;
    private double _cellWidth;
    private double _cellHeight;
    private double _gridLeft;
    private double _gridTop;

    static FlowBitGrid()
    {
        AffectsRender<FlowBitGrid>(ModelProperty, TriggerVersionProperty, ForegroundProperty, BackgroundProperty);
    }

    public FlowBitGrid()
    {
        ClipToBounds = true;
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _flashTimer.Tick += (_, _) => OnFlashTick();
    }

    /// <summary>Raised with the canonical bit number (byte * 8 + bit) when a cell is clicked.</summary>
    public event EventHandler<int>? BitClicked;

    public FlowBitGridModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public int TriggerVersion
    {
        get => GetValue(TriggerVersionProperty);
        set => SetValue(TriggerVersionProperty, value);
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
        if (change.Property == ModelProperty)
        {
            ApplyModel(change.GetNewValue<FlowBitGridModel?>());
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 260 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 190 : availableSize.Height;
        return new Size(width, height);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var model = Model;
        if (model is null || _cellWidth <= 0)
        {
            return;
        }

        var point = e.GetPosition(this);
        var column = (int)Math.Floor((point.X - _gridLeft) / _cellWidth);
        var row = (int)Math.Floor((point.Y - _gridTop) / _cellHeight);
        if (column < 0 || row < 0 || column >= _columns || row >= _rows)
        {
            return;
        }

        var byteIndex = row * _bytesPerRow + column / 8;
        if (byteIndex < 0 || byteIndex >= model.ByteCount)
        {
            return;
        }

        var bitInByte = 7 - (column & 7);
        BitClicked?.Invoke(this, byteIndex * 8 + bitInByte);
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
        {
            context.FillRectangle(background, bounds);
        }

        var model = Model;
        if (model is null || model.ByteCount <= 0)
        {
            _cellWidth = 0;
            _cellHeight = 0;
            return;
        }

        var foreground = Foreground ?? Brushes.Gainsboro;
        var byteCount = Math.Min(model.ByteCount, FlowViewSession.MaxBytes);
        _bytesPerRow = byteCount <= 8 ? 1 : byteCount <= 32 ? 2 : 4;
        _columns = _bytesPerRow * 8;
        _rows = (byteCount + _bytesPerRow - 1) / _bytesPerRow;

        // Fill the panel: one wide bar per bit, full width, rows split the available height.
        var availableWidth = Math.Max(1, bounds.Width - RowLabelWidth);
        var availableHeight = Math.Max(1, bounds.Height - HeaderHeight);
        _cellWidth = availableWidth / _columns;
        _cellHeight = availableHeight / _rows;
        _gridLeft = RowLabelWidth;
        _gridTop = HeaderHeight;

        var fontSize = Math.Clamp(Math.Min(_cellWidth, _cellHeight) * 0.45, 7, 20);
        var labelSize = Math.Clamp(_cellHeight * 0.4, 7, 12);
        var showBitNumbers = _cellWidth >= 13 && _cellHeight >= 13;

        // Bit numbers across the top (bit 7 on the left of each byte).
        for (var column = 0; column < _columns; column++)
        {
            DrawText(
                context,
                (7 - (column & 7)).ToString(CultureInfo.InvariantCulture),
                new Rect(_gridLeft + column * _cellWidth, 0, _cellWidth, HeaderHeight),
                labelSize,
                foreground,
                TextAlignment.Center,
                centerVertically: true);
        }

        // Byte labels down the left, plus the cells themselves.
        for (var row = 0; row < _rows; row++)
        {
            var firstByte = row * _bytesPerRow;
            DrawText(
                context,
                firstByte.ToString(CultureInfo.InvariantCulture),
                new Rect(0, _gridTop + row * _cellHeight, RowLabelWidth - 4, _cellHeight),
                labelSize,
                foreground,
                TextAlignment.Right,
                centerVertically: true);

            for (var column = 0; column < _columns; column++)
            {
                var byteIndex = firstByte + column / 8;
                if (byteIndex >= byteCount)
                {
                    continue;
                }

                var bitInByte = 7 - (column & 7);
                var bit = byteIndex * 8 + bitInByte;
                var mask = 1 << bitInByte;
                var isSet = (model.Current[byteIndex] & mask) != 0;

                var changed = model.FlashDurationMs > 0
                    ? _flash[bit]
                    : (model.Current[byteIndex] & mask) != (model.Reference[byteIndex] & mask);
                var fill = changed
                    ? isSet ? ChangedOnBrush : ChangedOffBrush
                    : isSet ? SteadyOnBrush : SteadyOffBrush;

                var cell = new Rect(
                    _gridLeft + column * _cellWidth,
                    _gridTop + row * _cellHeight,
                    _cellWidth,
                    _cellHeight);
                context.FillRectangle(fill, cell);

                if (!showBitNumbers)
                {
                    continue;
                }

                var triggered = model.Triggers.Contains(bit);
                var textBrush = triggered
                    ? TriggerTextBrush
                    : isSet && !changed ? Brushes.White : Brushes.Black;
                DrawText(
                    context,
                    bitInByte.ToString(CultureInfo.InvariantCulture),
                    cell,
                    fontSize,
                    textBrush,
                    TextAlignment.Center,
                    centerVertically: true,
                    bold: triggered);
            }
        }
    }

    private void ApplyModel(FlowBitGridModel? model)
    {
        Array.Clear(_flash);
        Array.Clear(_flashUntil);
        _flashTimer.Stop();

        if (model is null || model.FlashDurationMs <= 0)
        {
            InvalidateVisual();
            return;
        }

        var deadline = Environment.TickCount64 + model.FlashDurationMs;
        var any = false;
        for (var bit = 0; bit < model.ByteCount * 8 && bit < _flash.Length; bit++)
        {
            var index = bit >> 3;
            var mask = 1 << (bit & 7);
            if (((model.Current[index] ^ model.Reference[index]) & mask) == 0)
            {
                continue;
            }

            _flash[bit] = true;
            _flashUntil[bit] = deadline;
            any = true;
        }

        if (any)
        {
            _flashTimer.Start();
        }

        InvalidateVisual();
    }

    private void OnFlashTick()
    {
        var now = Environment.TickCount64;
        var any = false;
        for (var i = 0; i < _flash.Length; i++)
        {
            if (!_flash[i])
            {
                continue;
            }

            if (now >= _flashUntil[i])
            {
                _flash[i] = false;
            }
            else
            {
                any = true;
            }
        }

        if (!any)
        {
            _flashTimer.Stop();
        }

        InvalidateVisual();
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        Rect rect,
        double fontSize,
        IBrush brush,
        TextAlignment alignment,
        bool centerVertically,
        bool bold = false)
    {
        var typeface = bold
            ? new Typeface(DefaultTypeface.FontFamily, FontStyle.Normal, FontWeight.Bold)
            : DefaultTypeface;
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
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
