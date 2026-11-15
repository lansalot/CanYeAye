using System.Buffers.Binary;
using System.Text;

namespace WinCAN.Core.FileFormats;

/// <summary>
/// CANserver binary log (load only), versions v2 (marker bytes 0xCD/0xCE/0xCF) and v3
/// (0xD0/0xA0/0xB0). Records are: log marks, time-sync (uint64 base in µs) and CAN frames with
/// a per-version time-offset/ID encoding. Concatenated files are handled by re-reading headers.
/// </summary>
public sealed class CanServerLogFormat : LogFormatBase
{
    private const string V2Header = "CANSERVER_v2_CANSERVER";
    private const string V3Header = "CANSERVER_v3_CANSERVER";

    public override string Name => "CANServer Binary Log";

    public override string DefaultExtension => ".log";

    public override bool CanSave => false;

    public override bool IsMatch(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[22];
        var read = stream.ReadAtLeast(header, 22, throwOnEndOfStream: false);
        if (read != 22)
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(header);
        return text == V2Header || text == V3Header;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[22];
        if (stream.Read(header, 0, 22) != 22)
        {
            return false;
        }

        var headerText = Encoding.ASCII.GetString(header);
        var version = headerText switch
        {
            V2Header => 1,
            V3Header => 2,
            _ => 0,
        };
        if (version == 0)
        {
            return false;
        }

        var timeStampBase = 0UL;
        var single = new byte[1];
        var timeSync = new byte[8];

        while (stream.Position < stream.Length)
        {
            if (stream.Read(single, 0, 1) != 1)
            {
                break;
            }

            var marker = single[0];
            if (marker == (byte)'C')
            {
                var position = stream.Position;
                var possible = new byte[21];
                if (stream.Read(possible, 0, 21) == 21)
                {
                    var text = Encoding.ASCII.GetString(possible);
                    if (text == "ANSERVER_v2_CANSERVER")
                    {
                        version = 1;
                        continue;
                    }

                    if (text == "ANSERVER_v3_CANSERVER")
                    {
                        version = 2;
                        continue;
                    }
                }

                stream.Position = position;
            }
            else if ((version == 1 && marker == 0xCD) || (version == 2 && marker == 0xD0))
            {
                if (stream.Read(single, 0, 1) != 1)
                {
                    break;
                }

                Skip(stream, single[0]);
            }
            else if ((version == 1 && marker == 0xCE) || (version == 2 && marker == 0xA0))
            {
                if (stream.Read(timeSync, 0, 8) != 8)
                {
                    break;
                }

                timeStampBase = BinaryPrimitives.ReadUInt64LittleEndian(timeSync);
            }
            else if ((version == 1 && marker == 0xCF) || (version == 2 && (marker & 0xF0) == 0xB0))
            {
                var frameHeader = new byte[5];
                if (stream.Read(frameHeader, 0, 5) != 5)
                {
                    break;
                }

                uint timeOffset;
                uint messageId;
                if (version == 1)
                {
                    timeOffset = (uint)(((frameHeader[1] << 8) | frameHeader[0]) * 1000);
                    messageId = (uint)((frameHeader[3] << 8) | frameHeader[2]);
                }
                else
                {
                    timeOffset = (uint)((marker & 0x0F)
                        + (frameHeader[0] << 4)
                        + (frameHeader[1] << 12)
                        + (((frameHeader[2] & 0xF8) >> 3) << 20));
                    messageId = (uint)(((frameHeader[2] & 0x07) << 8) + frameHeader[3]);
                }

                var length = Math.Min(frameHeader[4] & 0x0F, 8);
                var bus = frameHeader[4] >> 4;
                var payload = new byte[length];
                if (length > 0 && stream.Read(payload, 0, length) != length)
                {
                    break;
                }

                var frame = new CanFrame();
                frame.SetFrameType(FrameType.CanDataFrame);
                frame.SetFrameId(messageId);
                frame.SetReceived(true);
                frame.Bus = (byte)bus;
                frame.Timestamp = FrameTimestamp.FromMicroseconds((long)(timeStampBase + timeOffset));
                frame.SetPayload(payload);
                frames.Add(frame);
            }
        }

        return true;
    }

    private static void Skip(Stream stream, int count)
    {
        var remaining = count;
        var scratch = new byte[256];
        while (remaining > 0)
        {
            var read = stream.Read(scratch, 0, Math.Min(scratch.Length, remaining));
            if (read <= 0)
            {
                return;
            }

            remaining -= read;
        }
    }
}
