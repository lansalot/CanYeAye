using System.Globalization;
using System.Text;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// GVRET "native" CSV (SavvyCAN's own format). V1 has no direction column; V2 adds Dir and can
/// carry CAN FD flags and FlexRay frames. Timestamps are raw microseconds.
/// </summary>
public sealed class NativeCsvFormat : LogFormatBase
{
    public const string V2Header = "Time Stamp,ID,Extended,Dir,Bus,LEN,D1,D2,D3,D4,D5,D6,D7,D8";

    public override string Name => "GVRET Logs";

    public override string DefaultExtension => ".csv";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path);
        if (lines.Length == 0)
        {
            return false;
        }

        var header = lines[0].ToUpperInvariant();
        var version = 1;
        if (header.Length < 23)
        {
            return false;
        }

        if (header[23] == 'D')
        {
            version = 2;
        }

        if (!header.Contains("TIME STAMP", StringComparison.Ordinal) ||
            !header.Contains("EXTENDED", StringComparison.Ordinal) ||
            !header.Contains("D1", StringComparison.Ordinal))
        {
            return false;
        }

        if (lines.Length > 1)
        {
            var line = FormatHelpers.Simplified(lines[1]);
            if (line.Length > 2 && !line.StartsWith('#'))
            {
                var tokens = line.Split(',');
                if (tokens.Length < 5)
                {
                    return false;
                }

                if (version == 1)
                {
                    if (FormatHelpers.ParseInt(tokens[4]) > 8)
                    {
                        return false;
                    }
                }
                else if (FormatHelpers.ParseInt(tokens[5]) > 255)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        if (lines.Length == 0)
        {
            return false;
        }

        var header = lines[0].ToUpperInvariant();
        var version = header.Length > 23 && header[23] == 'D' ? 2 : 1;
        var timeStamp = FormatHelpers.MillisecondsSinceMidnight();
        var foundErrors = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = FormatHelpers.Simplified(lines[i]);
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.Length <= 2)
            {
                continue;
            }

            var tokens = line.Split(',');
            if (tokens.Length < 5)
            {
                foundErrors = true;
                continue;
            }

            var frame = new CanFrame();

            if (tokens[0].Length > 3)
            {
                var raw = FormatHelpers.LeadingUnsigned(tokens[0]);
                if (raw == 0)
                {
                    raw = (ulong)FormatHelpers.LeadingSigned(tokens[0]);
                }

                frame.Timestamp = FrameTimestamp.FromMicroseconds((long)raw);
            }
            else
            {
                timeStamp += 5;
                frame.Timestamp = FrameTimestamp.FromMicroseconds(timeStamp);
            }

            frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[1]));
            frame.SetExtended(tokens[2].ToUpperInvariant().Contains("TRUE", StringComparison.Ordinal));
            if (frame.FrameId > 0x7FF)
            {
                frame.SetExtended(true);
            }

            frame.SetFrameType(FrameType.CanDataFrame);

            if (version == 1)
            {
                frame.SetReceived(true);
                frame.Bus = (byte)FormatHelpers.ParseInt(tokens[3]);
                var length = (int)Math.Clamp(FormatHelpers.ParseInt(tokens[4]), 0, 8);
                length = Math.Min(length, tokens.Length - 5);
                var payload = new byte[Math.Max(0, length)];
                for (var d = 0; d < payload.Length; d++)
                {
                    payload[d] = FormatHelpers.ParseByte(tokens[5 + d]);
                }

                frame.SetPayload(payload);
            }
            else
            {
                frame.SetReceived(FormatHelpers.FirstChar(tokens[3]) == 'R');
                frame.Bus = (byte)FormatHelpers.ParseInt(tokens[4]);

                var length = (int)Math.Clamp(FormatHelpers.ParseInt(tokens[5]), 0, 255);
                length = Math.Min(length, tokens.Length - 6);

                var flagsIndex = 6 + Math.Max(8, length);
                var flags = flagsIndex < tokens.Length ? tokens[flagsIndex].Trim().ToUpperInvariant() : string.Empty;
                var flexRay = flags == "FR" || flags.StartsWith("FR+", StringComparison.Ordinal);

                if (flexRay)
                {
                    frame.SetFrameType(FrameType.FlexRayDataFrame);
                    if (flags.Contains("+NULL", StringComparison.Ordinal))
                    {
                        frame.SetFlexRayNullFrame(true);
                    }

                    if (flags.Contains("+CRCERR", StringComparison.Ordinal))
                    {
                        frame.SetFlexRayCrcStatus(true, false);
                    }
                }
                else if (length > 64)
                {
                    length = 64;
                }

                var payload = new byte[Math.Max(0, length)];
                for (var d = 0; d < payload.Length; d++)
                {
                    payload[d] = FormatHelpers.ParseByte(tokens[6 + d]);
                }

                frame.SetPayload(payload);

                if (!flexRay && (flags.StartsWith("FD", StringComparison.Ordinal) || length > 8))
                {
                    frame.SetFlexibleDataRate(true);
                    frame.SetBitrateSwitch(flags.Contains("BRS", StringComparison.Ordinal));
                    frame.SetErrorStateIndicator(flags.Contains("ESI", StringComparison.Ordinal));
                }
            }

            frames.Add(frame);
        }

        return !foundErrors;
    }

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var writer = new StreamWriter(path, false, Encoding.Latin1);
        writer.Write(V2Header);
        writer.Write('\n');

        foreach (var frame in frames)
        {
            writer.Write(frame.Timestamp.ToMicroseconds().ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(FormatHelpers.Hex8(frame.FrameId));
            writer.Write(',');
            writer.Write(frame.IsExtended ? "true," : "false,");
            writer.Write(frame.IsReceived ? "Rx," : "Tx,");
            writer.Write(frame.Bus.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(frame.Length.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');

            var fd = frame.IsFlexibleDataRate;
            var flexRay = frame.IsFlexRay;
            var columns = fd || flexRay ? Math.Max(8, frame.Length) : 8;
            for (var i = 0; i < columns; i++)
            {
                if (i < frame.Length)
                {
                    writer.Write(frame.Payload[i].ToString("X2", CultureInfo.InvariantCulture));
                }
                else
                {
                    writer.Write("00");
                }

                writer.Write(',');
            }

            if (fd)
            {
                writer.Write("FD");
                if (frame.HasBitrateSwitch)
                {
                    writer.Write("+BRS");
                }

                if (frame.HasErrorStateIndicator)
                {
                    writer.Write("+ESI");
                }

                writer.Write(',');
            }
            else if (flexRay)
            {
                writer.Write("FR");
                if (frame.IsFlexRayNullFrame)
                {
                    writer.Write("+NULL");
                }

                if (frame.HasFlexRayCrcError)
                {
                    writer.Write("+CRCERR");
                }

                writer.Write(',');
            }

            writer.Write('\n');
        }

        return true;
    }
}
