using System.Buffers.Binary;
using System.IO.Compression;

namespace WinCAN.Core.FileFormats;

/// <summary>
/// Vector Binary Logging Format (load only), ported from blfhandler.cpp. The file is a sequence
/// of "LOBJ" objects; only container objects carry frames, and their payload is either stored
/// raw or zlib-compressed and holds a concatenation of further objects.
/// </summary>
public sealed class BlfFormat : LogFormatBase
{
    private const uint FileSignature = 0x47474F4C; // "LOGG"
    private const uint ObjectSignature = 0x4A424F4C; // "LOBJ"
    private const int BaseHeaderSize = 16;
    private const int V1HeaderSize = 32;
    private const int V2HeaderSize = 40;
    private const int ContainerHeaderSize = 16;
    private const byte RemoteFlag = 0x80;

    private const uint TypeCanMessage = 1;
    private const uint TypeCanMessage2 = 86;
    private const uint TypeCanFdMessage = 100;
    private const uint TypeCanFdMessage64 = 101;
    private const uint TypeContainer = 10;

    public override string Name => "CANalyzer Binary Log Files";

    public override string DefaultExtension => ".blf";

    public override bool CanSave => false;

    public override bool IsMatch(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            return stream.Read(header) == 4 &&
                   BinaryPrimitives.ReadUInt32LittleEndian(header) == FileSignature;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        using var stream = File.OpenRead(path);
        var fileHeader = new byte[144];
        if (stream.Read(fileHeader, 0, fileHeader.Length) != fileHeader.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(fileHeader) != FileSignature)
        {
            return false;
        }

        var tail = Array.Empty<byte>();
        var baseHeader = new byte[BaseHeaderSize];
        while (true)
        {
            var read = stream.Read(baseHeader, 0, BaseHeaderSize);
            if (read == 0)
            {
                break;
            }

            if (read != BaseHeaderSize ||
                BinaryPrimitives.ReadUInt32LittleEndian(baseHeader) != ObjectSignature)
            {
                return false;
            }

            var objectSize = BinaryPrimitives.ReadUInt32LittleEndian(baseHeader.AsSpan(8));
            var objectType = BinaryPrimitives.ReadUInt32LittleEndian(baseHeader.AsSpan(12));
            if (objectSize < BaseHeaderSize)
            {
                return false;
            }

            var dataSize = (int)(objectSize - BaseHeaderSize);
            if (stream.Position + dataSize > stream.Length)
            {
                return false;
            }

            var objectData = new byte[dataSize];
            stream.ReadExactly(objectData);

            var paddingSize = (int)(objectSize % 4);
            if (paddingSize > 0)
            {
                stream.Seek(paddingSize, SeekOrigin.Current);
            }

            if (objectType != TypeContainer || objectData.Length < ContainerHeaderSize)
            {
                continue;
            }

            var compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(objectData);
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(objectData.AsSpan(8));
            var payload = objectData.AsSpan(ContainerHeaderSize).ToArray();

            if (compressionMethod == 2)
            {
                try
                {
                    payload = Decompress(payload);
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            }
            else if (compressionMethod != 0)
            {
                return false;
            }

            if (uncompressedSize != 0 && payload.Length != uncompressedSize)
            {
                return false;
            }

            if (!ParseContainer(payload, frames, ref tail))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static bool ParseContainer(byte[] data, List<CanFrame> frames, ref byte[] tail)
    {
        var current = new byte[tail.Length + data.Length];
        tail.CopyTo(current, 0);
        data.CopyTo(current, tail.Length);
        tail = [];

        var pos = FindSignature(current, 0, current.Length);
        while (pos >= 0)
        {
            if (pos + BaseHeaderSize > current.Length)
            {
                tail = current[pos..];
                return true;
            }

            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(current.AsSpan(pos + 4));
            var headerVersion = BinaryPrimitives.ReadUInt16LittleEndian(current.AsSpan(pos + 6));
            var objectSize = BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(pos + 8));
            var objectType = BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(pos + 12));

            if (headerSize < BaseHeaderSize || objectSize < headerSize)
            {
                return false;
            }

            if (headerVersion == 1 && headerSize < V1HeaderSize)
            {
                return false;
            }

            if (headerVersion == 2 && headerSize < V2HeaderSize)
            {
                return false;
            }

            if (pos + (long)objectSize > current.Length)
            {
                tail = current[pos..];
                return true;
            }

            var payloadOffset = pos + headerSize;
            var payloadSize = (int)(objectSize - headerSize);
            var timestamp = headerSize >= V1HeaderSize ? TimestampMicroseconds(current, pos, headerVersion) : 0;

            switch (objectType)
            {
                case TypeCanMessage:
                case TypeCanMessage2:
                    if (payloadSize < 16)
                    {
                        return false;
                    }

                    ParseClassic(current, payloadOffset, timestamp, frames);
                    break;
                case TypeCanFdMessage:
                    if (payloadSize < 84)
                    {
                        return false;
                    }

                    ParseCanFd(current, payloadOffset, timestamp, frames);
                    break;
                case TypeCanFdMessage64:
                    if (payloadSize < 40)
                    {
                        return false;
                    }

                    ParseCanFd64(current, payloadOffset, payloadSize, timestamp, frames);
                    break;
            }

            var nextPos = pos + (int)objectSize;
            pos = FindSignature(current, nextPos, nextPos + 8);
            if (pos < 0)
            {
                tail = current[nextPos..];
                return true;
            }
        }

        if (current.Length > 0)
        {
            tail = current;
        }

        return true;
    }

    private static void ParseClassic(byte[] data, int offset, long timestamp, List<CanFrame> frames)
    {
        var channel = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        var flags = data[offset + 2];
        var dlc = data[offset + 3];
        var id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
        var length = Math.Min((int)dlc, 8);
        var payload = data.AsSpan(offset + 8, length).ToArray();

        SetFrame(
            frames,
            id,
            (id & 0x80000000u) != 0,
            (flags & RemoteFlag) != 0,
            channel,
            payload,
            timestamp,
            received: true,
            dlc,
            fd: false,
            brs: false,
            esi: false);
    }

    private static void ParseCanFd(byte[] data, int offset, long timestamp, List<CanFrame> frames)
    {
        var channel = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        var flags = data[offset + 2];
        var dlc = data[offset + 3];
        var id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
        var fdFlags = data[offset + 13];
        var validBytes = data[offset + 14];
        var length = Math.Min((int)validBytes, 64);
        var payload = data.AsSpan(offset + 20, length).ToArray();

        SetFrame(
            frames,
            id,
            (id & 0x80000000u) != 0,
            (flags & RemoteFlag) != 0,
            channel,
            payload,
            timestamp,
            received: true,
            dlc,
            (fdFlags & 0x01) != 0,
            (fdFlags & 0x02) != 0,
            (fdFlags & 0x04) != 0);
    }

    private static void ParseCanFd64(byte[] data, int offset, int payloadSize, long timestamp, List<CanFrame> frames)
    {
        var channel = data[offset];
        var dlc = data[offset + 1];
        var validBytes = data[offset + 2];
        var frameId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));
        var direction = data[offset + 38];
        var length = Math.Min((int)validBytes, Math.Max(0, payloadSize - 40));
        var payload = data.AsSpan(offset + 40, length).ToArray();

