using System.Buffers.Binary;

namespace WinCAN.Core.FileFormats;

/// <summary>
/// Tesla Autopilot snapshot (load only): packed 28-byte records
/// <c>int64 sec, int32 nano, int32 padding, uint16 id, uint8 ctr, uint8 data[8], uint8 padding</c>.
/// <c>ctr</c> holds the payload length in its high nibble and the bus in its low nibble. The
/// format cannot represent extended identifiers, so every frame loads as standard.
/// </summary>
public sealed class TeslaApFormat : LogFormatBase
{
    private const int RecordSize = 28;

    public override string Name => "Tesla Autopilot Snapshot";

    public override string DefaultExtension => ".can";

    public override bool CanSave => false;

    public override bool IsMatch(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length % RecordSize != 0)
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var record = new byte[RecordSize];
        while (stream.Read(record, 0, RecordSize) == RecordSize)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(16));
            var counter = record[18];
            if (id > 0x7FF || (counter >> 4) > 8 || (counter & 0x0F) > 6)
            {
                return false;
            }
        }

        return true;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        using var stream = File.OpenRead(path);
        var record = new byte[RecordSize];
        while (stream.Read(record, 0, RecordSize) == RecordSize)
        {
            var seconds = BinaryPrimitives.ReadInt64LittleEndian(record);
            var nanoseconds = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8));
            var id = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(16));
            var counter = record[18];
            var length = counter >> 4;
            var bus = counter & 0x0F;

            var frame = new CanFrame();
            frame.SetReceived(true);
            frame.SetExtended(false);
            frame.SetFrameType(FrameType.CanDataFrame);
            frame.SetFrameId(id);
            frame.Bus = (byte)bus;
            frame.Timestamp = FrameTimestamp.FromMicroseconds(seconds * 1_000_000 + (nanoseconds / 1000));
            frame.SetPayload(record.AsSpan(19, Math.Min(length, 8)));
            frames.Add(frame);
        }

        return true;
    }
}

