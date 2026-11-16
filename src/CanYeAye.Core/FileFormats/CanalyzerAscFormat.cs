using System.Globalization;
using System.Text;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// Vector CANalyzer ASCII log (*.asc). Handles the classic "d"/"r" rows and the CANFD rows
/// (with an optional symbolic name column).
/// </summary>
public sealed class CanalyzerAscFormat : LogFormatBase
{
    private static readonly int[] FdLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    public override string Name => "CANalyzer Ascii Log";

    public override string DefaultExtension => ".asc";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path, maxLines: 2);
        if (lines.Length < 2)
        {
            return false;
        }

        return lines[0].StartsWith("date", StringComparison.Ordinal) &&
               lines[1].StartsWith("base", StringComparison.Ordinal);
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        var foundErrors = false;
        var inHeader = true;
        var lineCounter = 0;

        foreach (var rawLine in lines)
        {
            lineCounter++;
            if (inHeader)
            {
                if (rawLine.StartsWith("//", StringComparison.Ordinal))
                {
                    inHeader = false;
                    continue;
                }

                if (lineCounter > 4)
                {
                    inHeader = false;
                }
            }

            if (inHeader || rawLine.Length <= 2 || rawLine.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = FormatHelpers.Simplified(rawLine).Split(' ');
            if (tokens[0].Contains("Begin", StringComparison.Ordinal))
            {
                continue;
            }

            if (tokens.Length <= 5)
            {
                continue;
            }

            var direction = tokens[3].ToUpperInvariant();
            if (!direction.StartsWith("RX", StringComparison.Ordinal) &&
                !direction.StartsWith("TX", StringComparison.Ordinal))
            {
                continue;
            }

            var frame = new CanFrame
            {
                Timestamp = FrameTimestamp.FromMicroseconds((long)(FormatHelpers.ParseDouble(tokens[0]) * 1_000_000.0)),
            };

            if (tokens[1].Contains("CAN", StringComparison.Ordinal))
            {
                var idToken = tokens[4];
                if (idToken.EndsWith('x'))
                {
                    frame.SetFrameId((uint)FormatHelpers.ParseHex(idToken[..^1]));
                    frame.SetExtended(true);
                }
                else
                {
                    frame.SetFrameId((uint)FormatHelpers.ParseHex(idToken));
                    frame.SetExtended(frame.FrameId > 0x7FF);
                }

                var nameOffset = tokens[5].Length > 0 && char.IsAsciiDigit(tokens[5][0]) ? 0 : 1;
                if (tokens.Length < 9 + nameOffset)
                {
                    foundErrors = true;
                    continue;
                }

                var payloadLength = (int)FormatHelpers.ParseInt(tokens[8 + nameOffset]);
                if (payloadLength > 64 || payloadLength < 0)
                {
                    return false;
                }

                frame.SetReceived(direction.Contains("RX", StringComparison.Ordinal));
                frame.Bus = (byte)Math.Max(0, FormatHelpers.ParseInt(tokens[2]) - 1);

                var payload = new byte[payloadLength];
                var dataStart = 9 + nameOffset;
                for (var d = 0; d < payloadLength; d++)
                {
                    if (dataStart + d < tokens.Length)
                    {
                        payload[d] = FormatHelpers.ParseByte(tokens[dataStart + d]);
                    }
                    else
                    {
                        foundErrors = true;
                    }
                }

                frame.SetPayload(payload);

                var brs = FormatHelpers.ParseInt(tokens[5 + nameOffset]) == 1;
                var esi = FormatHelpers.ParseInt(tokens[6 + nameOffset]) == 1;
                var flagsIndex = dataStart + payloadLength + 2;
                var isFd = payloadLength > 8 || brs;
                if (flagsIndex < tokens.Length &&
                    uint.TryParse(tokens[flagsIndex], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags))
                {
                    isFd = (flags & 0x1000) != 0 || payloadLength > 8;
                }

                if (isFd)
                {
                    frame.SetFlexibleDataRate(true);
                    frame.SetBitrateSwitch(brs);
                    frame.SetErrorStateIndicator(esi);
                }
            }
            else
            {
                var payloadLength = (int)FormatHelpers.ParseInt(tokens[5]);
                var idToken = tokens[2];
                if (idToken.EndsWith('x'))
                {
                    frame.SetFrameId((uint)FormatHelpers.ParseHex(idToken[..^1]));
                    frame.SetExtended(true);
                }
                else
                {
                    frame.SetFrameId((uint)FormatHelpers.ParseHex(idToken));
                    frame.SetExtended(frame.FrameId > 0x7FF);
                }

                var isRemote = tokens[4] == "r";
                if (payloadLength > 64 || (payloadLength > 8 && isRemote) || payloadLength < 0)
                {
                    return false;
                }

                frame.SetReceived(direction.Contains("RX", StringComparison.Ordinal));
                frame.Bus = (byte)Math.Max(0, FormatHelpers.ParseInt(tokens[1]) - 1);
                if (isRemote)
                {
                    frame.SetFrameType(FrameType.RemoteRequestFrame);
                }

                var payload = new byte[payloadLength];
                var count = isRemote ? 0 : payloadLength;
                for (var d = 0; d < count; d++)
                {
                    if (6 + d < tokens.Length)
                    {
                        payload[d] = FormatHelpers.ParseByte(tokens[6 + d]);
                    }
                    else
                    {
                        foundErrors = true;
                    }
                }

                frame.SetPayload(payload);
                if (payloadLength > 8)
                {
                    frame.SetFlexibleDataRate(true);
                }
            }

            frames.Add(frame);
        }

        return !foundErrors;
    }

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var writer = new StreamWriter(path, false, Encoding.Latin1);
        var offsetTime = frames.Count > 0 ? frames[0].Timestamp.ToMicroseconds() : 0;
        foreach (var frame in frames)
        {
            offsetTime = Math.Min(offsetTime, frame.Timestamp.ToMicroseconds());
        }

        var now = offsetTime > 10_000_000_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(offsetTime / 1000).LocalDateTime
            : DateTime.Now;

        writer.Write("date " + now.ToString("ddd MMM dd h:mm:ss.fff tt yyyy", CultureInfo.InvariantCulture));
        writer.Write("\nbase hex  timestamps absolute\n");
        writer.Write("no internal event logging\n");
        writer.Write("// version 11.0.0\n");

        foreach (var frame in frames)
        {
            var elapsed = frame.Timestamp.ToMicroseconds() - offsetTime;
            var seconds = elapsed / 1_000_000.0;
            var integerDigits = ((long)(elapsed / 1_000_000)).ToString(CultureInfo.InvariantCulture).Length;
            var precision = integerDigits > 3 ? 9 - integerDigits : 6;
            writer.Write(seconds.ToString("F" + Math.Clamp(precision, 0, 6), CultureInfo.InvariantCulture).PadLeft(10));
            writer.Write(' ');

            if (frame.IsFlexibleDataRate)
            {
                var dlc = 15;
                for (var i = 0; i < FdLengths.Length; i++)
                {
                    if (frame.Length <= FdLengths[i])
                    {
                        dlc = i;
                        break;
                    }
                }

                var flags = 0x1000 | (frame.HasBitrateSwitch ? 0x2000 : 0) | (frame.HasErrorStateIndicator ? 0x4000 : 0);
                writer.Write("CANFD ");
                writer.Write(frame.Bus + 1);
                writer.Write(frame.IsReceived ? " Rx " : " Tx ");
                writer.Write(frame.FrameId.ToString("X", CultureInfo.InvariantCulture));
                if (frame.IsExtended)
                {
                    writer.Write('x');
                }

                writer.Write(frame.HasBitrateSwitch ? " 1" : " 0");
                writer.Write(frame.HasErrorStateIndicator ? " 1 " : " 0 ");
                writer.Write(dlc.ToString("X", CultureInfo.InvariantCulture));
                writer.Write(' ');
                writer.Write(frame.Length.ToString(CultureInfo.InvariantCulture));
                foreach (var b in frame.Payload)
                {
                    writer.Write(' ');
                    writer.Write(b.ToString("X2", CultureInfo.InvariantCulture));
                }

                writer.Write(" 0 0 ");
                writer.Write(flags.ToString("X", CultureInfo.InvariantCulture));
                writer.Write(" 0 0 0 0 0\n");
                continue;
            }

            writer.Write((frame.Bus + 1).ToString(CultureInfo.InvariantCulture));
            writer.Write("  ");
            if (frame.IsExtended)
            {
                writer.Write(FormatHelpers.Hex8(frame.FrameId));
                writer.Write('x');
            }
            else
            {
                writer.Write(frame.FrameId.ToString("X3", CultureInfo.InvariantCulture));
                writer.Write("      ");
            }

            writer.Write("   ");
            writer.Write(frame.IsReceived ? "Rx " : "Tx ");
            writer.Write(frame.FrameType == FrameType.RemoteRequestFrame ? "r " : "d ");
            writer.Write(frame.Length.ToString(CultureInfo.InvariantCulture));
            writer.Write("  ");

            if (frame.FrameType != FrameType.RemoteRequestFrame)
            {
                foreach (var b in frame.Payload)
                {
                    writer.Write(b.ToString("X2", CultureInfo.InvariantCulture));
                    writer.Write("  ");
                }
            }

            writer.Write('\n');
        }

        return true;
    }
}
