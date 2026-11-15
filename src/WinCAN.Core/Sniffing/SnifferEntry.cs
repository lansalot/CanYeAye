using WinCAN.Core;

namespace WinCAN.Core.Sniffing;

/// <summary>How a data byte changed since the previous frame of the same ID.</summary>
public enum SnifferByteChange
{
    None,
    Increased,
    Decreased,
}

/// <summary>
/// Per-ID state for the sniffer window (re/sniffer/snifferitem.cpp): last/current payload,
/// changed-bit markers, notch masks, inter-frame timing and a rolling arrival rate.
/// Time is supplied by the caller (monotonic milliseconds) so the behaviour is testable.
/// </summary>
public sealed class SnifferEntry
{
    public const int MaxBytes = 64;
    private const long RateWindowMs = 1000;

    private readonly byte[] _current = new byte[MaxBytes];
    private readonly byte[] _last = new byte[MaxBytes];
    private readonly byte[] _marker = new byte[MaxBytes];
    private readonly byte[] _lastMarker = new byte[MaxBytes];
    private readonly byte[] _notch = new byte[MaxBytes];
    private readonly uint[] _currentSequence = new uint[MaxBytes];
    private readonly LinkedList<long> _arrivals = [];

    public SnifferEntry(CanFrame frame, long nowMs, uint sequence)
    {
        Id = frame.FrameId;
        Extended = frame.IsExtended;
        Bus = frame.Bus;
        FirstSeenMs = nowMs;
        LastSeenMs = nowMs;
        FrameCount = 1;
        CurrentSequence = sequence;
        Length = Math.Min(frame.Length, MaxBytes);
        var payload = frame.Payload;
        for (var i = 0; i < Length; i++)
        {
            _current[i] = payload[i];
            _currentSequence[i] = sequence;
        }

        _previousFrameUs = frame.Timestamp.ToMicroseconds();
        _arrivals.AddLast(nowMs);
    }

    public uint Id { get; }

    public bool Extended { get; }

    public byte Bus { get; }

    /// <summary>Current payload length (bytes beyond this are not displayed).</summary>
    public int Length { get; private set; }

    public long FrameCount { get; private set; }

    /// <summary>Device timestamp difference between the last two frames (0 for the first frame).</summary>
    public long DeltaMicroseconds { get; private set; }

    public long FirstSeenMs { get; }

    public long LastSeenMs { get; private set; }

    /// <summary>Sequence of the most recent update (used for byte fade).</summary>
    public uint CurrentSequence { get; private set; }

    public long ElapsedMs(long nowMs) => Math.Max(0, nowMs - LastSeenMs);

    /// <summary>Observed arrival rate over the last second (frames/second).</summary>
    public double RateHz(long nowMs)
    {
        while (_arrivals.First is { } first && nowMs - first.Value >= RateWindowMs)
        {
            _arrivals.RemoveFirst();
        }

        var window = Math.Min(RateWindowMs, nowMs - FirstSeenMs);
        if (window <= 0 || _arrivals.Count == 0)
        {
            return 0;
        }

        return _arrivals.Count * 1000.0 / window;
    }

    /// <summary>Data byte value, or -1 when the index is past the current payload.</summary>
    public int Data(int index) => index >= 0 && index < Length ? _current[index] : -1;

    /// <summary>Previous frame's value for a byte, or -1 when the index is past the payload.</summary>
    public int LastData(int index) => index >= 0 && index < Length ? _last[index] : -1;

    /// <summary>
    /// Whether the byte changed (and in which direction) since the previous frame, honouring
    /// the notch mask and only colouring bits that changed during the previous notch window.
    /// </summary>
    public SnifferByteChange ChangeFor(int index)
    {
        if (index < 0 || index >= Length)
        {
            return SnifferByteChange.None;
        }

        if (_lastMarker[index] == 0)
        {
            return SnifferByteChange.None;
        }

        var notch = _notch[index];
        var value = _current[index];
        var last = _last[index];
        if (notch == 0)
        {
            return value >= last ? SnifferByteChange.Increased : SnifferByteChange.Decreased;
        }

        value = (byte)(value & ~notch);
        if (value == 0)
        {
            return SnifferByteChange.None;
        }

        last = (byte)(last & ~notch);
        if (value == last)
        {
            return SnifferByteChange.None;
        }

        return value >= last ? SnifferByteChange.Increased : SnifferByteChange.Decreased;
    }

    /// <summary>Notched (ignored) bits for a data byte.</summary>
    public byte NotchMask(int index) => index >= 0 && index < MaxBytes ? _notch[index] : (byte)0;

    /// <summary>
    /// Fade amount 0..0.88 for a byte that has not changed for several sequence ticks,
    /// mirroring SavvyCAN's <c>seqInterval * 10</c> per 200 ms tick.
    /// </summary>
    public double FadeFactor(int index, uint sequence, int tickMs)
    {
        if (index < 0 || index >= Length)
        {
            return 0;
        }

        var ticks = sequence >= _currentSequence[index] ? sequence - _currentSequence[index] : 0;
        var value = ticks * tickMs * 0.05; // 200 ms tick => *10, like the reference
        value = Math.Clamp(value, 0, 225);
        return value / 255.0;
    }

    /// <summary>Applies a received frame. <paramref name="muteNotched"/> hides notched-bit-only updates.</summary>
    public void Update(CanFrame frame, long nowMs, uint sequence, bool muteNotched)
    {
        FrameCount++;
        CurrentSequence = sequence;

        DeltaMicroseconds = Math.Max(0, frame.Timestamp.ToMicroseconds() - _previousFrameUs);
        _previousFrameUs = frame.Timestamp.ToMicroseconds();

        Array.Copy(_current, _last, MaxBytes);
        var payload = frame.Payload;
        Length = Math.Min(payload.Length, MaxBytes);
        for (var i = 0; i < Length; i++)
        {
            var value = payload[i];
            var compareValue = value;
            var compareCurrent = _current[i];
            if (muteNotched)
            {
                compareValue = (byte)(value & ~_notch[i]);
                compareCurrent = (byte)(_current[i] & ~_notch[i]);
            }

            if (compareCurrent != compareValue)
            {
                _current[i] = value;
                _currentSequence[i] = sequence;
            }
        }

        for (var i = 0; i < MaxBytes; i++)
        {
            _marker[i] |= (byte)(_last[i] ^ _current[i]);
        }

        LastSeenMs = nowMs;
        _arrivals.AddLast(nowMs);
        while (_arrivals.First is { } first && nowMs - first.Value >= RateWindowMs)
        {
            _arrivals.RemoveFirst();
        }
    }

    /// <summary>Closes the current notch window: the bits that changed become "last marker" bits.</summary>
    public void RotateMarker()
    {
        Array.Copy(_marker, _lastMarker, MaxBytes);
        Array.Clear(_marker);
    }

    /// <summary>Adds the bits changed during the last notch window to the ignore mask.</summary>
    public void Notch()
    {
        for (var i = 0; i < MaxBytes; i++)
        {
            _notch[i] |= _lastMarker[i];
        }
    }

    /// <summary>Clears every notch mask.</summary>
    public void Unnotch() => Array.Clear(_notch);

    private long _previousFrameUs;
}
