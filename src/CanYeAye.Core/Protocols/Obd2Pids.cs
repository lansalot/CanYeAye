namespace CanYeAye.Core.Protocols;

/// <summary>OBD-II (SAE J1979) Mode 01 parameter IDs and the bit layout of their data bytes.
/// Bit 0 is the most significant bit of data byte A, so "(256A + B) / 4" is start bit 0,
/// length 16, scale 0.25.</summary>
public static class Obd2
{
    public const byte ShowCurrentData = 0x01;
    public const byte VehicleInfo = 0x09;
    public const byte PositiveReplyOffset = 0x40;

    private const double Percent = 100.0 / 255.0;
    private const double Trim = 100.0 / 128.0;

    public sealed record ValueName(int Value, string Description);

    public sealed class Field
    {
        public required string Name { get; init; }

        public string Unit { get; init; } = string.Empty;

        public int StartBit { get; init; }

        public int BitLength { get; init; }

        public double Scale { get; init; } = 1.0;

        public double Offset { get; init; }

        public bool IsSigned { get; init; }

        public IReadOnlyList<ValueName> ValueNames { get; init; } = [];

        public double Minimum()
        {
            var rawMin = IsSigned ? -Math.Pow(2, BitLength - 1) : 0.0;
            var rawMax = IsSigned ? Math.Pow(2, BitLength - 1) - 1 : Math.Pow(2, BitLength) - 1;
            return Math.Min(rawMin * Scale + Offset, rawMax * Scale + Offset);
        }

        public double Maximum()
        {
            var rawMin = IsSigned ? -Math.Pow(2, BitLength - 1) : 0.0;
            var rawMax = IsSigned ? Math.Pow(2, BitLength - 1) - 1 : Math.Pow(2, BitLength) - 1;
            return Math.Max(rawMin * Scale + Offset, rawMax * Scale + Offset);
        }
    }

    public sealed record PidInfo(byte Pid, string Name, int DataBytes, IReadOnlyList<Field> Fields);

    public sealed record Value(Field Field, long Raw, double Scaled, string Text);

    public sealed record Reply(byte Pid, byte[] Data);

    private static Field Number(string name, string unit, int startBit, int bitLength,
        double scale = 1.0, double offset = 0.0, bool isSigned = false) =>
        new()
        {
            Name = name,
            Unit = unit,
            StartBit = startBit,
            BitLength = bitLength,
            Scale = scale,
            Offset = offset,
            IsSigned = isSigned,
        };

    private static Field State(string name, int startBit, int bitLength, IReadOnlyList<ValueName> valueNames) =>
        new()
        {
            Name = name,
            StartBit = startBit,
            BitLength = bitLength,
            ValueNames = valueNames,
        };

    private static readonly IReadOnlyList<ValueName> OffOn = [new(0, "Off"), new(1, "On")];

    private static readonly IReadOnlyList<ValueName> FuelSystemStatus =
    [
        new(0, "Off"),
        new(1, "Open loop, engine not warm enough"),
        new(2, "Closed loop, using oxygen sensor feedback"),
        new(4, "Open loop, engine load or fuel cut"),
        new(8, "Open loop, system failure"),
        new(16, "Closed loop, fault in an oxygen sensor"),
    ];

    private static readonly IReadOnlyList<ValueName> SecondaryAirStatus =
    [
        new(1, "Upstream"),
        new(2, "Downstream of catalytic converter"),
        new(4, "From the outside atmosphere or off"),
        new(8, "Pump commanded on for diagnostics"),
    ];

    private static readonly IReadOnlyList<ValueName> ObdStandards =
    [
        new(1, "OBD-II (CARB)"), new(2, "OBD (EPA)"), new(3, "OBD and OBD-II"), new(4, "OBD-I"),
        new(5, "Not OBD compliant"), new(6, "EOBD"), new(7, "EOBD and OBD-II"), new(8, "EOBD and OBD"),
        new(9, "EOBD, OBD and OBD-II"), new(10, "JOBD"), new(11, "JOBD and OBD-II"), new(12, "JOBD and EOBD"),
        new(13, "JOBD, EOBD and OBD-II"),
    ];

