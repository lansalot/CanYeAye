using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanYeAye.Connections;
using CanYeAye.Core;
using CanYeAye.Core.Analysis;

namespace CanYeAye.App.ViewModels;

/// <summary>A node in the communication graph.</summary>
public sealed class NodeGraphNode
{
    public NodeGraphNode(int address, string label)
    {
        Address = address;
        Label = label;
    }

    public int Address { get; }

    public string Label { get; set; }

    public long Sent { get; set; }

    public long Received { get; set; }

    public long Involvement => Sent + Received;

    /// <summary>Node position in control coordinates; assigned/updated by the graph control.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public bool HasPosition { get; set; }
}

/// <summary>Aggregated traffic between two nodes (both directions combined).</summary>
public sealed class NodeGraphEdge
{
    public required NodeGraphNode From { get; init; }

    public required NodeGraphNode To { get; init; }

    public long Frames { get; set; }

    public long Forward { get; set; }

    public long Backward { get; set; }
}

public sealed class NodeGraphBroadcast
{
    public required NodeGraphNode Source { get; init; }

    public long Frames { get; init; }
}

/// <summary>Immutable view of the current graph.</summary>
public sealed class NodeGraphSnapshot
{
    public static NodeGraphSnapshot Empty { get; } = new();

    public IReadOnlyList<NodeGraphNode> Nodes { get; init; } = [];

    public IReadOnlyList<NodeGraphEdge> Edges { get; init; } = [];

    public IReadOnlyList<NodeGraphBroadcast> Broadcast { get; init; } = [];

    public long TotalFrames { get; init; }

    public long MaxEdgeFrames { get; init; }

    public long MaxBroadcastFrames { get; init; }
}

/// <summary>
/// Builds the J1939 node/communication graph either from live traffic (frames arriving while
/// the window is open) or as a one-off snapshot of the current capture/log in the main window.
/// </summary>
public sealed partial class NodeGraphViewModel : ObservableObject, IDisposable
{
    private readonly J1939NodeNames _names;
    private readonly Func<IReadOnlyList<CanFrame>> _capture;
    private readonly CanConnectionManager _manager;
    private readonly NodeTraffic _traffic = new();
    private readonly DispatcherTimer _liveTimer;
    private bool _dirty;
    private long _framesSinceRate;
    private long _rateTimestamp = Environment.TickCount64;

    public NodeGraphViewModel(
        J1939NodeNames names,
        Func<IReadOnlyList<CanFrame>> capture,
        CanConnectionManager manager)
    {
        _names = names;
        _capture = capture;
        _manager = manager;
        _names.Changed += OnNamesChanged;

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _liveTimer.Tick += (_, _) => OnLiveTick();
        _liveTimer.Start();

        // Prefer showing a loaded capture; a fresh session starts in live mode.
        _live = capture().Count == 0;
        if (_live)
        {
            Attach();
        }
        else
        {
            LoadCapture();
        }
    }

    /// <summary>True: accumulate incoming traffic. False: one-off view of the main capture.</summary>
    [ObservableProperty]
    private bool _live;

    [ObservableProperty]
    private NodeGraphSnapshot _graph = NodeGraphSnapshot.Empty;

    [ObservableProperty]
    private string _summary = "No J1939 traffic";

    [ObservableProperty]
    private string _topTalkers = string.Empty;

    [ObservableProperty]
    private double _framesPerSecond;

    /// <summary>Current user-assigned name for a node (null when unnamed).</summary>
    public string? GetNodeName(int address) => _names[address];

    /// <summary>Assigns (or clears, with an empty value) a node name from the graph.</summary>
    public void SetNodeName(int address, string? name) => _names.SetName(address, name);

    public void Dispose()
    {
        _liveTimer.Stop();
        _names.Changed -= OnNamesChanged;
        Detach();
    }

