using WinCAN.Core;
using WinCAN.Core.Dbc;

namespace WinCAN.Tests.Dbc;

public class DbcModelTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void BmsDbcLoadsWithoutFaults()
    {
        var file = DbcFileReader.Load(Fixture("bms.dbc"), out var faults);

        Assert.Empty(faults);
        Assert.Equal(6, file.Messages.Count);
        Assert.Equal(30, file.Messages.Values.Sum(m => m.Signals.Count));
        Assert.Equal("Vector__XXX", file.Nodes[0].Name);
        Assert.Equal(["Vector__XXX", "EVTVBMS", "GEVCU", "INVERTER"], file.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void BmsMessagesAreSortedAndResolveSenders()
    {
        var file = DbcFileReader.Load(Fixture("bms.dbc"), out _);

        var ids = file.Messages.Keys.ToArray();
        Assert.True(ids.SequenceEqual(ids.OrderBy(id => id)));

        Assert.True(file.Messages.TryGetValue(1542, out var first));
        Assert.Equal("BMS_STATUS", first.Name);
        Assert.Equal("EVTVBMS", first.Sender.Name);
        Assert.Equal(8u, first.Length);

        // PACKAMPS is a signed Intel 16-bit signal with factor 0.01.
        Assert.True(first.Signals.TryGetValue("PACKAMPS", out var packAmps));
        Assert.True(packAmps.IsSigned);
        Assert.True(packAmps.IntelByteOrder);
        Assert.Equal(16, packAmps.SignalSize);
        Assert.Equal(0.01, packAmps.Factor);
    }

    [Fact]
    public void LeafPowertrainBusKeepsEmptyMessage()
    {
        var file = DbcFileReader.Load(Fixture("LeafPowertrainBus.dbc"), out var faults);

        Assert.Empty(faults);
        Assert.Equal(10, file.Messages.Count);
        Assert.Equal(21, file.Messages.Values.Sum(m => m.Signals.Count));

        Assert.True(file.Messages.TryGetValue(498, out var empty));
        Assert.Empty(empty.Signals);
        Assert.Equal("Ctrl2", empty.Name);

        Assert.Equal(["Vector__XXX", "VCM", "Inverter", "LBC", "Charger"], file.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void ThinkCityLoads()
    {
        var file = DbcFileReader.Load(Fixture("ThinkCity.dbc"), out var faults);

        Assert.Empty(faults);
        Assert.Equal(501, file.Messages.Count);
        Assert.Equal(548, file.Messages.Values.Sum(m => m.Signals.Count));
    }
}
