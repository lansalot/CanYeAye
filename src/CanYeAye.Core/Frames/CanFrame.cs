using CanYeAye.Core.Formatting;

namespace CanYeAye.Core;

/// <summary>Frame kinds supported by SavvyCAN (CommFrame::FrameType).</summary>
public enum FrameType
{
    CanDataFrame = 0x0,
    CanFdDataFrame = 0x1,
    FlexRayDataFrame = 0x2,
    LinDataFrame = 0x3,
    ErrorFrame = 0x4,
    RemoteRequestFrame = 0x5,
    InvalidFrame = 0x6,
}

/// <summary>Error bits carried by error frames (CommFrame::FrameError).</summary>
[Flags]
public enum FrameError : uint
{
    None = 0,
    TransmissionTimeout = 1 << 0,
    LostArbitration = 1 << 1,
    Controller = 1 << 2,
    ProtocolViolation = 1 << 3,
    Transceiver = 1 << 4,
    MissingAcknowledgment = 1 << 5,
    BusOff = 1 << 6,
    BusError = 1 << 7,
    ControllerRestart = 1 << 8,
    Unknown = 1 << 9,
    Any = 0x1FFFFFFF,
}

/// <summary>
/// Extended frame type used throughout CanYeAye. Ported from SavvyCAN's CommFrame
/// (can_structs.h) so file formats, DBC decoding and bus transports share the same
/// semantics: CAN / CAN-FD / FlexRay / LIN frames, remote and error frames, payloads
/// up to 254 bytes, 11/29-bit IDs and a seconds+microseconds timestamp.
/// </summary>
public sealed class CanFrame
{
    public const int FlexRayMaxPayload = 254;
    public const byte FlexRayNullFrameIndicator = 0x04;

    private byte[] _payload = [];

    public CanFrame()
    {
        IsValidFrameId = true;
        SetFrameId(0);
        SetFrameType(FrameType.CanDataFrame);
    }

    public CanFrame(uint identifier, ReadOnlySpan<byte> data)
    {
        IsValidFrameId = true;
        SetFrameType(data.Length > 8 ? FrameType.CanFdDataFrame : FrameType.CanDataFrame);
        IsFlexibleDataRate = data.Length > 8;
        SetFrameId(identifier);
        _payload = data.ToArray();
    }

    public FrameType FrameType { get; private set; }

    /// <summary>Frame identifier masked to 29 bits; 0 for error frames.</summary>
    public uint FrameId => FrameType == FrameType.ErrorFrame ? 0 : _msgId & 0x1FFFFFFF;

    /// <summary>Raw identifier storage; error frames keep their error bits here.</summary>
    public uint RawId => _msgId;

    public bool IsExtended { get; private set; }

    public bool IsValidFrameId { get; private set; }

    public bool IsFlexibleDataRate { get; private set; }

    public bool HasBitrateSwitch { get; private set; }

    public bool HasErrorStateIndicator { get; private set; }

    public bool IsLocalEcho { get; private set; }

    public bool IsReceived => !IsLocalEcho;

    public byte Bus { get; set; }

    public ulong TimeDelta { get; set; }

    public uint FrameCount { get; set; }

    public FrameTimestamp Timestamp { get; set; } = FrameTimestamp.Zero;

    public byte[] Payload => _payload;

    public int Length => _payload.Length;

    public FrameError Error
    {
        get => FrameType == FrameType.ErrorFrame ? (FrameError)(_msgId & 0x1FFFFFFF) : FrameError.None;
        set
        {
            if (FrameType == FrameType.ErrorFrame)
            {
                _msgId = (uint)(value & FrameError.Any);
            }
        }
    }

    // FlexRay header details. SavvyCAN packs these into a 64-bit word; the observable
    // behaviour is the same with explicit fields.
    public bool HasFlexRayHeader { get; private set; }
    public byte FlexRayIndicators { get; private set; }
    public ushort FlexRayHeaderCrc { get; private set; }
    public uint FlexRayFrameCrc { get; private set; }
    public bool FlexRayCrcChecked { get; private set; }
    public bool FlexRayHeaderCrcOk { get; private set; }
    public bool FlexRayFrameCrcOk { get; private set; }
    public bool HasFlexRayDeviceTimestamp { get; private set; }

    public bool IsFlexRay => FrameType == FrameType.FlexRayDataFrame;

    public bool IsFlexRayNullFrame =>
        FrameType == FrameType.FlexRayDataFrame &&
        HasFlexRayHeader &&
        (FlexRayIndicators & FlexRayNullFrameIndicator) == 0;

    public bool HasFlexRayCrcError =>
        FlexRayCrcChecked && !(FlexRayHeaderCrcOk && FlexRayFrameCrcOk);

    public bool IsDataFrame =>
        FrameType is FrameType.CanDataFrame or FrameType.CanFdDataFrame ||
        (FrameType == FrameType.FlexRayDataFrame && !IsFlexRayNullFrame);

    public void SetFrameType(FrameType frameType)
    {
        FrameType = frameType;
        IsFlexibleDataRate = frameType == FrameType.CanFdDataFrame;
        if (!IsFlexibleDataRate)
        {
            HasBitrateSwitch = false;
            HasErrorStateIndicator = false;
        }
    }

