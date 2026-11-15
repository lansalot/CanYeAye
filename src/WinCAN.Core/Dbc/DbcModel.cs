using WinCAN.Core;

namespace WinCAN.Core.Dbc;

public enum DbcSignalValueType
{
    UnsignedInt,
    SignedInt,
    SingleFloat,
    DoubleFloat,
    String,
}

public enum DbcAttributeTarget
{
    General,
    Node,
    Message,
    Signal,
    Any,
}

public enum DbcAttributeValueKind
{
    Int,
    Float,
    String,
    Enum,
}

public enum MatchingCriteria
{
    Exact,
    J1939,
    Gmlan,
}

public sealed class DbcNode
{
    public string Name { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    /// <summary>DBC base name without extension (used by the node tree UI).</summary>
    public string SourceFileName { get; set; } = string.Empty;

    public List<DbcAttributeValue> Attributes { get; } = [];

    public override string ToString() => Name;
}

public sealed record DbcValueEntry(int Value, string Description);

public sealed class DbcValueTable
{
    public string Name { get; set; } = string.Empty;

    public List<DbcValueEntry> Entries { get; } = [];
}

public sealed class DbcAttribute
{
    public string Name { get; set; } = string.Empty;

    public DbcAttributeValueKind ValueKind { get; set; } = DbcAttributeValueKind.Int;

    public DbcAttributeTarget Target { get; set; } = DbcAttributeTarget.General;

    public double Upper { get; set; }

    public double Lower { get; set; }

    public List<string> EnumValues { get; } = [];

    public object? DefaultValue { get; set; }
}

public sealed class DbcAttributeValue
{
    public string Name { get; set; } = string.Empty;

    /// <summary>int, double or string, matching the owning definition's kind.</summary>
    public object? Value { get; set; }
}

/// <summary>
/// A CAN signal inside a message (DBC_SIGNAL). Multiplex state, the sawtooth bit layout and the
/// decode path are all driven from here via <see cref="SignalCodec"/>.
/// </summary>
public sealed class DbcSignal
{
    public string Name { get; set; } = string.Empty;

    /// <summary>SavvyCAN uses 1-based start bits and 0 for spares (unused); a missing start bit
    /// disturbs the message layout. The reference default is 1.</summary>
    public int StartBit { get; set; } = 1;

    public int SignalSize { get; set; } = 1;

    public bool IntelByteOrder { get; set; }

    public bool IsMultiplexor { get; set; }

    public bool IsMultiplexed { get; set; }

    public bool HasExtendedMultiplexing { get; set; }

    public List<(int Min, int Max)> MultiplexRanges { get; } = [];

    public DbcSignal? MultiplexParent { get; set; }

    public List<DbcSignal> MultiplexedChildren { get; } = [];

    public DbcSignalValueType ValueType { get; set; } = DbcSignalValueType.UnsignedInt;

    public double Factor { get; set; } = 1.0;

    public double Bias { get; set; }

    public double Min { get; set; }

    public double Max { get; set; } = 1.0;

    public DbcNode Receiver { get; set; } = null!;

    public DbcMessage ParentMessage { get; set; } = null!;

