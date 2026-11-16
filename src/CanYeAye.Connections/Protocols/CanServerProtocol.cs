using System.Buffers.Binary;
using CanYeAye.Core;

namespace CanYeAye.Connections.Protocols;

/// <summary>
/// CANserver / PANDA "v1" UDP stream (connections/canserver.cpp).
/// <para>
/// Requests are the 5-byte ASCII words <c>hello</c> (also the 3.5 s heartbeat) and <c>bye</c>,
/// sent to UDP port 1338. Replies are a series of 16-byte slots:
/// <c>[header1:4 LE][header2:4 LE][data:8]</c> with <c>id = header1 &gt;&gt; 21</c>,
/// <c>len = header2 &amp; 0xF</c> and <c>bus = header2 &gt;&gt; 4</c>. Bus 15 is the server's
/// synthetic "special" bus and maps to local bus 2.
/// </para>
/// </summary>
public sealed class CanServerProtocol
{
    public const int DefaultPort = 1338;
    public const int PacketSize = 16;

    /// <summary>ASCII <c>hello</c>, sent on connect and every 3.5 s.</summary>
    public static ReadOnlySpan<byte> Hello => "hello"u8;

    /// <summary>ASCII <c>bye</c>, sent on disconnect.</summary>
    public static ReadOnlySpan<byte> Bye => "bye"u8;

    public bool CaptureSuspended { get; set; }

    /// <summary>Receive timestamps are always wall-clock in the reference, even with useSystemTime off.</summary>
    public Func<long> NowMicroseconds { get; set; } =
        static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public event EventHandler<CanFrame>? FrameReceived;

    /// <summary>Decodes one UDP datagram; a trailing partial slot is ignored.</summary>
    public void Feed(ReadOnlySpan<byte> datagram)
    {
        // SavvyCAN still drains the socket while capture is suspended, but parses nothing.
        if (CaptureSuspended)
        {
            return;
        }

        var now = NowMicroseconds();
        var count = datagram.Length / PacketSize;
        for (var i = 0; i < count; i++)
        {
            var slot = datagram.Slice(i * PacketSize, PacketSize);
            var header1 = BinaryPrimitives.ReadUInt32LittleEndian(slot);
            var header2 = BinaryPrimitives.ReadUInt32LittleEndian(slot[4..]);

            var frameId = header1 >> 21;
            var length = (int)(header2 & 0x0F);
            var bus = (int)(header2 >> 4);
            if (bus == 15)
            {
                bus = 2;
            }

            // A slot only carries 8 payload bytes, so a longer DLC would read into the next
            // slot in the reference; clamp instead.
            length = Math.Clamp(length, 0, 8);

            var frame = new CanFrame();
            frame.SetFrameId(frameId);
            frame.SetExtended(false);
            frame.Bus = (byte)bus;
            frame.SetFrameType(FrameType.CanDataFrame);
            frame.SetReceived(true);
            frame.Timestamp = FrameTimestamp.FromMicroseconds(now);
            frame.SetPayload(slot.Slice(8, length));

            FrameReceived?.Invoke(this, frame);
        }
    }
}
