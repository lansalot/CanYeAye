using System.Globalization;
using System.Text;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// PCAN Viewer text trace (*.trc). Four layout versions are supported (1.1, 1.3, 2.0, 2.1);
/// the version comes from the <c>;$FILEVERSION=</c> comment.
/// </summary>
public sealed class PcanFormat : LogFormatBase
{
    private static readonly int[] FdLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    public override string Name => "PCAN Viewer";

    public override string DefaultExtension => ".trc";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path, maxLines: 25);
        var hasFileVersion = false;

        foreach (var line in lines)
        {
            if (line.StartsWith(";$FILEVERSION=", StringComparison.Ordinal))
            {
                hasFileVersion = true;
            }

            if (hasFileVersion && line.ToUpperInvariant().Contains("PCAN", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        var version = 11;

        foreach (var rawLine in lines)
        {
            if (rawLine.StartsWith(';'))
            {
                if (rawLine.Contains("$FILEVERSION=2.1", StringComparison.Ordinal))
                {
                    version = 21;
                }
                else if (rawLine.Contains("$FILEVERSION=2.0", StringComparison.Ordinal))
                {
                    version = 20;
                }
                else if (rawLine.Contains("$FILEVERSION=1.3", StringComparison.Ordinal))
                {
                    version = 13;
                }
                else if (rawLine.Contains("$FILEVERSION=1.1", StringComparison.Ordinal))
                {
                    version = 11;
                }

                continue;
            }

            var line = FormatHelpers.Simplified(rawLine);
            if (line.Length == 0)
            {
                continue;
            }

            var tokens = line.Split(' ');
            switch (version)
            {
                case 11 when tokens.Length > 4:
                    frames.Add(LoadV11(tokens));
                    break;
                case 13 when tokens.Length > 6:
                    frames.Add(LoadV13(tokens));
                    break;
                case 20 when tokens.Length > 5:
                    frames.Add(LoadV20(tokens));
                    break;
                case 21 when tokens.Length > 7:
                    frames.Add(LoadV21(tokens));
                    break;
            }
        }

        return true;
    }

    private static CanFrame LoadV11(string[] tokens)
    {
        var frame = new CanFrame
        {
            Timestamp = FrameTimestamp.FromMicroseconds((long)(FormatHelpers.ParseDouble(tokens[1]) * 1000.0)),
            Bus = 0,
        };

        frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[3]));
        var isRemote = ValueAt(tokens, 5) == "R";
        var numBytes = (int)FormatHelpers.ParseInt(tokens[4]);
        if (!isRemote && tokens.Length - 5 < numBytes)
        {
            numBytes = Math.Max(0, tokens.Length - 5);
        }

        frame.SetReceived(true);
        frame.SetExtended(frame.FrameId > 0x10000000 || tokens[3].Length >= 8);
        frame.SetFrameType(isRemote ? FrameType.RemoteRequestFrame : FrameType.CanDataFrame);

        var payload = new byte[Math.Max(0, numBytes)];
        if (!isRemote)
        {
            for (var d = 0; d < payload.Length; d++)
            {
                payload[d] = FormatHelpers.ParseByte(ValueAt(tokens, d + 5));
            }
        }

        frame.SetPayload(payload);
        return frame;
    }

    private static CanFrame LoadV13(string[] tokens)
    {
        var frame = new CanFrame
        {
            Timestamp = FrameTimestamp.FromMicroseconds((long)(FormatHelpers.ParseDouble(tokens[1]) * 1000.0)),
            Bus = (byte)FormatHelpers.ParseInt(tokens[2]),
        };

        frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[4]));
        var isRemote = ValueAt(tokens, 7) == "R";
        var numBytes = (int)FormatHelpers.ParseInt(tokens[6]);
        if (!isRemote && tokens.Length - 7 < numBytes)
        {
            numBytes = Math.Max(0, tokens.Length - 7);
        }

        frame.SetReceived(true);
        frame.SetExtended(frame.FrameId > 0x10000000 || tokens[4].Length >= 8);
        frame.SetFrameType(isRemote ? FrameType.RemoteRequestFrame : FrameType.CanDataFrame);

        var payload = new byte[Math.Max(0, numBytes)];
        if (!isRemote)
        {
            for (var d = 0; d < payload.Length; d++)
            {
                payload[d] = FormatHelpers.ParseByte(ValueAt(tokens, d + 7));
            }
        }

        frame.SetPayload(payload);
        return frame;
    }

    private static CanFrame LoadV20(string[] tokens)
    {
        var frame = new CanFrame
        {
            Timestamp = FrameTimestamp.FromMicroseconds((long)(FormatHelpers.ParseDouble(tokens[1]) * 1000.0)),
            Bus = 0,
        };

        var messageType = tokens[2].ToUpperInvariant();
        var isFd = IsFdType(messageType);
        frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[3]));

        var numBytes = isFd ? FdLength((int)FormatHelpers.ParseInt(tokens[5])) : (int)FormatHelpers.ParseInt(tokens[5]);
        if (messageType != "RR" && tokens.Length - 6 < numBytes)
        {
            numBytes = Math.Max(0, tokens.Length - 6);
        }

        frame.SetReceived(true);
        frame.SetExtended(frame.FrameId > 0x10000000 || tokens[3].Length >= 8);

        var isRemote = messageType == "RR" || ValueAt(tokens, 6) == "R";
        if (isRemote)
        {
            frame.SetFrameType(FrameType.RemoteRequestFrame);
        }
        else if (isFd)
        {
            ApplyFdType(frame, messageType);
        }
        else
        {
            frame.SetFrameType(FrameType.CanDataFrame);
        }

        var payload = new byte[Math.Max(0, numBytes)];
        if (!isRemote)
        {
            for (var d = 0; d < payload.Length; d++)
            {
                payload[d] = FormatHelpers.ParseByte(ValueAt(tokens, d + 6));
            }
        }

        frame.SetPayload(payload);
        return frame;
    }

    private static CanFrame LoadV21(string[] tokens)
    {
        var frame = new CanFrame
        {
            Timestamp = FrameTimestamp.FromMicroseconds((long)(FormatHelpers.ParseDouble(tokens[1]) * 1000.0)),
            Bus = (byte)FormatHelpers.ParseInt(tokens[3]),
        };

        var messageType = tokens[2].ToUpperInvariant();
        var isFd = IsFdType(messageType);
        frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[4]));

        var numBytes = isFd ? FdLength((int)FormatHelpers.ParseInt(tokens[7])) : (int)FormatHelpers.ParseInt(tokens[7]);
        if (messageType != "RR" && tokens.Length - 8 < numBytes)
        {
            numBytes = Math.Max(0, tokens.Length - 8);
        }

        frame.SetReceived(true);
        frame.SetExtended(frame.FrameId > 0x10000000 || tokens[4].Length >= 8);

        var isRemote = messageType is "RR" or "R";
        if (isRemote)
        {
            frame.SetFrameType(FrameType.RemoteRequestFrame);
        }
        else if (isFd)
        {
            ApplyFdType(frame, messageType);
        }
        else
        {
            frame.SetFrameType(FrameType.CanDataFrame);
        }

        var payload = new byte[Math.Max(0, numBytes)];
        if (!isRemote)
        {
            for (var d = 0; d < payload.Length; d++)
            {
                payload[d] = FormatHelpers.ParseByte(ValueAt(tokens, d + 8));
            }
        }

        frame.SetPayload(payload);
        return frame;
    }

    private static void ApplyFdType(CanFrame frame, string messageType)
    {
        frame.SetFrameType(FrameType.CanFdDataFrame);
        frame.SetBitrateSwitch(messageType is "FB" or "BI");
        frame.SetErrorStateIndicator(messageType is "FE" or "BI");
    }

    private static bool IsFdType(string messageType) => messageType is "FD" or "FB" or "FE" or "BI";

    private static int FdLength(int dlc) => dlc is < 0 or > 15 ? (dlc < 0 ? 0 : 64) : FdLengths[dlc];

    private static string ValueAt(string[] tokens, int index) => index < tokens.Length ? tokens[index] : string.Empty;

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var writer = new StreamWriter(path, false, Encoding.Latin1);
        var offsetTime = frames.Count > 0 ? frames[0].Timestamp.ToMicroseconds() : 0;
        foreach (var frame in frames)
        {
            offsetTime = Math.Min(offsetTime, frame.Timestamp.ToMicroseconds());
        }

        var start = offsetTime > 10_000_000_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(offsetTime / 1000).UtcDateTime
            : DateTime.UtcNow;
        var oleDate = start.Ticks / (double)TimeSpan.TicksPerMillisecond / 86_400_000.0 + 25569.0;

        writer.Write(";$FILEVERSION=2.1\n");
        writer.Write($";$STARTTIME={oleDate.ToString("F10", CultureInfo.InvariantCulture)}\n");
        writer.Write(";\n");
        writer.Write(";   Message    Time    Type    ID     Rx/Tx\n");
        writer.Write(";   Number     Offset  |  Bus  [hex]  |  Reserved\n");
        writer.Write(";   |          [ms]    |  |    |      |  |  Data Length Code\n");
        writer.Write(";   |          |       |  |    |      |  |  |    Data [hex] ...\n");
        writer.Write(";   |          |       |  |    |      |  |  |    |\n");
        writer.Write(";---+--- ------+------ +- +- --+----- +- +- +--- +- -- -- -- -- -- -- --\n");

        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var timeMs = (frame.Timestamp.ToMicroseconds() - offsetTime) / 1000.0;
            writer.Write((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(6));
            writer.Write(")");
            writer.Write("  ");
            writer.Write(timeMs.ToString("F3", CultureInfo.InvariantCulture).PadLeft(12));
            writer.Write(frame.FrameType == FrameType.RemoteRequestFrame ? " R" : " DT");
            writer.Write(' ');
            writer.Write(frame.Bus);
            writer.Write(' ');
            writer.Write(frame.IsExtended
                ? FormatHelpers.Hex8(frame.FrameId)
                : frame.FrameId.ToString("X4", CultureInfo.InvariantCulture));
            writer.Write(frame.IsReceived ? " Rx" : " Tx");
            writer.Write(" -");
            writer.Write("  ");
            writer.Write(frame.Length.ToString(CultureInfo.InvariantCulture));
            if (frame.FrameType != FrameType.RemoteRequestFrame)
            {
                foreach (var b in frame.Payload)
                {
                    writer.Write(' ');
                    writer.Write(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }

            writer.Write('\n');
        }

        return true;
    }
}

