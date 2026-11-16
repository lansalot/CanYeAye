using System.Net.Sockets;
using System.Text;
using CanYeAye.Connections.Protocols;
using CanYeAye.Core;

namespace CanYeAye.Connections.Transports;

public sealed record CanLogServerConnectionOptions(string Host, int Port = 23);

/// <summary>
/// CANlogserver ASCII TCP stream (connections/canlogserver.cpp). Lines are newline-delimited, so
/// a <see cref="StreamReader"/> is used instead of the byte accumulator that socketcand needs.
/// TX is unsupported.
/// </summary>
public sealed class CanLogServerConnection : CanConnectionBase
{
    private readonly CanLogServerConnectionOptions _options;
    private readonly CanLogServerProtocol _protocol = new();

    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    public CanLogServerConnection(CanLogServerConnectionOptions options)
        : base([new CanBusInfo(0, "CAN0")])
    {
        _options = options;
        _protocol.FrameReceived += (_, frame) => Publish(frame);
    }

    public override string Name => $"CANlogserver {_options.Host}:{_options.Port}";

    public override ConnectionType Type => ConnectionType.CanLogServer;

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
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);
            _client = client;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readLoop = Task.Run(() => ReadLoopAsync(client, _cts.Token), CancellationToken.None);

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

        await CloseAsync().ConfigureAwait(false);
        if (_readLoop is { } loop)
        {
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
        cts?.Dispose();
        SetState(ConnectionState.Disconnected);
    }

    public override Task SendFrameAsync(int localBus, CanFrame frame, CancellationToken cancellationToken = default)
    {
        // The reference discards TX on CANlogserver.
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 4096, leaveOpen: true);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                _protocol.Feed(line + "\n");
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                SetState(ConnectionState.Disconnected, "CANlogserver closed the connection");
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetState(ConnectionState.Error, ex.Message);
            }
        }
    }

    private Task CloseAsync()
    {
        var client = _client;
        _client = null;
        try
        {
            client?.Close();
        }
        catch
        {
            // ignore
        }

        client?.Dispose();
        return Task.CompletedTask;
    }
}
