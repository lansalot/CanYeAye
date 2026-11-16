using System.Net;
using System.Net.Sockets;
using CanYeAye.Connections;
using CanYeAye.Connections.Protocols;
using CanYeAye.Connections.Transports;
using CanYeAye.Core;

namespace CanYeAye.Tests.Transports;

/// <summary>
/// Exercises the GVRET TCP transport against a local mock device: the reference uses
/// port 23 (connections/gvretserial.cpp) and only reports CONNECTED once the device
/// answers the init sequence.
/// </summary>
public class GvretConnectionTests
{
    [Fact]
    public async Task ConnectsOverTcpAndReceivesFrames()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var preamble = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = ServeAsync(listener, preamble, timeout.Token);

        await using var connection = new GvretConnection(new GvretConnectionOptions
        {
            Host = "127.0.0.1",
            TcpPort = port,
            ValidateComm = true,
        });

        var frameReceived = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.FrameReceived += (_, frame) => frameReceived.TrySetResult(frame);

        await connection.ConnectAsync(timeout.Token);
        Assert.Equal(ConnectionState.Connected, connection.State);

        var frame = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
        Assert.Equal(0x123U, frame.FrameId);
        Assert.Equal(0, frame.Bus);
        Assert.False(frame.IsExtended);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80 }, frame.Payload);

        // The device reported a single bus, so the transport exposes one bus.
        var bus = Assert.Single(connection.Buses);
        Assert.Equal(0, bus.LocalIndex);

        // The mock saw the binary-mode + init preamble and the bus configuration write.
        var received = await preamble.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
        var init = GvretProtocol.EncodeInitSequence();
        var busConfig = GvretProtocol.EncodeBusConfig(500_000, true, false, 500_000, true, false, false);
        Assert.Equal(init, received.Take(init.Length).ToArray());
        Assert.Equal(busConfig, received.Skip(init.Length).Take(busConfig.Length).ToArray());

        await connection.DisconnectAsync();
        await device.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
    }

    [Fact]
    public async Task ReportsErrorWhenDeviceDoesNotAnswer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Accept but never answer: the transport must not claim to be connected.
        var device = AcceptSilentlyAsync(listener, timeout.Token);

        await using var connection = new GvretConnection(new GvretConnectionOptions
        {
            Host = "127.0.0.1",
            TcpPort = port,
            ValidateComm = true,
        });

        var states = new List<ConnectionState>();
        connection.StateChanged += (_, e) => states.Add(e.State);

        await connection.ConnectAsync(timeout.Token);

        Assert.Equal(ConnectionState.Error, connection.State);
        Assert.Contains(ConnectionState.Error, states);
        Assert.DoesNotContain(ConnectionState.Connected, states);
        await connection.DisconnectAsync();
        await device.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
    }

    private static async Task ServeAsync(
        TcpListener listener,
        TaskCompletionSource<byte[]> preamble,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        var received = new List<byte>();
        var buffer = new byte[1024];

        // Init sequence (14 bytes) followed by the bus configuration command (12 bytes).
        var expected = GvretProtocol.EncodeInitSequence().Length +
            GvretProtocol.EncodeBusConfig(0, false, false, 0, false, false, false).Length;
        while (received.Count < expected)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        preamble.TrySetResult(received.ToArray());

        try
        {
            // Bus parameter reply: both buses enabled at 500 kbit/s.
            await stream.WriteAsync(
                new byte[] { 0xF1, 0x06, 0x01, 0x20, 0xA1, 0x07, 0x00, 0x41, 0x20, 0xA1, 0x07, 0x00 },
                cancellationToken);
            await stream.WriteAsync(new byte[] { 0xF1, 0x0C, 0x01 }, cancellationToken);
            await stream.WriteAsync(new byte[] { 0xF1, 0x09 }, cancellationToken);

            // One classic frame on bus 0: 0x123, eight bytes.
            var frame = new List<byte> { 0xF1, 0x00 };
            frame.AddRange(BitConverter.GetBytes(1_000u));
            frame.AddRange(BitConverter.GetBytes(0x123u));
            frame.Add(0x08);
            frame.AddRange([0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80]);
            await stream.WriteAsync(frame.ToArray(), cancellationToken);

            // Keep the socket open until the client disconnects.
            while (await stream.ReadAsync(buffer, cancellationToken) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The transport closed the socket; nothing left to serve.
        }
    }

    private static async Task AcceptSilentlyAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        var buffer = new byte[1024];
        try
        {
            while (await stream.ReadAsync(buffer, cancellationToken) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The transport gave up and closed the socket.
        }
    }
}