        SetFrame(
            frames,
            frameId,
            (frameId & 0x80000000u) != 0,
            (flags & 0x10) != 0,
            channel,
            payload,
            timestamp,
            direction == 0,
            dlc,
            (flags & 0x1000) != 0,
            (flags & 0x2000) != 0,
            (flags & 0x4000) != 0);
    }

    private static void SetFrame(
        List<CanFrame> frames,
        uint id,
        bool extended,
        bool remote,
        int bus,
        byte[] payload,
        long timestamp,
        bool received,
        byte dlc,
        bool fd,
        bool brs,
        bool esi)
    {
        var frame = new CanFrame();
        frame.Bus = (byte)bus;
        frame.SetExtended(extended);
        frame.SetFrameId(id & 0x1FFFFFFFu);
        frame.SetReceived(received);

        if (remote)
        {
            frame.SetFrameType(FrameType.RemoteRequestFrame);
            frame.SetPayload(new byte[Math.Min((int)dlc, 8)]);
        }
        else
        {
            frame.SetFrameType(fd ? FrameType.CanFdDataFrame : FrameType.CanDataFrame);
            frame.SetPayload(payload);
            if (fd)
            {
                frame.SetFlexibleDataRate(true);
                frame.SetBitrateSwitch(brs);
                frame.SetErrorStateIndicator(esi);
            }
        }

        frame.Timestamp = FrameTimestamp.FromMicroseconds(timestamp);
        frames.Add(frame);
    }

    private static long TimestampMicroseconds(byte[] data, int position, int headerVersion)
    {
        if (headerVersion is not (1 or 2))
        {
            return 0;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 16));
        var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(position + 24));
        return flags == 1 ? (long)(timestamp / 100UL) : (long)(timestamp / 1000UL);
    }

    private static int FindSignature(byte[] data, int start, int end)
    {
        end = Math.Min(end, data.Length - 4);
        for (var pos = Math.Max(start, 0); pos <= end; pos++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) == ObjectSignature)
            {
                return pos;
            }
        }

        return -1;
    }
}

