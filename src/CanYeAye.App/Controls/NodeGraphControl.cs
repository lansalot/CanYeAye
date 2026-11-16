using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CanYeAye.App.ViewModels;
using CanYeAye.Core.Analysis;

namespace CanYeAye.App.Controls;

/// <summary>
/// Interactive, touch-friendly node graph: J1939 sources as circles, point-to-point
/// conversations as lines whose thickness is proportional to the traffic volume (both
/// directions combined), and a broadcast bar along the bottom.
///
/// Gestures: drag a node to move it, drag empty space to pan, wheel or pinch to zoom,
/// double-click or long-press a node to name it.
/// </summary>
public sealed class NodeGraphControl : Control
{
    public static readonly StyledProperty<NodeGraphSnapshot?> GraphProperty =
        AvaloniaProperty.Register<NodeGraphControl, NodeGraphSnapshot?>(nameof(Graph));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<NodeGraphControl>();

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<NodeGraphControl>();

    private const double MinZoom = 0.3;
    private const double MaxZoom = 4.0;
    private const double LongPressMilliseconds = 600;
    private const double DragThreshold = 6;

    private static readonly IBrush[] NodeBrushes =
    [
        new SolidColorBrush(Color.FromRgb(0x3A, 0x8D, 0xDE)),
        new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x7D)),
        new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27)),
        new SolidColorBrush(Color.FromRgb(0xC9, 0x5B, 0x5B)),
        new SolidColorBrush(Color.FromRgb(0x8E, 0x6F, 0xD8)),
        new SolidColorBrush(Color.FromRgb(0x4F, 0xB3, 0xC4)),
    ];

    private static readonly IBrush EdgeBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0x8A, 0x9A, 0xAA));
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x4F, 0xA3, 0xFF));
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.FromArgb(0xF0, 0x1B, 0x1B, 0x1B));
    private static readonly IBrush CardBorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly Typeface DefaultTypeface = new(FontFamily.Default);

    private readonly Dictionary<int, Point> _positions = [];
    private readonly Dictionary<int, double> _radii = [];
    private readonly Dictionary<int, Point> _touchPoints = [];
    private readonly DispatcherTimer _longPressTimer;

    private double _zoom = 1.0;
    private Vector _pan = default;
    private double _pinchDistance;
    private int? _hoverAddress;
    private int? _pressAddress;
    private Point _pressPoint;
    private int? _dragAddress;
    private Point _dragOffset;
    private bool _dragging;
    private bool _panning;
    private Point _lastPanPoint;
    private bool _pressWasLongPress;

    public NodeGraphControl()
    {
        _longPressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LongPressMilliseconds),
        };
        _longPressTimer.Tick += (_, _) => OnLongPress();
    }

    /// <summary>Raised on long-press or double-click of a node; parameter is the source address.</summary>
    public event EventHandler<int>? NodeNameRequested;

    static NodeGraphControl()
    {
        AffectsRender<NodeGraphControl>(GraphProperty, ForegroundProperty, BackgroundProperty);
    }

    public NodeGraphSnapshot? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
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

    private NodeGraphNode? HoverNode => _hoverAddress is { } address
        ? Graph?.Nodes.FirstOrDefault(node => node.Address == address)
        : null;

    /// <summary>Puts every node back on the circular layout and resets zoom/pan.</summary>
    public void ResetView()
    {
        _positions.Clear();
        _zoom = 1.0;
        _pan = default;
        InvalidateVisual();
    }

    public void ZoomIn()
    {
        SetZoomAroundCenter(_zoom * 1.2);
    }

    public void ZoomOut()
    {
        SetZoomAroundCenter(_zoom / 1.2);
    }

    public IReadOnlyList<(int Address, double X, double Y)> CaptureLayout() =>
        _positions.Select(pair => (pair.Key, pair.Value.X, pair.Value.Y)).ToArray();

    public void ApplyLayout(IEnumerable<(int Address, double X, double Y)> layout)
    {
        _positions.Clear();
        foreach (var (address, x, y) in layout)
        {
            _positions[address] = new Point(x, y);
        }

        InvalidateVisual();
    }

    public (double Zoom, double PanX, double PanY) CaptureView() => (_zoom, _pan.X, _pan.Y);

    public void ApplyView(double zoom, double panX, double panY)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _pan = new Vector(panX, panY);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Math.Max(Bounds.Width, 1);
        var height = Math.Max(Bounds.Height, 1);
        if (Background is { } background)
        {
            context.FillRectangle(background, new Rect(0, 0, width, height));
        }

        var graph = Graph;
        if (graph is null || graph.Nodes.Count == 0)
        {
            DrawCentered(context, "No J1939 nodes yet", width / 2, height / 2, Foreground ?? Brushes.Gray, 14);
            return;
        }

        EnsureLayout(graph, width, height);
        var foreground = Foreground ?? Brushes.Gainsboro;
        var hover = HoverNode;

        var maxInvolvement = Math.Max(1, graph.Nodes.Max(node => node.Involvement));
        foreach (var node in graph.Nodes)
        {
            _radii[node.Address] = 14 + 20 * Math.Sqrt(node.Involvement / (double)maxInvolvement);
        }

        var maxEdge = Math.Max(1, graph.MaxEdgeFrames);
        var maxBroadcast = Math.Max(1, graph.MaxBroadcastFrames);
        var bar = BroadcastBar(width, height);

        var scene = Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y);
        using (context.PushTransform(scene))
        {
            // Point-to-point edges.
            foreach (var edge in graph.Edges)
            {
                var highlighted = hover is not null &&
                    (edge.From.Address == hover.Address || edge.To.Address == hover.Address);
                var thickness = 1 + 9 * Math.Sqrt(edge.Frames / (double)maxEdge);
                DrawArrow(
                    context,
                    edge.From,
                    edge.To,
                    thickness,
                    highlighted ? HighlightBrush : EdgeBrush,
                    atTarget: edge.Forward > 0,
                    atSource: edge.Backward > 0);
            }

            // Broadcast links and the bar.
            foreach (var broadcast in graph.Broadcast)
            {
                if (!_positions.TryGetValue(broadcast.Source.Address, out var from))
                {
                    continue;
                }

                var to = new Point(Math.Clamp(from.X, bar.Left + 12, bar.Right - 12), bar.Top);
                var thickness = 1 + 9 * Math.Sqrt(broadcast.Frames / (double)maxBroadcast);
                var highlighted = hover is not null && hover.Address == broadcast.Source.Address;
                DrawArrowFromPoint(
                    context,
                    from,
                    _radii.GetValueOrDefault(broadcast.Source.Address, 14),
                    to,
                    0,
                    thickness,
                    highlighted ? HighlightBrush : EdgeBrush,
                    atTarget: true,
                    atSource: false);
            }

            context.FillRectangle(
                new SolidColorBrush(Color.FromArgb(0x4D, 0x8A, 0x9A, 0xAA)),
                bar);
            context.DrawRectangle(
                new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x8A, 0x9A, 0xAA)), 1),
                bar);
            DrawCentered(context, "Broadcast", bar.Center.X, bar.Center.Y, foreground, 12);

            // Nodes.
            foreach (var node in graph.Nodes)
            {
                if (!_positions.TryGetValue(node.Address, out var position))
                {
                    continue;
                }

                var radius = _radii[node.Address];
                var isHover = hover?.Address == node.Address;
                var brush = NodeBrushes[Math.Abs(node.Address) % NodeBrushes.Length];
                context.DrawEllipse(brush, isHover ? new Pen(foreground, 2) : null, position, radius, radius);

                if (radius >= 15)
                {
                    DrawCentered(
                        context,
                        J1939NodeNames.FormatAddress(node.Address),
                        position.X,
                        position.Y,
                        foreground,
                        radius >= 22 ? 11 : 9);
                }

                if (!string.Equals(node.Label, J1939NodeNames.FormatAddress(node.Address), StringComparison.Ordinal))
                {
                    DrawCentered(context, node.Label, position.X, position.Y + radius + 11, foreground, 11);
                }
            }
        }

        DrawText(
            context,
            "Drag to rearrange · wheel/pinch to zoom · long-press a node to name it",
            new Point(12, height - 14),
            foreground,
            10);

        DrawText(
            context,
            $"Zoom {_zoom * 100:0}%",
            new Point(12, 12),
            foreground,
            10);

        if (hover is not null)
        {
            DrawNodeCard(context, hover, graph, foreground);
        }
    }

    private void SetZoomAroundCenter(double zoom)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var world = ScreenToWorld(center);
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _pan = new Vector(center.X - world.X * _zoom, center.Y - world.Y * _zoom);
        InvalidateVisual();
    }

    private Point ScreenToWorld(Point point) =>
        new((point.X - _pan.X) / _zoom, (point.Y - _pan.Y) / _zoom);

    private void DrawArrow(
        DrawingContext context,
        NodeGraphNode from,
        NodeGraphNode to,
        double thickness,
        IBrush brush,
        bool atTarget,
        bool atSource)
    {
        if (!_positions.TryGetValue(from.Address, out var start) ||
            !_positions.TryGetValue(to.Address, out var end))
        {
            return;
        }

        DrawArrowFromPoint(
            context,
            start,
            _radii.GetValueOrDefault(from.Address, 14),
            end,
            _radii.GetValueOrDefault(to.Address, 14),
            thickness,
            brush,
            atTarget,
            atSource);
    }

    private static void DrawArrowFromPoint(
        DrawingContext context,
        Point start,
        double startRadius,
        Point end,
        double endRadius,
        double thickness,
        IBrush brush,
        bool atTarget,
        bool atSource)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
        {
            return;
        }

        var ux = dx / length;
        var uy = dy / length;
        var lineStart = new Point(start.X + ux * startRadius, start.Y + uy * startRadius);
        var lineEnd = new Point(end.X - ux * endRadius, end.Y - uy * endRadius);
        if (Math.Abs(lineEnd.X - lineStart.X) < 0.1 && Math.Abs(lineEnd.Y - lineStart.Y) < 0.1)
        {
            return;
        }

        context.DrawLine(new Pen(brush, thickness), lineStart, lineEnd);

        var head = 6 + thickness * 1.2;
        if (atTarget)
        {
            DrawArrowHead(context, lineEnd, ux, uy, head, brush);
        }

        if (atSource)
        {
            DrawArrowHead(context, lineStart, -ux, -uy, head, brush);
        }
    }

    private static void DrawArrowHead(
        DrawingContext context,
        Point tip,
        double ux,
        double uy,
        double size,
        IBrush brush)
    {
        var baseX = tip.X - ux * size;
        var baseY = tip.Y - uy * size;
        var nx = -uy;
        var ny = ux;
        var half = size * 0.45;
        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(tip, isFilled: true);
            geometryContext.LineTo(new Point(baseX + nx * half, baseY + ny * half));
            geometryContext.LineTo(new Point(baseX - nx * half, baseY - ny * half));
            geometryContext.EndFigure(isClosed: true);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    private void DrawNodeCard(
        DrawingContext context,
        NodeGraphNode node,
        NodeGraphSnapshot graph,
        IBrush foreground)
    {
        var lines = new List<string>
        {
            node.Label,
            $"{J1939NodeNames.FormatAddress(node.Address)} · sent {node.Sent:N0} · received {node.Received:N0}",
        };

        var partners = graph.Edges
            .Where(edge => edge.From.Address == node.Address || edge.To.Address == node.Address)
            .Select(edge =>
            {
                var other = edge.From.Address == node.Address ? edge.To : edge.From;
                return (Other: other, edge.Frames);
            })
            .OrderByDescending(pair => pair.Frames)
            .Take(3)
            .Select(pair => $"→ {pair.Other.Label}: {pair.Frames:N0}");
        lines.AddRange(partners);

        if (lines.Count == 2)
        {
            lines.Add("No point-to-point conversations");
        }

        var formatted = lines
            .Select(line => new FormattedText(
                line,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                DefaultTypeface,
                11,
                foreground))
            .ToList();
        var width = formatted.Max(text => text.Width) + 20;
        var height = formatted.Sum(text => text.Height + 2) + 14;
        var origin = new Point(12, 30);
        var card = new Rect(origin, new Size(width, height));
        context.FillRectangle(CardBrush, card);
        context.DrawRectangle(new Pen(CardBorderBrush, 1), card);

        var y = origin.Y + 7;
        foreach (var text in formatted)
        {
            context.DrawText(text, new Point(origin.X + 10, y));
            y += text.Height + 2;
        }
    }

    private void EnsureLayout(NodeGraphSnapshot graph, double width, double height)
    {
        var center = new Point(width / 2, height * 0.42);
        var radius = Math.Max(80, Math.Min(width / 2 - 110, height * 0.32));
        var nodes = graph.Nodes;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (_positions.TryGetValue(node.Address, out var existing))
            {
                node.X = existing.X;
                node.Y = existing.Y;
                node.HasPosition = true;
                continue;
            }

            // New nodes take the free spot on the circle that is farthest from the
            // nodes already placed, so later arrivals never land on an existing node.
            var best = default(Point);
            var bestDistance = -1.0;
            const int slots = 24;
            for (var slot = 0; slot < slots; slot++)
            {
                var angle = -Math.PI / 2 + 2 * Math.PI * slot / slots;
                var candidate = new Point(
                    center.X + radius * Math.Cos(angle),
                    center.Y + radius * Math.Sin(angle));
                var nearest = _positions.Count == 0
                    ? double.MaxValue
                    : _positions.Values.Min(position => Distance(position, candidate));
                if (nearest > bestDistance)
                {
                    bestDistance = nearest;
                    best = candidate;
                }
            }

            _positions[node.Address] = best;
            node.X = best.X;
            node.Y = best.Y;
            node.HasPosition = true;
        }

        var live = nodes.Select(node => node.Address).ToHashSet();
        foreach (var address in _positions.Keys.Where(address => !live.Contains(address)).ToList())
        {
            _positions.Remove(address);
            _radii.Remove(address);
        }
    }

    private static Rect BroadcastBar(double width, double height) =>
        new(50, height - 44, Math.Max(40, width - 100), 24);

    private static void DrawCentered(DrawingContext context, string text, double x, double y, IBrush brush, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, DefaultTypeface, size, brush);
        context.DrawText(formatted, new Point(x - formatted.Width / 2, y - formatted.Height / 2));
    }

    private static void DrawText(DrawingContext context, string text, Point origin, IBrush brush, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, DefaultTypeface, size, brush);
        context.DrawText(formatted, origin);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var screen = e.GetPosition(this);
        var world = ScreenToWorld(screen);
        var factor = e.Delta.Y >= 0 ? 1.12 : 1 / 1.12;
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        _pan = new Vector(screen.X - world.X * _zoom, screen.Y - world.Y * _zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var screen = e.GetPosition(this);
        var point = ScreenToWorld(screen);

        if (e.Pointer.Type == PointerType.Touch)
        {
            _touchPoints[e.Pointer.Id] = screen;
            if (_touchPoints.Count >= 2)
            {
                CancelPress();
                _pinchDistance = PinchDistance();
                e.Handled = true;
                return;
            }
        }

        var address = HitTest(point);
        if (address is { } hit)
        {
            if (e.ClickCount == 2)
            {
                CancelPress();
                NodeNameRequested?.Invoke(this, hit);
                e.Handled = true;
                return;
            }

            _pressAddress = hit;
            _pressPoint = screen;
            _pressWasLongPress = false;
            _dragOffset = new Point(point.X - _positions[hit].X, point.Y - _positions[hit].Y);
            _longPressTimer.Stop();
            _longPressTimer.Start();
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        _panning = true;
        _lastPanPoint = screen;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var screen = e.GetPosition(this);

        if (e.Pointer.Type == PointerType.Touch && _touchPoints.ContainsKey(e.Pointer.Id))
        {
            _touchPoints[e.Pointer.Id] = screen;
            if (_touchPoints.Count >= 2)
            {
                UpdatePinch();
                return;
            }
        }

        if (_dragging && _dragAddress is { } dragged)
        {
            var world = ScreenToWorld(screen);
            _positions[dragged] = new Point(world.X - _dragOffset.X, world.Y - _dragOffset.Y);
            InvalidateVisual();
            return;
        }

        if (_pressAddress is { } pressed)
        {
            if (Distance(screen, _pressPoint) > DragThreshold)
            {
                _longPressTimer.Stop();
                _dragging = true;
                _dragAddress = pressed;
                _pressAddress = null;
            }

            return;
        }

        if (_panning)
        {
            _pan += new Vector(screen.X - _lastPanPoint.X, screen.Y - _lastPanPoint.Y);
            _lastPanPoint = screen;
            InvalidateVisual();
            return;
        }

        var hover = HitTest(ScreenToWorld(screen));
        if (hover != _hoverAddress)
        {
            _hoverAddress = hover;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Type == PointerType.Touch)
        {
            _touchPoints.Remove(e.Pointer.Id);
            if (_touchPoints.Count < 2)
            {
                _pinchDistance = 0;
            }
        }

        _longPressTimer.Stop();
        _dragging = false;
        _dragAddress = null;
        _pressAddress = null;
        _panning = false;
        _pressWasLongPress = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_dragging && !_panning && _hoverAddress is not null)
        {
            _hoverAddress = null;
            InvalidateVisual();
        }
    }

    private void OnLongPress()
    {
        _longPressTimer.Stop();
        if (_pressWasLongPress || _dragging || _panning || _pressAddress is not { } address)
        {
            return;
        }

        _pressWasLongPress = true;
        NodeNameRequested?.Invoke(this, address);
    }

    private void CancelPress()
    {
        _longPressTimer.Stop();
        _pressAddress = null;
        _dragging = false;
        _dragAddress = null;
        _panning = false;
    }

    private void UpdatePinch()
    {
        if (_touchPoints.Count < 2)
        {
            _pinchDistance = 0;
            return;
        }

        var points = _touchPoints.Values.ToArray();
        var first = points[0];
        var second = points[1];
        var center = new Point((first.X + second.X) / 2, (first.Y + second.Y) / 2);
        var distance = Distance(first, second);
        if (_pinchDistance > 0)
        {
            var world = ScreenToWorld(center);
            _zoom = Math.Clamp(_zoom * (distance / _pinchDistance), MinZoom, MaxZoom);
            _pan = new Vector(center.X - world.X * _zoom, center.Y - world.Y * _zoom);
            InvalidateVisual();
        }

        _pinchDistance = distance;
    }

    private double PinchDistance()
    {
        var points = _touchPoints.Values.ToArray();
        return points.Length < 2 ? 0 : Distance(points[0], points[1]);
    }

    private int? HitTest(Point point)
    {
        var graph = Graph;
        if (graph is null)
        {
            return null;
        }

        foreach (var node in graph.Nodes)
        {
            if (_positions.TryGetValue(node.Address, out var position) &&
                Distance(point, position) <= _radii.GetValueOrDefault(node.Address, 14) + 2)
            {
                return node.Address;
            }
        }

        return null;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