    private static readonly IReadOnlyList<ValueName> FuelTypes =
    [
        new(0, "Not available"), new(1, "Gasoline"), new(2, "Methanol"), new(3, "Ethanol"), new(4, "Diesel"),
        new(5, "LPG"), new(6, "CNG"), new(7, "Propane"), new(8, "Electric"), new(9, "Bifuel running gasoline"),
        new(10, "Bifuel running methanol"), new(11, "Bifuel running ethanol"), new(12, "Bifuel running LPG"),
        new(13, "Bifuel running CNG"), new(14, "Bifuel running propane"), new(15, "Bifuel running electricity"),
        new(16, "Bifuel running electric and combustion engine"), new(17, "Hybrid gasoline"),
        new(18, "Hybrid ethanol"), new(19, "Hybrid diesel"), new(20, "Hybrid electric"),
        new(21, "Hybrid running electric and combustion engine"), new(22, "Hybrid regenerative"),
        new(23, "Bifuel running diesel"),
    ];

    private static readonly PidInfo[] Mode01Pids = BuildMode01Pids();

    public static IReadOnlyList<PidInfo> Mode01 => Mode01Pids;

    public static PidInfo? FindMode01(byte pid)
    {
        foreach (var info in Mode01Pids)
        {
            if (info.Pid == pid)
            {
                return info;
            }
        }

        return null;
    }

    public static bool IsSupportBitmapPid(byte pid) => pid % 0x20 == 0;

    /// <summary>PIDs a support-bitmap reply announces, in order (the last may be the next bitmap PID).</summary>
    public static IReadOnlyList<byte> SupportedPids(byte bitmapPid, ReadOnlySpan<byte> data)
    {
        var pids = new List<byte>();
        if (!IsSupportBitmapPid(bitmapPid) || data.Length < 4)
        {
            return pids;
        }

        for (var i = 0; i < 32; i++)
        {
            var pid = bitmapPid + i + 1;
            if (pid > 0xFF)
            {
                break;
            }

            if ((data[i / 8] & (0x80 >> (i % 8))) != 0)
            {
                pids.Add((byte)pid);
            }
        }

        return pids;
    }

    /// <summary>Big-endian MSB-first bit extraction used by the OBD-II bit numbering.</summary>
    public static bool TryExtractBits(ReadOnlySpan<byte> data, int startBit, int bitLength, bool isSigned, out long raw)
    {
        raw = 0;
        if (startBit < 0 || bitLength < 1 || bitLength > 32 || startBit + bitLength > data.Length * 8)
        {
            return false;
        }

        ulong value = 0;
        for (var i = startBit; i < startBit + bitLength; i++)
        {
            var bit = (data[i / 8] >> (7 - (i % 8))) & 1;
            value = (value << 1) | (ulong)(uint)bit;
        }

        raw = (long)value;
        if (isSigned && (value & (1UL << (bitLength - 1))) != 0)
        {
            raw -= (long)(1UL << bitLength);
        }

        return true;
    }

    public static bool TryDecode(PidInfo info, ReadOnlySpan<byte> data, out IReadOnlyList<Value> values)
    {
        values = [];
        if (data.Length < info.DataBytes)
        {
            return false;
        }

        var list = new List<Value>(info.Fields.Count);
        foreach (var field in info.Fields)
        {
            if (!TryExtractBits(data, field.StartBit, field.BitLength, field.IsSigned, out var raw))
            {
                return false;
            }

            var text = string.Empty;
            foreach (var name in field.ValueNames)
            {
                if (name.Value == raw)
                {
                    text = name.Description;
                    break;
                }
            }

            list.Add(new Value(field, raw, raw * field.Scale + field.Offset, text));
        }

        values = list;
        return true;
    }

