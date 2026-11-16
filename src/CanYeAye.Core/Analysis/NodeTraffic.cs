using CanYeAye.Core.Protocols;

namespace CanYeAye.Core.Analysis;

/// <summary>
/// Aggregates J1939 traffic per source node, per source→destination conversation and
/// per source broadcast volume. Only 29-bit (extended) identifiers carry a J1939 node
/// address; standard 11-bit frames are ignored.
/// </summary>
public sealed class NodeTraffic
{
    private readonly Dictionary<int, long> _nodeFrames = [];
    private readonly Dictionary<(int Source, int Destination), long> _conversations = [];
    private readonly Dictionary<int, long> _broadcast = [];

    public long TotalFrames { get; private set; }

    public long IgnoredFrames { get; private set; }

    /// <summary>Frames sent per source address.</summary>
    public IReadOnlyDictionary<int, long> NodeFrames => _nodeFrames;

    /// <summary>Point-to-point traffic, keyed by (source, destination).</summary>
    public IReadOnlyDictionary<(int Source, int Destination), long> Conversations => _conversations;

    /// <summary>Broadcast (PDU2 or destination 0xFF) frames per source address.</summary>
    public IReadOnlyDictionary<int, long> BroadcastFrames => _broadcast;

    public void Add(CanFrame frame)
    {
        if (!frame.IsExtended)
        {
            IgnoredFrames++;
            return;
        }

        var id = J1939Id.Decode(frame.FrameId);
        TotalFrames++;
        _nodeFrames[id.Source] = _nodeFrames.GetValueOrDefault(id.Source) + 1;

        if (id.IsBroadcast || id.Destination == 0xFF)
        {
            _broadcast[id.Source] = _broadcast.GetValueOrDefault(id.Source) + 1;
            return;
        }

        if (id.Source == id.Destination)
        {
            return;
        }

        var key = (id.Source, id.Destination);
        _conversations[key] = _conversations.GetValueOrDefault(key) + 1;
    }

    public void AddRange(IEnumerable<CanFrame> frames)
    {
        foreach (var frame in frames)
        {
            Add(frame);
        }
    }

    public void Clear()
    {
        _nodeFrames.Clear();
        _conversations.Clear();
        _broadcast.Clear();
        TotalFrames = 0;
        IgnoredFrames = 0;
    }

    /// <summary>Combined traffic between two nodes (both directions).</summary>
    public long BidirectionalFrames(int first, int second) =>
        _conversations.GetValueOrDefault((first, second)) +
        _conversations.GetValueOrDefault((second, first));
}
