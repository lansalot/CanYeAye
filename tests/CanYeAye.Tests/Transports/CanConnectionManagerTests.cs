using CanYeAye.Connections;
using CanYeAye.Connections.Transports;
using CanYeAye.Core;

namespace CanYeAye.Tests.Transports;

public class CanConnectionManagerTests
{
    [Fact]
    public async Task RenumbersBusesWhenTransportDiscoversFewerBuses()
    {
        await using var manager = new CanConnectionManager();
        var first = new FakeConnection(2);

        var entry = manager.Attach(first);
        Assert.Equal(0, entry.FirstBus);
        Assert.Equal(1, entry.LastBus);

        // GVRET devices only report their real bus count during the init handshake.
        first.ChangeBusCount(1);

        var updated = Assert.Single(manager.Connections);
        Assert.Equal(0, updated.FirstBus);
        Assert.Equal(0, updated.LastBus);

        var second = new FakeConnection(1);
        var secondEntry = manager.Attach(second);
        Assert.Equal(1, secondEntry.FirstBus);

        var frames = new List<CanFrame>();
        manager.FrameReceived += (_, frame) => frames.Add(frame);

        first.Raise(new CanFrame(0x100, [1]));
        second.Raise(new CanFrame(0x200, [2]));

        Assert.Equal(0, frames[0].Bus);
        Assert.Equal(1, frames[1].Bus);
    }

    private sealed class FakeConnection : CanConnectionBase
    {
        public FakeConnection(int busCount)
            : base(CreateBuses(busCount))
        {
        }

        public override string Name => "fake";

        public override ConnectionType Type => ConnectionType.GvretSerial;

        public override Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(ConnectionState.Connected);
            return Task.CompletedTask;
        }

        public override Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(ConnectionState.Disconnected);
            return Task.CompletedTask;
        }

        public override Task SendFrameAsync(int localBus, CanFrame frame, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void ChangeBusCount(int count) => SetBuses(CreateBuses(count));

        public void Raise(CanFrame frame) => Publish(frame);

        private static IReadOnlyList<CanBusInfo> CreateBuses(int count) =>
            Enumerable.Range(0, count).Select(i => new CanBusInfo(i, $"CAN{i}")).ToArray();
    }
}
