using System.Buffers.Binary;
using System.Globalization;
using CanYeAye.Core;

namespace CanYeAye.Connections.Protocols;

/// <summary>
/// MQTT frame payload and topic grammar (connections/mqtt_bus.cpp).
/// <para>
/// Payload: 8-byte little-endian microsecond timestamp, one flag byte, then the payload bytes.
/// Flags: 1 = extended, 2 = remote, 4 = CAN FD, 8 = error, 16 = bit-rate switch, 32 = ESI.
/// Topics: frames are published to <c>&lt;base&gt;/s/&lt;id decimal&gt;</c> and received from
/// <c>&lt;base&gt;/+</c> (i.e. <c>&lt;base&gt;/&lt;id decimal&gt;</c>).
/// </para>
/// <para>
/// The reference reads the id from the second topic segment unconditionally, so an
/// <c>&lt;base&gt;/s/&lt;id&gt;</c> echo becomes id 0 (its subscription cannot match that topic
/// anyway). CanYeAye uses the third segment for the send channel, which is what the topic layout
/// implies.
/// </para>
/// </summary>
public static class MqttFrameCodec
{
    public const byte FlagExtended = 0x01;
    public const byte FlagRemote = 0x02;
    public const byte FlagFd = 0x04;
    public const byte FlagError = 0x08;
    public const byte FlagBitrateSwitch = 0x10;
    public const byte FlagErrorStateIndicator = 0x20;

    /// <summary>Serialises a frame; the timestamp is always the current wall clock.</summary>
    public static byte[] Encode(CanFrame frame, long nowMicroseconds)
    {
        var payload = new byte[9 + frame.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, (ulong)nowMicroseconds);

        byte flags = 0;
        if (frame.IsExtended)
        {
            flags |= FlagExtended;
        }

        if (frame.FrameType == FrameType.RemoteRequestFrame)
        {
            flags |= FlagRemote;
        }

        if (frame.IsFlexibleDataRate || frame.FrameType == FrameType.CanFdDataFrame)
        {
            flags |= FlagFd;
        }

        if (frame.FrameType == FrameType.ErrorFrame)
        {
            flags |= FlagError;
        }

        if (frame.HasBitrateSwitch)
        {
            flags |= FlagBitrateSwitch;
        }

        if (frame.HasErrorStateIndicator)
        {
            flags |= FlagErrorStateIndicator;
        }

        payload[8] = flags;
        frame.Payload.CopyTo(payload, 9);
        return payload;
    }

    /// <summary>Rebuilds a frame from a topic id and payload, or returns null when too short.</summary>
    public static CanFrame? Decode(
        uint frameId,
        ReadOnlySpan<byte> payload,
        bool useSystemTime,
        long nowMicroseconds)
    {
        if (payload.Length < 8)
        {
            return null;
        }

        var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        var flags = payload.Length >= 9 ? payload[8] : (byte)0;
        var data = payload.Length > 9 ? payload[9..] : ReadOnlySpan<byte>.Empty;

        var frame = new CanFrame();
        frame.SetFrameId(frameId);
        frame.Bus = 0;
        frame.SetReceived(true);
        frame.SetExtended((flags & FlagExtended) != 0);

        if ((flags & FlagError) != 0)
        {
            frame.SetFrameType(FrameType.ErrorFrame);
        }
        else if ((flags & FlagRemote) != 0)
        {
            frame.SetFrameType(FrameType.RemoteRequestFrame);
        }
        else if ((flags & FlagFd) != 0)
        {
            frame.SetFrameType(FrameType.CanFdDataFrame);
            frame.SetFlexibleDataRate(true);
            frame.SetBitrateSwitch((flags & FlagBitrateSwitch) != 0);
            frame.SetErrorStateIndicator((flags & FlagErrorStateIndicator) != 0);
        }
        else
        {
            frame.SetFrameType(FrameType.CanDataFrame);
        }

        frame.Timestamp = useSystemTime
            ? FrameTimestamp.FromMicroseconds(nowMicroseconds)
            : FrameTimestamp.FromMicroseconds((long)timestamp);
        frame.SetPayload(data);

        return frame;
    }

    /// <summary>Topic used when publishing a frame (the <c>s</c> segment marks the send channel).</summary>
    public static string FrameTopic(string baseTopic, uint frameId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{baseTopic}/s/{frameId}");

    /// <summary>Subscription filter: one wildcard level below the base topic.</summary>
    public static string SubscriptionTopic(string baseTopic) => baseTopic + "/+";

    /// <summary>
    /// Extracts the frame id from a received topic. Accepts <c>&lt;base&gt;/&lt;id&gt;</c> and
    /// <c>&lt;base&gt;/s/&lt;id&gt;</c>.
    /// </summary>
    public static uint? ParseTopicFrameId(string topic)
    {
        var parts = topic.Split('/');
        if (parts.Length < 2)
        {
            return null;
        }

        var idPart = parts.Length >= 3 && parts[1] == "s" ? parts[2] : parts[1];
        return uint.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>All-zero client id of the requested length (A-Z, a-z, 0-9 alphabet).</summary>
    public static string CreateClientId(int length = 12, Random? random = null)
    {
        random ??= Random.Shared;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var buffer = new char[length];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = alphabet[random.Next(alphabet.Length)];
        }

        return new string(buffer);
    }
}
