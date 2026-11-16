namespace CanYeAye.Core.Analysis;

/// <summary>Which parts of the flow view a newly appended frame affected.</summary>
public readonly record struct FlowViewAppendResult(bool IsNewId, bool IsSelectedId);

/// <summary>
/// Flow-view state machine (ported from <c>re/flowviewwindow.cpp</c>): the frames of one
/// selected ID plus the reference, seek and trigger-bit state used while stepping through them.
/// Deliberately UI-independent so the stepping/seek/reference rules can be unit tested.
/// </summary>
/// <remarks>
/// SavvyCAN semantics preserved:
/// <list type="bullet">
/// <item>The reference defaults to the first frame of the selected ID.</item>
/// <item>With <see cref="AutoReference"/> the reference becomes the previously displayed frame
/// on every move, so the grid shows what changed between consecutive frames.</item>
/// <item>A byte whose seek value is 0..255 stops playback when the current frame matches.</item>
/// <item>Clicking a bit marks it as a trigger; playback stops when that bit changes.</item>
/// </list>
/// The flow view covers the first 64 payload bytes (FlexRay frames can be longer).
/// </remarks>
public sealed class FlowViewSession
{
    public const int MaxBytes = 64;

    private readonly List<CanFrame> _all = [];
    private readonly List<CanFrame> _frames = [];
    private readonly List<uint> _ids = [];
    private readonly HashSet<uint> _idSet = [];
    private readonly HashSet<int> _triggers = [];
    private readonly int[] _seek = new int[MaxBytes];
    private readonly byte[] _current = new byte[MaxBytes];
    private readonly byte[] _reference = new byte[MaxBytes];
    private readonly byte[] _scratch = new byte[MaxBytes];
    private int _position;

    public FlowViewSession()
    {
        ResetSeek();
    }

    public FlowViewSession(IEnumerable<CanFrame> frames)
        : this()
    {
        Reset(frames);
    }

    /// <summary>Every frame currently known (capture snapshot plus any live frames).</summary>
    public IReadOnlyList<CanFrame> AllFrames => _all;

    /// <summary>Frames of the selected ID.</summary>
    public IReadOnlyList<CanFrame> Frames => _frames;

    /// <summary>IDs seen in the capture, ascending.</summary>
    public IReadOnlyList<uint> Ids => _ids;

    public uint? SelectedId { get; private set; }

    /// <summary>Largest payload length of the selected ID, clamped to <see cref="MaxBytes"/>.</summary>
    public int ByteCount { get; private set; }

    public int FrameCount => _frames.Count;

    public int Position => _position;

    /// <summary>Use the previously displayed frame as the reference for change colouring.</summary>
    public bool AutoReference { get; set; } = true;

    /// <summary>True when the current frame matched a non-negative seek value.</summary>
    public bool SeekHit { get; private set; }

    /// <summary>True when a clicked trigger bit changed reaching the current frame.</summary>
    public bool TriggerBitChanged { get; private set; }

    public IReadOnlyCollection<int> Triggers => _triggers;

    /// <summary>Current frame payload, zero padded to <see cref="MaxBytes"/>.</summary>
    public byte[] CurrentBytes => _current;

    /// <summary>Reference payload used to colour changed bits.</summary>
    public byte[] ReferenceBytes => _reference;

    /// <summary>Replaces the capture, keeping the user's seek values and trigger bits.</summary>
    public void Reset(IEnumerable<CanFrame> frames)
    {
        _all.Clear();
        _frames.Clear();
        _ids.Clear();
        _idSet.Clear();
        SelectedId = null;
        ByteCount = 0;
        _position = 0;
        SeekHit = false;
        TriggerBitChanged = false;
        Array.Clear(_current);
        Array.Clear(_reference);

        foreach (var frame in frames)
        {
            _all.Add(frame);
            if (_idSet.Add(frame.FrameId))
            {
                _ids.Add(frame.FrameId);
            }
        }

        _ids.Sort();
    }

    /// <summary>
    /// Adds one live frame. IDs that are selected grow in place; other IDs only extend the
    /// ID list, so the flow view can follow live traffic without replaying it.
    /// </summary>
    public FlowViewAppendResult Append(CanFrame frame)
    {
        _all.Add(frame);
        var isNewId = _idSet.Add(frame.FrameId);
        if (isNewId)
        {
            _ids.Add(frame.FrameId);
            _ids.Sort();
        }

        var isSelected = SelectedId == frame.FrameId;
        if (isSelected)
        {
            _frames.Add(frame);
            ByteCount = Math.Max(ByteCount, Math.Min(frame.Payload.Length, MaxBytes));
        }

        return new FlowViewAppendResult(isNewId, isSelected);
    }

