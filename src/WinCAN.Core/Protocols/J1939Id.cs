namespace WinCAN.Core.Protocols;

/// <summary>J1939 29-bit identifier fields (re/frameinfowindow.cpp's J1939 decoding).</summary>
public readonly record struct J1939Id(
    int Source,
    int Destination,
    int Pgn,
    int DataPage,
    int ExtendedDataPage,
    int PduFormat,
    int PduSpecific,
    int Priority,
    bool IsBroadcast)
{
    public static J1939Id Decode(uint id)
    {
        var source = (int)(id & 0xFF);
        var priority = (int)((id >> 26) & 0x07);
        var extendedDataPage = (int)((id >> 25) & 0x01);
        var dataPage = (int)((id >> 24) & 0x01);
        var pgn = (int)((id >> 8) & 0x3FFFF);
        var pduFormat = (int)((id >> 16) & 0xFF);
        var pduSpecific = (int)((id >> 8) & 0xFF);

        if (pduFormat > 0xEF)
        {
            // PDU2: broadcast, destination is the group extension
            return new J1939Id(
                source,
                0xFFFF,
                pgn,
                dataPage,
                extendedDataPage,
                pduFormat,
                pduSpecific,
                priority,
                IsBroadcast: true);
        }

        // PDU1: destination-specific, PGN clears the low byte
        return new J1939Id(
            source,
            pduSpecific,
            pgn & 0xFFFF00,
            dataPage,
            extendedDataPage,
            pduFormat,
            pduSpecific,
            priority,
            IsBroadcast: false);
    }
}
