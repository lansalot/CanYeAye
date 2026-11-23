using System.Globalization;

namespace CanYeAye.Core.Protocols;

/// <summary>State of one of the four J1939 diagnostic lamps (2-bit fields in the DM1 header).</summary>
public enum J1939LampState
{
    Off,
    On,
    Reserved,
    NotAvailable,
}

/// <summary>Flash state of a J1939 diagnostic lamp (0 = slow, 1 = fast).</summary>
public enum J1939LampFlash
{
    Slow,
    Fast,
    Reserved,
    NotAvailable,
}

/// <summary>One lamp field from the DM1/DM2 header.</summary>
public sealed record J1939Lamp(string Name, J1939LampState State, J1939LampFlash Flash);

/// <summary>One 4-byte diagnostic trouble code from a DM1/DM2/DM12 payload.</summary>
public sealed record J1939Dtc(int Spn, int Fmi, int OccurrenceCount, bool ConversionMethod)
{
    /// <summary>SPNs in this range are manufacturer defined (SAE J1939-71).</summary>
    public bool IsProprietarySpn => Spn is >= 516096 and <= 524287;

    /// <summary>127 means the ECU does not report an occurrence count.</summary>
    public bool OccurrenceCountNotAvailable => OccurrenceCount == 127;

    public string FmiText => J1939Diagnostics.DescribeFmi(Fmi);

    /// <summary>Search text that reliably finds the fault in an online SPN/FMI lookup.</summary>
    public string LookupQuery => string.Create(CultureInfo.InvariantCulture, $"SPN {Spn} FMI {Fmi}");
}

/// <summary>A decoded DM1, DM2 or DM12 diagnostic message.</summary>
public sealed class J1939DiagnosticMessage(
    int pgn,
    string name,
    string emptyText,
    int source,
    IReadOnlyList<J1939Lamp> lamps,
    IReadOnlyList<J1939Dtc> dtcs)
{
    public int Pgn { get; } = pgn;

    /// <summary>Message name, e.g. <c>DM1 Active DTCs</c>.</summary>
    public string Name { get; } = name;

    /// <summary>Wording used when the list is empty, e.g. <c>no active DTCs</c>.</summary>
    public string EmptyText { get; } = emptyText;

    public int Source { get; } = source;

    public IReadOnlyList<J1939Lamp> Lamps { get; } = lamps;

    public IReadOnlyList<J1939Dtc> Dtcs { get; } = dtcs;

    public bool HasFaults => Dtcs.Count > 0;

    public bool AnyLampOn => Lamps.Any(lamp => lamp.State == J1939LampState.On);
}

/// <summary>
/// Decodes the J1939-73 diagnostic messages that carry DTCs (DM1 active, DM2 previously active,
/// DM12 emissions related). Only the SAE-defined layout is used, so this works without a J1939 DBC
/// or the paid J1939 Digital Annex: the SPN number and FMI meaning are enough to look the fault up.
/// </summary>
public static class J1939Diagnostics
{
    public const int Dm1Pgn = 65226; // 0xFECA
    public const int Dm2Pgn = 65227; // 0xFECB
    public const int Dm12Pgn = 65236; // 0xFED4

    private static readonly string[] FmiTexts =
    [
        "data valid but above normal operating range, most severe",
        "data valid but below normal operating range, most severe",
        "data erratic, intermittent or incorrect",
        "voltage above normal or shorted to high source",
        "voltage below normal or shorted to low source",
        "current below normal or open circuit",
        "current above normal or grounded circuit",
        "mechanical system not responding or out of adjustment",
        "abnormal frequency, pulse width or period",
        "abnormal update rate",
        "abnormal rate of change",
        "root cause not known",
        "bad intelligent device or component",
        "out of calibration",
        "special instructions",
        "data valid but above normal operating range, least severe",
        "data valid but above normal operating range, moderately severe",
        "data valid but below normal operating range, least severe",
        "data valid but below normal operating range, moderately severe",
        "received network data in error",
        "data drifted high",
        "data drifted low",
    ];

