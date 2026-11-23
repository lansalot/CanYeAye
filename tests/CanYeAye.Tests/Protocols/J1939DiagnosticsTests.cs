using CanYeAye.Core;
using CanYeAye.Core.Protocols;

namespace CanYeAye.Tests.Protocols;

public class J1939DiagnosticsTests
{
    // 0x18FECA00: priority 6, PGN 0xFECA (DM1), source 0x00.
    private static CanFrame Dm1(params byte[] payload) => new(0x18FECA00, payload);

    [Fact]
    public void DecodesNoFaultDm1()
    {
        // Real-world idle DM1: lamps mostly "not available", flash bits reserved, DTC zeroed.
        var frame = Dm1(0xC3, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF);

        Assert.True(J1939Diagnostics.TryDecode(frame, out var message));
        Assert.NotNull(message);
        Assert.Equal(J1939Diagnostics.Dm1Pgn, message.Pgn);
        Assert.Equal(0, message.Source);
        Assert.False(message.HasFaults);
        Assert.False(message.AnyLampOn);
        Assert.Equal(J1939LampState.NotAvailable, message.Lamps[0].State);
        Assert.Contains("no active DTCs", J1939Diagnostics.Describe(message, "Engine"));
    }

    [Fact]
    public void DecodesSingleActiveDtcWithLamps()
    {
        // MIL off, Red stop off, Amber on, Protect off; SPN 100, FMI 1, OC 5, CM 0.
        var frame = Dm1(0x04, 0x00, 0x64, 0x00, 0x01, 0x05, 0xFF, 0xFF);

        Assert.True(J1939Diagnostics.TryDecode(frame, out var message));
        Assert.NotNull(message);
        Assert.True(message.AnyLampOn);
        Assert.Equal(J1939LampState.On, message.Lamps[2].State);

        var dtc = Assert.Single(message.Dtcs);
        Assert.Equal(100, dtc.Spn);
        Assert.Equal(1, dtc.Fmi);
        Assert.Equal(5, dtc.OccurrenceCount);
        Assert.False(dtc.ConversionMethod);
        Assert.False(dtc.IsProprietarySpn);

        var text = J1939Diagnostics.Describe(message, "Engine");
        Assert.Contains("DM1 Active DTCs (Engine)", text);
        Assert.Contains("Amber warning", text);
        Assert.Contains("SPN 100 / FMI 1", text);
        Assert.Contains("below normal operating range, most severe", text);
        Assert.Contains("OC 5", text);
    }

    [Fact]
    public void DecodesProprietarySpnAndLegacyConversionMethod()
    {
        // SPN 516096 (0x7E000) is the start of the manufacturer-defined range; FMI 31; OC 127; CM 1.
        var frame = Dm1(0x00, 0xFF, 0x00, 0xE0, 0xFF, 0xFF);

        Assert.True(J1939Diagnostics.TryDecode(frame, out var message));
        Assert.NotNull(message);
        var dtc = Assert.Single(message.Dtcs);

        Assert.Equal(516096, dtc.Spn);
        Assert.Equal(31, dtc.Fmi);
        Assert.True(dtc.IsProprietarySpn);
        Assert.True(dtc.OccurrenceCountNotAvailable);
        Assert.True(dtc.ConversionMethod);

        var text = J1939Diagnostics.DescribeDtc(dtc);
        Assert.Contains("condition exists", text);
        Assert.Contains("OC n/a", text);
        Assert.Contains("CM 1", text);
        Assert.Contains("proprietary SPN", text);
    }

    [Fact]
    public void DecodesSecondDtcRecord()
    {
        // Two DTCs in an (already reassembled) payload: SPN 100/FMI 1 and SPN 190/FMI 2.
        byte[] payload =
        [
            0x00, 0x00,
            0x64, 0x00, 0x01, 0x01,
            0xBE, 0x00, 0x02, 0x02,
        ];

        Assert.True(J1939Diagnostics.TryDecode(J1939Diagnostics.Dm1Pgn, payload, 0x03, out var message));
        Assert.NotNull(message);
        Assert.Equal(2, message.Dtcs.Count);
        Assert.Equal(190, message.Dtcs[1].Spn);
        Assert.Equal(2, message.Dtcs[1].Fmi);
        Assert.Equal(0x03, message.Source);
    }