    /// <summary>Selects an ID and resets the position to its first frame.</summary>
    public bool Select(uint id)
    {
        SelectedId = id;
        _frames.Clear();
        ByteCount = 0;

        foreach (var frame in _all)
        {
            if (frame.FrameId != id)
            {
                continue;
            }

            _frames.Add(frame);
            ByteCount = Math.Max(ByteCount, Math.Min(frame.Payload.Length, MaxBytes));
        }

        _position = 0;
        SeekHit = false;
        TriggerBitChanged = false;
        LoadCurrent();
        Array.Copy(_current, _reference, MaxBytes);
        return _frames.Count > 0;
    }

    /// <summary>Moves to a frame index (clamped). Returns false when the ID has no frames.</summary>
    public bool MoveTo(int position)
    {
        if (_frames.Count == 0)
        {
            _position = 0;
            SeekHit = false;
            TriggerBitChanged = false;
            return false;
        }

        _position = Math.Clamp(position, 0, _frames.Count - 1);
        Fill(_frames[_position], _scratch);

        // Match processNewPosition(): the previous frame becomes the reference before the
        // new frame is loaded.
        if (AutoReference)
        {
            Array.Copy(_current, _reference, MaxBytes);
        }

        TriggerBitChanged = TriggerChanged(_current, _scratch);
        Array.Copy(_scratch, _current, MaxBytes);
        SeekHit = SeekMatched();
        return true;
    }

    /// <summary>
    /// Steps by <paramref name="delta"/> frames. Without looping, stepping past either end
    /// does nothing and returns false (which is what stops playback).
    /// </summary>
    public bool Step(int delta, bool loop)
    {
        if (_frames.Count == 0 || delta == 0)
        {
            return false;
        }

        var next = _position + delta;
        if (loop)
        {
            next = ((next % _frames.Count) + _frames.Count) % _frames.Count;
        }
        else if (next < 0 || next >= _frames.Count)
        {
            return false;
        }

        return MoveTo(next);
    }

    /// <summary>Sets a byte's seek value; -1 disables it (SavvyCAN's default).</summary>
    public bool SetSeek(int byteIndex, int value)
    {
        if (byteIndex < 0 || byteIndex >= MaxBytes)
        {
            return false;
        }

        _seek[byteIndex] = value < 0 ? -1 : Math.Min(value, 0xFF);
        return true;
    }

    public int GetSeek(int byteIndex) =>
        byteIndex < 0 || byteIndex >= MaxBytes ? -1 : _seek[byteIndex];

    /// <summary>Edits the static reference byte used when auto reference is off.</summary>
    public bool SetReferenceByte(int byteIndex, byte value)
    {
        if (byteIndex < 0 || byteIndex >= MaxBytes)
        {
            return false;
        }

        _reference[byteIndex] = value;
        return true;
    }

    /// <summary>Toggles a grid bit (0..<see cref="MaxBytes"/>*8-1) as a playback trigger.</summary>
    public bool ToggleTrigger(int bit)
    {
        if (bit < 0 || bit >= MaxBytes * 8)
        {
            return false;
        }

        if (!_triggers.Add(bit))
        {
            _triggers.Remove(bit);
            return false;
        }

        return true;
    }

    public bool IsTrigger(int bit) => _triggers.Contains(bit);

    public void ClearTriggers() => _triggers.Clear();

    private void ResetSeek()
    {
        for (var i = 0; i < MaxBytes; i++)
        {
            _seek[i] = -1;
        }
    }

    private void LoadCurrent()
    {
        Array.Clear(_current);
        Array.Clear(_reference);
        if (_frames.Count > 0)
        {
            Fill(_frames[0], _current);
        }
    }

    private static void Fill(CanFrame frame, byte[] buffer)
    {
        Array.Clear(buffer);
        var length = Math.Min(frame.Payload.Length, MaxBytes);
        if (length > 0)
        {
            Array.Copy(frame.Payload, buffer, length);
        }
    }

    private bool SeekMatched()
    {
        for (var i = 0; i < ByteCount; i++)
        {
            if (_seek[i] >= 0 && _current[i] == _seek[i])
            {
                return true;
            }
        }

        return false;
    }

    private bool TriggerChanged(byte[] previous, byte[] next)
    {
        foreach (var bit in _triggers)
        {
            var index = bit >> 3;
            if (index >= MaxBytes)
            {
                continue;
            }

            var mask = 1 << (bit & 7);
            if ((previous[index] & mask) != (next[index] & mask))
            {
                return true;
            }
        }

        return false;
    }
}
