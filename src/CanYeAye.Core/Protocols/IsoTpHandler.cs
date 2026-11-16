using CanYeAye.Core;

namespace CanYeAye.Core.Protocols;

/// <summary>A reassembled ISO-TP message (ISOTP_MESSAGE): a CAN frame with the full payload.</summary>
public sealed class IsoTpMessage
{
    public required CanFrame Frame { get; init; }

    public int ReportedLength { get; set; }

    public int LastSequence { get; set; }

    public bool IsMultiframe { get; set; }
}

/// <summary>
/// ISO-TP (ISO 15765-2) transport (bus_protocols/isotp_handler.cpp). Handles single and
/// multi-frame reception, flow control and segmented transmission. The reference drives
/// transmission from a QTimer; CanYeAye exposes <see cref="Tick"/> so the owner controls timing.
/// </summary>
public sealed class IsoTpHandler
{
    private const int CanFrameSize = 8;

    private readonly List<(int Bus, uint Id, uint Mask)> _filters = [];
    private readonly Dictionary<uint, IsoTpMessage> _messageBuffer = [];
    private readonly Queue<CanFrame> _sendingFrames = new();

    private bool _waitingForFlow;
    private int _framesUntilFlow = -1;

    public bool ExtendedAddressing { get; set; }

    public bool ProcessAll { get; set; }

    public bool EmitPartials { get; set; }

    public bool FlowControl { get; set; }

    public byte PaddingByte { get; set; } = 0xAA;

    public bool Receiving { get; private set; }

    public uint LastSenderId { get; private set; }

    public uint LastSenderBus { get; private set; }

    /// <summary>Raised with the reassembled single/multi-frame message.</summary>
    public event EventHandler<CanFrame>? MessageReceived;

    /// <summary>Raised with each CAN frame the transport wants to transmit.</summary>
    public event EventHandler<CanFrame>? FrameToSend;

    private int PciOffset => ExtendedAddressing ? 2 : 1;

    private int SingleFrameLength => CanFrameSize - PciOffset;

    private int FirstFramePciOffset => ExtendedAddressing ? 3 : 2;

    private int FirstFrameLength => CanFrameSize - FirstFramePciOffset;

    private int ConsecutiveFrameLength => CanFrameSize - PciOffset;

    public void SetReception(bool enabled) => Receiving = enabled;

    public void SetFlowControl(bool enabled) => FlowControl = enabled;

    public void AddFilter(int bus, uint id, uint mask)
    {
        if (!_filters.Any(f => f.Bus == bus && f.Id == id && f.Mask == mask))
        {
            _filters.Add((bus, id, mask));
        }
    }

    public void RemoveFilter(int bus, uint id, uint mask) =>
        _filters.RemoveAll(f => f.Bus == bus && f.Id == id && f.Mask == mask);

    public void ClearFilters() => _filters.Clear();

    /// <summary>Feeds a received CAN frame; filters apply unless <see cref="ProcessAll"/> is set.</summary>
    public void ProcessFrame(CanFrame frame)
    {
        if (!Receiving)
        {
            return;
        }

        if (!ProcessAll &&
            !_filters.Any(f => frame.Bus == f.Bus && (frame.FrameId & f.Mask) == f.Id))
        {
            return;
        }

        var payload = frame.Payload;
        if (payload.Length == 0)
        {
            return;
        }

        uint id = frame.FrameId;
        int frameType;
        int frameLength;
        if (ExtendedAddressing)
        {
            if (payload.Length < 2)
            {
                return;
            }

            id = (id << 8) + payload[0];
            frameType = payload[1] >> 4;
            frameLength = payload[1] & 0x0F;
        }
        else
        {
            frameType = payload[0] >> 4;
            frameLength = payload[0] & 0x0F;
        }

        switch (frameType)
        {
            case 0:
                ProcessSingleFrame(frame, id, frameLength);
                break;
            case 1:
                ProcessFirstFrame(frame, id, frameLength);
                break;
            case 2:
                ProcessConsecutiveFrame(frame, id);
                break;
            case 3:
                ProcessFlowControl(payload, frameLength);
                break;
        }
    }

    /// <summary>
    /// Sends an ISO-TP payload: single frame when it fits, otherwise a first frame plus queued
    /// consecutive frames flushed by <see cref="Tick"/> (after flow control).
    /// </summary>
    public void SendIsoTp(int bus, uint id, ReadOnlySpan<byte> data)
    {
        if (bus < 0)
        {
            return;
        }

        LastSenderId = id;
        LastSenderBus = (uint)bus;

        var frame = new CanFrame();
        frame.SetFrameType(FrameType.CanDataFrame);
        frame.Bus = (byte)bus;
        frame.SetFrameId(id);
        frame.SetExtended(id > 0x7FF);

        if (data.Length < CanFrameSize)
        {
            var bytes = new byte[CanFrameSize];
            Array.Fill(bytes, PaddingByte);
            bytes[0] = (byte)data.Length;
            data.CopyTo(bytes.AsSpan(1));
            frame.SetPayload(bytes);
            FrameToSend?.Invoke(this, frame);
            return;
        }

        var first = new byte[CanFrameSize];
        Array.Fill(first, PaddingByte);
        first[0] = (byte)(0x10 + (data.Length / 256));
        first[1] = (byte)(data.Length & 0xFF);
        data[..6].CopyTo(first.AsSpan(2));
        frame.SetPayload(first);
        FrameToSend?.Invoke(this, frame);

        _waitingForFlow = true;
        var current = 6;
        var sequence = 1;
        while (current < data.Length)
        {
            var bytes = new byte[CanFrameSize];
            Array.Fill(bytes, PaddingByte);
            bytes[0] = (byte)(0x20 + sequence);
            sequence = (sequence + 1) & 0x0F;
            var count = Math.Min(7, data.Length - current);
            data.Slice(current, count).CopyTo(bytes.AsSpan(1));
            current += count;

            var queued = frame.Clone();
            queued.SetPayload(bytes);
            _sendingFrames.Enqueue(queued);
        }
    }

