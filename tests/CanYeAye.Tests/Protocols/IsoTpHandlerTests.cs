using CanYeAye.Core;
using CanYeAye.Core.Protocols;

namespace CanYeAye.Tests.Protocols;

public class IsoTpHandlerTests
{
    private static CanFrame Frame(uint id, params byte[] payload)
    {
        var frame = new CanFrame();
        frame.SetFrameId(id);
        frame.SetFrameType(FrameType.CanDataFrame);
        frame.SetPayload(payload);
        return frame;
    }

    private static IsoTpHandler CreateHandler()
    {
        var handler = new IsoTpHandler();
        handler.SetReception(true);
        handler.ProcessAll = true;
        return handler;
    }

    private static byte[] Pad(params byte[] data)
    {
        var payload = new byte[8];
        data.CopyTo(payload, 0);
        return payload;
    }

    [Fact]
    public void ReassemblesSingleFrame()
    {
        var handler = CreateHandler();
        var messages = new List<CanFrame>();
        handler.MessageReceived += (_, f) => messages.Add(f);

        handler.ProcessFrame(Frame(0x7E0, Pad(0x03, 0x11, 0x22, 0x33)));

        var message = Assert.Single(messages);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, message.Payload);
        Assert.Equal(0x7E0U, message.FrameId);
    }

    [Fact]
    public void ReassemblesMultiframe()
    {
        var handler = CreateHandler();
        var messages = new List<CanFrame>();
        handler.MessageReceived += (_, f) => messages.Add(f);

        // 10 bytes: first frame carries 6, consecutive frame carries 4.
        handler.ProcessFrame(Frame(0x7E0, Pad(0x10, 0x0A, 1, 2, 3, 4, 5, 6)));
        Assert.Empty(messages);

        handler.ProcessFrame(Frame(0x7E0, Pad(0x21, 7, 8, 9, 10)));

        var message = Assert.Single(messages);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, message.Payload);
    }

    [Fact]
    public void FlowControlAnswerIsSentForNewMultiframe()
    {
        var handler = CreateHandler();
        handler.SetFlowControl(true);
        var sent = new List<CanFrame>();
        handler.FrameToSend += (_, f) => sent.Add(f);

        handler.SendIsoTp(0, 0x7E0, new byte[] { 1, 2, 3 });
        sent.Clear();

        handler.ProcessFrame(Frame(0x7E0, Pad(0x10, 0x0A, 1, 2, 3, 4, 5, 6)));

        var flow = Assert.Single(sent);
        Assert.Equal(0x7E0U, flow.FrameId);
        Assert.Equal(0x30, flow.Payload[0]);
    }

    [Fact]
    public void SendsSingleFrameWithPadding()
    {
        var handler = CreateHandler();
        handler.PaddingByte = 0xAA;
        var sent = new List<CanFrame>();
        handler.FrameToSend += (_, f) => sent.Add(f);

        handler.SendIsoTp(0, 0x123, new byte[] { 0x11, 0x22 });

        var frame = Assert.Single(sent);
        Assert.Equal(0x123U, frame.FrameId);
        Assert.Equal(new byte[] { 0x02, 0x11, 0x22, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA }, frame.Payload);
    }

    [Fact]
    public void SendsMultiframeWhenFlowControlDisabled()
    {
        var handler = CreateHandler();
        var sent = new List<CanFrame>();
        handler.FrameToSend += (_, f) => sent.Add(f);

        handler.SendIsoTp(0, 0x123, Enumerable.Range(1, 10).Select(v => (byte)v).ToArray());

        Assert.Single(sent); // only the first frame until a flow-control frame arrives
        Assert.Equal(0x10, sent[0].Payload[0]);
        Assert.True(handler.HasPendingTransmit);

        // A "continue to send, no more FC" flow-control frame unblocks the queue.
        handler.ProcessFrame(Frame(0x123, Pad(0x30, 0x00, 0x00)));
        handler.Tick();
        Assert.Equal(2, sent.Count);
        Assert.Equal(0x21, sent[1].Payload[0]);
    }

    [Fact]
    public void FiltersRejectOtherBusesAndIds()
    {
        var handler = new IsoTpHandler();
        handler.SetReception(true);
        handler.AddFilter(1, 0x7E0, 0x7FF);

        var messages = new List<CanFrame>();
        handler.MessageReceived += (_, f) => messages.Add(f);

        var wrongBus = Frame(0x7E0, Pad(0x03, 1, 2, 3));
        wrongBus.Bus = 0;
        handler.ProcessFrame(wrongBus);

        var rightBus = Frame(0x7E0, Pad(0x03, 1, 2, 3));
        rightBus.Bus = 1;
        handler.ProcessFrame(rightBus);

        Assert.Single(messages);
    }

    [Fact]
    public void UdsSendBuildsServiceSubFunctionAndData()
    {
        var uds = new UdsHandler();
        var sent = new List<CanFrame>();
        uds.FrameToSend += (_, f) => sent.Add(f);

        uds.SendUds(0, 0x7E0, 0x10, 1, 0x03, new byte[] { 0xAA, 0xBB });

        var frame = Assert.Single(sent);
        Assert.Equal(new byte[] { 0x04, 0x10, 0x03, 0xAA, 0xBB }, frame.Payload[..5]);
    }

    [Fact]
    public void UdsReassemblesPositiveResponse()
    {
        var uds = new UdsHandler();
        uds.IsoTp.ProcessAll = true;
        var messages = new List<UdsMessage>();
        uds.MessageReceived += (_, m) => messages.Add(m);

        uds.ProcessFrame(Frame(0x7E8, Pad(0x03, 0x50, 0x03, 0x00)));

        var message = Assert.Single(messages);
        Assert.Equal(0x50, message.Service);
        Assert.Equal(0x03, message.SubFunc);
        Assert.False(message.IsErrorReply);
    }

    [Fact]
    public void UdsNegativeResponseReportsOriginalService()
    {
        var uds = new UdsHandler();
        uds.IsoTp.ProcessAll = true;
        var messages = new List<UdsMessage>();
        uds.MessageReceived += (_, m) => messages.Add(m);

        uds.ProcessFrame(Frame(0x7E8, Pad(0x03, 0x7F, 0x22, 0x31)));

        var message = Assert.Single(messages);
        Assert.True(message.IsErrorReply);
        Assert.Equal(0x22, message.Service);
        Assert.Equal(0x31, message.SubFunc);
    }
}
