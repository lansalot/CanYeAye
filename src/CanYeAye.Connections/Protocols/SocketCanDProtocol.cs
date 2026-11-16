using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CanYeAye.Core;

namespace CanYeAye.Connections.Protocols;

/// <summary>socketcand's per-bus handshake state (socketcand.h's <c>KAYAKSTATE</c>).</summary>
public enum SocketCanDState
{
    Idle = 0,
    Bcm = 1,
    SwitchingToRaw = 2,
    RawMode = 3,
    IsoTp = 4,
}

/// <summary>
/// socketcand / Kayak TCP protocol (connections/socketcand.cpp). One instance drives one
/// socket, which serves one bus.
/// <para>
/// Handshake: the host greets with <c>&lt; hi &gt;</c>, the client opens the bus with
/// <c>&lt; open can0 &gt;</c>, waits for <c>&lt; ok &gt;</c>, sends <c>&lt; rawmode &gt;</c> and
/// waits for a second <c>&lt; ok &gt;</c>. Only then are <c>&lt; frame ... &gt;</c> datagrams
/// decoded, and only then may frames be sent.
/// </para>
/// <para>
/// The reference builds transmit IDs in a signed <c>int</c>, so a 29-bit ID (bit 31 set) prints
/// with a minus sign, which no socketcand host understands. CanYeAye prints the ID as an unsigned
/// 32-bit value with bit 31 as the extended-frame flag, which is what the wire format expects.
/// </para>
/// </summary>
public sealed class SocketCanDProtocol
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private string _buffer = string.Empty;

    public SocketCanDProtocol(int busIndex, string busName = "can0")
    {
        BusIndex = busIndex;
        BusName = busName;
    }

    /// <summary>Local bus number stamped onto received frames (SavvyCAN's <c>setBus</c>).</summary>
    public int BusIndex { get; }

    /// <summary>Bus name used in the <c>&lt; open ... &gt;</c> command (from the beacon).</summary>
    public string BusName { get; }

    public SocketCanDState State { get; private set; } = SocketCanDState.Idle;

    /// <summary>Drops received frames while capture is suspended.</summary>
    public bool CaptureSuspended { get; set; }

    /// <summary>Raised for every complete frame decoded from the socket.</summary>
    public event EventHandler<CanFrame>? FrameReceived;

    /// <summary>Raised with text that must be written back to the socket verbatim.</summary>
    public event EventHandler<string>? SendRequested;

    /// <summary>True once the bus is in raw mode and frames may be transmitted.</summary>
    public bool CanSend => State == SocketCanDState.RawMode;

    /// <summary>Processes one datagram read from the socket (SavvyCAN's <c>procRXData</c>).</summary>
    public void Feed(string data)
    {
        switch (State)
        {
            case SocketCanDState.Idle:
                if (data == "< hi >")
                {
                    Send($"< open {BusName} >");
                    State = SocketCanDState.Bcm;
                }

                break;

            case SocketCanDState.Bcm:
                if (data == "< ok >")
                {
                    Send("< rawmode >");
                    State = SocketCanDState.SwitchingToRaw;
                    _buffer = string.Empty;
                }

                break;

            case SocketCanDState.SwitchingToRaw:
                if (data == "< ok >")
                {
                    State = SocketCanDState.RawMode;
                }
                else if (data.StartsWith("< ok >", StringComparison.OrdinalIgnoreCase))
                {
                    // The host answered and appended the first frames to the same datagram.
                    State = SocketCanDState.RawMode;
                    _buffer = DecodeFrames(data);
                }
                else if (data.IndexOf("< ok >", StringComparison.OrdinalIgnoreCase) > 0)
                {
                    State = SocketCanDState.RawMode;
                    _buffer = DecodeFrames(data);
                }

                break;

            case SocketCanDState.RawMode:
                _buffer = DecodeFrames(_buffer + data);
                if (_buffer.Length > 128)
                {
                    _buffer = string.Empty;
                }

                break;

            case SocketCanDState.IsoTp:
                break;
        }
    }

    /// <summary>
    /// Encodes a frame as a <c>&lt; send ... &gt;</c> command, or returns null when the bus is
    /// not in raw mode or the frame must not be sent to an adapter.
    /// </summary>
    public string? EncodeFrame(CanFrame frame)
    {
        if (State != SocketCanDState.RawMode)
        {
            return null;
        }

        // The reference's error-frame guard cannot fire (frameId() masks 0x20000000 off);
        // CanYeAye skips error frames explicitly.
        if (frame.FrameType == FrameType.ErrorFrame)
        {
            return null;
        }

        var id = frame.FrameId;
        if (frame.IsExtended)
        {
            id |= 1u << 31;
        }

        var builder = new StringBuilder(frame.Length * 2 + 20);
        builder.Append("< send ")
            .Append(id.ToString("x", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(frame.Length.ToString(CultureInfo.InvariantCulture))
            .Append(' ');

        foreach (var value in frame.Payload)
        {
            builder.Append(value.ToString("x", CultureInfo.InvariantCulture)).Append(' ');
        }

        builder.Append('>');
        return builder.ToString();
    }

    /// <summary>
    /// Consumes as many complete <c>&lt; frame ... &gt;</c> blocks as possible and returns the
    /// unprocessed remainder, mirroring the reference's recursive <c>decodeFrames</c>.
    /// </summary>
    private string DecodeFrames(string data)
    {
        if (!data.Contains('<'))
        {
            return string.Empty;
        }

        if (data.Length >= 8 && !data.Contains("< frame "))
        {
            return string.Empty;
        }

        var start = data.IndexOf("< frame ", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        // Anything before the first frame marker is a fragment from a previous datagram.
        var framePart = data[start..];
        var close = framePart.IndexOf('>');
        var frameString = close >= 0 ? framePart[..(close + 1)] : string.Empty;

        var tokens = SplitTokens(frameString);
        if (tokens.Length < 3)
        {
            // Incomplete: keep the raw text for the next read.
            return framePart;
        }

        var id = (uint)ParseHex(tokens[1]);
        var frame = new CanFrame
        {
            Bus = (byte)BusIndex,
        };
        frame.SetFrameId(id);
        frame.SetExtended(id > 0x7FF);
        frame.Timestamp = FrameTimestamp.FromMicroseconds(
            (long)(ParseDouble(tokens[2]) * 1_000_000d));
        frame.SetFrameType(FrameType.CanDataFrame);
        frame.SetReceived(true);

        var length = tokens.Length == 4 ? tokens[3].Length / 2 : 0;
        frame.SetPayload(ReadData(tokens.Length == 4 ? tokens[3] : string.Empty, length));

        if (!CaptureSuspended)
        {
            FrameReceived?.Invoke(this, frame);
        }

        // Consume the processed block (and any leading fragment) and recurse.
        if (framePart.Length > frameString.Length)
        {
            return DecodeFrames(framePart[frameString.Length..]);
        }

        return string.Empty;
    }

    private void Send(string command) => SendRequested?.Invoke(this, command);

    private static string[] SplitTokens(string frameString)
    {
        var inner = frameString;
        if (inner.StartsWith('<'))
        {
            inner = inner[1..];
        }

        if (inner.EndsWith('>'))
        {
            inner = inner[..^1];
        }

        inner = Whitespace.Replace(inner, " ").Trim();
        return inner.Length == 0 ? [] : inner.Split(' ');
    }

    private static byte[] ReadData(string text, int length)
    {
        var payload = new byte[Math.Max(0, length)];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)ParseHex(text.Substring(i * 2, 2));
        }

        return payload;
    }

    private static int ParseHex(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var value = 0L;
        foreach (var c in text)
        {
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'F' => c - 'A' + 10,
                >= 'a' and <= 'f' => c - 'a' + 10,
                _ => -1,
            };

            if (digit < 0)
            {
                return 0;
            }

            value = (value * 16) + digit;
        }

        return (int)Math.Min(value, int.MaxValue);
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0d;
}
