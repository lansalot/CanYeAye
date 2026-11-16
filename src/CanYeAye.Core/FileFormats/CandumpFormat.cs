using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// candump / kayak text log: "(seconds) iface ID#DATA", "ID##FLAGSDATA" (CAN FD) or
/// "ID#R&lt;len&gt;" (remote), plus the expanded "[len]" dialect.
/// </summary>
public sealed partial class CandumpFormat : LogFormatBase
{
    [GeneratedRegex(@"^\((\S+)\)$")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"^([0-9A-F]+)(##?)(\S*)$")]
    private static partial Regex IdValueRegex();

    [GeneratedRegex(@"(\S{2})")]
    private static partial Regex ByteRegex();

    public override string Name => "Candump/Kayak";

    public override string DefaultExtension => ".log";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path);
        var isMatch = true;
        var considered = 0;

        foreach (var rawLine in lines)
        {
            if (considered++ >= 100)
            {
                break;
            }

            var line = rawLine.ToUpperInvariant();
            if (line.Length <= 1)
            {
                continue;
            }

            var tokens = FormatHelpers.Simplified(line).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                isMatch = false;
                continue;
            }

            var timeMatch = TimeRegex().Match(tokens[0]);
            if (!timeMatch.Success)
            {
                isMatch = false;
            }
            else if (!double.TryParse(timeMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                isMatch = false;
            }

            if (line.Contains('['))
            {
                if (tokens.Length < 4)
                {
                    isMatch = false;
                    continue;
                }

                var id = FormatHelpers.ParseHex(tokens[2]);
                if (id == 0 || id > 0x1FFFFFFF)
                {
                    isMatch = false;
                }

                if (tokens[3].Length < 2)
                {
                    isMatch = false;
                    continue;
                }

                var length = tokens[3][1] - '0';
                if (length is < 0 or > 8)
                {
                    isMatch = false;
                }
            }
            else
            {
                var match = IdValueRegex().Match(tokens[2]);
                if (!match.Success)
                {
                    isMatch = false;
                    continue;
                }

                var isFd = match.Groups[2].Value == "##";
                var value = match.Groups[3].Value;
                if (isFd)
                {
                    value = value.Length > 0 ? value[1..] : value;
                }

                var maxLength = isFd ? 64 : 8;
                if (!isFd && value.StartsWith('R') && value.Length > 1 && char.IsAsciiDigit(value[1]))
                {
                    var remoteLength = value[1] - '0';
                    if (remoteLength is < 0 or > 8)
                    {
                        isMatch = false;
                        continue;
                    }
                }
                else
                {
                    var count = 0;
                    foreach (Match byteMatch in ByteRegex().Matches(value))
                    {
                        count++;
                        if (count > maxLength ||
                            !byte.TryParse(byteMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                        {
                            isMatch = false;
                            break;
                        }
                    }
                }
            }
        }

        return isMatch;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        foreach (var rawLine in FormatHelpers.ReadLines(path))
        {
            var line = rawLine.ToUpperInvariant();
            if (line.Length <= 1)
            {
                continue;
            }

            var tokens = FormatHelpers.Simplified(line).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                continue;
            }

            var timeMatch = TimeRegex().Match(tokens[0]);
            if (!timeMatch.Success)
            {
                continue;
            }

            var frame = new CanFrame
            {
                Timestamp = FrameTimestamp.FromMicroseconds(
                    (long)(FormatHelpers.ParseDouble(timeMatch.Groups[1].Value) * 1_000_000.0)),
            };

            var busToken = tokens[1];
            var bus = 0;
            for (var i = 0; i < busToken.Length; i++)
            {
                if (char.IsAsciiDigit(busToken[i]))
                {
                    bus = (int)FormatHelpers.LeadingUnsigned(busToken[i..]);
                    break;
                }
            }

            frame.Bus = (byte)bus;

            if (line.Contains('['))
            {
                frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[2]));
                frame.SetExtended(frame.FrameId > 0x7FF);
                frame.SetFrameType(FrameType.CanDataFrame);

                var numBytes = tokens[3].Length > 2 && tokens[3][2] == ']'
                    ? tokens[3][1] - '0'
                    : (tokens[3][1] - '0') * 10 + (tokens[3][2] - '0');

                var payload = new byte[Math.Max(0, numBytes)];
                for (var c = 0; c < payload.Length; c++)
                {
                    if (4 + c < tokens.Length)
                    {
                        payload[c] = FormatHelpers.ParseByte(tokens[4 + c]);
                    }
                }

                frame.SetPayload(payload);
                if (numBytes > 8)
                {
                    frame.SetFlexibleDataRate(true);
                }
            }
            else
            {
                var match = IdValueRegex().Match(tokens[2]);
                if (!match.Success)
                {
                    continue;
                }

                frame.SetFrameId((uint)FormatHelpers.ParseHex(match.Groups[1].Value));
                frame.SetExtended(match.Groups[1].Value.Length > 3);

                var isFd = match.Groups[2].Value == "##";
                var value = match.Groups[3].Value;
                List<byte> bytes = [];

                if (!isFd && value.StartsWith('R'))
                {
                    var remoteLength = value.Length > 1 && char.IsAsciiDigit(value[1]) ? value[1] - '0' : 0;
                    bytes.AddRange(new byte[remoteLength]);
                    frame.SetFrameType(FrameType.RemoteRequestFrame);
                }
                else if (isFd)
                {
                    var flags = FormatHelpers.ParseHex(value.Length > 0 ? value[..1] : "0");
                    value = value.Length > 0 ? value[1..] : value;
                    frame.SetFrameType(FrameType.CanFdDataFrame);
                    frame.SetBitrateSwitch((flags & 1) != 0);
                    frame.SetErrorStateIndicator((flags & 2) != 0);
                    foreach (Match byteMatch in ByteRegex().Matches(value))
                    {
                        bytes.Add(FormatHelpers.ParseByte(byteMatch.Groups[1].Value));
                    }
                }
                else
                {
                    frame.SetFrameType(FrameType.CanDataFrame);
                    foreach (Match byteMatch in ByteRegex().Matches(value))
                    {
                        bytes.Add(FormatHelpers.ParseByte(byteMatch.Groups[1].Value));
                    }
                }

                frame.SetPayload(bytes.ToArray());
            }

            if (frame.Length > (frame.IsFlexibleDataRate ? 64 : 8))
            {
                continue;
            }

            frame.SetReceived(true);
            frames.Add(frame);
        }

        return true;
    }

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var writer = new StreamWriter(path, false, Encoding.Latin1);
        foreach (var frame in frames)
        {
            writer.Write('(');
            var seconds = frame.Timestamp.ToMicroseconds() / 1_000_000.0;
            writer.Write(seconds.ToString("F6", CultureInfo.InvariantCulture).PadLeft(17, '0'));
            writer.Write(") vcan0 ");
            writer.Write(frame.IsExtended ? FormatHelpers.Hex8(frame.FrameId) : frame.FrameId.ToString("X3", CultureInfo.InvariantCulture));

            if (frame.IsFlexibleDataRate)
            {
                var flags = (frame.HasBitrateSwitch ? 1 : 0) | (frame.HasErrorStateIndicator ? 2 : 0);
                writer.Write("##");
                writer.Write(flags.ToString("X", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.Write('#');
            }

            if (frame.FrameType == FrameType.RemoteRequestFrame)
            {
                writer.Write('R');
                writer.Write(frame.Length.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                foreach (var b in frame.Payload)
                {
                    writer.Write(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }

            writer.Write('\n');
        }

        return true;
    }
}
