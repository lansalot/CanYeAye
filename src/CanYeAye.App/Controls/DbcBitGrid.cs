using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CanYeAye.Core.Dbc;

namespace CanYeAye.App.Controls;

public sealed class BitGridClickEventArgs(int bit, bool rightButton) : EventArgs
{
    public int Bit { get; } = bit;

    public bool RightButton { get; } = rightButton;
}

/// <summary>
/// The signal editor's bit layout (CANDataGrid in <c>GridMode::SIGNAL_VIEW</c>). Cells are
/// grouped in 8-byte blocks; each block shows one byte per row with bit 7 on the left. The current
/// signal's bits are hatched green, the start bit carries a ring, and bits claimed by the message's
/// other signals are filled with a per-signal colour and labelled with the signal name.
/// </summary>
public sealed class DbcBitGrid : Control
{
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly IBrush SignalFill = new SolidColorBrush(Color.FromRgb(0x2C, 0xA0, 0x2C));
    private static readonly IBrush SignalHatch = new SolidColorBrush(Color.FromRgb(0x18, 0x60, 0x18));
    private static readonly IBrush GridLine = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly IBrush LightTextBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly IBrush StartRing = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly Typeface Face = new("Segoe UI");

    private static readonly Color[] SignalColors =
    [
        Color.FromRgb(0x6C, 0x5C, 0xA8),
        Color.FromRgb(0x3F, 0x6F, 0x9E),
        Color.FromRgb(0x8A, 0x5A, 0x36),
        Color.FromRgb(0x4E, 0x7D, 0x52),
        Color.FromRgb(0x8B, 0x4A, 0x6B),
        Color.FromRgb(0x5C, 0x5C, 0x5C),
    ];

    private DbcMessage? _message;
    private DbcSignal? _current;
    private int _bytes = 8;
    private IReadOnlyList<DbcSignal> _signals = [];

    public event EventHandler<BitGridClickEventArgs>? BitClicked;

    public DbcMessage? Message => _message;

    public void SetState(DbcMessage? message, DbcSignal? current)
    {
        _message = message;
        _current = current;
        _bytes = Math.Clamp((int)(message?.Length ?? 8), 1, 64);
        _signals = message is null ? [] : message.Signals.Values.ToArray();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var layout = ComputeLayout(Bounds);
        if (layout.Cell <= 0)
        {
            return;
        }

        var blockWidth = layout.Cell * 8;
        var blockHeight = layout.Cell * 8;

        // Headers: bit numbers over every block, byte index down the left edge.
        for (var block = 0; block < layout.Blocks; block++)
        {
            var (bx, by) = layout.BlockOrigin(block);
            for (var column = 0; column < 8; column++)
            {
                var label = (7 - column).ToString(CultureInfo.InvariantCulture);
                var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face,
                    Math.Max(9, layout.Cell * 0.24), TextBrush);
                context.DrawText(text,
                    new Point(bx + column * layout.Cell + (layout.Cell - text.Width) / 2, by - text.Height - 2));
            }

            var rows = Math.Min(8, _bytes - block * 8);
            for (var row = 0; row < rows; row++)
            {
                var text = new FormattedText((block * 8 + row).ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face,
                    Math.Max(9, layout.Cell * 0.24), TextBrush);
                context.DrawText(text,
                    new Point(bx - text.Width - 4, by + row * layout.Cell + (layout.Cell - text.Height) / 2));
            }
        }

        var currentBits = _current is null
            ? new HashSet<int>()
            : new HashSet<int>(DbcEditor.BitPositions(_current.StartBit, _current.SignalSize, _current.IntelByteOrder));

        var owner = BuildOwnership(layout.Blocks * 8 * 8);

        for (var block = 0; block < layout.Blocks; block++)
        {
            var (bx, by) = layout.BlockOrigin(block);
            var rows = Math.Min(8, _bytes - block * 8);

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < 8; column++)
                {
                    var bit = (block * 8 + row) * 8 + (7 - column);
                    if (bit >= 512)
                    {
                        continue;
                    }

                    var rect = new Rect(bx + column * layout.Cell, by + row * layout.Cell, layout.Cell, layout.Cell);
                    var ownerIndex = owner[bit];

                    if (currentBits.Contains(bit))
                    {
                        context.FillRectangle(SignalFill, rect);
                        DrawHatch(context, rect);
                    }
                    else if (ownerIndex >= 0)
                    {
                        var color = SignalColors[ownerIndex % SignalColors.Length];
                        context.FillRectangle(new SolidColorBrush(color), rect);
                    }
                    else
                    {
                        context.FillRectangle(Background, rect);
                    }

                    context.DrawRectangle(null, new Pen(GridLine, 1), rect);

                    var bitText = new FormattedText(bit.ToString(CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face,
                        Math.Max(8, layout.Cell * 0.22), TextBrush);
                    context.DrawText(bitText, rect.TopLeft + new Vector(2, 1));

                    if (_current is not null && bit == _current.StartBit)
                    {
                        var centre = rect.Center;
                        context.DrawEllipse(null, new Pen(StartRing, 2), centre, 4, 4);
                    }
                }
            }

