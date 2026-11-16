using WinCAN.Core;

namespace WinCAN.Connections.Transports;

/// <summary>PCAN-USB settings. Channel 1 is <c>PCAN_USBBUS1</c> (USB1).</summary>
public sealed record PcanConnectionOptions
{
    public int Channel { get; init; } = 1;

    public uint Bitrate { get; init; } = 500_000;
}

/// <summary>
/// PEAK PCAN-USB transport over PCAN-Basic (classic CAN). The driver handles USB
/// enumeration; the configured channel selects USB1..USB16.
/// </summary>
public sealed class PcanConnection : CanConnectionBase
{
    private readonly PcanConnectionOptions _options;
    private readonly ushort _channel;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private long _timestampBasis;
    private bool _hasTimestampBasis;

    public PcanConnection(PcanConnectionOptions options)
        : base([new CanBusInfo(0, $"PCAN-USB {options.Channel}")])
    {
        _options = options;
        _channel = PcanNative.ChannelHandle(options.Channel);
    }

    public override string Name => $"PCAN-USB {_options.Channel}";

    public override ConnectionType Type => ConnectionType.Pcan;

    /// <summary>BTR0/BTR1 constant for a bit rate, or null when PCAN-Basic cannot express it.</summary>
    public static ushort? GetBitTiming(uint bitrate) => PcanNative.BitTiming(bitrate);

    /// <summary>PCAN-USB channels currently reported by the driver (1..16).</summary>
    public static IReadOnlyList<int> GetAvailableChannels() => PcanNative.GetAvailableChannels();

    public static string GetHardwareName(int channel) => PcanNative.GetHardwareName(channel);

    /// <summary>True when this platform's PCAN-Basic library (PCANBasic.dll / libPCBUSB.dylib / libpcanbasic.so) loads.</summary>
    public static bool IsDriverAvailable() => PcanNative.IsDriverAvailable();

    /// <summary>Name of the driver library this platform needs, for user-facing messages.</summary>
    public static string DriverLibraryName => PcanNative.DriverLibraryName;

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            return;
        }

        SetState(ConnectionState.Connecting);
        var bitTiming = PcanNative.BitTiming(_options.Bitrate);
        if (bitTiming is null)
        {
            var message = $"PCAN-Basic does not support {_options.Bitrate} bit/s";
            SetState(ConnectionState.Error, message);
            throw new InvalidOperationException(message);
        }

        uint status;
        try
        {
            status = PcanNative.Initialize(_channel, bitTiming.Value, 0, 0, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            var message = $"PCAN driver not installed ({PcanNative.DriverLibraryName})";
            SetState(ConnectionState.Error, message);
            throw new InvalidOperationException(message, ex);
        }

        if (status != PcanNative.ErrorOk)
        {
            var message = PcanNative.Describe(status);
            SetState(ConnectionState.Error, message);
            throw new InvalidOperationException(message);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _hasTimestampBasis = false;
        SetState(ConnectionState.Connected);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public override async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is ConnectionState.Disconnected && _readLoop is null)
        {
            return;
        }

        SetState(ConnectionState.Disconnecting);
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        PcanNative.Uninitialize(_channel);

        if (_readLoop is { } loop)
        {
            _readLoop = null;
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // the loop may already have stopped with a driver error
            }
        }

        cts?.Dispose();
        SetState(ConnectionState.Disconnected);
    }

    public override Task SendFrameAsync(int localBus, CanFrame frame, CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("PCAN connection is not open.");
        }

        if (frame.IsFlexibleDataRate || frame.FrameType == FrameType.CanFdDataFrame)
        {
            throw new NotSupportedException("PCAN CAN-FD transmission is not implemented yet.");
        }

        var length = Math.Min(frame.Length, 8);
        var message = new PcanNative.PcanMessage
        {
            Id = frame.FrameId,
            MessageType = (byte)((frame.IsExtended ? PcanNative.MessageExtended : PcanNative.MessageStandard) |
                                 (frame.FrameType == FrameType.RemoteRequestFrame ? PcanNative.MessageRemoteRequest : 0)),
            Length = (byte)length,
            Data = new byte[8],
        };
        frame.Payload.AsSpan(0, length).CopyTo(message.Data);

        var status = PcanNative.Write(_channel, ref message);
        if (status != PcanNative.ErrorOk)
        {
            throw new InvalidOperationException(PcanNative.Describe(status));
        }

        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = PcanNative.Read(_channel, out var message, out var timestamp);
                if (status == PcanNative.ErrorOk)
                {
                    Publish(Convert(message, timestamp));
                    continue;
                }

                if (status == PcanNative.ErrorQrcvEmpty || status == PcanNative.ErrorQOverrun)
                {
                    await Task.Delay(3, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (status is PcanNative.ErrorBusLight or PcanNative.ErrorBusHeavy)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                SetState(ConnectionState.Error, PcanNative.Describe(status));
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private CanFrame Convert(PcanNative.PcanMessage message, PcanNative.PcanTimestamp timestamp)
    {
        var deviceMicroseconds =
            (((ulong)timestamp.MillisOverflow << 32) | timestamp.Millis) * 1000UL + timestamp.Micros;
        if (!_hasTimestampBasis)
        {
            _timestampBasis = NowMicroseconds() - (long)deviceMicroseconds;
            _hasTimestampBasis = true;
        }

        var frame = new CanFrame();
        frame.SetFrameId(message.Id & 0x1FFFFFFF);
        frame.SetExtended((message.MessageType & PcanNative.MessageExtended) != 0);
        frame.SetFrameType((message.MessageType & PcanNative.MessageRemoteRequest) != 0
            ? FrameType.RemoteRequestFrame
            : FrameType.CanDataFrame);
        frame.Bus = 0;
        frame.Timestamp = FrameTimestamp.FromMicroseconds(_timestampBasis + (long)deviceMicroseconds);
        var length = Math.Min((int)message.Length, 8);
        frame.SetPayload(message.Data is null ? [] : message.Data.AsSpan(0, length));
        frame.SetReceived(true);
        return frame;
    }

    private static long NowMicroseconds() =>
        (DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
}
