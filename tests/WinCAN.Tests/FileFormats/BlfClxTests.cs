using System.Buffers.Binary;
using WinCAN.Core;
using WinCAN.Core.FileFormats;

namespace WinCAN.Tests.FileFormats;

public class BlfClxTests
{
    private static string TempFile(string extension) =>
        Path.Combine(Path.GetTempPath(), $"wincan-blf-{Guid.NewGuid():N}{extension}");

    private static void WriteU16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteU64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteObjectHeader(Stream stream, ushort headerSize, ushort headerVersion, uint objSize, uint objType)
    {
        WriteU32(stream, 0x4A424F4C); // "LOBJ"
        WriteU16(stream, headerSize);
        WriteU16(stream, headerVersion);
        WriteU32(stream, objSize);
        WriteU32(stream, objType);
    }

    private static byte[] BuildInnerCanObject()
    {
        using var inner = new MemoryStream();
        WriteObjectHeader(inner, 32, 1, 48, 1); // v1 header, CAN message
        WriteU32(inner, 0);       // flags: 0 => ns timestamps
        WriteU16(inner, 0);       // client
        WriteU16(inner, 0);       // object version
        WriteU64(inner, 1_234_567); // timestamp in ns
        WriteU16(inner, 1);       // channel/bus
        inner.WriteByte(0);       // flags: not remote
        inner.WriteByte(8);       // dlc
        WriteU32(inner, 0x123);   // id
        inner.Write([1, 2, 3, 4, 5, 6, 7, 8]);
        return inner.ToArray();
    }

    [Fact]
    public void BlfContainerWithCanMessage()
    {
        var path = TempFile(".blf");
        try
        {
            var inner = BuildInnerCanObject();
            using (var stream = File.Create(path))
            {
                var fileHeader = new byte[144];
                BinaryPrimitives.WriteUInt32LittleEndian(fileHeader, 0x47474F4C); // "LOGG"
                stream.Write(fileHeader);

                var objSize = (uint)(32 + inner.Length);
                WriteObjectHeader(stream, 32, 1, objSize, 10); // container
                WriteU16(stream, 0); // no compression
                WriteU16(stream, 0);
                WriteU16(stream, 0);
                WriteU16(stream, 0);
                WriteU32(stream, (uint)inner.Length);
                WriteU32(stream, 0);
                stream.Write(inner);
                var padding = objSize % 4;
                for (var i = 0; i < padding; i++)
                {
                    stream.WriteByte(0);
                }
            }

            Assert.True(LogFormatRegistry.Blf.IsMatch(path));
            var frames = new List<CanFrame>();
            Assert.True(LogFormatRegistry.Blf.Load(path, frames));

            var frame = Assert.Single(frames);
            Assert.Equal(0x123U, frame.FrameId);
            Assert.Equal(1, frame.Bus);
            Assert.False(frame.IsExtended);
            Assert.Equal(1234, frame.Timestamp.ToMicroseconds()); // 1_234_567 ns / 1000
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frame.Payload);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string ClxHeader(int timeFormat = 6)
    {
        var lines = new List<string>
        {
            "# Logger type: CLX000",
            "# Device: test",
            "# Firmware: 1.0",
            "# Hardware: 1",
            "# Serial: 1",
            "# Channel: 1",
            "# Time: 20240102T030405",
            "# Value separator: \",\"",
            $"# Time format: {timeFormat}",
            "# Time separator: \":\"",
            "# Time separator ms: \".\"",
            "# Date separator: \"-\"",
            "# Time and date separator: \" \"",
            "# skipped 1",
            "# skipped 2",
            "# skipped 3",
            "Timestamp,Type,ID,Length,Data",
        };
        return string.Join('\n', lines) + "\n";
    }

    [Fact]
    public void Clx000ParsesRows()
    {
        var path = TempFile(".txt");
        try
        {
            File.WriteAllText(path,
                ClxHeader() +
                "2024-01-02 03:04:05.678,0,123,8,0102030405060708\n" +
                "2024-01-02 03:04:06.000,9,18DAF110,2,AABB\n");

            Assert.True(LogFormatRegistry.Clx000.IsMatch(path));
            var frames = new List<CanFrame>();
            Assert.True(LogFormatRegistry.Clx000.Load(path, frames));

            Assert.Equal(2, frames.Count);
            var first = frames[0];
            Assert.Equal(0x123U, first.FrameId);
            Assert.False(first.IsExtended);
            Assert.True(first.IsReceived);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, first.Payload);
            var expected = new DateTimeOffset(
                new DateTime(2024, 1, 2, 3, 4, 5, 678, DateTimeKind.Local)).ToUnixTimeMilliseconds() * 1000;
            Assert.Equal(expected, first.Timestamp.ToMicroseconds());

            var second = frames[1];
            Assert.Equal(0x18DAF110U, second.FrameId);
            Assert.True(second.IsExtended); // type bit 0
            Assert.False(second.IsReceived); // type bit 3 => TX
            Assert.Equal(new byte[] { 0xAA, 0xBB }, second.Payload);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DetectionFindsBlfAndClx()
    {
        var blfPath = TempFile(".blf");
        var clxPath = TempFile(".txt");
        try
        {
            var inner = BuildInnerCanObject();
            using (var stream = File.Create(blfPath))
            {
                var fileHeader = new byte[144];
                BinaryPrimitives.WriteUInt32LittleEndian(fileHeader, 0x47474F4C);
                stream.Write(fileHeader);
                var objSize = (uint)(32 + inner.Length);
                WriteObjectHeader(stream, 32, 1, objSize, 10);
                WriteU16(stream, 0);
                WriteU16(stream, 0);
                WriteU16(stream, 0);
                WriteU16(stream, 0);
                WriteU32(stream, (uint)inner.Length);
                WriteU32(stream, 0);
                stream.Write(inner);
            }

            File.WriteAllText(clxPath, ClxHeader() + "2024-01-02 03:04:05.678,0,123,8,0102030405060708\n");

            Assert.Equal("CANalyzer Binary Log Files", LogFormatRegistry.AutoDetect(blfPath)?.Name);
            Assert.Equal("CLX000", LogFormatRegistry.AutoDetect(clxPath)?.Name);
        }
        finally
        {
            File.Delete(blfPath);
            File.Delete(clxPath);
        }
    }
}