    /// <summary>Sends at most one queued consecutive frame when flow control allows it.</summary>
    public void Tick()
    {
        if (_waitingForFlow || _sendingFrames.Count == 0)
        {
            return;
        }

        var frame = _sendingFrames.Dequeue();
        FrameToSend?.Invoke(this, frame);

        if (_framesUntilFlow > 0)
        {
            _framesUntilFlow--;
            if (_framesUntilFlow == 0)
            {
                _waitingForFlow = true;
            }
        }
    }

    public bool HasPendingTransmit => _sendingFrames.Count > 0;

    private void ProcessSingleFrame(CanFrame frame, uint id, int frameLength)
    {
        FlushIfComplete(id);

        if (frameLength == 0 || frameLength > SingleFrameLength)
        {
            return;
        }

        var payload = frame.Payload;
        var start = PciOffset;
        var count = Math.Min(frameLength, payload.Length - start);
        var data = count <= 0 ? [] : payload.AsSpan(start, count).ToArray();

        var message = CloneHeader(frame, id);
        message.SetPayload(data);
        MessageReceived?.Invoke(this, message);
    }

    private void ProcessFirstFrame(CanFrame frame, uint id, int frameLength)
    {
        FlushIfComplete(id);
        if (frame.Payload.Length < CanFrameSize)
        {
            return;
        }

        var payload = frame.Payload;
        var reported = ((frameLength << 8) + (ExtendedAddressing ? payload[2] : payload[1])) & 0xFFF;

        var data = payload.AsSpan(FirstFramePciOffset, FirstFrameLength).ToArray();

        var message = new IsoTpMessage
        {
            Frame = CloneHeader(frame, id),
            ReportedLength = reported,
            LastSequence = -1,
            IsMultiframe = true,
        };
        message.Frame.SetPayload(data);
        _messageBuffer[id] = message;

        if (FlowControl && LastSenderId > 0 && LastSenderBus == frame.Bus)
        {
            var flow = new CanFrame();
            flow.SetFrameType(FrameType.CanDataFrame);
            flow.Bus = (byte)LastSenderBus;
            flow.SetFrameId(LastSenderId);
            flow.SetExtended(false);
            flow.SetPayload(new byte[] { 0x30, 0x00, 0x03, 0, 0, 0, 0, 0 });
            FrameToSend?.Invoke(this, flow);
        }
    }

    private void ProcessConsecutiveFrame(CanFrame frame, uint id)
    {
        if (!_messageBuffer.TryGetValue(id, out var message) || !message.IsMultiframe)
        {
            return;
        }

        var payload = frame.Payload;
        var existing = message.Frame.Payload;
        var remaining = message.ReportedLength - existing.Length;
        if (remaining <= 0)
        {
            return;
        }

        var count = Math.Min(remaining, Math.Min(ConsecutiveFrameLength, payload.Length - PciOffset));
        if (count <= 0)
        {
            return;
        }

        var combined = new byte[existing.Length + count];
        existing.CopyTo(combined, 0);
        payload.AsSpan(PciOffset, count).CopyTo(combined.AsSpan(existing.Length));
        message.Frame.SetPayload(combined);
        message.LastSequence = payload[0] & 0x0F;

        if (message.ReportedLength <= message.Frame.Length)
        {
            FlushIfComplete(message.Frame.FrameId);
        }
    }

    private void ProcessFlowControl(byte[] payload, int flowType)
    {
        switch (flowType)
        {
            case 0:
                _waitingForFlow = false;
                _framesUntilFlow = payload.Length > 1 && payload[1] != 0 ? payload[1] : -1;
                break;
            case 1:
                _waitingForFlow = true;
                break;
            case 2:
                _sendingFrames.Clear();
                _waitingForFlow = false;
                break;
        }
    }

    private void FlushIfComplete(uint id)
    {
        if (!_messageBuffer.TryGetValue(id, out var message))
        {
            return;
        }

        if (message.ReportedLength <= message.Frame.Length)
        {
            if (message.ReportedLength > 0)
            {
                MessageReceived?.Invoke(this, message.Frame);
            }
        }
        else if (EmitPartials && message.ReportedLength > 0)
        {
            MessageReceived?.Invoke(this, message.Frame);
        }

        _messageBuffer.Remove(id);
    }

    private static CanFrame CloneHeader(CanFrame source, uint id)
    {
        var frame = new CanFrame();
        frame.SetFrameType(FrameType.CanDataFrame);
        frame.Bus = source.Bus;
        frame.SetFrameId(id);
        frame.SetExtended(source.IsExtended);
        frame.SetReceived(source.IsReceived);
        frame.Timestamp = source.Timestamp;
        return frame;
    }
}