            DrawSignalNames(context, layout, block, owner);
        }
    }

    private void DrawSignalNames(DrawingContext context, GridLayout layout, int block, int[] owner)
    {
        var (bx, by) = layout.BlockOrigin(block);
        var rows = Math.Min(8, _bytes - block * 8);

        for (var row = 0; row < rows; row++)
        {
            var column = 0;
            while (column < 8)
            {
                var bit = (block * 8 + row) * 8 + (7 - column);
                var ownerIndex = bit < 512 ? owner[bit] : -1;
                if (ownerIndex < 0)
                {
                    column++;
                    continue;
                }

                var start = column;
                while (column < 8)
                {
                    var nextBit = (block * 8 + row) * 8 + (7 - column);
                    if (nextBit >= 512 || owner[nextBit] != ownerIndex)
                    {
                        break;
                    }

                    column++;
                }

                // Signals are short in practice; two cells are enough to show a name fragment.
                if (column - start < 2)
                {
                    continue;
                }

                var clip = new Rect(bx + start * layout.Cell, by + row * layout.Cell,
                    (column - start) * layout.Cell, layout.Cell);
                var name = _signals[ownerIndex].Name;
                var text = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face,
                    Math.Max(8, layout.Cell * 0.19), LightTextBrush)
                {
                    MaxTextWidth = Math.Max(1, clip.Width - 4),
                    Trimming = TextTrimming.CharacterEllipsis,
                };

                using (context.PushClip(clip))
                {
                    context.DrawText(text, new Point(clip.X + 2, clip.Y + clip.Height * 0.45));
                }
            }
        }
    }

    private static void DrawHatch(DrawingContext context, Rect rect)
    {
        const double spacing = 7;
        using (context.PushClip(rect))
        {
            var pen = new Pen(SignalHatch, 1.2);
            for (var offset = -rect.Height; offset < rect.Width; offset += spacing)
            {
                context.DrawLine(pen,
                    new Point(rect.X + offset, rect.Bottom),
                    new Point(rect.X + offset + rect.Height, rect.Y));
            }
        }
    }

    /// <summary>Signal index (into <see cref="_signals"/>) that owns each bit, or -1.</summary>
    private int[] BuildOwnership(int bitCount)
    {
        var owner = new int[Math.Max(bitCount, 512)];
        Array.Fill(owner, -1);

        for (var index = 0; index < _signals.Count; index++)
        {
            var signal = _signals[index];
            if (_current is not null && !ShowsWithCurrent(signal, _current))
            {
                continue;
            }

            foreach (var bit in DbcEditor.BitPositions(signal.StartBit, signal.SignalSize, signal.IntelByteOrder))
            {
                if (bit >= 0 && bit < owner.Length && bit < _bytes * 8)
                {
                    owner[bit] = index;
                }
            }
        }

        return owner;
    }

    private static bool ShowsWithCurrent(DbcSignal signal, DbcSignal current)
    {
        // Signals without a multiplex parent are always visible; multiplexed signals only show
        // when they sit under the current signal's multiplexor with the same values.
        if (signal.MultiplexParent is null)
        {
            return true;
        }

        return signal.MultiplexParent == current.MultiplexParent &&
               DbcEditor.SignalsShareMultiplex(signal, current);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
        {
            return;
        }

        var bit = HitTestBit(point.Position);
        if (bit is null)
        {
            return;
        }

        BitClicked?.Invoke(this, new BitGridClickEventArgs(bit.Value, point.Properties.IsRightButtonPressed));
        e.Handled = true;
    }

    private int? HitTestBit(Point position)
    {
        var layout = ComputeLayout(Bounds);
        if (layout.Cell <= 0)
        {
            return null;
        }

        for (var block = 0; block < layout.Blocks; block++)
        {
            var (bx, by) = layout.BlockOrigin(block);
            var rows = Math.Min(8, _bytes - block * 8);
            if (position.X < bx || position.X >= bx + layout.Cell * 8 ||
                position.Y < by || position.Y >= by + layout.Cell * rows)
            {
                continue;
            }

            var column = (int)((position.X - bx) / layout.Cell);
            var row = (int)((position.Y - by) / layout.Cell);
            var bit = (block * 8 + row) * 8 + (7 - column);
            return bit < _bytes * 8 ? bit : null;
        }

        return null;
    }

    private GridLayout ComputeLayout(Rect bounds)
    {
        var blocks = Math.Max(1, (_bytes + 7) / 8);
        var across = blocks <= 2 ? blocks : blocks <= 4 ? 2 : 4;
        var down = (blocks + across - 1) / across;

        const double labelsLeft = 26;
        const double labelsTop = 20;
        const double gap = 14;

        var availableWidth = Math.Max(0, bounds.Width - labelsLeft - gap * (across - 1) - 4);
        var availableHeight = Math.Max(0, bounds.Height - labelsTop - gap * (down - 1) - 4);
        var cell = Math.Min(availableWidth / (across * 8.0), availableHeight / (down * 8.0));
        cell = Math.Clamp(cell, 4, 56);

        return new GridLayout(cell, across, down, blocks, labelsLeft, labelsTop, gap);
    }

    private readonly record struct GridLayout(
        double Cell,
        int Across,
        int Down,
        int Blocks,
        double OriginX,
        double OriginY,
        double Gap)
    {
        public (double X, double Y) BlockOrigin(int block)
        {
            var bx = block % Across;
            var by = block / Across;
            return (OriginX + bx * (Cell * 8 + Gap), OriginY + by * (Cell * 8 + Gap));
        }
    }
}
