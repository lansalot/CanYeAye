using System.Buffers.Binary;

namespace WinCAN.Core.FileFormats;

/// <summary>
/// Wireshark/tcpdump capture (load only). Reads classic pcap and pcapng and decodes Linux
/// CAN frames from DLT_CAN_SOCKETCAN (227) captures, and from Linux cooked captures (113) that
/// wrap a can_frame after their 16-byte pseudo-header - the two layouts SavvyCAN understands.
/// Timestamps are made relative to the first packet.
/// </summary>
public sealed class PcapFormat : LogFormatBase
{
    private const int LinkTypeSocketCan = 227;
    private const int LinkTypeLinuxSll = 113;
    private const int LinkTypeLinuxSll2 = 276;

    private sealed record Packet(long TimestampMicroseconds, int LinkType, byte[] Data);

    public override string Name => "Wireshark";

    public override string DefaultExtension => ".pcap";

    public override bool CanSave => false;

    public override bool IsMatch(string path)
    {
        if (!TryReadPackets(path, out var packets, out var linkTypes))
        {
            return false;
        }

        return linkTypes.Count > 0 && linkTypes.All(IsCanLinkType) && packets.Count > 0;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        if (!TryReadPackets(path, out var packets, out var linkTypes) || !linkTypes.All(IsCanLinkType))
        {
            return false;
        }

        var start = packets.Count > 0 ? packets[0].TimestampMicroseconds : 0;
        foreach (var packet in packets)
        {
            if (DecodeCanFrame(packet, start) is { } frame)
            {
                frames.Add(frame);
            }
        }

        return true;
    }

    private static bool IsCanLinkType(int linkType) => linkType is LinkTypeSocketCan or LinkTypeLinuxSll or LinkTypeLinuxSll2;

    private static CanFrame? DecodeCanFrame(Packet packet, long startTimestamp)
    {
        var data = packet.Data;
        var offset = packet.LinkType switch
        {
            LinkTypeSocketCan => 0,
            LinkTypeLinuxSll => 16,
            LinkTypeLinuxSll2 => 20,
            _ => -1,
        };
        if (offset < 0 || data.Length < offset + 8)
        {
            return null;
        }

        var canId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        var frame = new CanFrame();
        frame.Bus = 0;
        frame.Timestamp = FrameTimestamp.FromMicroseconds(packet.TimestampMicroseconds - startTimestamp);

        if ((canId & 0x80000000u) != 0)
        {
            frame.SetExtended(true);
            frame.SetFrameId(canId & 0x1FFFFFFFu);
        }
        else
        {
            frame.SetExtended(false);
            frame.SetFrameId(canId & 0x7FFu);
        }

        if ((canId & 0x20000000u) != 0)
        {
            frame.SetFrameType(FrameType.ErrorFrame);
        }
        else if ((canId & 0x40000000u) != 0)
        {
            frame.SetFrameType(FrameType.RemoteRequestFrame);
        }
        else
        {
            frame.SetFrameType(FrameType.CanDataFrame);
        }

        // The direction byte is only meaningful in raw SocketCAN captures.
        frame.SetReceived(packet.LinkType != LinkTypeSocketCan || data[offset + 6] != 1);

        var flags = data[offset + 5];
        var available = data.Length - offset;
        var isFd = (canId & 0x60000000u) == 0 && (available > 16 || (flags & 0x04) != 0);
        var length = (int)data[offset + 4];
        length = Math.Min(length, isFd ? 64 : 8);
        length = Math.Min(length, Math.Max(0, available - 8));

        frame.SetPayload(data.AsSpan(offset + 8, length));
        if (isFd)
        {
            frame.SetFlexibleDataRate(true);
            frame.SetBitrateSwitch((flags & 0x01) != 0);
            frame.SetErrorStateIndicator((flags & 0x02) != 0);
        }

        return frame;
    }

    private static bool TryReadPackets(string path, out List<Packet> packets, out List<int> linkTypes)
    {
        packets = [];
        linkTypes = [];
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return false;
        }

        if (content.Length < 4)
        {
            return false;
        }

        if (content[0] == 0x0A && content[1] == 0x0D && content[2] == 0x0D && content[3] == 0x0A)
        {
            return TryReadPcapNg(content, packets, linkTypes);
        }

