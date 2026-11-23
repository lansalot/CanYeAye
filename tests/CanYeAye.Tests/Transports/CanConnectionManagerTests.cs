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

    [Fact]
    public async Task SendingWithoutAConnectionEchoesTheFrameIntoTheLog()
    {
        // SavvyCAN queues frames that no connection owns (buslessFrames) and replays them as
        // received frames, so scripts still show traffic without a live bus.
        await using var manager = new CanConnectionManager();
        var frames = new List<CanFrame>();
        manager.FrameReceived += (_, frame) => frames.Add(frame);

        var sent = new CanFrame(0x7E0, [0x0D, 1, 0, 0, 0, 0, 0, 0]);
        await manager.SendFrameAsync(sent);

        var echoed = Assert.Single(frames);
        Assert.Equal(0x7E0u, echoed.FrameId);
        Assert.Equal((byte)0, echoed.Bus);
        Assert.Equal(sent.Payload, echoed.Payload);
        Assert.True(echoed.IsReceived);
    }

    [Fact]
    public async Task SendingOnAnUnownedBusAlsoEchoesInsteadOfThrowing()
    {
        await using var manager = new CanConnectionManager();
        var connection = new FakeConnection(1);
        manager.Attach(connection);

        var frames = new List<CanFrame>();
        manager.FrameReceived += (_, frame) => frames.Add(frame);

        var sent = new CanFrame(0x123, [9]) { Bus = 7 };
        await manager.SendFrameAsync(sent);

        Assert.Equal(sent.FrameId, Assert.Single(frames).FrameId);
        Assert.Empty(connection.Sent);
    }

    [Fact]
    public async Task SendingHandsTheFrameToTheOwningConnectionWithALocalBusNumber()
    {
        await using var manager = new CanConnectionManager();
        var first = new FakeConnection(2);
        var second = new FakeConnection(1);
        manager.Attach(first);
        manager.Attach(second);

        var sent = new CanFrame(0x200, [1, 2]) { Bus = 2 };
        await manager.SendFrameAsync(sent);

        var (localBus, frame) = Assert.Single(second.Sent);
        Assert.Equal(0, localBus);
        Assert.Equal(sent.FrameId, frame.FrameId);
        Assert.Empty(first.Sent);
    }

    private sealed class FakeConnection : CanConnectionBase
    {
        public FakeConnection(int busCount)
            : base(CreateBuses(busCount))
        {
        }

        public List<(int LocalBus, CanFrame Frame)> Sent { get; } = [];

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

        public override Task SendFrameAsync(int localBus, CanFrame frame, CancellationToken cancellationToken = default)
        {
            Sent.Add((localBus, frame));
            return Task.CompletedTask;
        }

        public void ChangeBusCount(int count) => SetBuses(CreateBuses(count));

        public void Raise(CanFrame frame) => Publish(frame);

        private static IReadOnlyList<CanBusInfo> CreateBuses(int count) =>
            Enumerable.Range(0, count).Select(i => new CanBusInfo(i, $"CAN{i}")).ToArray();
    }
}
