using CanYeAye.Connections.Discovery;
using CanYeAye.Core.Protocols;

namespace CanYeAye.Tests.Protocols;

public class DiscoveryAndJ1939Tests
{
    [Fact]
    public void KayakBeaconIsParsed()
    {
        const string xml = """
            <?xml version="1.0"?>
            <CANBeacon name="beaglebone" version="2.0">
              <URL>can://192.168.0.42:29536</URL>
              <Bus name="can0"/>
              <Bus name="can1"/>
            </CANBeacon>
            """;

        var device = ConnectionDiscovery.ParseKayakBeacon(xml, "192.168.0.42");

        Assert.NotNull(device);
        Assert.Equal(DiscoveredConnectionType.SocketCand, device!.Type);
        Assert.Equal("192.168.0.42", device.Address);
        Assert.Equal(29536, device.Port);
        Assert.Equal(["can0", "can1"], device.Buses);
        Assert.Equal("can0,can1@beaglebone (can://192.168.0.42:29536)", device.PortString);
    }

    [Fact]
    public void BadBeaconIsIgnored()
    {
        Assert.Null(ConnectionDiscovery.ParseKayakBeacon("not xml", "1.2.3.4"));
        Assert.Null(ConnectionDiscovery.ParseKayakBeacon("<Other/>", "1.2.3.4"));
    }

    [Fact]
    public void GvretSenderIsDescribed()
    {
        var device = ConnectionDiscovery.DescribeGvretSender("10.0.0.5");
        Assert.Equal(DiscoveredConnectionType.Gvret, device.Type);
        Assert.Equal("10.0.0.5", device.Address);
    }

    [Fact]
    public void J1939Pdu2IsBroadcast()
    {
        // priority 6, PGN 0xFECA (PDU2), source 0x80
        var decoded = J1939Id.Decode(0x18FECA80);
        Assert.True(decoded.IsBroadcast);
        Assert.Equal(0xFFFF, decoded.Destination);
        Assert.Equal(0x80, decoded.Source);
        Assert.Equal(6, decoded.Priority);
        Assert.Equal(0xFECA, decoded.Pgn);
        Assert.Equal(0xFE, decoded.PduFormat);
    }

    [Fact]
    public void J1939Pdu1UsesLowByteAsDestination()
    {
        // priority 6, PDU1 0xEA, destination 0x10, source 0x80, PGN 0xEA00
        var decoded = J1939Id.Decode(0x18EA1080);
        Assert.False(decoded.IsBroadcast);
        Assert.Equal(0x10, decoded.Destination);
        Assert.Equal(0xEA00, decoded.Pgn);
        Assert.Equal(0x80, decoded.Source);
    }
}
