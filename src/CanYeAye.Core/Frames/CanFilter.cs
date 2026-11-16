namespace CanYeAye.Core;

/// <summary>
/// SavvyCAN-compatible ID filter (canfilter.cpp). A bus of -1 matches any bus.
/// Matching is <c>(id &amp; Mask) == Id</c>.
/// </summary>
public sealed class CanFilter
{
    public CanFilter(uint id = 0, uint mask = 0, int bus = -1)
    {
        Id = id;
        Mask = mask;
        Bus = bus;
    }

    public uint Id { get; set; }

    public uint Mask { get; set; }

    public int Bus { get; set; }

    public void SetFilter(uint id, uint mask, int bus)
    {
        Id = id;
        Mask = mask;
        Bus = bus;
    }

    public bool CheckFilter(uint id, int bus)
    {
        if (bus != -1 && bus != Bus)
        {
            return false;
        }

        return (id & Mask) == Id;
    }
}
