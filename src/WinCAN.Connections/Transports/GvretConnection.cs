using System.IO.Ports;
using System.Net.Sockets;
using WinCAN.Connections.Protocols;
using WinCAN.Core;

namespace WinCAN.Connections.Transports;

/// <summary>
/// GVRET binary protocol over a serial port or TCP socket (connections/gvretserial.cpp).
/// The ESP-based adapters need a short settle delay before the init sequence; plain serial
/// and TCP devices do not.
/// </summary>
public sealed record GvretConnectionOptions
{
    public string? SerialPort { get; init; }

    public int BaudRate { get; init; } = 115200;

    public string? Host { get; init; }

    public int TcpPort { get; init; } = 23;

    /// <summary>ESP32 adapters reboot on open, so the reference waits three seconds.</summary>
    public bool EspSerialMode { get; init; } = true;

    /// <summary>SavvyCAN's Main/ValidateComm: require an opcode-9 reply before "connected".</summary>
    public bool ValidateComm { get; init; } = true;

    public uint Bus0Speed { get; init; } = 500_000;

    public uint Bus1Speed { get; init; } = 500_000;

    public bool Bus0ListenOnly { get; init; }

    public bool Bus1ListenOnly { get; init; }

    public bool SingleWire { get; init; }
}

public sealed class GvretConnection : CanConnectionBase
{
    private readonly GvretConnectionOptions _options;
    private readonly GvretProtocol _protocol = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _sync = new();

    private SerialPort? _serialPort;
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private Task? _tickLoop;
    private TaskCompletionSource? _firstReply;
    private int _validationCounter = 10;

    public GvretConnection(GvretConnectionOptions options)
        : base([new CanBusInfo(0, "CAN0"), new CanBusInfo(1, "CAN1")])
    {
        _options = options;
        _protocol.FrameReceived += (_, frame) => Publish(frame);
        _protocol.NumBusesReceived += (_, _) => UpdateBusNames();
        _protocol.ValidationReceived += (_, _) => OnDeviceReply();
        _protocol.BusParametersReceived += (_, _) => OnDeviceReply();
        _protocol.DeviceInfoReceived += (_, _) => OnDeviceReply();
        _protocol.FdSettingsReceived += (_, _) => OnDeviceReply();
    }

    public override string Name => _options.SerialPort is { Length: > 0 } port
        ? $"GVRET {port}"
        : $"GVRET {_options.Host}:{_options.TcpPort}";

    public override ConnectionType Type => ConnectionType.GvretSerial;

    /// <summary>SavvyCAN's useSystemTime toggle for device timestamps.</summary>
    public bool UseSystemTime
    {
        get => _protocol.UseSystemTime;
        set => _protocol.UseSystemTime = value;
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
            await OpenTransportAsync(cancellationToken).ConfigureAwait(false);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _protocol.NumBuses = Math.Max(1, Buses.Count);
            _protocol.SystemTimeBasisMicroseconds = GvretProtocol.NowMicroseconds();
            _firstReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);

            await WriteAsync(GvretProtocol.EncodeInitSequence(), cancellationToken).ConfigureAwait(false);
            await WriteAsync(
                GvretProtocol.EncodeBusConfig(
                    _options.Bus0Speed,
                    true,
                    _options.Bus0ListenOnly,
                    _options.Bus1Speed,
                    true,
                    _options.Bus1ListenOnly,
                    _options.SingleWire),
                cancellationToken).ConfigureAwait(false);

            if (!_options.ValidateComm)
            {
                SetState(ConnectionState.Connected);
            }
            else
            {
                var timeout = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                var completed = await Task.WhenAny(_firstReply.Task, timeout).ConfigureAwait(false);
                if (completed != _firstReply.Task)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SetState(ConnectionState.Error, "GVRET device did not answer the init sequence");
                    await CloseTransportAsync().ConfigureAwait(false);
                    return;
                }

                SetState(ConnectionState.Connected);
            }

            _tickLoop = Task.Run(() => TickLoopAsync(_cts.Token), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            SetState(ConnectionState.Disconnected, "connection cancelled");
            throw;
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Error, ex.Message);
            await CloseTransportAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnecting);
        var cts = _cts;
        _cts = null;
        cts?.Cancel();

        await CloseTransportAsync().ConfigureAwait(false);

        var loops = new[] { _readLoop, _tickLoop };
        _readLoop = null;
        _tickLoop = null;
        foreach (var loop in loops)
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
                // The loop may already have ended with an I/O error.
            }
        }

        cts?.Dispose();
        SetState(ConnectionState.Disconnected);
    }

    public override async Task SendFrameAsync(int localBus, CanFrame frame, CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("GVRET connection is not open.");
        }

        var copy = frame.Clone();
        copy.Bus = (byte)localBus;
        var bytes = GvretProtocol.EncodeFrame(copy);
        if (bytes.Length == 0)
        {
            return;
        }

        await WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private void OnDeviceReply()
    {
        _validationCounter = 10;
        _firstReply?.TrySetResult();
    }

    private void UpdateBusNames()
    {
        var count = Math.Clamp(_protocol.NumBuses, 1, 16);
        if (count == Buses.Count)
        {
            return;
        }

        SetBuses(Enumerable.Range(0, count).Select(i => new CanBusInfo(i, $"CAN{i}")).ToArray());
    }

    private async Task OpenTransportAsync(CancellationToken cancellationToken)
    {
        if (_options.SerialPort is { Length: > 0 } portName)
        {
            var serial = new SerialPort(portName, _options.BaudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.RequestToSend,
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = 500,
                WriteTimeout = 500,
            };
            serial.Open();
            _serialPort = serial;
            _stream = serial.BaseStream;

            if (_options.EspSerialMode)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (_options.Host is not { Length: > 0 } host)
        {
            throw new InvalidOperationException("GVRET needs a serial port or a TCP host.");
        }

        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, _options.TcpPort, cancellationToken).ConfigureAwait(false);
        _tcpClient = client;
        _stream = client.GetStream();
    }

    private async Task CloseTransportAsync()
    {
        Stream? stream;
        SerialPort? serial;
        TcpClient? tcp;
        lock (_sync)
        {
            stream = _stream;
            serial = _serialPort;
            tcp = _tcpClient;
            _stream = null;
            _serialPort = null;
            _tcpClient = null;
        }

        try
        {
            stream?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            serial?.Dispose();
        }
        catch
        {
            // ignore
        }

        tcp?.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("GVRET transport is closed.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var stream = _stream;
                if (stream is null)
                {
                    break;
                }

                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                _protocol.Feed(buffer.AsSpan(0, read));
            }

            // Only report a device-initiated close while the link was healthy. A
            // deliberate close after a failure (Error) or Disconnect must not
            // overwrite that state.
            if (!cancellationToken.IsCancellationRequested && State == ConnectionState.Connected)
            {
                SetState(ConnectionState.Disconnected, "GVRET device closed the connection");
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!cancellationToken.IsCancellationRequested &&
                State is ConnectionState.Connected or ConnectionState.Connecting)
            {
                SetState(ConnectionState.Error, ex.Message);
            }
        }
    }

    private async Task TickLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (State != ConnectionState.Connected)
                {
                    continue;
                }

                if (_options.ValidateComm)
                {
                    _validationCounter--;
                    if (_validationCounter <= 0)
                    {
                        SetState(ConnectionState.Error, "GVRET communication validation failed");
                        return;
                    }
                }

                try
                {
                    await WriteAsync(GvretProtocol.EncodeValidationRequest(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
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
}
