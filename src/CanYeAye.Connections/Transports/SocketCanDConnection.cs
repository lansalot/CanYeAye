using System.Net.Sockets;
using System.Text;
using CanYeAye.Connections.Protocols;
using CanYeAye.Core;

namespace CanYeAye.Connections.Transports;

public sealed record SocketCanDConnectionOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 29536;

    /// <summary>Bus names as announced by the beacon, e.g. <c>can0</c>, <c>can1</c>.</summary>
    public IReadOnlyList<string> BusNames { get; init; } = ["can0"];

    /// <summary>Listen-only mode: the app never transmits on this connection.</summary>
    public bool ListenOnly { get; init; }
}

/// <summary>
/// socketcand / Kayak over TCP: one socket per bus, each with its own handshake and raw-mode
/// state (connections/socketcand.cpp). The reference also polls every 2 s to reconnect; CanYeAye
/// surfaces a dropped socket as an error instead.
/// </summary>
public sealed class SocketCanDConnection : CanConnectionBase
{
    private readonly SocketCanDConnectionOptions _options;
    private readonly TcpClient?[] _clients;
    private readonly SocketCanDProtocol[] _protocols;
    private readonly Stream?[] _streams;
    private readonly Task?[] _readLoops;
    private readonly SemaphoreSlim[] _writeLocks;
    private CancellationTokenSource? _cts;

    public SocketCanDConnection(SocketCanDConnectionOptions options)
        : base([.. options.BusNames.Select((name, index) => new CanBusInfo(index, name))], options.ListenOnly)
    {
        _options = options;
        var count = Math.Max(1, options.BusNames.Count);
        _clients = new TcpClient?[count];
        _protocols = new SocketCanDProtocol[count];
        _streams = new Stream?[count];
        _readLoops = new Task?[count];
        _writeLocks = new SemaphoreSlim[count];

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var name = i < options.BusNames.Count ? options.BusNames[i] : $"can{i}";
            _protocols[i] = new SocketCanDProtocol(i, name);
            _protocols[i].FrameReceived += (_, frame) => Publish(frame);
            _protocols[i].SendRequested += (_, text) => SendText(index, text);
            _writeLocks[i] = new SemaphoreSlim(1, 1);
        }
    }

    public override string Name => $"socketcand {_options.Host}:{_options.Port}";

    public override ConnectionType Type => ConnectionType.SocketCanD;

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            return;
        }

        SetState(ConnectionState.Connecting);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        try
        {
            for (var i = 0; i < _protocols.Length; i++)
            {
                var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);
                _clients[i] = client;
                _streams[i] = client.GetStream();
                var index = i;
                _readLoops[i] = Task.Run(() => ReadLoopAsync(index, token), CancellationToken.None);
            }

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

        foreach (var loop in _readLoops)
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
                // Loop already ended.
            }
        }

        Array.Clear(_readLoops);
        cts?.Dispose();
        SetState(ConnectionState.Disconnected);
    }

    public override async Task SendFrameAsync(int localBus, CanFrame frame, CancellationToken cancellationToken = default)
    {
        ThrowIfListenOnly();

        if (localBus < 0 || localBus >= _protocols.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(localBus));
        }

        var text = _protocols[localBus].EncodeFrame(frame);
        if (text is null)
        {
            throw new InvalidOperationException(
                $"socketcand bus {localBus} is not in raw mode (state {_protocols[localBus].State}).");
        }

        await WriteAsync(localBus, Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    private async void SendText(int bus, string text)
    {
        try
        {
            await WriteAsync(bus, Encoding.UTF8.GetBytes(text), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            SetState(ConnectionState.Error, ex.Message);
        }
    }

    private async Task WriteAsync(int bus, byte[] bytes, CancellationToken cancellationToken)
    {
        var stream = _streams[bus] ?? throw new InvalidOperationException("socketcand socket is closed.");
        await _writeLocks[bus].WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLocks[bus].Release();
        }
    }

    private async Task ReadLoopAsync(int bus, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var stream = _streams[bus];
                if (stream is null)
                {
                    break;
                }

                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                _protocols[bus].Feed(Encoding.UTF8.GetString(buffer, 0, read));
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                SetState(ConnectionState.Disconnected, "socketcand host closed the connection");
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
        for (var i = 0; i < _clients.Length; i++)
        {
            try
            {
                _streams[i]?.Dispose();
            }
            catch
            {
                // ignore
            }

            _streams[i] = null;
            _clients[i]?.Dispose();
            _clients[i] = null;
        }

        return Task.CompletedTask;
    }
}