    public void SetFrameId(uint newFrameId)
    {
        if (newFrameId < 0x20000000U)
        {
            IsValidFrameId = true;
            _msgId = newFrameId;
            SetExtended(IsExtended || (newFrameId & 0x1FFFF800U) != 0);
        }
        else
        {
            IsValidFrameId = false;
            _msgId = 0;
        }
    }

    public void SetExtended(bool extended) => IsExtended = extended;

    /// <summary>
    /// Sets the payload; more than 8 bytes turns a CAN data frame into CAN FD.
    /// Other frame types (FlexRay, LIN, ...) keep their type, matching SavvyCAN.
    /// </summary>
    public void SetPayload(ReadOnlySpan<byte> data)
    {
        _payload = data.ToArray();
        if (data.Length > 8)
        {
            SetFlexibleDataRate(true);
        }
    }

    public void SetFlexibleDataRate(bool flexibleDataRate)
    {
        if (flexibleDataRate)
        {
            if (FrameType is not (FrameType.CanDataFrame or FrameType.CanFdDataFrame))
            {
                return;
            }

            FrameType = FrameType.CanFdDataFrame;
            IsFlexibleDataRate = true;
        }
        else
        {
            IsFlexibleDataRate = false;
            if (FrameType == FrameType.CanFdDataFrame)
            {
                FrameType = FrameType.CanDataFrame;
            }

            HasBitrateSwitch = false;
            HasErrorStateIndicator = false;
        }
    }

    public void SetBitrateSwitch(bool bitrateSwitch)
    {
        if (bitrateSwitch)
        {
            SetFlexibleDataRate(true);
        }

        HasBitrateSwitch = bitrateSwitch && IsFlexibleDataRate;
    }

    public void SetErrorStateIndicator(bool errorStateIndicator)
    {
        if (errorStateIndicator)
        {
            SetFlexibleDataRate(true);
        }

        HasErrorStateIndicator = errorStateIndicator && IsFlexibleDataRate;
    }

    public void SetLocalEcho(bool localEcho) => IsLocalEcho = localEcho;

    public void SetReceived(bool received) => IsLocalEcho = !received;

    public void SetFlexRayHeader(byte indicators, ushort headerCrc, uint frameCrc)
    {
        HasFlexRayHeader = true;
        FlexRayIndicators = (byte)(indicators & 0x1F);
        FlexRayHeaderCrc = (ushort)(headerCrc & 0x7FF);
        FlexRayFrameCrc = frameCrc & 0xFFFFFF;
    }

    public void SetFlexRayNullFrame(bool isNull)
    {
        var indicators = HasFlexRayHeader ? FlexRayIndicators : FlexRayNullFrameIndicator;
        indicators = isNull
            ? (byte)(indicators & ~FlexRayNullFrameIndicator)
            : (byte)(indicators | FlexRayNullFrameIndicator);
        SetFlexRayHeader(indicators, FlexRayHeaderCrc, FlexRayFrameCrc);
    }

    public void SetFlexRayCrcStatus(bool headerCrcOk, bool frameCrcOk)
    {
        FlexRayCrcChecked = true;
        FlexRayHeaderCrcOk = headerCrcOk;
        FlexRayFrameCrcOk = frameCrcOk;
    }

    public void SetFlexRayDeviceTimestamp(bool fromDevice) => HasFlexRayDeviceTimestamp = fromDevice;

    public void ClearFlexRayInfo()
    {
        HasFlexRayHeader = false;
        FlexRayIndicators = 0;
        FlexRayHeaderCrc = 0;
        FlexRayFrameCrc = 0;
        FlexRayCrcChecked = false;
        FlexRayHeaderCrcOk = false;
        FlexRayFrameCrcOk = false;
        HasFlexRayDeviceTimestamp = false;
    }

    /// <summary>Validity rules copied from CommFrame::isValid().</summary>
    public bool IsValid()
    {
        if (FrameType == FrameType.InvalidFrame)
        {
            return false;
        }

        if (!IsExtended && (_msgId & 0x1FFFF800U) != 0)
        {
            return false;
        }

        if (!IsValidFrameId)
        {
            return false;
        }

        var length = _payload.Length;
        if (FrameType == FrameType.FlexRayDataFrame)
        {
            return !IsExtended && _msgId <= 0x7FFU && length >= 1 && length <= 1 + FlexRayMaxPayload;
        }

        if (IsFlexibleDataRate)
        {
            if (FrameType == FrameType.RemoteRequestFrame)
            {
                return false;
            }

            return length <= 8 ||
                   length is 12 or 16 or 20 or 24 or 32 or 48 or 64;
        }

        return length <= 8;
    }

    public CanFrame Clone()
    {
        var clone = (CanFrame)MemberwiseClone();
        clone._payload = _payload.ToArray();
        return clone;
    }

    public override string ToString()
    {
        var id = IsExtended ? $"{FrameId:X8}" : $"{FrameId:X3}";
        var data = HexFormat.SpaceDelimited(_payload);
        return $"{Timestamp} bus{Bus} {FrameType} {id} [{_payload.Length}] {data}";
    }

    private uint _msgId;
}