    public string UnitName { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public object? CachedValue { get; set; }

    public List<DbcAttributeValue> Attributes { get; } = [];

    public List<DbcValueEntry> ValueDescriptions { get; } = [];

    // Decision #1: preserve the original numeric literal text so lossless round trips survive
    // fields the user has not edited.
    public string? FactorText { get; set; }

    public string? BiasText { get; set; }

    public string? MinText { get; set; }

    public string? MaxText { get; set; }

    public bool IsSigned => ValueType == DbcSignalValueType.SignedInt;

    public void AddMultiplexRange(int min, int max)
    {
        if (min != max)
        {
            HasExtendedMultiplexing = true;
        }

        IsMultiplexed = true;
        if (!MultiplexRanges.Any(r => r.Min == min && r.Max == max))
        {
            MultiplexRanges.Add((min, max));
        }
    }

    public bool IsValueMatchingMultiplex(int value) =>
        MultiplexRanges.Any(r => value >= r.Min && value <= r.Max);

    public int GetSimpleMultiplexValue() => MultiplexRanges[0].Min;

    public IEnumerable<DbcSignal> MultiplexedDescendants()
    {
        foreach (var child in MultiplexedChildren)
        {
            yield return child;
            foreach (var grandChild in child.MultiplexedDescendants())
            {
                yield return grandChild;
            }
        }
    }

    public bool IsSignalInMessage(CanFrame frame)
    {
        if (IsMultiplexor && !IsMultiplexed)
        {
            return true;
        }

        if (!IsMultiplexed)
        {
            return true;
        }

        if (ParentMessage.MultiplexorSignal is not { } mux)
        {
            return false;
        }

        if (MultiplexParent is null || !MultiplexParent.IsSignalInMessage(frame))
        {
            return false;
        }

        if (!MultiplexParent.TryProcessAsInt(frame, out var value))
        {
            return false;
        }

        return IsValueMatchingMultiplex(value);
    }

    public bool TryProcessAsInt(CanFrame frame, out int value)
    {
        value = 0;
        if (ValueType is DbcSignalValueType.String
            or DbcSignalValueType.SingleFloat
            or DbcSignalValueType.DoubleFloat)
        {
            return false;
        }

        var raw = SignalCodec.ExtractBits(
            frame.Payload,
            StartBit,
            SignalSize,
            IntelByteOrder,
            IsSigned);
        var scaled = (double)raw * Factor + Bias;
        value = (int)scaled;
        CachedValue = value;
        return true;
    }

    public bool TryProcessAsDouble(CanFrame frame, out double value)
    {
        value = 0;
        if (SignalCodec.LastByteOfSignal(StartBit, SignalSize, IntelByteOrder) >= frame.Length)
        {
            return false;
        }

        if (ValueType is DbcSignalValueType.SingleFloat or DbcSignalValueType.DoubleFloat)
        {
            var ok = SignalCodec.TryProcessFloat(frame.Payload, StartBit, SignalSize, IntelByteOrder, ValueType, out var f);
            if (!ok)
            {
                return false;
            }

            value = f * Factor + Bias;
            CachedValue = value;
            return true;
        }

        var raw = (long)SignalCodec.ExtractBits(frame.Payload, StartBit, SignalSize, IntelByteOrder, IsSigned);
        value = raw * Factor + Bias;
        CachedValue = value;
        return true;
    }

    /// <summary>Pretty text for the frame list / detail views (section 7.2).</summary>
    public string ProcessAsText(CanFrame frame, bool outputName = true, bool outputUnit = true)
    {
        if (!TryProcessAsDouble(frame, out var scalar))
        {
            return string.Empty;
        }

        var intValue = (long)scalar;
        var isInteger = Factor == Math.Floor(Factor) && ValueType is DbcSignalValueType.UnsignedInt or DbcSignalValueType.SignedInt;

        var text = FormatValue(intValue, scalar, isInteger);

        var result = outputName ? Name + ": " : string.Empty;
        result += text;
        if (outputUnit)
        {
            // Reference appends the unit unconditionally, so an empty unit leaves a trailing space.
            result += " " + UnitName;
        }

        return result;
    }

    public bool TryGetValueDescription(long intValue, out string description)
    {
        foreach (var entry in ValueDescriptions)
        {
            if (entry.Value == intValue)
            {
                description = entry.Description;
                return true;
            }
        }

        description = string.Empty;
        return false;
    }

    public string FormatWithValueDescriptions(CanFrame frame, bool outputName = true)
    {
        if (!TryProcessAsInt(frame, out var intValue))
        {
            return string.Empty;
        }

        var valueText = TryGetValueDescription(intValue, out var description)
            ? description
            : intValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return (outputName ? Name + ": " : string.Empty) + valueText;
    }

    private string FormatValue(long intValue, double scalar, bool isInteger)
    {
        if (TryGetValueDescription(intValue, out var description))
        {
            return description;
        }

        return isInteger
            ? intValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : scalar.ToString("g6", System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed class DbcMessage
{
    public uint Id { get; set; }

    public bool IsExtended { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public uint Length { get; set; }

    public bool IsCanFd { get; set; }

    public bool BitRateSwitch { get; set; } = true;

    public DbcNode Sender { get; set; } = null!;

    public string BackgroundColor { get; set; } = string.Empty;

    public string ForegroundColor { get; set; } = string.Empty;

    public List<DbcAttributeValue> Attributes { get; } = [];

    public SortedDictionary<string, DbcSignal> Signals { get; } = new(StringComparer.Ordinal);

    public DbcSignal? MultiplexorSignal { get; set; }

    public IEnumerable<DbcSignal> ActiveSignals(CanFrame frame)
    {
        foreach (var signal in Signals.Values)
        {
            if (signal.IsSignalInMessage(frame))
            {
                yield return signal;
            }
        }
    }
}
