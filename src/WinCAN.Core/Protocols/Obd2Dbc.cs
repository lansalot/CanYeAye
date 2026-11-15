using System.Globalization;

namespace WinCAN.Core.Protocols;

/// <summary>
/// Generates a DBC for OBD-II Mode 01 replies from the PID table. A reply is
/// <c>[ISO-TP length][0x41][PID][data...]</c>; the DBC multiplexes Service (byte 1) and PID
/// (byte 2) and links the per-PID signals with <c>SG_MUL_VAL_</c>, all big-endian as J1979 sends.
/// </summary>
public static class Obd2Dbc
{
    private const int ReplyDataOffset = 3; // ISO-TP length, service, PID
    private const string Node = "OBD2_ECU";

    public static string SignalName(byte pid, Obd2.Field field) =>
        $"PID_{pid:X2}_{field.Name}";

    public static int StartBit(Obd2.Field field)
    {
        var msb = (ReplyDataOffset * 8) + field.StartBit;
        return ((msb / 8) * 8) + (7 - (msb % 8));
    }

    public static string MessageName(uint ecuReplyId) =>
        "OBD2_ECU_" + ecuReplyId.ToString(ecuReplyId > 0x7FF ? "X8" : "X3", CultureInfo.InvariantCulture);

    public static string MakeDbc(IEnumerable<uint> ecuReplyIds)
    {
        var lines = new List<string>();
        var comments = new List<string>();
        var values = new List<string>();
        var multiplexValues = new List<string>();

        lines.Add("VERSION \"\"");
        lines.Add(string.Empty);
        lines.Add("NS_ :");
        lines.Add(string.Empty);
        lines.Add("BS_:");
        lines.Add(string.Empty);
        lines.Add($"BU_: {Node}");
        lines.Add(string.Empty);

        foreach (var ecu in ecuReplyIds)
        {
            var id = DbcId(ecu);
            lines.Add($"BO_ {id} {MessageName(ecu)}: 8 {Node}");
            lines.Add(" SG_ Service M : 15|8@0+ (1,0) [0|255] \"\" Vector__XXX");
            lines.Add(" SG_ PID m65M : 23|8@0+ (1,0) [0|255] \"\" Vector__XXX");
            multiplexValues.Add($"SG_MUL_VAL_ {id} PID Service 65-65;");
            comments.Add($"CM_ BO_ {id} \"OBD-II Mode 01 replies from ECU 0x{ecu:X}, made by WinCAN's OBD-II Poller\";");

            foreach (var info in Obd2.Mode01)
            {
                foreach (var field in info.Fields)
                {
                    var name = SignalName(info.Pid, field);
                    lines.Add(
                        $" SG_ {name} m{info.Pid} : {StartBit(field)}|{field.BitLength}@0{(field.IsSigned ? "-" : "+")} " +
                        $"({Number(field.Scale)},{Number(field.Offset)}) [{Number(field.Minimum())}|{Number(field.Maximum())}] " +
                        $"{Quoted(field.Unit)} Vector__XXX");
                    multiplexValues.Add($"SG_MUL_VAL_ {id} {name} PID {info.Pid}-{info.Pid};");
                    comments.Add($"CM_ SG_ {id} {name} {Quoted(info.Name)};");
                    if (field.ValueNames.Count > 0)
                    {
                        var line = $"VAL_ {id} {name}";
                        foreach (var valueName in field.ValueNames)
                        {
                            line += $" {valueName.Value} {Quoted(valueName.Description)}";
                        }

                        values.Add(line + " ;");
                    }
                }
            }

            lines.Add(string.Empty);
        }

        var all = new List<string>(lines);
        all.AddRange(comments);
        all.Add(string.Empty);
        all.AddRange(values);
        all.Add(string.Empty);
        all.AddRange(multiplexValues);
        return string.Join('\n', all) + "\n";
    }

    private static uint DbcId(uint ecuReplyId) => ecuReplyId > 0x7FF ? ecuReplyId | 0x80000000u : ecuReplyId;

    private static string Number(double value)
    {
        var text = value.ToString("g15", CultureInfo.InvariantCulture);
        return text.Replace('E', 'e');
    }

    private static string Quoted(string text) => "\"" + text.Replace('"', '\'') + "\"";
}
