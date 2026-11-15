using WinCAN.Core;
using WinCAN.Core.Protocols;

namespace WinCAN.Tests.Protocols;

public class Obd2Tests
{
    private static byte[] Pad(params byte[] data)
    {
        var payload = new byte[8];
        data.CopyTo(payload, 0);
        return payload;
    }

    [Fact]
    public void DecodesEngineSpeed()
    {
        var info = Obd2.FindMode01(0x0C);
        Assert.NotNull(info);
        Assert.True(Obd2.TryDecode(info!, [0x1A, 0xF8], out var values));
        var value = Assert.Single(values);
        Assert.Equal(1726.0, value.Scaled, 3);
        Assert.Equal("EngineSpeed", value.Field.Name);
    }

    [Fact]
    public void DecodesStateFieldWithValueName()
    {
        var info = Obd2.FindMode01(0x01)!;
        Assert.True(Obd2.TryDecode(info, [0x81, 0x07, 0x65, 0x04], out var values));
        Assert.Equal("On", values[0].Text); // MIL bit
        Assert.Equal(0x01, values[1].Raw); // DTC count
    }

    [Fact]
    public void SupportedPidBitmap()
    {
        var supported = Obd2.SupportedPids(0x00, [0x18, 0x00, 0x00, 0x00]);
        Assert.Equal([(byte)0x04, (byte)0x05], supported);
    }

    [Fact]
    public void ParsesMultiPidReply()
    {
        // 41 0C 1A F8 0D 32  -> PID 0C (2 bytes) and PID 0D (1 byte)
        var ok = Obd2.TryParseMode01Reply([0x41, 0x0C, 0x1A, 0xF8, 0x0D, 0x32], out var replies);

        Assert.True(ok);
        Assert.Equal(2, replies.Count);
        Assert.Equal(0x0C, replies[0].Pid);
        Assert.Equal(new byte[] { 0x1A, 0xF8 }, replies[0].Data);
        Assert.Equal(0x0D, replies[1].Pid);
        Assert.Equal(new byte[] { 0x32 }, replies[1].Data);
    }

    [Fact]
    public void Mode09VinText()
    {
        var payload = new byte[] { 0x49, 0x02, 0x01, (byte)'1', (byte)'H', (byte)'G', 0, 0 };
        Assert.True(Obd2.TryDecodeMode09Text(payload, 0x02, out var text));
        Assert.Equal("1HG", text);
    }

    [Fact]
    public void DbcGenerationUsesMultiplexedPids()
    {
        var dbc = Obd2Dbc.MakeDbc([0x7E8]);

        Assert.Contains("BU_: OBD2_ECU", dbc);
        Assert.Contains("BO_ 2024 OBD2_ECU_7E8: 8 OBD2_ECU", dbc);
        Assert.Contains("SG_ Service M : 15|8@0+", dbc);
        Assert.Contains("SG_ PID m65M : 23|8@0+", dbc);
        Assert.Contains("SG_ PID_0C_EngineSpeed m12", dbc);
        Assert.Contains("SG_MUL_VAL_ 2024 PID Service 65-65;", dbc);
        Assert.Contains("SG_MUL_VAL_ 2024 PID_0C_EngineSpeed PID 12-12;", dbc);
        Assert.Contains("VAL_ 2024 PID_01_MIL", dbc);
        Assert.Contains("CM_ SG_ 2024 PID_0C_EngineSpeed \"Engine speed\";", dbc);
    }

    [Fact]
    public void ExtendedReplyIdIsMarkedExtended()
    {
        var dbc = Obd2Dbc.MakeDbc([0x18DAF110]);
        var wireId = 0x18DAF110u | 0x80000000u;
        Assert.Contains($"BO_ {wireId} OBD2_ECU_18DAF110: 8 OBD2_ECU", dbc);
    }

    [Fact]
    public void PollerDiscoversAndPolls()
    {
        var iso = new IsoTpHandler();
        iso.SetReception(true);
        var sent = new List<CanFrame>();
        iso.FrameToSend += (_, frame) => sent.Add(frame);

        long now = 0;
        var poller = new Obd2Poller(iso, () => now);
        poller.SetBus(0);
        poller.SetPollList([new Obd2Poller.PollItem(0, 0x0C, 0)]);

        var found = new List<uint>();
        var updates = new List<(uint Ecu, byte Pid, IReadOnlyList<Obd2.Value> Values)>();
        var discovered = false;
        poller.EcuFound += (_, id) => found.Add(id);
        poller.DiscoveryFinished += (_, _) => discovered = true;
        poller.PidUpdated += (_, update) => updates.Add((update.EcuReplyId, update.Pid, update.Values));

        poller.Discover();
        var request = Assert.Single(sent);
        Assert.Equal(0x7DFU, request.FrameId);
        Assert.Equal(new byte[] { 0x02, 0x01, 0x00 }, request.Payload[..3]);

        // The functional request's reply (bitmap supporting 0x04 and 0x05).
        var reply = new CanFrame();
        reply.SetFrameId(0x7E8);
        reply.Bus = 0;
        reply.SetPayload(Pad(0x06, 0x41, 0x00, 0x18, 0x00, 0x00, 0x00));
        iso.ProcessFrame(reply);

        Assert.Contains(0x7E8U, found);
        var ecu = Assert.Single(poller.Ecus);
        Assert.Equal([(byte)0x04, (byte)0x05], ecu.SupportedPids);

        // Functional requests wait for the timeout even after the first reply.
        now = 60;
        poller.Service();
        Assert.True(discovered);

        // Poll PID 0x0C.
        sent.Clear();
        updates.Clear();
        poller.Start();
        request = Assert.Single(sent);
        Assert.Equal(0x7DFU, request.FrameId);

        var speedReply = new CanFrame();
        speedReply.SetFrameId(0x7E8);
        speedReply.Bus = 0;
        speedReply.SetPayload(Pad(0x04, 0x41, 0x0C, 0x1A, 0xF8));
        iso.ProcessFrame(speedReply);

        var engineSpeed = Assert.Single(updates);
        Assert.Equal((byte)0x0C, engineSpeed.Pid);
        Assert.Equal(1726.0, Assert.Single(engineSpeed.Values).Scaled, 3);
    }

    [Fact]
    public void RefusesWritingServices()
    {
        var iso = new IsoTpHandler();
        iso.SetReception(true);
        var poller = new Obd2Poller(iso);
        Assert.False(poller.Request(0x7E8, [0x04, 0x00])); // clear DTCs
        Assert.True(poller.Request(0x7E8, [0x01, 0x0C]));
    }
}

