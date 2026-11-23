using CanYeAye.Core.Dbc;

namespace CanYeAye.Tests.Dbc;

/// <summary>
/// The editing operations behind the DBC editor windows (DBCEditor).
/// </summary>
public class DbcEditorTests
{
    private static DbcFile NewFile()
    {
        var file = new DbcFile();
        DbcEditor.EnsureDefaultNode(file);
        return file;
    }

    private static DbcMessage NewMessage(DbcFile file, DbcNode sender, uint id, string name, uint length = 8)
    {
        var message = new DbcMessage { Id = id, Name = name, Sender = sender, Length = length };
        Assert.True(DbcEditor.AddMessage(file, message));
        return message;
    }

    private static DbcSignal NewSignal(DbcMessage message, string name, int startBit = 0, int size = 8)
    {
        var signal = new DbcSignal
        {
            Name = name,
            StartBit = startBit,
            SignalSize = size,
            ParentMessage = message,
            Receiver = message.Sender,
            IntelByteOrder = true,
        };
        DbcEditor.AddSignal(message, signal);
        return signal;
    }

    [Fact]
    public void AddNodeMakesTheNameUniqueAndSanitizesSpaces()
    {
        var file = NewFile();

        var first = DbcEditor.AddNode(file, "My Node");
        var second = DbcEditor.AddNode(file, "My Node");

        Assert.Equal("My_Node", first.Name);
        Assert.Equal("My_Node2", second.Name);
        Assert.Contains(first, file.Nodes);
        Assert.True(file.IsDirty);
    }

    [Fact]
    public void AddNodeUsesAnUnnamedDefaultWhenNoNameIsGiven()
    {
        var file = NewFile();

        var node = DbcEditor.AddNode(file, null, new Random(1));

        Assert.StartsWith("Unnamed", node.Name);
    }

    [Fact]
    public void DeleteNodeRemovesItsMessagesAndRepointsReceivers()
    {
        var file = NewFile();
        var doomed = DbcEditor.AddNode(file, "Doomed");
        var keeper = DbcEditor.AddNode(file, "Keeper");

        var doomedMessage = NewMessage(file, doomed, 0x100, "GONE");
        NewSignal(doomedMessage, "A");

        var survivor = NewMessage(file, keeper, 0x200, "STAYS");
        var receiver = NewSignal(survivor, "B");
        receiver.Receiver = doomed;

        DbcEditor.DeleteNode(file, doomed);

        Assert.DoesNotContain(doomed, file.Nodes);
        Assert.False(file.Messages.ContainsKey(0x100));
        Assert.True(file.Messages.ContainsKey(0x200));
        Assert.Same(file.Nodes[0], receiver.Receiver);
    }

    [Fact]
    public void ChangeMessageIdReKeysTheMessageMap()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        var message = NewMessage(file, node, 0x100, "M");

