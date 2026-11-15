using WinCAN.Core;
using WinCAN.Core.Dbc;

namespace WinCAN.Tests.Dbc;

public class DbcRoundTripTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static DbcFile Parse(string text) => DbcFileReader.Parse(text, out _);

    [Fact]
    public void BmsRoundTripIsStable()
    {
        var text = File.ReadAllText(Fixture("bms.dbc"));
        var first = DbcFileWriter.Serialize(Parse(text));
        var second = DbcFileWriter.Serialize(Parse(first));

        Assert.Equal(first, second);
    }

    [Fact]
    public void AttributeAndValueTableRoundTrip()
    {
        var text = """
            BA_DEF_ BO_ "GenMsgCycleTime" INT 0 65535;
            BA_DEF_DEF_ "GenMsgCycleTime" 100;
            BA_DEF_ "Baudrate" INT 0 1000000;
            BA_DEF_DEF_ "Baudrate" 500000;
            VAL_TABLE_ Errors 1 "Bad" 2 "Worse" ;
            BU_: ECU 
            BO_ 256 Status: 8 ECU
             SG_ State : 0|8@1+ (1,0) [0|0] "" ECU
            BA_ "GenMsgCycleTime" BO_ 256 200;
            BA_ "Baudrate" BU_ ECU 500000;
            VAL_ 256 State 1 "Error" 0 "OK" ;
            CM_ BU_ ECU "the main ECU";
            CM_ BO_ 256 "status message";
            CM_ SG_ 256 State "state signal";
            """;

        var file = Parse(text);
        Assert.Single(file.Messages);
        Assert.Single(file.ValueTables);

        var serialized = DbcFileWriter.Serialize(file);
        var reloaded = Parse(serialized);

        Assert.True(reloaded.Messages.TryGetValue(256, out var message));
        Assert.Equal("status message", message.Comment);
        Assert.Equal("state signal", message.Signals["State"].Comment);
        Assert.Equal("the main ECU", reloaded.FindNode("ECU").Comment);
        Assert.Equal(2, message.Signals["State"].ValueDescriptions.Count);
        Assert.Equal(2, reloaded.FindValueTable("Errors")!.Entries.Count);

        // Byte-stable on the second pass.
        Assert.Equal(serialized, DbcFileWriter.Serialize(reloaded));
    }

    [Fact]
    public void MultiplexSignalsAreLinked()
    {
        var file = Parse("""
            BU_: ECU 
            BO_ 256 MUX: 8 ECU
             SG_ MuxSig M : 0|8@1+ (1,0) [0|0] "" ECU
             SG_ SigA m0 : 8|8@1+ (1,0) [0|0] "" ECU
             SG_ SigB m1 : 8|8@1+ (1,0) [0|0] "" ECU
            """);

        var message = file.Messages[256];
        Assert.NotNull(message.MultiplexorSignal);
        Assert.Equal("MuxSig", message.MultiplexorSignal!.Name);

        var sigA = message.Signals["SigA"];
        Assert.True(sigA.IsMultiplexed);
        Assert.Equal((0, 0), sigA.MultiplexRanges.Single());

        var frame = new CanFrame();
        frame.SetPayload(new byte[] { 0, 0x11 }); // mux value 0 -> SigA active
        frame.SetFrameType(FrameType.CanDataFrame);

        var active = message.ActiveSignals(frame).Select(s => s.Name).ToArray();
        Assert.Contains("MuxSig", active);
        Assert.Contains("SigA", active);
        Assert.DoesNotContain("SigB", active);
    }

    [Fact]
    public void ExtendedMultiplexRange()
    {
        var file = Parse("""
            BU_: ECU 
            BO_ 256 MUX: 8 ECU
             SG_ MuxSig M : 0|8@1+ (1,0) [0|0] "" ECU
             SG_ Child : 8|8@1+ (1,0) [0|0] "" ECU
            SG_MUL_VAL_ 256 Child MuxSig 1-3;
            """);

        var message = file.Messages[256];
        var child = message.Signals["Child"];
        Assert.True(child.IsMultiplexed);
        Assert.Equal([(1, 3)], child.MultiplexRanges);
        Assert.Same(message.MultiplexorSignal, child.MultiplexParent);
    }

    [Fact]
    public void FloatSignalDecodesAndResaves()
    {
        var file = Parse("""
            BU_: ECU 
            BO_ 256 F: 8 ECU
             SG_ Temp : 0|32@1- (1,0) [0|0] "" ECU
            SIG_VALTYPE_ 256 Temp : 1;
            """);

        var signal = file.Messages[256].Signals["Temp"];
        Assert.Equal(DbcSignalValueType.SingleFloat, signal.ValueType);

        var bits = BitConverter.SingleToInt32Bits(100.0f);
        var frame = new CanFrame();
        frame.SetPayload(BitConverter.GetBytes(bits));
        frame.SetFrameType(FrameType.CanDataFrame);

        Assert.True(signal.TryProcessAsDouble(frame, out var value));
        Assert.Equal(100.0, value, 3);

        var serialized = DbcFileWriter.Serialize(file);
        Assert.Contains("SIG_VALTYPE_ 256 Temp : 1;", serialized);
    }

    [Fact]
    public void ExtendedMessageIdRoundTripsBit31()
    {
        // BO_ 2147483916 == 268 with bit 31 set (extended).
        var file = Parse("BU_: ECU \nBO_ 2147483916 EMsg: 8 ECU\n SG_ A : 0|8@1+ (1,0) [0|0] \"\" ECU\n");

        var message = file.Messages[268];
        Assert.True(message.IsExtended);

        var serialized = DbcFileWriter.Serialize(file);
        Assert.Contains("BO_ 2147483916 EMsg: 8 ECU", serialized);
    }

    [Fact]
    public void CanFdMessageAddsFrameFormatAttributesOnSave()
    {
        var file = Parse("BU_: ECU \nBO_ 256 FDMsg: 64 ECU\n SG_ A : 0|8@1+ (1,0) [0|0] \"\" ECU\n");
        var message = file.Messages[256];
        Assert.True(message.IsCanFd); // length 64 > 8

        var serialized = DbcFileWriter.Serialize(file);

        Assert.Contains("VFrameFormat", serialized);
        Assert.Contains("CANFD_BRS", serialized);
        Assert.Contains("\"StandardCAN_FD\"", serialized);

        var file2 = Parse(serialized);
        Assert.True(file2.Messages[256].IsCanFd);
        // A second pass must not duplicate the injected definitions.
        Assert.Equal(serialized, DbcFileWriter.Serialize(file2));
    }

    [Fact]
    public void J1939MatchingFindsPduByPgn()
    {
        var file = Parse("BO_ 256 Status: 8 ECU\n SG_ A : 0|8@1+ (1,0) [0|0] \"\" ECU\n");
        file.MatchingCriteria = MatchingCriteria.J1939;

        // 256 == 0x100. PGN is masked differently, but the exact match must win.
        Assert.NotNull(file.FindMessageById(256));
    }

    [Fact]
    public void GmlanMatchingUsesArbitrationIdMask()
    {
        var file = Parse("BO_ 520 Lo: 8 ECU\n SG_ A : 0|8@1+ (1,0) [0|0] \"\" ECU\n");
        file.MatchingCriteria = MatchingCriteria.Gmlan;

        // 520 = 0x208; arbitration-id mask 0x3FFE000 keeps upper arbitration bits.
        Assert.NotNull(file.FindMessageById(520));
    }

    [Fact]
    public void DuplicateMessageIdLastWins()
    {
        var file = Parse("BO_ 256 First: 8 ECU\nBO_ 256 Second: 5 ECU\n");
        Assert.Single(file.Messages);
        Assert.Equal("Second", file.Messages[256].Name);
        Assert.Equal(5u, file.Messages[256].Length);
    }
}