    public static byte[] Mode01Request(byte pid) => [ShowCurrentData, pid];

    public static bool TryParseMode01Reply(ReadOnlySpan<byte> payload, out IReadOnlyList<Reply> replies)
    {
        replies = [];
        if (payload.Length < 2 || payload[0] != ShowCurrentData + PositiveReplyOffset)
        {
            return false;
        }

        var list = new List<Reply>();
        var pos = 1;
        while (pos < payload.Length)
        {
            var pid = payload[pos];
            var info = FindMode01(pid);
            if (info is null)
            {
                if (pos == 1)
                {
                    list.Add(new Reply(pid, payload[(pos + 1)..].ToArray()));
                }

                break;
            }

            if (pos + 1 + info.DataBytes > payload.Length)
            {
                break;
            }

            list.Add(new Reply(pid, payload.Slice(pos + 1, info.DataBytes).ToArray()));
            pos += 1 + info.DataBytes;
        }

        replies = list;
        return list.Count > 0;
    }

    public static byte[] Mode09Request(byte pid) => [VehicleInfo, pid];

    /// <summary>Mode 09 VIN (0x02) or ECU name (0x0A) text; NUL padding removed.</summary>
    public static bool TryDecodeMode09Text(ReadOnlySpan<byte> payload, byte pid, out string text)
    {
        text = string.Empty;
        if (payload.Length < 4 ||
            payload[0] != VehicleInfo + PositiveReplyOffset ||
            payload[1] != pid)
        {
            return false;
        }

        var chars = new List<char>();
        foreach (var b in payload[3..])
        {
            if (b != 0)
            {
                chars.Add((char)b);
            }
        }

        text = new string(chars.ToArray()).Trim();
        return true;
    }

