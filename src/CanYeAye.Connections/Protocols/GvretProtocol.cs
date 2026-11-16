using CanYeAye.Core;

namespace CanYeAye.Connections.Protocols;

public sealed record GvretBusParameters(
    bool Bus0Enabled,
    bool Bus0ListenOnly,
    uint Bus0Speed,
    bool Bus1Enabled,
    bool Bus1ListenOnly,
    bool SingleWire,
    uint Bus1Speed);

public sealed record GvretDeviceInfo(ushort BuildNumber, byte SingleWireMode);

public sealed record GvretFdSettingsEntry(bool Capable, bool Enabled, uint DataRate);

/// <summary>
/// GVRET binary serial/TCP protocol (connections/gvretserial.cpp). Feed bytes with
/// <see cref="Feed"/>; completed frames and device replies are raised as events.
/// </summary>
public sealed class GvretProtocol
{
    public const byte CommandMarker = 0xF1;

    private enum RxState
    {
        Idle,
        GetCommand,
        BuildCanFrame,
        TimeSync,
        DigitalInputs,
        AnalogInputs,
        CanBusParams,
        DeviceInfo,
        NumBuses,
        ExtBuses,
        BuildFdFrame,
        FdSettings,
    }

    private RxState _state;
    private int _step;
    private uint _rawTimestamp;
    private uint _rawId;
    private readonly byte[] _data = new byte[64];
    private int _dataLength;
    private bool _fdBitrateSwitch;

    private byte _fdBusCount;
    private readonly List<GvretFdSettingsEntry> _fdEntries = [];

    private uint _buildTimeBasis;
    private long _timeAtGvretSync;

    /// <summary>Device -> application microsecond offset, matching GVRetSerial::timeBasis.</summary>
    public int TimeBasis { get; set; }

    /// <summary>Manager time basis (epoch µs) used when re-synchronising device time.</summary>
    public long SystemTimeBasisMicroseconds { get; set; }

    public bool UseSystemTime { get; set; }

    public int NumBuses { get; set; } = 2;

    public bool HasFdSetup { get; private set; }

    public event EventHandler<CanFrame>? FrameReceived;

    public event EventHandler<GvretBusParameters>? BusParametersReceived;

    public event EventHandler<GvretDeviceInfo>? DeviceInfoReceived;

    public event EventHandler<uint>? NumBusesReceived;

    public event EventHandler? ValidationReceived;

