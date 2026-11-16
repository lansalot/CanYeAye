using System.Runtime.InteropServices;
using CanYeAye.Connections;
using CanYeAye.Connections.Transports;

namespace CanYeAye.Tests.Connections;

public class PcanConnectionTests
{
    [Theory]
    [InlineData(1_000_000u, 0x0014)]
    [InlineData(500_000u, 0x001C)]
    [InlineData(250_000u, 0x011C)]
    [InlineData(125_000u, 0x031C)]
    [InlineData(100_000u, 0x432F)]
    [InlineData(83_333u, 0x852B)]
    [InlineData(50_000u, 0x472F)]
    [InlineData(33_333u, 0x8B2F)]
    public void MapsBusSpeedsToPcanBitTiming(uint bitrate, int expected)
    {
        Assert.Equal((ushort)expected, PcanConnection.GetBitTiming(bitrate));
    }

    [Fact]
    public void UnsupportedBitrateHasNoPcanConstant()
    {
        Assert.Null(PcanConnection.GetBitTiming(123_456));
    }

    [Fact]
    public void PcanConnectionTypeIsIdentified()
    {
        var connection = new PcanConnection(new PcanConnectionOptions());

        Assert.Equal(ConnectionType.Pcan, connection.Type);
        Assert.Equal("PCAN-USB 1", connection.Name);
        Assert.Single(connection.Buses);
    }

    [Fact]
    public void MessageStructMatchesNativeLayout()
    {
        // TPCANMsg in PCANBasic.h / PCBUSB.h: DWORD ID, BYTE MSGTYPE, BYTE LEN, BYTE DATA[8].
        Assert.Equal(16, Marshal.SizeOf<PcanNative.PcanMessage>());
        Assert.Equal(4, (int)Marshal.OffsetOf<PcanNative.PcanMessage>(nameof(PcanNative.PcanMessage.MessageType)));
        Assert.Equal(6, (int)Marshal.OffsetOf<PcanNative.PcanMessage>(nameof(PcanNative.PcanMessage.Data)));
        Assert.Equal(8, Marshal.SizeOf<PcanNative.PcanTimestamp>());
    }

    [Fact]
    public void LibraryCandidatesMatchPlatformDriver()
    {
        var candidates = PcanNative.LibraryCandidates();

        if (OperatingSystem.IsMacOS())
        {
            Assert.All(candidates, c => Assert.EndsWith("libPCBUSB.dylib", c, StringComparison.Ordinal));
            Assert.Contains("mac-can.com", PcanConnection.DriverLibraryName, StringComparison.Ordinal);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.All(candidates, c => Assert.Contains("libpcanbasic.so", c, StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(["PCANBasic.dll"], candidates);
        }
    }

    [Fact]
    public void ChannelDetectionNeverThrowsWithoutDriver()
    {
        var channels = PcanConnection.GetAvailableChannels();

        if (!PcanConnection.IsDriverAvailable())
        {
            Assert.Empty(channels);
            Assert.Equal(string.Empty, PcanConnection.GetHardwareName(1));
        }
    }
}
