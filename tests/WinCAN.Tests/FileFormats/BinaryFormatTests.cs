using System.Buffers.Binary;
using System.Text;
using WinCAN.Core;
using WinCAN.Core.FileFormats;

namespace WinCAN.Tests.FileFormats;

/// <summary>Binary and fixed-column formats that have no text fixture in the reference repo:
/// Kvaser, CANserver, Tesla AP and pcap/pcapng are generated in the test.</summary>
public class BinaryFormatTests
{
    private static string TempFile(string extension) =>
        Path.Combine(Path.GetTempPath(), $"wincan-bin-{Guid.NewGuid():N}{extension}");

    private static void Put(char[] line, int index, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            line[index + i] = text[i];
        }
    }

    private static string KvaserLine(string id, string[] bytes, string time, char direction)
    {
        var line = new char[80];
        Array.Fill(line, ' ');
        Put(line, 0, " 0 ");
        Put(line, 4, id);
        Put(line, 21, " 8 ");
        for (var i = 0; i < bytes.Length; i++)
        {
            Put(line, 25 + (i * 4), bytes[i]);
        }

        Put(line, 57, time);
        line[72] = direction;
        return new string(line);
    }

    [Fact]
    public void KvaserDecimalAndHex()
    {
        var header = "Chn Identifier Flg   DLC  D0   D1   D2   D3   D4   D5   D6   D7       Time     Dir";
        var decimalPath = TempFile(".txt");
        var hexPath = TempFile(".txt");
        try
        {
            File.WriteAllText(decimalPath, header + "\n" +
                KvaserLine("000000123", ["001", "002", "003", "004", "005", "006", "007", "008"], "154.266550", 'R') + "\n");
            File.WriteAllText(hexPath, header + "\n" +
                KvaserLine("000000AD ", ["01", "02", "03", "04", "05", "06", "07", "08"], "1.500000", 'T') + "\n");

            Assert.True(LogFormatRegistry.KvaserDecimal.IsMatch(decimalPath));
            Assert.True(LogFormatRegistry.KvaserHex.IsMatch(hexPath));

            var decimalFrames = new List<CanFrame>();
            Assert.True(LogFormatRegistry.KvaserDecimal.Load(decimalPath, decimalFrames));
            var frame = Assert.Single(decimalFrames);
            Assert.Equal(123u, frame.FrameId);
            Assert.Equal(154_266_550, frame.Timestamp.ToMicroseconds());
            Assert.True(frame.IsReceived);
            Assert.Equal(1, frame.Payload[0]);
            Assert.Equal(8, frame.Payload[7]);

            var hexFrames = new List<CanFrame>();
            Assert.True(LogFormatRegistry.KvaserHex.Load(hexPath, hexFrames));
            var hexFrame = Assert.Single(hexFrames);
            Assert.Equal(0xADu, hexFrame.FrameId);
            Assert.Equal(1_500_000, hexFrame.Timestamp.ToMicroseconds());
            Assert.False(hexFrame.IsReceived);
        }
        finally
        {
            File.Delete(decimalPath);
            File.Delete(hexPath);
        }
    }

    [Fact]
    public void CanServerV3Binary()
    {
        var path = TempFile(".log");
        try
        {
            using (var stream = File.Create(path))
            {
                stream.Write(Encoding.ASCII.GetBytes("CANSERVER_v3_CANSERVER"));
                // time sync: 0xA0 + uint64 LE base in microseconds
                stream.WriteByte(0xA0);
                var baseBytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(baseBytes, 5_000_000);
                stream.Write(baseBytes);

                // frame: offset 16 (marker low nibble 0, h0 = 1, h1/h2 high = 0), id 0x123, len 2, bus 1
                stream.WriteByte(0xB0);
                var h = new byte[5];
                h[0] = 0x01;
                h[1] = 0x00;
                h[2] = 0x01; // high 3 bits of the id
                h[3] = 0x23;
                h[4] = (byte)(2 | (1 << 4));
                stream.Write(h);
                stream.Write([0xAA, 0xBB]);
            }

            Assert.True(LogFormatRegistry.CanServerLog.IsMatch(path));
            var frames = new List<CanFrame>();
            Assert.True(LogFormatRegistry.CanServerLog.Load(path, frames));

            var frame = Assert.Single(frames);
            Assert.Equal(0x123U, frame.FrameId);
            Assert.Equal(1, frame.Bus);
            Assert.Equal(5_000_016, frame.Timestamp.ToMicroseconds());
            Assert.Equal(new byte[] { 0xAA, 0xBB }, frame.Payload);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TeslaSnapshot()
    {
        var path = TempFile(".can");
        try
        {
            var record = new byte[28];
            BinaryPrimitives.WriteInt64LittleEndian(record, 1_700_000_000);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), 123_000_000);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 0x2A0);
            record[18] = (8 << 4) | 2; // length 8, bus 2
            for (var i = 0; i < 8; i++)
            {
                record[19 + i] = (byte)(i + 1);
            }

            File.WriteAllBytes(path, record);

            Assert.True(LogFormatRegistry.TeslaAp.IsMatch(path));
            var frames = new List<CanFrame>();
            Assert.True(LogFormatRegistry.TeslaAp.Load(path, frames));

            var frame = Assert.Single(frames);
            Assert.Equal(0x2A0U, frame.FrameId);
            Assert.Equal(2, frame.Bus);
            Assert.False(frame.IsExtended);
            Assert.Equal(1_700_000_000_123_000, frame.Timestamp.ToMicroseconds());
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frame.Payload);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] ClassicCanPacket(uint canId, byte dlc, byte[] data)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(packet, canId);
        packet[4] = dlc;
        packet[6] = 0; // receive direction
        data.CopyTo(packet, 8);
        return packet;
    }

    private static void WritePcapRecord(Stream stream, uint seconds, uint micros, byte[] packet)
    {
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, seconds);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), micros);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)packet.Length);
        stream.Write(header);
        stream.Write(packet);
    }

    [Fact]
    public void PcapSocketCan()
    {
        var path = TempFile(".pcap");
        try
        {
            using (var stream = File.Create(path))
            {
                var global = new byte[24];
                BinaryPrimitives.WriteUInt32LittleEndian(global, 0xA1B2C3D4);
                BinaryPrimitives.WriteUInt16LittleEndian(global.AsSpan(4), 2);
                BinaryPrimitives.WriteUInt16LittleEndian(global.AsSpan(6), 4);
                BinaryPrimitives.WriteUInt32LittleEndian(global.AsSpan(20), 227); // DLT_CAN_SOCKETCAN
                stream.Write(global);

                WritePcapRecord(stream, 10, 500_000,
                    ClassicCanPacket(0x123, 8, [1, 2, 3, 4, 5, 6, 7, 8]));
                WritePcapRecord(stream, 11, 0,
                    ClassicCanPacket(0x80000000u | 0x18DAF110u, 2, [0xAA, 0xBB]));
            }

            Assert.True(LogFormatRegistry.Pcap.IsMatch(path));
            var frames = new List<CanFrame>();
            Assert.True(LogFormatRegistry.Pcap.Load(path, frames));

            Assert.Equal(2, frames.Count);
            Assert.Equal(0, frames[0].Timestamp.ToMicroseconds());
            Assert.Equal(0x123U, frames[0].FrameId);
            Assert.False(frames[0].IsExtended);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frames[0].Payload);

            Assert.Equal(500_000, frames[1].Timestamp.ToMicroseconds());
            Assert.Equal(0x18DAF110U, frames[1].FrameId);
            Assert.True(frames[1].IsExtended);
            Assert.Equal(new byte[] { 0xAA, 0xBB }, frames[1].Payload);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AutodetectFindsBinaryFormats()
    {
        var path = TempFile(".can");
        try
        {
            var record = new byte[28];
            BinaryPrimitives.WriteInt64LittleEndian(record, 1_700_000_000);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 0x100);
            record[18] = 1 << 4;
            File.WriteAllBytes(path, record);

            var detected = LogFormatRegistry.AutoDetect(path);
            Assert.Equal("Tesla Autopilot Snapshot", detected?.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