    partial void OnLiveChanged(bool value)
    {
        if (value)
        {
            // A live view starts counting from the moment it is enabled.
            _traffic.Clear();
            _framesSinceRate = 0;
            _rateTimestamp = Environment.TickCount64;
            Attach();
            Rebuild();
        }
        else
        {
            Detach();
            LoadCapture();
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        if (Live)
        {
            _traffic.Clear();
            _framesSinceRate = 0;
            _rateTimestamp = Environment.TickCount64;
            Rebuild();
        }
        else
        {
            LoadCapture();
        }
    }

    private void LoadCapture()
    {
        _traffic.Clear();
        _traffic.AddRange(_capture());
        Rebuild();
    }

    private void Attach()
    {
        _manager.FrameReceived += OnFrameReceived;
    }

    private void Detach()
    {
        _manager.FrameReceived -= OnFrameReceived;
    }

    private void OnFrameReceived(object? sender, CanFrame frame) =>
        Dispatcher.UIThread.Post(() =>
        {
            var before = _traffic.TotalFrames;
            _traffic.Add(frame);
            if (_traffic.TotalFrames != before)
            {
                _framesSinceRate++;
                _dirty = true;
            }
        });

    private void OnLiveTick()
    {
        var now = Environment.TickCount64;
        var elapsed = now - _rateTimestamp;
        if (Live && elapsed >= 1000)
        {
            FramesPerSecond = _framesSinceRate * 1000.0 / elapsed;
            _framesSinceRate = 0;
            _rateTimestamp = now;
        }

        if (_dirty)
        {
            _dirty = false;
            Rebuild();
        }
    }

    private void OnNamesChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        var names = _names;
        var nodes = new Dictionary<int, NodeGraphNode>();
        NodeGraphNode GetNode(int address)
        {
            if (!nodes.TryGetValue(address, out var node))
            {
                node = new NodeGraphNode(address, names.GetLabel(address));
                nodes[address] = node;
            }

            return node;
        }

        foreach (var (address, count) in _traffic.NodeFrames)
        {
            GetNode(address).Sent = count;
        }

        // Aggregate point-to-point traffic per unordered pair so one line represents
        // the bidirectional conversation.
        var pairs = new Dictionary<(int A, int B), NodeGraphEdge>();
        long maxEdge = 0;
        foreach (var ((source, destination), count) in _traffic.Conversations)
        {
            var from = GetNode(source);
            var to = GetNode(destination);
            to.Received += count;
            var key = source <= destination ? (source, destination) : (destination, source);
            if (!pairs.TryGetValue(key, out var edge))
            {
                edge = new NodeGraphEdge { From = from, To = to };
                pairs[key] = edge;
            }

            edge.Frames += count;
            if (ReferenceEquals(edge.From, from))
            {
                edge.Forward += count;
            }
            else
            {
                edge.Backward += count;
            }

            maxEdge = Math.Max(maxEdge, edge.Frames);
        }

        var broadcast = new List<NodeGraphBroadcast>();
        long maxBroadcast = 0;
        foreach (var (source, count) in _traffic.BroadcastFrames)
        {
            broadcast.Add(new NodeGraphBroadcast { Source = GetNode(source), Frames = count });
            maxBroadcast = Math.Max(maxBroadcast, count);
        }

        var edges = pairs.Values.Where(edge => !ReferenceEquals(edge.From, edge.To)).ToList();
        var orderedNodes = nodes.Values.OrderBy(node => node.Address).ToList();

        Graph = new NodeGraphSnapshot
        {
            Nodes = orderedNodes,
            Edges = edges,
            Broadcast = broadcast,
            TotalFrames = _traffic.TotalFrames,
            MaxEdgeFrames = maxEdge,
            MaxBroadcastFrames = maxBroadcast,
        };

        Summary = _traffic.TotalFrames == 0
            ? (Live ? "Waiting for J1939 traffic…" : "No J1939 traffic in the current capture")
            : $"{orderedNodes.Count} nodes · {edges.Count} conversations · {_traffic.TotalFrames:N0} J1939 frames";

        var talkers = orderedNodes
            .OrderByDescending(node => node.Involvement)
            .Take(4)
            .Select(node =>
            {
                var address = J1939NodeNames.FormatAddress(node.Address);
                return string.Equals(node.Label, address, StringComparison.Ordinal)
                    ? $"{address}: {node.Involvement:N0}"
                    : $"{node.Label} ({address}): {node.Involvement:N0}";
            });
        TopTalkers = orderedNodes.Count == 0
            ? string.Empty
            : "Top talkers: " + string.Join("  ·  ", talkers);
    }
}
