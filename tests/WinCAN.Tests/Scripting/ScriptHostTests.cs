using WinCAN.Core;
using WinCAN.Core.Scripting;

namespace WinCAN.Tests.Scripting;

/// <summary>
/// Runs the four example scripts shipped with SavvyCAN unmodified and checks the documented
/// host/can/isotp/uds API surface.
/// </summary>
public class ScriptHostTests
{
    private static string Script(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Scripts", name);

    private static (ScriptHost Host, List<string> Logs, List<CanFrame> Sent) Create(string file)
    {
        var host = new ScriptHost();
        var logs = new List<string>();
        var sent = new List<CanFrame>();
        host.LogEmitted += (_, text) => logs.Add(text);
        host.FrameToSend += (_, frame) => sent.Add(frame);
        host.FileName = file;
        Assert.True(host.Compile(File.ReadAllText(Script(file)), file), string.Join("\n", host.CompileErrors));
        return (host, logs, sent);
    }

    private static CanFrame Frame(uint id, params byte[] payload)
    {
        var frame = new CanFrame();
        frame.SetFrameId(id);
        frame.SetFrameType(FrameType.CanDataFrame);
        frame.Bus = 0;
        frame.SetPayload(payload);
        return frame;
    }

    [Fact]
    public void GetCanAndTickRuns()
    {
        var (host, logs, sent) = Create("GetCANandTick.js");

        Assert.Contains("Example script 1.0", logs);
        Assert.Equal(20000, host.TickInterval);

        var startup = Assert.Single(sent);
        Assert.Equal(0x7E0U, startup.FrameId);
        Assert.Equal(new byte[] { 0x0D, 1, 0, 0, 0, 0, 0, 0 }, startup.Payload);

        host.DispatchFrame(Frame(0x123, 1, 2, 3));
        Assert.Contains("Bus: 0  id: 123", logs);

        host.Tick();
        Assert.Contains("TICK!", logs);
    }

    [Fact]
    public void CaptureAllMsgTypesRuns()
    {
        var (host, logs, sent) = Create("CaptureAllMsgTypes.js");

        // CAN frame + UDS tester-present request.
        Assert.Equal(2, sent.Count);
        Assert.Equal(0x200U, sent[0].FrameId);
        Assert.Equal(0x604U, sent[1].FrameId);
        Assert.Equal(new byte[] { 0x02, 0x3E, 0x00 }, sent[1].Payload[..3]);

        // A UDS positive response on the filtered id reaches gotUDSMessage.
        host.DispatchFrame(Frame(0x604, 0x03, 0x50, 0x03, 0x00));
        Assert.Contains(logs, line => line.Contains("Sv: 50"));
    }

    [Fact]
    public void RlecChallengeResponseRuns()
    {
        var (host, logs, sent) = Create("RLEC.js");
        Assert.Contains("RLEC ID Changer", logs);
        sent.Clear();

        host.DispatchFrame(Frame(0x7E0, 0x0D, 1, 0xAA, 0x12, 0x34, 0, 0, 0));

        var response = Assert.Single(sent);
        Assert.Equal(0x7E0U, response.FrameId);
        Assert.Equal(new byte[] { 0x0D, 0x02, 0xBE, 0xDC, 0, 0, 0, 0 }, response.Payload);
        Assert.Contains(logs, line => line.StartsWith("Got challenge"));
    }

    [Fact]
    public void UpdateParameterRuns()
    {
        var (host, _, _) = Create("UpdateParameter.js");

        Assert.Equal(2000, host.TickInterval);
        Assert.Contains("testingVar", host.Parameters);
        Assert.Equal("10", host.GetParameterValue("testingVar"));

        host.Tick();
        Assert.Equal("11", host.GetParameterValue("testingVar"));

        host.UpdateParameter("testingVar", "100");
        host.Tick();
        Assert.Equal("101", host.GetParameterValue("testingVar"));
    }

    [Fact]
    public void ScriptCanSendFdFrame()
    {
        var host = new ScriptHost();
        var sent = new List<CanFrame>();
        host.FrameToSend += (_, f) => sent.Add(f);

        Assert.True(host.Compile(
            "function setup() { can.sendFDFrame(1, 0x123, 10, [1,2,3], true); }"));

        var frame = Assert.Single(sent);
        Assert.Equal(1, frame.Bus);
        Assert.True(frame.IsFlexibleDataRate);
        Assert.True(frame.HasBitrateSwitch);
        Assert.Equal(12, frame.Length); // rounded up to the next valid CAN FD length
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Payload[..3]);
    }

    [Fact]
    public void CompileErrorIsReported()
    {
        var host = new ScriptHost();
        var logs = new List<string>();
        host.LogEmitted += (_, text) => logs.Add(text);

        Assert.False(host.Compile("function setup( {"));
        Assert.Contains("SCRIPT EXCEPTION!", logs);
    }
}