    private static PidInfo[] BuildMode01Pids()
    {
        var pids = new List<PidInfo>();
        for (var baseValue = 0x00; baseValue <= 0xE0; baseValue += 0x20)
        {
            var range = $"{baseValue + 1:X2}-{baseValue + 0x20:X2}";
            pids.Add(new PidInfo((byte)baseValue, $"PIDs supported [{range}]", 4, []));
        }

        pids.Add(new PidInfo(0x01, "Monitor status since DTCs cleared", 4,
        [
            State("MIL", 0, 1, OffOn),
            Number("DTC_Count", "", 1, 7),
            State("IgnitionType", 12, 1, [new(0, "Spark"), new(1, "Compression")]),
        ]));
        pids.Add(new PidInfo(0x03, "Fuel system status", 2,
        [
            State("FuelSystem1_Status", 0, 8, FuelSystemStatus),
            State("FuelSystem2_Status", 8, 8, FuelSystemStatus),
        ]));
        pids.Add(new PidInfo(0x04, "Calculated engine load", 1, [Number("EngineLoad", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x05, "Engine coolant temperature", 1, [Number("CoolantTemp", "degC", 0, 8, 1.0, -40.0)]));
        pids.Add(new PidInfo(0x06, "Short term fuel trim, bank 1", 1, [Number("ShortTermFuelTrim_B1", "%", 0, 8, Trim, -100.0)]));
        pids.Add(new PidInfo(0x07, "Long term fuel trim, bank 1", 1, [Number("LongTermFuelTrim_B1", "%", 0, 8, Trim, -100.0)]));
        pids.Add(new PidInfo(0x08, "Short term fuel trim, bank 2", 1, [Number("ShortTermFuelTrim_B2", "%", 0, 8, Trim, -100.0)]));
        pids.Add(new PidInfo(0x09, "Long term fuel trim, bank 2", 1, [Number("LongTermFuelTrim_B2", "%", 0, 8, Trim, -100.0)]));
        pids.Add(new PidInfo(0x0A, "Fuel pressure (gauge)", 1, [Number("FuelPressure", "kPa", 0, 8, 3.0)]));
        pids.Add(new PidInfo(0x0B, "Intake manifold absolute pressure", 1, [Number("IntakeManifoldPressure", "kPa", 0, 8)]));
        pids.Add(new PidInfo(0x0C, "Engine speed", 2, [Number("EngineSpeed", "rpm", 0, 16, 0.25)]));
        pids.Add(new PidInfo(0x0D, "Vehicle speed", 1, [Number("VehicleSpeed", "km/h", 0, 8)]));
        pids.Add(new PidInfo(0x0E, "Timing advance", 1, [Number("TimingAdvance", "deg", 0, 8, 0.5, -64.0)]));
        pids.Add(new PidInfo(0x0F, "Intake air temperature", 1, [Number("IntakeAirTemp", "degC", 0, 8, 1.0, -40.0)]));
        pids.Add(new PidInfo(0x10, "Mass air flow rate", 2, [Number("MAF", "g/s", 0, 16, 0.01)]));
        pids.Add(new PidInfo(0x11, "Throttle position", 1, [Number("ThrottlePosition", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x12, "Commanded secondary air status", 1, [State("SecondaryAirStatus", 0, 8, SecondaryAirStatus)]));
        pids.Add(new PidInfo(0x13, "Oxygen sensors present (2 banks)", 1, [Number("O2SensorsPresent2Banks", "", 0, 8)]));
        pids.Add(new PidInfo(0x1C, "OBD standards this vehicle conforms to", 1, [State("OBDStandard", 0, 8, ObdStandards)]));
        pids.Add(new PidInfo(0x1D, "Oxygen sensors present (4 banks)", 1, [Number("O2SensorsPresent4Banks", "", 0, 8)]));
        pids.Add(new PidInfo(0x1E, "Auxiliary input status", 1, [State("PowerTakeOff", 7, 1, OffOn)]));
        pids.Add(new PidInfo(0x1F, "Run time since engine start", 2, [Number("RunTime", "s", 0, 16)]));
        pids.Add(new PidInfo(0x21, "Distance travelled with MIL on", 2, [Number("DistanceWithMIL", "km", 0, 16)]));
        pids.Add(new PidInfo(0x22, "Fuel rail pressure (relative to manifold vacuum)", 2, [Number("FuelRailPressureRelative", "kPa", 0, 16, 0.079)]));
        pids.Add(new PidInfo(0x23, "Fuel rail gauge pressure", 2, [Number("FuelRailGaugePressure", "kPa", 0, 16, 10.0)]));
        pids.Add(new PidInfo(0x2C, "Commanded EGR", 1, [Number("CommandedEGR", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x2D, "EGR error", 1, [Number("EGRError", "%", 0, 8, Trim, -100.0)]));
        pids.Add(new PidInfo(0x2E, "Commanded evaporative purge", 1, [Number("CommandedEvapPurge", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x2F, "Fuel tank level", 1, [Number("FuelLevel", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x30, "Warm-ups since codes cleared", 1, [Number("WarmUpsSinceClear", "", 0, 8)]));
        pids.Add(new PidInfo(0x31, "Distance travelled since codes cleared", 2, [Number("DistanceSinceClear", "km", 0, 16)]));
        pids.Add(new PidInfo(0x32, "Evap system vapour pressure", 2, [Number("EvapVapourPressure", "Pa", 0, 16, 0.25, 0.0, true)]));
        pids.Add(new PidInfo(0x33, "Absolute barometric pressure", 1, [Number("BarometricPressure", "kPa", 0, 8)]));
        pids.Add(new PidInfo(0x3C, "Catalyst temperature, bank 1 sensor 1", 2, [Number("CatalystTemp_B1S1", "degC", 0, 16, 0.1, -40.0)]));
        pids.Add(new PidInfo(0x3D, "Catalyst temperature, bank 2 sensor 1", 2, [Number("CatalystTemp_B2S1", "degC", 0, 16, 0.1, -40.0)]));
        pids.Add(new PidInfo(0x3E, "Catalyst temperature, bank 1 sensor 2", 2, [Number("CatalystTemp_B1S2", "degC", 0, 16, 0.1, -40.0)]));
        pids.Add(new PidInfo(0x3F, "Catalyst temperature, bank 2 sensor 2", 2, [Number("CatalystTemp_B2S2", "degC", 0, 16, 0.1, -40.0)]));
        pids.Add(new PidInfo(0x42, "Control module voltage", 2, [Number("ControlModuleVoltage", "V", 0, 16, 0.001)]));
        pids.Add(new PidInfo(0x43, "Absolute load value", 2, [Number("AbsoluteLoad", "%", 0, 16, Percent)]));
        pids.Add(new PidInfo(0x44, "Commanded air-fuel equivalence ratio", 2, [Number("CommandedLambda", "", 0, 16, 2.0 / 65536.0)]));
        pids.Add(new PidInfo(0x45, "Relative throttle position", 1, [Number("RelativeThrottlePosition", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x46, "Ambient air temperature", 1, [Number("AmbientAirTemp", "degC", 0, 8, 1.0, -40.0)]));
        pids.Add(new PidInfo(0x47, "Absolute throttle position B", 1, [Number("ThrottlePositionB", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x48, "Absolute throttle position C", 1, [Number("ThrottlePositionC", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x49, "Accelerator pedal position D", 1, [Number("AcceleratorPedalD", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x4A, "Accelerator pedal position E", 1, [Number("AcceleratorPedalE", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x4B, "Accelerator pedal position F", 1, [Number("AcceleratorPedalF", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x4C, "Commanded throttle actuator", 1, [Number("CommandedThrottle", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x4D, "Time run with MIL on", 2, [Number("TimeWithMIL", "min", 0, 16)]));
        pids.Add(new PidInfo(0x4E, "Time since trouble codes cleared", 2, [Number("TimeSinceClear", "min", 0, 16)]));
        pids.Add(new PidInfo(0x51, "Fuel type", 1, [State("FuelType", 0, 8, FuelTypes)]));
        pids.Add(new PidInfo(0x52, "Ethanol fuel percentage", 1, [Number("EthanolPercent", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x53, "Absolute evap system vapour pressure", 2, [Number("AbsEvapVapourPressure", "kPa", 0, 16, 0.005)]));
        pids.Add(new PidInfo(0x55, "Short term secondary oxygen sensor trim, banks 1 and 3", 2,
        [
            Number("ShortTermSecondaryO2Trim_B1", "%", 0, 8, Trim, -100.0),
            Number("ShortTermSecondaryO2Trim_B3", "%", 8, 8, Trim, -100.0),
        ]));
        pids.Add(new PidInfo(0x56, "Long term secondary oxygen sensor trim, banks 1 and 3", 2,
        [
            Number("LongTermSecondaryO2Trim_B1", "%", 0, 8, Trim, -100.0),
            Number("LongTermSecondaryO2Trim_B3", "%", 8, 8, Trim, -100.0),
        ]));
        pids.Add(new PidInfo(0x57, "Short term secondary oxygen sensor trim, banks 2 and 4", 2,
        [
            Number("ShortTermSecondaryO2Trim_B2", "%", 0, 8, Trim, -100.0),
            Number("ShortTermSecondaryO2Trim_B4", "%", 8, 8, Trim, -100.0),
        ]));
        pids.Add(new PidInfo(0x58, "Long term secondary oxygen sensor trim, banks 2 and 4", 2,
        [
            Number("LongTermSecondaryO2Trim_B2", "%", 0, 8, Trim, -100.0),
            Number("LongTermSecondaryO2Trim_B4", "%", 8, 8, Trim, -100.0),
        ]));
        pids.Add(new PidInfo(0x59, "Fuel rail absolute pressure", 2, [Number("FuelRailAbsPressure", "kPa", 0, 16, 10.0)]));
        pids.Add(new PidInfo(0x5A, "Relative accelerator pedal position", 1, [Number("RelativeAcceleratorPedal", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x5B, "Hybrid battery pack remaining life", 1, [Number("HybridBatteryRemaining", "%", 0, 8, Percent)]));
        pids.Add(new PidInfo(0x5C, "Engine oil temperature", 1, [Number("OilTemp", "degC", 0, 8, 1.0, -40.0)]));
        pids.Add(new PidInfo(0x5D, "Fuel injection timing", 2, [Number("FuelInjectionTiming", "deg", 0, 16, 1.0 / 128.0, -210.0)]));
        pids.Add(new PidInfo(0x5E, "Engine fuel rate", 2, [Number("FuelRate", "L/h", 0, 16, 0.05)]));
        pids.Add(new PidInfo(0x61, "Driver's demand engine percent torque", 1, [Number("DemandTorque", "%", 0, 8, 1.0, -125.0)]));
        pids.Add(new PidInfo(0x62, "Actual engine percent torque", 1, [Number("ActualTorque", "%", 0, 8, 1.0, -125.0)]));
        pids.Add(new PidInfo(0x63, "Engine reference torque", 2, [Number("ReferenceTorque", "Nm", 0, 16)]));
        pids.Add(new PidInfo(0x64, "Engine percent torque data", 5,
        [
            Number("TorqueIdle", "%", 0, 8, 1.0, -125.0),
            Number("TorquePoint1", "%", 8, 8, 1.0, -125.0),
            Number("TorquePoint2", "%", 16, 8, 1.0, -125.0),
            Number("TorquePoint3", "%", 24, 8, 1.0, -125.0),
            Number("TorquePoint4", "%", 32, 8, 1.0, -125.0),
        ]));
        pids.Add(new PidInfo(0x8E, "Engine friction percent torque", 1, [Number("FrictionTorque", "%", 0, 8, 1.0, -125.0)]));
        pids.Add(new PidInfo(0xA6, "Odometer", 4, [Number("Odometer", "km", 0, 32, 0.1)]));

        AddOxygenSensors(pids);

        pids.Sort((a, b) => a.Pid.CompareTo(b.Pid));
        return pids.ToArray();
    }

    private static void AddOxygenSensors(List<PidInfo> pids)
    {
        for (var s = 0; s < 8; s++)
        {
            var n = (s + 1).ToString();
            pids.Add(new PidInfo((byte)(0x14 + s), $"Oxygen sensor {n} voltage and fuel trim", 2,
            [
                Number("O2S" + n + "_Voltage", "V", 0, 8, 0.005),
                Number("O2S" + n + "_ShortTermFuelTrim", "%", 8, 8, Trim, -100.0),
            ]));
        }

        for (var s = 0; s < 8; s++)
        {
            var n = (s + 1).ToString();
            pids.Add(new PidInfo((byte)(0x24 + s), $"Oxygen sensor {n} air-fuel ratio and voltage", 4,
            [
                Number("O2S" + n + "_Lambda", "", 0, 16, 2.0 / 65536.0),
                Number("O2S" + n + "_WideVoltage", "V", 16, 16, 8.0 / 65536.0),
            ]));
        }

        for (var s = 0; s < 8; s++)
        {
            var n = (s + 1).ToString();
            pids.Add(new PidInfo((byte)(0x34 + s), $"Oxygen sensor {n} air-fuel ratio and current", 4,
            [
                Number("O2S" + n + "_CurrentLambda", "", 0, 16, 2.0 / 65536.0),
                Number("O2S" + n + "_Current", "mA", 16, 16, 1.0 / 256.0, -128.0),
            ]));
        }
    }
}
