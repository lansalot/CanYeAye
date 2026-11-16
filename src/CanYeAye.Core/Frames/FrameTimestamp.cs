namespace CanYeAye.Core;

/// <summary>
/// Frame timestamp in SavvyCAN's native representation: whole seconds plus microseconds.
/// Mirrors CommFrame::TimeStamp in can_structs.h. Capture timing stays in integer
/// microseconds end-to-end; do not convert to <see cref="DateTime"/>.
/// </summary>
public readonly record struct FrameTimestamp(long Seconds, long Microseconds) : IComparable<FrameTimestamp>
{
    public static readonly FrameTimestamp Zero = new(0, 0);

    public static FrameTimestamp FromMicroseconds(long totalMicroseconds) =>
        new(totalMicroseconds / 1_000_000, totalMicroseconds % 1_000_000);

    public long ToMicroseconds() => Seconds * 1_000_000 + Microseconds;

    public double ToSeconds() => Seconds + Microseconds / 1_000_000.0;

    public int CompareTo(FrameTimestamp other) => ToMicroseconds().CompareTo(other.ToMicroseconds());

    public static bool operator <(FrameTimestamp left, FrameTimestamp right) => left.CompareTo(right) < 0;
    public static bool operator >(FrameTimestamp left, FrameTimestamp right) => left.CompareTo(right) > 0;
    public static bool operator <=(FrameTimestamp left, FrameTimestamp right) => left.CompareTo(right) <= 0;
    public static bool operator >=(FrameTimestamp left, FrameTimestamp right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Seconds}.{Microseconds:D6}";
}