        return TryReadPcap(content, packets, linkTypes);
    }

    private static bool TryReadPcap(byte[] content, List<Packet> packets, List<int> linkTypes)
    {
        if (content.Length < 24)
        {
            return false;
        }

        var bigEndian = content[0] == 0xA1 && content[1] == 0xB2;
        var nanosecond = (content[0] == 0xA1 && content[1] == 0xB2 && content[2] == 0x3C)
            || (content[0] == 0x4D && content[1] == 0x3C && content[2] == 0xB2);

        uint ReadUInt32(int offset, bool useBigEndian) => useBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(offset));

        var linkType = (int)ReadUInt32(20, bigEndian) & 0x0FFFFFFF;
        linkTypes.Add(linkType);

        var position = 24;
        while (position + 16 <= content.Length)
        {
            var seconds = ReadUInt32(position, bigEndian);
            var fraction = ReadUInt32(position + 4, bigEndian);
            var included = ReadUInt32(position + 8, bigEndian);
            position += 16;

            if (included > int.MaxValue || position + (int)included > content.Length)
            {
                break;
            }

            var timestamp = (long)seconds * 1_000_000L + (nanosecond ? fraction / 1000L : fraction);
            packets.Add(new Packet(timestamp, linkType, content.AsSpan(position, (int)included).ToArray()));
            position += (int)included;
        }

        return true;
    }

    private static bool TryReadPcapNg(byte[] content, List<Packet> packets, List<int> linkTypes)
    {
        var position = 0;
        var bigEndian = false;
        var interfaces = new List<(int LinkType, long TicksPerSecond)>();

        while (position + 12 <= content.Length)
        {
            var blockType = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(position));
            uint ReadUInt32(int offset)
            {
                var slice = content.AsSpan(position + offset);
                return bigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(slice)
                    : BinaryPrimitives.ReadUInt32LittleEndian(slice);
            }

            if (blockType == 0x0A0D0D0A)
            {
                // Section header: byte-order magic decides the section's endianness.
                var magic = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(position + 8));
                bigEndian = magic == 0x1A2B3C4D;
            }

            var blockLength = ReadUInt32(4);
            if (blockLength < 12 || blockLength > int.MaxValue || position + (int)blockLength > content.Length)
            {
                break;
            }

            var bodyOffset = position + 8;
            var bodyLength = (int)blockLength - 12;
            switch (blockType)
            {
                case 1: // Interface Description Block
                {
                    var linkType = bigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(bodyOffset))
                        : BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(bodyOffset));
                    var ticksPerSecond = 1_000_000L; // default: microseconds
                    var optionsOffset = bodyOffset + 8;
                    var optionsEnd = bodyOffset + bodyLength;
                    while (optionsOffset + 4 <= optionsEnd)
                    {
                        var code = bigEndian
                            ? BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(optionsOffset))
                            : BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(optionsOffset));
                        var length = bigEndian
                            ? BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(optionsOffset + 2))
                            : BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(optionsOffset + 2));
                        optionsOffset += 4;
                        if (code == 0)
                        {
                            break;
                        }

                        if (code == 9 && length >= 1 && optionsOffset < optionsEnd)
                        {
                            var value = content[optionsOffset];
                            ticksPerSecond = (value & 0x80) != 0
                                ? 1L << (value & 0x7F)
                                : (long)Math.Pow(10, value);
                        }

                        optionsOffset += (length + 3) & ~3;
                    }

                    interfaces.Add((linkType, ticksPerSecond));
                    linkTypes.Add(linkType);
                    break;
                }

                case 6: // Enhanced Packet Block
                {
                    var interfaceId = ReadUInt32(8);
                    var timestampHigh = ReadUInt32(12);
                    var timestampLow = ReadUInt32(16);
                    var capturedLength = ReadUInt32(20);
                    var dataOffset = bodyOffset + 20;
                    if (capturedLength > int.MaxValue || dataOffset + (int)capturedLength > content.Length)
                    {
                        return false;
                    }

                    var ticks = ((long)timestampHigh << 32) | timestampLow;
                    var ticksPerSecond = interfaceId < interfaces.Count
                        ? interfaces[(int)interfaceId].TicksPerSecond
                        : 1_000_000L;
                    var linkType = interfaceId < interfaces.Count
                        ? interfaces[(int)interfaceId].LinkType
                        : -1;
                    var microseconds = ticksPerSecond == 1_000_000L
                        ? ticks
                        : (long)(ticks * 1_000_000d / ticksPerSecond);

                    packets.Add(new Packet(
                        microseconds,
                        linkType,
                        content.AsSpan(dataOffset, (int)capturedLength).ToArray()));
                    break;
                }
            }

            position += (int)blockLength;
        }

        return linkTypes.Count > 0;
    }
}

