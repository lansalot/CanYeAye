using WinCAN.Connections;
using WinCAN.Connections.Transports;

namespace WinCAN.Tests.Connections;

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
}