    public event EventHandler<IReadOnlyList<GvretFdSettingsEntry>>? FdSettingsReceived;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            Consume(b);
        }
    }

    private void Consume(byte c)
    {
        switch (_state)
        {
            case RxState.Idle:
                if (c == CommandMarker)
                {
                    _state = RxState.GetCommand;
                }

                return;

            case RxState.GetCommand:
                HandleCommand(c);
                return;

            case RxState.BuildCanFrame:
                ConsumeCanFrameByte(c);
                return;

            case RxState.BuildFdFrame:
                ConsumeFdFrameByte(c);
                return;

            case RxState.TimeSync:
                ConsumeTimeSyncByte(c);
                return;

            case RxState.DigitalInputs:
                if (++_step >= 2)
                {
                    Reset();
                }

                return;

            case RxState.AnalogInputs:
                if (++_step >= 9)
                {
                    Reset();
                }

                return;

            case RxState.CanBusParams:
                ConsumeBusParamsByte(c);
                return;

            case RxState.DeviceInfo:
                ConsumeDeviceInfoByte(c);
                return;

            case RxState.NumBuses:
                NumBuses = Math.Max(1, (int)c);
                NumBusesReceived?.Invoke(this, (uint)NumBuses);
                Reset();
                return;

            case RxState.ExtBuses:
                // 15-byte SWCAN/LIN reply; the values are not used by CanYeAye yet.
                if (++_step >= 15)
                {
                    Reset();
                }

                return;

            case RxState.FdSettings:
                ConsumeFdSettingsByte(c);
                return;
        }
    }

    private void HandleCommand(byte c)
    {
        _step = 0;
        switch (c)
        {
            case 0:
                PrepareFrame();
                _state = RxState.BuildCanFrame;
                break;
            case 1:
                _state = RxState.TimeSync;
                break;
            case 2:
                _state = RxState.DigitalInputs;
                break;
            case 3:
                _state = RxState.AnalogInputs;
                break;
            case 4:
            case 5:
            case 10:
                _state = RxState.Idle;
                break;
            case 6:
                _state = RxState.CanBusParams;
                break;
            case 7:
                _state = RxState.DeviceInfo;
                break;
            case 9:
                ValidationReceived?.Invoke(this, EventArgs.Empty);
                _state = RxState.Idle;
                break;
            case 12:
                _state = RxState.NumBuses;
                break;
            case 13:
                _state = RxState.ExtBuses;
                break;
            case 20:
                PrepareFrame();
                _state = RxState.BuildFdFrame;
                break;
            case 22:
                _fdBusCount = 0;
                _fdEntries.Clear();
                _state = RxState.FdSettings;
                break;
            default:
                // Unknown opcode: stay in GetCommand, like the reference.
                break;
        }
    }

    private void PrepareFrame()
    {
        _rawTimestamp = 0;
        _rawId = 0;
        _dataLength = 0;
        _fdBitrateSwitch = false;
    }

    private void ConsumeCanFrameByte(byte c)
    {
        switch (_step)
        {
            case 0:
                _rawTimestamp = c;
                break;
            case 1:
                _rawTimestamp |= (uint)c << 8;
                break;
            case 2:
                _rawTimestamp |= (uint)c << 16;
                break;
            case 3:
            {
                _rawTimestamp |= (uint)c << 24;
                var buildTimestamp = unchecked((long)_rawTimestamp + TimeBasis);
                BuildFrameTimestamp(buildTimestamp);
                break;
            }
            case 4:
                _rawId = c;
                break;
            case 5:
                _rawId |= (uint)c << 8;
                break;
            case 6:
                _rawId |= (uint)c << 16;
                break;
            case 7:
            {
                _rawId |= (uint)c << 24;
                ApplyRawId();
                break;
            }
            case 8:
            {
                _dataLength = c & 0x0F;
                var bus = (c & 0xF0) >> 4;
                CurrentBus = (byte)bus;
                if (_dataLength == 0)
                {
                    EmitBuiltFrame(FrameType.CanDataFrame);
                    return;
                }

                break;
            }
            default:
            {
                var index = _step - 9;
                if (index < _dataLength && index < _data.Length)
                {
                    _data[index] = c;
                }

                if (_step == _dataLength + 8)
                {
                    EmitBuiltFrame(FrameType.CanDataFrame);
                    return;
                }

                if (index >= _dataLength)
                {
                    Reset();
                    return;
                }

                break;
            }
        }

        _step++;
    }

    private void ConsumeFdFrameByte(byte c)
    {
        switch (_step)
        {
            case 0:
                _rawTimestamp = c;
                break;
            case 1:
                _rawTimestamp |= (uint)c << 8;
                break;
            case 2:
                _rawTimestamp |= (uint)c << 16;
                break;
            case 3:
            {
                _rawTimestamp |= (uint)c << 24;
                var buildTimestamp = unchecked((long)_rawTimestamp + TimeBasis);
                BuildFrameTimestamp(buildTimestamp);
                break;
            }
            case 4:
                _rawId = c;
                break;
            case 5:
                _rawId |= (uint)c << 8;
                break;
            case 6:
                _rawId |= (uint)c << 16;
                break;
            case 7:
            {
                _rawId |= (uint)c << 24;
                ApplyRawId();
                break;
            }
            case 8:
                if ((c & 0x7F) > 64)
                {
                    Reset();
                    return;
                }

                _dataLength = c & 0x7F;
                _fdBitrateSwitch = (c & 0x80) != 0;
                break;
            case 9:
                CurrentBus = c;
                break;
            default:
            {
                var index = _step - 10;
                if (index < _dataLength && index < _data.Length)
                {
                    _data[index] = c;
                }
                else if (index >= _dataLength)
                {
                    EmitBuiltFrame(FrameType.CanFdDataFrame);
                    return;
                }

                break;
            }
        }

        _step++;
    }

    private void ConsumeTimeSyncByte(byte c)
    {
        switch (_step)
        {
            case 0:
                _buildTimeBasis = c;
                break;
            case 1:
                _buildTimeBasis |= (uint)c << 8;
                break;
            case 2:
                _buildTimeBasis |= (uint)c << 16;
                break;
            case 3:
            {
                _buildTimeBasis |= (uint)c << 24;
                _timeAtGvretSync = NowMicroseconds();
                RebuildLocalTimeBasis();
                Reset();
                return;
            }
        }

        _step++;
    }

    private void ConsumeBusParamsByte(byte c)
    {
        switch (_step)
        {
            case 0:
                _bus0Flags = c;
                break;
            case 1:
                _bus0Speed = c;
                break;
            case 2:
                _bus0Speed |= (uint)c << 8;
                break;
            case 3:
                _bus0Speed |= (uint)c << 16;
                break;
            case 4:
                _bus0Speed |= (uint)c << 24;
                break;
            case 5:
                _bus1Flags = c;
                break;
            case 6:
                _bus1Speed = c;
                break;
            case 7:
                _bus1Speed |= (uint)c << 8;
                break;
            case 8:
                _bus1Speed |= (uint)c << 16;
                break;
            case 9:
            {
                _bus1Speed |= (uint)c << 24;
                var parameters = new GvretBusParameters(
                    (_bus0Flags & 0x0F) != 0,
                    (_bus0Flags >> 4) != 0,
                    _bus0Speed,
                    (_bus1Flags & 0x0F) != 0,
                    (_bus1Flags >> 4) != 0,
                    (_bus1Flags >> 6) != 0,
                    _bus1Speed);
                BusParametersReceived?.Invoke(this, parameters);
                Reset();
                return;
            }
        }

        _step++;
    }

    private void ConsumeDeviceInfoByte(byte c)
    {
        switch (_step)
        {
            case 0:
                _deviceBuild = c;
                break;
            case 1:
                _deviceBuild |= (ushort)(c << 8);
                break;
            case 5:
                DeviceInfoReceived?.Invoke(this, new GvretDeviceInfo(_deviceBuild, c));
                Reset();
                return;
        }

        _step++;
    }

    private void ConsumeFdSettingsByte(byte c)
    {
        if (_step == 0)
        {
            _fdBusCount = c;
            if (_fdBusCount is 0 or > 16)
            {
                Reset();
                return;
            }

            _step++;
            return;
        }

        var entryIndex = (_step - 1) / 5;
        var entryOffset = (_step - 1) % 5;
        if (entryOffset == 0)
        {
            _fdFlags = c;
            _fdDataRate = 0;
        }
        else if (entryOffset is >= 1 and <= 4)
        {
            _fdDataRate |= (uint)c << (8 * (entryOffset - 1));
        }

        if (entryOffset == 4)
        {
            _fdEntries.Add(new GvretFdSettingsEntry(
                (_fdFlags & 0x01) != 0,
                (_fdFlags & 0x02) != 0,
                _fdDataRate));

            if (entryIndex + 1 == _fdBusCount)
            {
                HasFdSetup = true;
                FdSettingsReceived?.Invoke(this, _fdEntries.ToArray());
                Reset();
                return;
            }
        }

        _step++;
    }

    private void BuildFrameTimestamp(long buildTimestamp)
    {
        CurrentTimestamp = UseSystemTime
            ? FrameTimestamp.FromMicroseconds(NowMicroseconds())
            : FrameTimestamp.FromMicroseconds(buildTimestamp);
    }

    private void ApplyRawId()
    {
        var extended = (_rawId & (1u << 31)) != 0;
        var id = _rawId & 0x7FFFFFFF;
        CurrentId = id;
        CurrentExtended = extended || id > 0x7FF;
    }

    private void EmitBuiltFrame(FrameType type)
    {
        var frame = new CanFrame();
        frame.Timestamp = CurrentTimestamp;
        frame.SetFrameId(CurrentId);
        frame.SetExtended(CurrentExtended);
        frame.Bus = CurrentBus;
        frame.SetPayload(_data.AsSpan(0, _dataLength));
        frame.SetFrameType(type);
        if (type == FrameType.CanFdDataFrame)
        {
            frame.SetFlexibleDataRate(true);
            frame.SetBitrateSwitch(_fdBitrateSwitch);
        }

        frame.SetReceived(true);

        var bus = frame.Bus;
        Reset();

        if (bus >= NumBuses)
        {
            return;
        }

        FrameReceived?.Invoke(this, frame);
    }

    private void RebuildLocalTimeBasis()
    {
        var systemDelta = _timeAtGvretSync - SystemTimeBasisMicroseconds;
        var localDelta = unchecked((int)(_buildTimeBasis - systemDelta));
        TimeBasis = unchecked(-localDelta);
    }

    private void Reset()
    {
        _state = RxState.Idle;
        _step = 0;
    }

    public static long NowMicroseconds() =>
        (DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    // ---- Encoding -------------------------------------------------------

    /// <summary>Put the device into binary mode, request bus info, sync time and validate.</summary>
    public static byte[] EncodeInitSequence()
    {
        return
        [
            0xE7, 0xE7,
            CommandMarker, 0x0C,
            CommandMarker, 0x06,
            CommandMarker, 0x07,
            CommandMarker, 0x16,
            CommandMarker, 0x01,
            CommandMarker, 0x09,
        ];
    }

    public static byte[] EncodeValidationRequest() => [CommandMarker, 0x09];

    public static byte[] EncodeGetNumBuses() => [CommandMarker, 0x0C];

    public static byte[] EncodeGetExtendedBuses() => [CommandMarker, 0x0D];

    /// <summary>Encodes a classic or CAN-FD transmit frame; error frames produce no bytes.</summary>
    public static byte[] EncodeFrame(CanFrame frame)
    {
        if ((frame.FrameId & 0x20000000u) != 0)
        {
            return [];
        }

        var isFd = frame.IsFlexibleDataRate || frame.FrameType == FrameType.CanFdDataFrame;
        var length = Math.Min(frame.Length, isFd ? 64 : 8);
        var id = frame.FrameId;
        if (frame.IsExtended)
        {
            id |= 1u << 31;
        }

        var buffer = new byte[2 + 4 + 1 + 1 + length + 1];
        var i = 0;
        buffer[i++] = CommandMarker;
        buffer[i++] = isFd ? (byte)20 : (byte)0;
        buffer[i++] = (byte)(id & 0xFF);
        buffer[i++] = (byte)((id >> 8) & 0xFF);
        buffer[i++] = (byte)((id >> 16) & 0xFF);
        buffer[i++] = (byte)((id >> 24) & 0xFF);
        buffer[i++] = (byte)(frame.Bus & 0x03);
        buffer[i++] = (byte)(length | (isFd && frame.HasBitrateSwitch ? 0x80 : 0));
        frame.Payload.AsSpan(0, length).CopyTo(buffer.AsSpan(i));
        // Final byte is the zero terminator and is already zero.
        return buffer;
    }

    /// <summary>Set both classic CAN bus speeds (GVRET opcode 5).</summary>
    public static byte[] EncodeBusConfig(
        uint bus0Speed,
        bool bus0Active,
        bool bus0ListenOnly,
        uint bus1Speed,
        bool bus1Active,
        bool bus1ListenOnly,
        bool singleWire)
    {
        var b0 = EncodeBusWord(bus0Speed, bus0Active, bus0ListenOnly, false);
        var b1 = EncodeBusWord(bus1Speed, bus1Active, bus1ListenOnly, singleWire);
        return
        [
            CommandMarker, 0x05,
            (byte)(b0 & 0xFF), (byte)((b0 >> 8) & 0xFF), (byte)((b0 >> 16) & 0xFF), (byte)((b0 >> 24) & 0xFF),
            (byte)(b1 & 0xFF), (byte)((b1 >> 8) & 0xFF), (byte)((b1 >> 16) & 0xFF), (byte)((b1 >> 24) & 0xFF),
            0x00,
        ];
    }

    /// <summary>Set CAN-FD mode/data rate for a bus (GVRET opcode 21).</summary>
    public static byte[] EncodeFdConfig(byte bus, bool enabled, uint dataRate)
    {
        return
        [
            CommandMarker, 0x15, bus, enabled ? (byte)0x02 : (byte)0x00,
            (byte)(dataRate & 0xFF), (byte)((dataRate >> 8) & 0xFF),
            (byte)((dataRate >> 16) & 0xFF), (byte)((dataRate >> 24) & 0xFF),
        ];
    }

    private static uint EncodeBusWord(uint speed, bool active, bool listenOnly, bool singleWire)
    {
        var word = speed | 0x80000000u;
        if (active)
        {
            word |= 0x40000000u;
        }

        if (listenOnly)
        {
            word |= 0x20000000u;
        }

        if (singleWire)
        {
            word |= 0x10000000u;
        }

        return word;
    }

    private byte _bus0Flags;
    private byte _bus1Flags;
    private uint _bus0Speed;
    private uint _bus1Speed;
    private ushort _deviceBuild;
    private byte _fdFlags;
    private uint _fdDataRate;

    private FrameTimestamp CurrentTimestamp { get; set; }
    private uint CurrentId { get; set; }
    private bool CurrentExtended { get; set; }
    private byte CurrentBus { get; set; }
}