        Assert.True(DbcEditor.ChangeMessageId(file, message, 0x1ABCDE));
        Assert.False(file.Messages.ContainsKey(0x100));
        Assert.True(file.Messages.TryGetValue(0x1ABCDE, out var moved));
        Assert.Same(message, moved);
        Assert.True(message.IsExtended);
    }

    [Fact]
    public void ChangeMessageIdRefusesACollision()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        var first = NewMessage(file, node, 0x100, "FIRST");
        NewMessage(file, node, 0x200, "SECOND");

        Assert.False(DbcEditor.ChangeMessageId(file, first, 0x200));
        Assert.True(file.Messages.ContainsKey(0x100));
    }

    [Fact]
    public void AddMessageRefusesADuplicateId()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        NewMessage(file, node, 0x100, "FIRST");

        var duplicate = new DbcMessage { Id = 0x100, Name = "SECOND", Sender = node, Length = 8 };
        Assert.False(DbcEditor.AddMessage(file, duplicate));
        Assert.Equal("FIRST", file.Messages[0x100].Name);
    }

    [Fact]
    public void RenameSignalMovesTheSortedMapEntry()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        var message = NewMessage(file, node, 0x100, "M");
        var signal = NewSignal(message, "OLD");

        Assert.True(DbcEditor.RenameSignal(message, signal, "NEW NAME"));

        Assert.False(message.Signals.ContainsKey("OLD"));
        Assert.True(message.Signals.TryGetValue("NEW_NAME", out var renamed));
        Assert.Same(signal, renamed);
        Assert.Equal("NEW_NAME", signal.Name);
    }

    [Fact]
    public void RenameSignalRefusesAnExistingName()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        var message = NewMessage(file, node, 0x100, "M");
        var first = NewSignal(message, "FIRST");
        NewSignal(message, "SECOND");

        Assert.False(DbcEditor.RenameSignal(message, first, "SECOND"));
    }

    [Fact]
    public void DeleteSignalDetachesItFromItsMultiplexParent()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        var message = NewMessage(file, node, 0x100, "M");
        var multiplexor = NewSignal(message, "MUX");
        var child = NewSignal(message, "CHILD", size: 4);

        DbcEditor.SetMultiplexMode(message, multiplexor, DbcMultiplexMode.Multiplexor);
        DbcEditor.SetMultiplexMode(message, child, DbcMultiplexMode.Multiplexed);
        DbcEditor.SetMultiplexRanges(child, [(1, 1)]);

        Assert.Same(multiplexor, child.MultiplexParent);
        Assert.Contains(child, multiplexor.MultiplexedChildren);

        DbcEditor.DeleteSignal(message, child);

        Assert.Empty(multiplexor.MultiplexedChildren);
        Assert.False(message.Signals.ContainsKey("CHILD"));
    }

    [Fact]
    public void MultiplexModesKeepTheMessageMultiplexorInSync()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        var message = NewMessage(file, node, 0x100, "M");
        var signal = NewSignal(message, "SIG");

        DbcEditor.SetMultiplexMode(message, signal, DbcMultiplexMode.Multiplexor);
        Assert.True(signal.IsMultiplexor);
        Assert.False(signal.IsMultiplexed);
        Assert.Same(signal, message.MultiplexorSignal);

        DbcEditor.SetMultiplexMode(message, signal, DbcMultiplexMode.Extended);
        Assert.True(signal.IsMultiplexor);
        Assert.True(signal.IsMultiplexed);
        Assert.Null(message.MultiplexorSignal);

        DbcEditor.SetMultiplexMode(message, signal, DbcMultiplexMode.None);
        Assert.False(signal.IsMultiplexor);
        Assert.False(signal.IsMultiplexed);
        Assert.Null(message.MultiplexorSignal);
    }

    [Fact]
    public void SwitchingToMultiplexedAdoptsTheMessageMultiplexor()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        var message = NewMessage(file, node, 0x100, "M");
        var multiplexor = NewSignal(message, "MUX");
        var child = NewSignal(message, "CHILD");

        DbcEditor.SetMultiplexMode(message, multiplexor, DbcMultiplexMode.Multiplexor);
        DbcEditor.SetMultiplexMode(message, child, DbcMultiplexMode.Multiplexed);

        Assert.Same(multiplexor, child.MultiplexParent);
        Assert.Contains(child, multiplexor.MultiplexedChildren);
    }

    [Fact]
    public void SetMultiplexParentRefusesCycles()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        var message = NewMessage(file, node, 0x100, "M");
        var parent = NewSignal(message, "PARENT");
        var child = NewSignal(message, "CHILD");

        Assert.True(DbcEditor.SetMultiplexParent(child, parent));
        Assert.False(DbcEditor.SetMultiplexParent(parent, child));
        Assert.False(DbcEditor.SetMultiplexParent(parent, parent));
    }

    [Theory]
    [InlineData("1;2;5-7", new[] { 1, 1, 2, 2, 5, 7 })]
    [InlineData(" 3 ", new[] { 3, 3 })]
    [InlineData("", new int[0])]
    public void MultiplexValuesRoundTrip(string text, int[] expected)
    {
        Assert.True(DbcEditor.TryParseMultiplexValues(text, out var ranges, out var error), error);

        var signal = new DbcSignal();
        DbcEditor.SetMultiplexRanges(signal, ranges);

        Assert.Equal(expected, ranges.SelectMany(range => new[] { range.Min, range.Max }).ToArray());
        Assert.Equal(text.Trim(), DbcEditor.FormatMultiplexValues(signal));
    }

    [Theory]
    [InlineData("1-2-3")]
    [InlineData("a")]
    [InlineData("1-")]
    public void MultiplexValuesReportErrors(string text)
    {
        Assert.False(DbcEditor.TryParseMultiplexValues(text, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void MultiplexRangesFlagExtendedMultiplexing()
    {
        var signal = new DbcSignal();

        DbcEditor.SetMultiplexRanges(signal, [(1, 1), (3, 4)]);

        Assert.True(signal.IsMultiplexed);
        Assert.True(signal.HasExtendedMultiplexing);
        Assert.Equal("1;3-4", DbcEditor.FormatMultiplexValues(signal));
    }

    [Fact]
    public void RebaseNodeShiftsEveryMessageOfTheNode()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        var first = NewMessage(file, node, 0x180, "FIRST");
        var second = NewMessage(file, node, 0x1A0, "SECOND");

        var error = DbcEditor.RebaseNode(file, node, 0x280);

        Assert.Null(error);
        Assert.Equal(0x280u, first.Id);
        Assert.Equal(0x2A0u, second.Id);
        Assert.False(file.Messages.ContainsKey(0x180));
        Assert.True(file.Messages.ContainsKey(0x280));
    }

    [Fact]
    public void RebaseNodeValidatesTheNewBase()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        NewMessage(file, node, 0x180, "FIRST");

        Assert.Equal("The new address is the same as the original.", DbcEditor.RebaseNode(file, node, 0x180));
        Assert.Equal("The new address is outside of the valid range.", DbcEditor.RebaseNode(file, node, 0));
        Assert.NotNull(DbcEditor.RebaseNode(file, node, uint.MaxValue));
        Assert.Equal("The node has no messages to change.", DbcEditor.RebaseNode(file, DbcEditor.AddNode(file, "Empty"), 0x300));
    }

    [Fact]
    public void RebaseNodeRefusesACollisionWithAnotherNode()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "N");
        var other = DbcEditor.AddNode(file, "O");
        var message = NewMessage(file, node, 0x180, "FIRST");
        NewMessage(file, other, 0x200, "OTHER");

        var error = DbcEditor.RebaseNode(file, node, 0x200);

        Assert.NotNull(error);
        Assert.Equal(0x180u, message.Id);
    }

    [Fact]
    public void DuplicateNodeCopiesMessagesSignalsAndMultiplexing()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "Source");
        var message = NewMessage(file, node, 0x180, "MESSAGE", length: 8);
        var multiplexor = NewSignal(message, "MUX", 0, 8);
        var child = NewSignal(message, "CHILD", 8, 8);
        child.UnitName = "km/h";
        child.Comment = "speed";
        child.ValueDescriptions.Add(new DbcValueEntry(1, "one"));
        DbcEditor.SetMultiplexMode(message, multiplexor, DbcMultiplexMode.Multiplexor);
        DbcEditor.SetMultiplexMode(message, child, DbcMultiplexMode.Multiplexed);
        DbcEditor.SetMultiplexRanges(child, [(1, 3)]);

        var error = DbcEditor.DuplicateNode(file, node, "Copy", 0x280, out var copy);

        Assert.Null(error);
        Assert.NotNull(copy);
        Assert.Contains(copy!, file.Nodes);
        Assert.True(file.Messages.TryGetValue(0x280, out var copiedMessage));
        Assert.Same(copy, copiedMessage.Sender);
        Assert.Equal("MESSAGE", copiedMessage.Name);
        Assert.Equal(2, copiedMessage.Signals.Count);

        var copiedChild = copiedMessage.Signals["CHILD"];
        Assert.Equal("km/h", copiedChild.UnitName);
        Assert.Equal("speed", copiedChild.Comment);
        Assert.Equal([new DbcValueEntry(1, "one")], copiedChild.ValueDescriptions);
        Assert.Same(copiedMessage.Signals["MUX"], copiedChild.MultiplexParent);
        Assert.Same(copiedMessage.Signals["MUX"], copiedMessage.MultiplexorSignal);
    }

    [Fact]
    public void DuplicateNodeRefusesACollision()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "Source");
        var other = DbcEditor.AddNode(file, "Other");
        NewMessage(file, node, 0x180, "MESSAGE");
        NewMessage(file, other, 0x280, "OTHER");

        var error = DbcEditor.DuplicateNode(file, node, "Copy", 0x280, out var copy);

        Assert.NotNull(error);
        Assert.Null(copy);
        Assert.Equal(["Vector__XXX", "Source", "Other"], file.Nodes.Select(node => node.Name).ToArray());
    }

    [Fact]
    public void BitPositionsFollowTheSawtooth()
    {
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], DbcEditor.BitPositions(0, 8, intelByteOrder: true));
        Assert.Equal([12, 11, 10, 9, 8, 23, 22, 21], DbcEditor.BitPositions(12, 8, intelByteOrder: false));
    }

    [Fact]
    public void EditedFileSerializesAndReadsBack()
    {
        var file = NewFile();
        var node = DbcEditor.AddNode(file, "ECU");
        var message = NewMessage(file, node, 0x123, "STATUS", length: 8);
        message.Comment = "Edited";
        var signal = NewSignal(message, "SPEED", 0, 16);
        signal.UnitName = "km/h";
        signal.ValueDescriptions.Add(new DbcValueEntry(0, "Idle"));

        var text = DbcFileWriter.Serialize(file);
        var readBack = DbcFileReader.Parse(text, out var faults);

        Assert.Empty(faults);
        Assert.True(readBack.Messages.TryGetValue(0x123, out var parsed));
        Assert.Equal("STATUS", parsed.Name);
        Assert.Equal("Edited", parsed.Comment);
        Assert.True(parsed.Signals.TryGetValue("SPEED", out var parsedSignal));
        Assert.Equal(16, parsedSignal.SignalSize);
        Assert.Equal("km/h", parsedSignal.UnitName);
        Assert.Contains(new DbcValueEntry(0, "Idle"), parsedSignal.ValueDescriptions);
    }
}
