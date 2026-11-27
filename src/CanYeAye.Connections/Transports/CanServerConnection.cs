using System.Net;
using System.Net.Sockets;
using CanYeAye.Connections.Protocols;
using CanYeAye.Core;

namespace CanYeAye.Connections.Transports;

public sealed record CanServerConnectionOptions(string Host, bool ListenOnly = false);

/// <summary>
/// CANserver (PANDA v1) over UDP (connections/canserver.cpp). The connection binds local port
/// 1338 best-effort, then sends <c>hello</c> on connect and every 3.5 s. TX is unsupported.
/// </summary>
public sealed class CanServerConnection : CanConnectionBase
{
    private readonly CanServerConnectionOptions _options;
    private readonly CanServerProtocol _protocol = new();

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private Task? _heartbeat;

    public CanServerConnection(CanServerConnectionOptions options)
        : base([new CanBusInfo(0, "CAN0"), new CanBusInfo(1, "CAN1"), new CanBusInfo(2, "CAN2")], options.ListenOnly)
    {
        _options = options;
        _protocol.FrameReceived += (_, frame) => Publish(frame);
    }

    public override string Name => $"CANserver {_options.Host}";

    public override ConnectionType Type => ConnectionType.CanServer;

    public bool CaptureSuspended
    {
        get => _protocol.CaptureSuspended;
        set => _protocol.CaptureSuspended = value;
    }

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            return;
        }

        SetState(ConnectionState.Connecting);
        try
        {
            var udp = new UdpClient();
            try
            {
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, CanServerProtocol.DefaultPort));
            }
            catch (SocketException)
            {
                // The port may already be taken; the reference logs and continues.
            }

            udp.Connect(_options.Host, CanServerProtocol.DefaultPort);
            await udp.SendAsync(CanServerProtocol.Hello.ToArray(), cancellationToken).ConfigureAwait(false);

            _udp = udp;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;
            _readLoop = Task.Run(() => ReadLoopAsync(udp, token), CancellationToken.None);
            _heartbeat = Task.Run(() => HeartbeatAsync(udp, token), CancellationToken.None);

            SetState(ConnectionState.Connected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(ConnectionState.Error, ex.Message);
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnecting);
        var cts = _cts;
        _cts = null;
        cts?.Cancel();

        try
        {
            if (_udp is { } udp)
            {
                await udp.SendAsync(CanServerProtocol.Bye.ToArray(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // best effort
        }

        await CloseAsync().ConfigureAwait(false);
        foreach (var loop in new[] { _readLoop, _heartbeat })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // already ended
            }
        }

        _readLoop = null;
        _heartbeat = null;
        cts?.Dispose();
        SetState(ConnectionState.Disconnected);
    }

    public override Task SendFrameAsync(int localBus, CanFrame frame, CancellationToken cancellationToken = default)
    {
        // The reference discards TX on CANserver.
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                _protocol.Feed(result.Buffer);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetState(ConnectionState.Error, ex.Message);
            }
        }
    }

    private async Task HeartbeatAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(3500));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await udp.SendAsync(CanServerProtocol.Hello.ToArray(), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    SetState(ConnectionState.Error, ex.Message);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private Task CloseAsync()
    {
        var udp = _udp;
        _udp = null;
        try
        {
            udp?.Close();
        }
        catch
        {
            // ignore
        }

        udp?.Dispose();
        return Task.CompletedTask;
    }
}