    [Fact]
    public void StopsAtPaddingRecords()
    {
        // A single DTC followed by 0xFF 0xFF 0xFF 0xFF padding must not decode a second code.
        var frame = Dm1(0x00, 0x00, 0x64, 0x00, 0x01, 0x01, 0xFF, 0xFF, 0xFF, 0xFF);

        Assert.True(J1939Diagnostics.TryDecode(frame, out var message));
        Assert.NotNull(message);
        Assert.Single(message.Dtcs);
    }

    [Fact]
    public void LampOnlyPayloadHasNoDtcs()
    {
        Assert.True(J1939Diagnostics.TryDecode(J1939Diagnostics.Dm1Pgn, [0x40, 0x00], 0x00, out var message));
        Assert.NotNull(message);
        Assert.True(message.AnyLampOn);
        Assert.Empty(message.Dtcs);
        Assert.Equal(J1939LampState.On, message.Lamps[0].State);
    }

    [Fact]
    public void DecodesDm2WithPreviouslyActiveWording()
    {
        var frame = new CanFrame(0x18FECB21, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

        Assert.True(J1939Diagnostics.TryDecode(frame, out var message));
        Assert.NotNull(message);
        Assert.Equal(J1939Diagnostics.Dm2Pgn, message.Pgn);
        Assert.Equal(0x21, message.Source);
        Assert.Contains("Previously Active", message.Name);
        Assert.Contains("no previously active DTCs", J1939Diagnostics.Describe(message));
    }

    [Fact]
    public void IgnoresStandardAndNonDiagnosticFrames()
    {
        Assert.False(J1939Diagnostics.TryDecode(new CanFrame(0x7E0, [0x64, 0, 1, 1]), out _));
        Assert.False(J1939Diagnostics.TryDecode(new CanFrame(0x18FEF100, [0, 0, 0, 0, 0, 0, 0, 0]), out _));
        Assert.False(J1939Diagnostics.TryDecode(0xFECA, [], 0, out _));
    }

    [Theory]
    [InlineData(0, "data valid but above normal operating range, most severe")]
    [InlineData(5, "current below normal or open circuit")]
    [InlineData(19, "received network data in error")]
    [InlineData(25, "reserved")]
    [InlineData(31, "condition exists")]
    public void DescribesFailureModeIdentifiers(int fmi, string expected) =>
        Assert.Equal(expected, J1939Diagnostics.DescribeFmi(fmi));

    [Fact]
    public void LookupQueryUsesSpnAndFmi()
    {
        var dtc = new J1939Dtc(3226, 2, 12, false);
        Assert.Equal("SPN 3226 FMI 2", dtc.LookupQuery);
    }

    [Fact]
    public void CompactSummaryFitsOneLine()
    {
        var frame = Dm1(0x04, 0x00, 0x64, 0x00, 0x01, 0x05, 0xFF, 0xFF);

        Assert.True(J1939Diagnostics.TryDecode(frame, out var message));
        Assert.NotNull(message);

        var text = J1939Diagnostics.DescribeCompact(message);
        Assert.DoesNotContain(Environment.NewLine, text);
        Assert.Equal(
            "DM1: Amber warning · SPN 100 / FMI 1 (below normal, most severe) · OC 5",
            text);
    }

    [Fact]
    public void CompactSummaryMarksLegacyConversionMethod()
    {
        var dtc = new J1939Dtc(3226, 2, 12, true);
        var text = J1939Diagnostics.DescribeDtcCompact(dtc);
        Assert.Contains("CM 1", text);
        Assert.DoesNotContain("legacy", text);
    }

    [Fact]
    public void CompactNoFaultSummaryMentionsLampsOnlyWhenOn()
    {
        Assert.True(J1939Diagnostics.TryDecode(
            J1939Diagnostics.Dm1Pgn, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00], 0x00, out var quiet));
        Assert.NotNull(quiet);
        Assert.Equal("DM1: no active DTCs", J1939Diagnostics.DescribeCompact(quiet));

        Assert.True(J1939Diagnostics.TryDecode(
            J1939Diagnostics.Dm1Pgn, [0x40, 0x00, 0x00, 0x00, 0x00, 0x00], 0x00, out var lit));
        Assert.NotNull(lit);
        Assert.Contains("(Malfunction on)", J1939Diagnostics.DescribeCompact(lit));
    }
}