    /// <summary>Short forms used in the one-line grid summary.</summary>
    private static readonly string[] FmiShortTexts =
    [
        "above normal, most severe",
        "below normal, most severe",
        "erratic or intermittent",
        "voltage high",
        "voltage low",
        "open circuit",
        "current high",
        "mechanical not responding",
        "abnormal frequency",
        "abnormal update rate",
        "abnormal rate of change",
        "root cause unknown",
        "bad device or component",
        "out of calibration",
        "special instructions",
        "above normal, least severe",
        "above normal, moderately severe",
        "below normal, least severe",
        "below normal, moderately severe",
        "network data in error",
        "drifted high",
        "drifted low",
    ];

    public static bool IsDiagnosticPgn(int pgn) => pgn is Dm1Pgn or Dm2Pgn or Dm12Pgn;

    /// <summary>Human-readable name for the DM1/DM2/DM12 PGNs, or null for anything else.</summary>
    public static string? PgnName(int pgn) => pgn switch
    {
        Dm1Pgn => "DM1 Active DTCs",
        Dm2Pgn => "DM2 Previously Active DTCs",
        Dm12Pgn => "DM12 Emissions Active DTCs",
        _ => null,
    };

    /// <summary>Full J1939-73 failure mode description for an FMI value.</summary>
    public static string DescribeFmi(int fmi) => fmi switch
    {
        >= 0 and < 22 => FmiTexts[fmi],
        22 => "reserved",
        <= 30 => "reserved",
        31 => "condition exists",
        _ => "unknown",
    };

    /// <summary>Short failure mode wording for the one-line frame summary.</summary>
    public static string DescribeFmiShort(int fmi) => fmi switch
    {
        >= 0 and < 22 => FmiShortTexts[fmi],
        22 => "reserved",
        <= 30 => "reserved",
        31 => "condition exists",
        _ => "unknown",
    };

    /// <summary>Decodes a single CAN frame when it is a DM1, DM2 or DM12 broadcast.</summary>
    public static bool TryDecode(CanFrame frame, out J1939DiagnosticMessage? message)
    {
        message = null;
        if (!frame.IsExtended)
        {
            return false;
        }

        var id = J1939Id.Decode(frame.FrameId);
        return TryDecode(id.Pgn, frame.Payload, id.Source, out message);
    }

    /// <summary>
    /// Decodes a DM1, DM2 or DM12 payload. Multi-packet messages (more than one DTC) have to be
    /// reassembled by the caller first; this handles the single-frame layout.
    /// </summary>
    public static bool TryDecode(int pgn, ReadOnlySpan<byte> payload, int source, out J1939DiagnosticMessage? message)
    {
        message = null;
        var name = PgnName(pgn);
        if (name is null || payload.Length < 2)
        {
            return false;
        }

        var lamps = new List<J1939Lamp>(4)
        {
            DecodeLamp("Malfunction", payload[0] >> 6, payload[1] >> 6),
            DecodeLamp("Red stop", payload[0] >> 4, payload[1] >> 4),
            DecodeLamp("Amber warning", payload[0] >> 2, payload[1] >> 2),
            DecodeLamp("Protect", payload[0], payload[1]),
        };

        var dtcs = DecodeDtcs(payload);

        message = new J1939DiagnosticMessage(
            pgn,
            name,
            pgn == Dm2Pgn ? "no previously active DTCs" : "no active DTCs",
            source & 0xFF,
            lamps,
            dtcs);
        return true;
    }

    /// <summary>Four-byte DTC records that follow the two lamp bytes.</summary>
    public static IReadOnlyList<J1939Dtc> DecodeDtcs(ReadOnlySpan<byte> payload)
    {
        var dtcs = new List<J1939Dtc>();
        for (var offset = 2; offset + 4 <= payload.Length; offset += 4)
        {
            var first = payload[offset];
            var second = payload[offset + 1];
            var third = payload[offset + 2];
            var fourth = payload[offset + 3];

            // A zero or all-ones record is the "no DTC" filler used for single-frame DM1 and
            // for the padding of the last transport-packet chunk.
            if ((first | second | third | fourth) == 0 ||
                (first & second & third & fourth) == 0xFF)
            {
                break;
            }

            var spn = first | (second << 8) | ((third >> 5) << 16);
            var fmi = third & 0x1F;
            var conversionMethod = (fourth & 0x80) != 0;
            var occurrenceCount = fourth & 0x7F;
            dtcs.Add(new J1939Dtc(spn, fmi, occurrenceCount, conversionMethod));
        }

        return dtcs;
    }

