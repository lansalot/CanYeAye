using CanYeAye.Core;

namespace CanYeAye.Core.Protocols;

/// <summary>A decoded UDS message (UDS_MESSAGE), including the raw ISO-TP payload.</summary>
public sealed class UdsMessage
{
    public required CanFrame Frame { get; init; }

    public int ReportedLength { get; set; }

    public byte Service { get; set; }

    public int SubFunc { get; set; }

    public int SubFuncLen { get; set; }

    public bool IsErrorReply { get; set; }
}

/// <summary>
/// UDS-on-ISO-TP (bus_protocols/uds_handler.cpp). Owns an <see cref="IsoTpHandler"/> with
/// reception enabled and turns reassembled messages into <see cref="UdsMessage"/> callbacks.
/// </summary>
public sealed class UdsHandler
{
    public IsoTpHandler IsoTp { get; } = new();

    public event EventHandler<UdsMessage>? MessageReceived;

    /// <summary>Raised with each CAN frame the underlying ISO-TP transport wants to send.</summary>
    public event EventHandler<CanFrame>? FrameToSend
    {
        add => IsoTp.FrameToSend += value;
        remove => IsoTp.FrameToSend -= value;
    }

    public UdsHandler()
    {
        IsoTp.SetReception(true);
        IsoTp.MessageReceived += (_, frame) =>
        {
            var message = TryIsoToUds(frame, out var valid);
            if (valid)
            {
                MessageReceived?.Invoke(this, message);
            }
        };
    }

    public void SetFlowControl(bool enabled) => IsoTp.SetFlowControl(enabled);

    public void SetReception(bool enabled) => IsoTp.SetReception(enabled);

    public void SetProcessAll(bool enabled) => IsoTp.ProcessAll = enabled;

    public void SetPaddingByte(byte value) => IsoTp.PaddingByte = value;

    public void AddFilter(int bus, uint id, uint mask) => IsoTp.AddFilter(bus, id, mask);

    public void RemoveFilter(int bus, uint id, uint mask) => IsoTp.RemoveFilter(bus, id, mask);

    public void ClearFilters() => IsoTp.ClearFilters();

    public void ProcessFrame(CanFrame frame) => IsoTp.ProcessFrame(frame);

    public void Tick() => IsoTp.Tick();

    /// <summary>Builds <c>[service][subFunc big-endian][data]</c> and hands it to ISO-TP.</summary>
    public void SendUds(int bus, uint id, int service, int subFuncLen, long subFunc, ReadOnlySpan<byte> data)
    {
        if (bus < 0 || service > 0xFF || service < 0)
        {
            return;
        }

        var payload = new byte[1 + Math.Max(0, subFuncLen) + data.Length];
        var index = 0;
        payload[index++] = (byte)service;
        for (var b = subFuncLen - 1; b >= 0; b--)
        {
            payload[index++] = (byte)((subFunc >> (8 * b)) & 0xFF);
        }

        data.CopyTo(payload.AsSpan(index));
        IsoTp.SendIsoTp(bus, id, payload);
    }

    /// <summary>Decodes one reassembled ISO-TP message; <paramref name="valid"/> is false when the
    /// payload is empty or a truncated negative response.</summary>
    public static UdsMessage TryIsoToUds(CanFrame frame, out bool valid)
    {
        var payload = frame.Payload;
        var message = new UdsMessage
        {
            Frame = frame,
            ReportedLength = payload.Length,
            SubFuncLen = 0,
        };

        valid = true;
        if (payload.Length == 0)
        {
            valid = false;
            return message;
        }

        message.Service = payload[0];
        if (message.Service == 0x7F)
        {
            message.IsErrorReply = true;
            if (payload.Length <= 1)
            {
                valid = false;
                return message;
            }

            message.Service = payload[1];
            if (payload.Length > 2)
            {
                message.SubFunc = payload[2];
            }
            else
            {
                valid = false;
                return message;
            }
        }
        else
        {
            message.IsErrorReply = false;
            if (payload.Length > 1)
            {
                message.SubFunc = payload[1];
            }
        }

        return message;
    }
}
