using System.IO.Ports;
using System.Text;
using CanYeAye.Connections.Protocols;
using CanYeAye.Core;

namespace CanYeAye.Connections.Transports;

public sealed record LawicelConnectionOptions
{
    public required string PortName { get; init; }

    public int BaudRate { get; init; } = 115200;

    public uint Bitrate { get; init; } = 500_000;

    public bool CanFd { get; init; }

    public uint DataRate { get; init; } = 2_000_000;

    public bool UseSystemTime { get; init; }

    /// <summary>Opens the bus with the SLCAN <c>L</c> (listen-only) command instead of <c>O</c>.</summary>
    public bool ListenOnly { get; init; }
}

/// <summary>
/// Lawicel ASCII adapter over a serial port (connections/lawicel_serial.cpp). The reference has
/// no validation timer or keepalive: it writes the C/S/Y/O sequence and declares the bus open.
/// </summary>
public sealed class LawicelConnection : CanConnectionBase
{
    private readonly LawicelConnectionOptions _options;
    private readonly LawicelProtocol _protocol = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private SerialPort? _serialPort;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    public LawicelConnection(LawicelConnectionOptions options)
        : base([new CanBusInfo(0, "CAN0")], options.ListenOnly)
    {
        _options = options;
        _protocol.UseSystemTime = options.UseSystemTime;
        _protocol.FrameReceived += (_, frame) => Publish(frame);
    }

    public override string Name => $"Lawicel {_options.PortName}";

    public override ConnectionType Type => ConnectionType.LawicelSerial;

    public bool UseSystemTime
    {
        get => _protocol.UseSystemTime;
        set => _protocol.UseSystemTime = value;
    }

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
            var serial = new SerialPort(
                _options.PortName,
                _options.BaudRate,
                Parity.None,
                8,
                StopBits.One)
            {
                Handshake = Handshake.RequestToSend,
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = 500,
                WriteTimeout = 500,
            };
            serial.Open();
            _serialPort = serial;

            _protocol.RebuildLocalTimeBasis();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readLoop = Task.Run(() => ReadLoopAsync(serial, _cts.Token), CancellationToken.None);

            var sequence = LawicelProtocol.BuildOpenSequence(
                _options.Bitrate,
                _options.CanFd,
                _options.DataRate,
                _options.ListenOnly);
            await WriteAsync(Encoding.ASCII.GetBytes(sequence), cancellationToken).ConfigureAwait(false);

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
            if (_serialPort is { IsOpen: true } open)
            {
                await WriteAsync("C\r"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Closing the channel is best effort.
        }

        await CloseAsync().ConfigureAwait(false);
        if (_readLoop is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Loop already ended.
            }
        }

        _readLoop = null;
        cts?.Dispose();
        SetState(ConnectionState.Disconnected);
    }

    public override async Task SendFrameAsync(int localBus, CanFrame frame, CancellationToken cancellationToken = default)
    {
        ThrowIfListenOnly();

        if (State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("Lawicel connection is not open.");
        }

        var text = _protocol.EncodeFrame(frame);
        if (text is null)
        {
            return;
        }

        await WriteAsync(Encoding.ASCII.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var serial = _serialPort ?? throw new InvalidOperationException("Lawicel transport is closed.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await serial.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await serial.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(SerialPort serial, CancellationToken cancellationToken)
    {
        var stream = serial.BaseStream;
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                _protocol.Feed(buffer.AsSpan(0, read));
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
        var serial = _serialPort;
        _serialPort = null;
        try
        {
            if (serial is { IsOpen: true })
            {
                serial.Close();
            }
        }
        catch
        {
            // ignore
        }

        serial?.Dispose();
        return Task.CompletedTask;
    }
}