    /// <summary>
    /// Compact multi-line summary for the frame grid and the clipboard.
    /// <paramref name="sourceLabel"/> is the node name when the user has named the source ECU.
    /// </summary>
    public static string Describe(J1939DiagnosticMessage message, string? sourceLabel = null)
    {
        var label = string.IsNullOrWhiteSpace(sourceLabel)
            ? "0x" + message.Source.ToString("X2", CultureInfo.InvariantCulture)
            : sourceLabel;
        var header = $"{message.Name} ({label})";

        if (!message.HasFaults)
        {
            var suffix = message.AnyLampOn
                ? " · lamps: " + DescribeLamps(message)
                : string.Empty;
            return header + ": " + message.EmptyText + suffix;
        }

        var lines = new List<string> { header };
        if (message.AnyLampOn)
        {
            lines.Add("Lamps: " + DescribeLamps(message));
        }

        foreach (var dtc in message.Dtcs)
        {
            lines.Add(DescribeDtc(dtc));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>One DTC line: code, FMI meaning, occurrence count and encoding notes.</summary>
    public static string DescribeDtc(J1939Dtc dtc)
    {
        var text = $"SPN {dtc.Spn} / FMI {dtc.Fmi} — {dtc.FmiText}";

        text += dtc.OccurrenceCountNotAvailable
            ? " · OC n/a"
            : string.Create(CultureInfo.InvariantCulture, $" · OC {dtc.OccurrenceCount}");

        if (dtc.ConversionMethod)
        {
            text += " · CM 1 (legacy SPN encoding)";
        }

        if (dtc.IsProprietarySpn)
        {
            text += " · proprietary SPN";
        }

        return text;
    }

    /// <summary>
    /// Single-line summary that fits a collapsed frame-grid row. The full text
    /// (<see cref="Describe"/>) is used for tooltips and the clipboard.
    /// </summary>
    public static string DescribeCompact(J1939DiagnosticMessage message)
    {
        var label = message.Name.Split(' ')[0] + ":";

        if (!message.HasFaults)
        {
            var lamps = message.AnyLampOn ? " (" + DescribeLamps(message) + " on)" : string.Empty;
            return label + " " + message.EmptyText + lamps;
        }

        var lampText = message.AnyLampOn ? DescribeLamps(message) + " · " : string.Empty;
        var dtcText = string.Join(" | ", message.Dtcs.Select(DescribeDtcCompact));
        return label + " " + lampText + dtcText;
    }

    /// <summary>Short DTC form for <see cref="DescribeCompact"/>.</summary>
    public static string DescribeDtcCompact(J1939Dtc dtc)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"SPN {dtc.Spn} / FMI {dtc.Fmi} ({DescribeFmiShort(dtc.Fmi)})");

        text += dtc.OccurrenceCountNotAvailable
            ? " · OC n/a"
            : string.Create(CultureInfo.InvariantCulture, $" · OC {dtc.OccurrenceCount}");

        if (dtc.ConversionMethod)
        {
            text += " · CM 1";
        }

        if (dtc.IsProprietarySpn)
        {
            text += " · proprietary";
        }

        return text;
    }

    private static string DescribeLamps(J1939DiagnosticMessage message)
    {
        var on = message.Lamps
            .Where(lamp => lamp.State == J1939LampState.On)
            .Select(lamp => lamp.Flash == J1939LampFlash.Fast ? lamp.Name + " (fast flash)" : lamp.Name)
            .ToList();
        return on.Count == 0 ? "none on" : string.Join(", ", on);
    }

    private static J1939Lamp DecodeLamp(string name, int statusBits, int flashBits)
    {
        var state = (statusBits & 0x03) switch
        {
            0 => J1939LampState.Off,
            1 => J1939LampState.On,
            2 => J1939LampState.Reserved,
            _ => J1939LampState.NotAvailable,
        };

        var flash = (flashBits & 0x03) switch
        {
            0 => J1939LampFlash.Slow,
            1 => J1939LampFlash.Fast,
            2 => J1939LampFlash.Reserved,
            _ => J1939LampFlash.NotAvailable,
        };

        return new J1939Lamp(name, state, flash);
    }
}
