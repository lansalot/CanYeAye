namespace CanYeAye.Core.FileFormats;

/// <summary>
/// CAN-DO binary log: 12-byte records with a minute/millisecond timestamp, 11-bit ID and up to
/// 8 data bytes padded with 0xFF. A record whose ID/length bytes are both 0xFF is a time header.
/// </summary>
public sealed class CanDoFormat : LogFormatBase
{
    private const int RecordSize = 12;

    public override string Name => "CAN-DO Log";

    public override string DefaultExtension => ".can";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[RecordSize];
            var read = 0;

            while (read < 200)
            {
                var count = stream.Read(buffer, 0, RecordSize);
                if (count == 0)
                {
                    break;
                }

                if (count < RecordSize)
                {
                    return false;
                }

                read++;
                var idLow = buffer[2];
                var idHigh = buffer[3];
                if (idLow == 0xFF && idHigh == 0xFF)
                {
                    continue;
                }

                var length = idHigh >> 4;
                var id = ((idHigh & 0x0F) << 8) | idLow;
                if (length > 8 || id > 0x7FF)
                {
                    return false;
                }

                if (length < 8 && buffer[4 + length] != 0xFF)
                {
                    return false;
                }
            }

            return read > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var bytes = File.ReadAllBytes(path);
        long timeOffset = 0;
        long lastTimestamp = 0;

        for (var i = 0; i + RecordSize <= bytes.Length; i += RecordSize)
        {
            var b0 = bytes[i];
            var b1 = bytes[i + 1];
            var b2 = bytes[i + 2];
            var b3 = bytes[i + 3];

            if (b2 == 0xFF && b3 == 0xFF)
            {
                continue;
            }

            var length = b3 >> 4;
            if (length > 8)
            {
                continue;
            }

            var id = (uint)(((b3 & 0x0F) << 8) | b2);
            var timestamp = (1_000_000L * (b0 >> 2)) + (((((b0 & 3) << 8) + b1)) * 1000L);

            if (timestamp < lastTimestamp)
            {
                timeOffset += 60_000_000;
            }

            lastTimestamp = timestamp;
            timestamp += timeOffset;

            var frame = new CanFrame
            {
                Timestamp = FrameTimestamp.FromMicroseconds(timestamp),
                Bus = 0,
            };
            frame.SetFrameId(id);
            frame.SetFrameType(FrameType.CanDataFrame);
            frame.SetReceived(true);
            frame.SetPayload(bytes.AsSpan(i + 4, length));
            frames.Add(frame);
        }

        return true;
    }

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        var first = frames.Count > 0 ? frames[0].Timestamp.ToMicroseconds() / 1000 : 0;
        WriteRecord(stream, first, 0xFF, 0xFF, ReadOnlySpan<byte>.Empty);

        foreach (var frame in frames)
        {
            if (frame.IsExtended || frame.Length > 8)
            {
                continue;
            }

            var idHigh = (byte)((byte)(frame.Length << 4) | (byte)((frame.FrameId >> 8) & 0x0F));
            WriteRecord(stream, frame.Timestamp.ToMicroseconds() / 1000, (byte)(frame.FrameId & 0xFF), idHigh, frame.Payload);
        }

        return true;
    }

    private static void WriteRecord(FileStream stream, long milliseconds, byte idLow, byte idHigh, ReadOnlySpan<byte> payload)
    {
        var record = new byte[RecordSize];
        record.AsSpan().Fill(0xFF);

        var minute = (int)(milliseconds / 1000 % 60);
        var ms = (int)(milliseconds % 1000);
        record[0] = (byte)((minute << 2) | ((ms >> 8) & 0x03));
        record[1] = (byte)(ms & 0xFF);
        record[2] = idLow;
        record[3] = idHigh;
        payload.CopyTo(record.AsSpan(4));
        stream.Write(record);
    }
}
