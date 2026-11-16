namespace CanYeAye.Core;

/// <summary>
/// Thread-safe in-memory frame store used by capture, file loading and playback.
/// Keeps insertion order and a snapshot for UI binding.
/// </summary>
public sealed class FrameStore
{
    private readonly List<CanFrame> _frames = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _frames.Count;
            }
        }
    }

    public void Add(CanFrame frame)
    {
        lock (_lock)
        {
            _frames.Add(frame);
        }
    }

    public void AddRange(IEnumerable<CanFrame> frames)
    {
        lock (_lock)
        {
            _frames.AddRange(frames);
        }
    }

    public IReadOnlyList<CanFrame> Snapshot()
    {
        lock (_lock)
        {
            return _frames.ToArray();
        }
    }

    public CanFrame[] SnapshotRange(int start, int count)
    {
        lock (_lock)
        {
            start = Math.Clamp(start, 0, _frames.Count);
            count = Math.Clamp(count, 0, _frames.Count - start);
            return _frames.GetRange(start, count).ToArray();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _frames.Clear();
        }
    }
}
