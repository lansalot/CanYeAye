namespace CanYeAye.Core.Dbc;

/// <summary>How a signal takes part in multiplexing (the four radio buttons in the signal editor).</summary>
public enum DbcMultiplexMode
{
    None,
    Multiplexed,
    Multiplexor,
    Extended,
}

/// <summary>
/// Edit operations behind the DBC editor windows (dbc/dbcmaineditor + the node dialogs). The
/// reference editors mutate the DBC structures in place and mark the file dirty; these helpers do
/// the same, including the re-keying a <see cref="SortedDictionary{TKey,TValue}"/> needs when an
/// ID or a name changes.
/// </summary>
public static class DbcEditor
{
    public const uint MaxMessageId = 0x1FFFFFFF;

    public static DbcNode EnsureDefaultNode(DbcFile file)
    {
        foreach (var node in file.Nodes)
        {
            if (string.Equals(node.Name, DbcFile.DefaultNodeName, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        // SavvyCAN adds this node whenever the tree is built and never writes it to the file, so
        // creating it does not make the file dirty.
        var created = new DbcNode
        {
            Name = DbcFile.DefaultNodeName,
            Comment = "Default node if no other node is specified",
        };
        file.Nodes.Add(created);
        return created;
    }

    public static DbcNode? FindNode(DbcFile file, string name)
    {
        foreach (var node in file.Nodes)
        {
            if (string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>Creates a node, giving it a unique "UnnamedNNNNN" name when none is supplied.</summary>
    public static DbcNode AddNode(DbcFile file, string? name, Random? random = null)
    {
        var nodeName = SanitizeName(name);
        if (nodeName.Length == 0)
        {
            random ??= Random.Shared;
            do
            {
                nodeName = "Unnamed" + random.Next(50000);
            }
            while (FindNode(file, nodeName) is not null);
        }
        else
        {
            var unique = nodeName;
            var suffix = 2;
            while (FindNode(file, unique) is not null)
            {
                unique = nodeName + suffix++;
            }

            nodeName = unique;
        }

        var node = new DbcNode { Name = nodeName };
        file.Nodes.Add(node);
        file.IsDirty = true;
        return node;
    }

    /// <summary>
    /// Deletes a node, its messages and their signals. Receivers pointing at the node fall back
    /// to the default node, as in DBCMainEditor::deleteNode.
    /// </summary>
    public static void DeleteNode(DbcFile file, DbcNode node)
    {
        var fallback = EnsureDefaultNode(file);

        foreach (var message in file.Messages.Values.Where(message => message.Sender == node).ToArray())
        {
            DeleteMessage(file, message);
        }

        foreach (var message in file.Messages.Values)
        {
            foreach (var signal in message.Signals.Values)
            {
                if (signal.Receiver == node)
                {
                    signal.Receiver = fallback;
                }
            }
        }

        file.Nodes.Remove(node);
        file.IsDirty = true;
    }

    /// <summary>Adds a message; refuses an ID that is already present (MessageHandler::addMessage).</summary>
    public static bool AddMessage(DbcFile file, DbcMessage message)
    {
        if (file.Messages.ContainsKey(message.Id))
        {
            return false;
        }

        message.Sender ??= EnsureDefaultNode(file);
        file.Messages[message.Id] = message;
        file.IsDirty = true;
        return true;
    }

    public static void DeleteMessage(DbcFile file, DbcMessage message)
    {
        foreach (var signal in message.Signals.Values.ToArray())
        {
            DeleteSignal(message, signal);
        }

        file.Messages.Remove(message.Id);
        file.IsDirty = true;
    }

    /// <summary>Re-keys the message map after the editor changed the frame ID.</summary>
    public static bool ChangeMessageId(DbcFile file, DbcMessage message, uint newId)
    {
        if (newId > MaxMessageId)
        {
            return false;
        }

        if (newId == message.Id)
        {
            return true;
        }

        if (file.Messages.TryGetValue(newId, out var existing) && existing != message)
        {
            return false;
        }

        file.Messages.Remove(message.Id);
        message.Id = newId;
        if (newId > 0x7FF)
        {
            message.IsExtended = true;
        }

        file.Messages[newId] = message;
        file.IsDirty = true;
        return true;
    }

    public static void AddSignal(DbcMessage message, DbcSignal signal)
    {
        signal.ParentMessage = message;
        signal.Receiver ??= message.Sender;
        message.Signals[signal.Name] = signal;

        if (signal.IsMultiplexor && !signal.IsMultiplexed && message.MultiplexorSignal is null)
        {
            message.MultiplexorSignal = signal;
        }
    }

    /// <summary>Renames a signal and moves it to the right place in the name-keyed signal map.</summary>
    public static bool RenameSignal(DbcMessage message, DbcSignal signal, string newName)
    {
        var name = SanitizeName(newName);
        if (name.Length == 0 || string.Equals(name, signal.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (message.Signals.ContainsKey(name))
        {
            return false;
        }

        message.Signals.Remove(signal.Name);
        signal.Name = name;
        message.Signals[name] = signal;
        return true;
    }

    public static void DeleteSignal(DbcMessage message, DbcSignal signal)
    {
        message.Signals.Remove(signal.Name);

        if (message.MultiplexorSignal == signal)
        {
            message.MultiplexorSignal = null;
        }

        if (signal.MultiplexParent is { } parent)
        {
            parent.MultiplexedChildren.Remove(signal);
        }

        // Children of a deleted multiplexor stay in the message but lose their parent, matching
        // what the reference leaves behind without keeping a dangling pointer.
        foreach (var child in signal.MultiplexedChildren.ToArray())
        {
            child.MultiplexParent = signal.MultiplexParent;
            if (child.MultiplexParent is null)
            {
                child.IsMultiplexed = false;
            }
            else
            {
                child.MultiplexParent.MultiplexedChildren.Add(child);
            }
        }

        signal.MultiplexedChildren.Clear();
    }

    public static void SetMultiplexMode(DbcMessage message, DbcSignal signal, DbcMultiplexMode mode)
    {
        switch (mode)
        {
            case DbcMultiplexMode.None:
                signal.IsMultiplexed = false;
                signal.IsMultiplexor = false;
                SetMultiplexParent(signal, null);
                if (message.MultiplexorSignal == signal)
                {
                    message.MultiplexorSignal = null;
                }

                break;

            case DbcMultiplexMode.Multiplexed:
                signal.IsMultiplexed = true;
                signal.IsMultiplexor = false;
                if (message.MultiplexorSignal == signal)
                {
                    message.MultiplexorSignal = null;
                }

                if (signal.MultiplexParent is null && message.MultiplexorSignal is not null)
                {
                    SetMultiplexParent(signal, message.MultiplexorSignal);
                }

                break;

            case DbcMultiplexMode.Multiplexor:
                signal.IsMultiplexed = false;
                signal.IsMultiplexor = true;
                SetMultiplexParent(signal, null);
                message.MultiplexorSignal = signal;
                break;

            case DbcMultiplexMode.Extended:
                signal.IsMultiplexed = true;
                signal.IsMultiplexor = true;
                if (message.MultiplexorSignal == signal)
                {
                    message.MultiplexorSignal = null;
                }

                break;
        }
    }

    /// <summary>Moves a multiplexed signal under another multiplexor, keeping both child lists in sync.</summary>
    public static bool SetMultiplexParent(DbcSignal signal, DbcSignal? parent)
    {
        if (parent == signal)
        {
            return false;
        }

        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.MultiplexParent)
        {
            if (ancestor == signal)
            {
                return false;
            }
        }

        signal.MultiplexParent?.MultiplexedChildren.Remove(signal);
        signal.MultiplexParent = parent;
        if (parent is not null && !parent.MultiplexedChildren.Contains(signal))
        {
            parent.MultiplexedChildren.Add(signal);
        }

        return true;
    }

    /// <summary>"1;2;5-7" - the signal editor's multiplex value field.</summary>
    public static string FormatMultiplexValues(DbcSignal signal)
    {
        var parts = new List<string>();
        foreach (var range in signal.MultiplexRanges)
        {
            parts.Add(range.Min == range.Max
                ? range.Min.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : range.Min.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
                  range.Max.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join(';', parts);
    }

    /// <summary>Parses the signal editor's "1;2;5-7" text (DBC_SIGNAL::parseDbcMultiplexUiString).</summary>
    public static bool TryParseMultiplexValues(
        string? text,
        out List<(int Min, int Max)> ranges,
        out string error)
    {
        ranges = [];
        error = string.Empty;

        foreach (var raw in (text ?? string.Empty).Split(';'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            var dash = part.IndexOf('-');
            if (dash >= 0)
            {
                if (part.IndexOf('-', dash + 1) >= 0)
                {
                    error = $"The {part} part contains more than one '-' sign";
                    ranges = [];
                    return false;
                }

                var lowText = part[..dash].Trim();
                var highText = part[(dash + 1)..].Trim();
                if (!int.TryParse(lowText, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var low))
                {
                    error = $"Unable to parse the {lowText} part to integer";
                    ranges = [];
                    return false;
                }

                if (!int.TryParse(highText, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var high))
                {
                    error = $"Unable to parse the {highText} part to integer";
                    ranges = [];
                    return false;
                }

                ranges.Add(low <= high ? (low, high) : (high, low));
            }
            else
            {
                if (!int.TryParse(part, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    error = $"Unable to parse the {part} part to integer";
                    ranges = [];
                    return false;
                }

                ranges.Add((value, value));
            }
        }

        return true;
    }

    public static void SetMultiplexRanges(DbcSignal signal, IEnumerable<(int Min, int Max)> ranges)
    {
        signal.MultiplexRanges.Clear();
        signal.HasExtendedMultiplexing = false;
        foreach (var (min, max) in ranges)
        {
            signal.AddMultiplexRange(min, max);
        }
    }

    /// <summary>
    /// The bit positions a signal occupies, in read order, using the same sawtooth as
    /// <see cref="SignalCodec"/>. The signal editor grid uses this for its occupancy overlay.
    /// </summary>
    public static IReadOnlyList<int> BitPositions(int startBit, int size, bool intelByteOrder)
    {
        var positions = new List<int>(Math.Max(size, 0));
        var bit = startBit;
        for (var index = 0; index < size; index++)
        {
            positions.Add(bit);
            bit += intelByteOrder ? 1 : ((bit % 8) == 0 ? 15 : -1);
        }

        return positions;
    }

    /// <summary>Motorola signals are addressed by their most significant bit; this is its index.</summary>
    public static int MotorolaMostSignificantBit(int startBit, int size)
    {
        var bit = startBit;
        for (var index = 1; index < size; index++)
        {
            bit += (bit % 8) == 0 ? 15 : -1;
        }

        return bit;
    }

    public static bool SignalsShareMultiplex(DbcSignal signal, DbcSignal other) =>
        signal.MultiplexParent == other.MultiplexParent &&
        signal.MultiplexRanges.Count == other.MultiplexRanges.Count &&
        signal.MultiplexRanges.All(range => other.MultiplexRanges.Contains(range));

    /// <summary>Node rebase: shift every message of a node by an offset so the lowest ID becomes <paramref name="newBase"/>.</summary>
    public static string? RebaseNode(DbcFile file, DbcNode node, uint newBase)
    {
        var messages = file.Messages.Values.Where(message => message.Sender == node).ToArray();
        if (messages.Length == 0)
        {
            return "The node has no messages to change.";
        }

        var lowest = messages.Min(message => message.Id);
        if (newBase == 0 || newBase > MaxMessageId)
        {
            return "The new address is outside of the valid range.";
        }

        if (newBase == lowest)
        {
            return "The new address is the same as the original.";
        }

        var difference = (long)newBase - lowest;
        var moving = new HashSet<DbcMessage>(messages);
        foreach (var message in messages)
        {
            var newId = (long)message.Id + difference;
            if (newId < 0 || newId > MaxMessageId)
            {
                return "The new starting address would cause a message to be outside of the valid address range.";
            }

            if (file.Messages.TryGetValue((uint)newId, out var existing) && !moving.Contains(existing))
            {
                return $"Message 0x{newId:X} already exists, so the range cannot be rebased there.";
            }
        }

        foreach (var message in messages)
        {
            ChangeMessageId(file, message, (uint)((long)message.Id + difference));
        }

        file.IsDirty = true;
        return null;
    }

    /// <summary>
    /// Duplicates a node and its messages at a new base ID (DBCNodeDuplicateEditor). Unlike the
    /// reference, signal comments, units, value descriptions and the multiplex hierarchy are
    /// cloned too.
    /// </summary>
    public static string? DuplicateNode(
        DbcFile file,
        DbcNode source,
        string newNodeName,
        uint newBase,
        out DbcNode? created)
    {
        created = null;

        var messages = file.Messages.Values.Where(message => message.Sender == source).ToArray();
        if (messages.Length == 0)
        {
            return "The node has no messages to duplicate.";
        }

        var name = SanitizeName(newNodeName);
        if (name.Length == 0)
        {
            return "The new node needs a name before it can be created.";
        }

        if (FindNode(file, name) is not null)
        {
            return $"A node named {name} already exists.";
        }

        var lowest = messages.Min(message => message.Id);
        if (newBase == 0 || newBase > MaxMessageId)
        {
            return "The new address is outside of the valid range.";
        }

        if (newBase == lowest)
        {
            return "The new address is the same as the original.";
        }

        var difference = (long)newBase - lowest;
        var newIds = new Dictionary<DbcMessage, uint>();
        foreach (var message in messages)
        {
            var newId = (long)message.Id + difference;
            if (newId < 0 || newId > MaxMessageId)
            {
                return "The new starting address would cause a message to be outside of the valid address range.";
            }

            if (file.Messages.ContainsKey((uint)newId))
            {
                return $"Message 0x{newId:X} already exists, so the node cannot be duplicated there.";
            }

            newIds[message] = (uint)newId;
        }

        var node = new DbcNode
        {
            Name = name,
            Comment = source.Comment,
            SourceFileName = source.SourceFileName,
        };
        foreach (var attribute in source.Attributes)
        {
            node.Attributes.Add(new DbcAttributeValue { Name = attribute.Name, Value = attribute.Value });
        }

        file.Nodes.Add(node);

        foreach (var message in messages)
        {
            // The clone map lets CloneMessage rebuild the multiplex parent / child links inside
            // the copied message without pointing back at the originals.
            var copy = CloneMessage(message, node, null, [], newIds[message]);
            file.Messages[copy.Id] = copy;
        }

        file.IsDirty = true;
        created = node;
        return null;
    }

    /// <summary>
    /// Copies a message and its signals. When <paramref name="signalMap"/> is supplied the clones
    /// are recorded there so callers can rebuild multiplex parent links afterwards.
    /// </summary>
    public static DbcMessage CloneMessage(
        DbcMessage source,
        DbcNode sender,
        DbcNode? receiver,
        Dictionary<string, DbcSignal>? signalMap,
        uint? newId = null)
    {
        var copy = new DbcMessage
        {
            Id = newId ?? source.Id,
            IsExtended = source.IsExtended,
            Name = source.Name,
            Comment = source.Comment,
            Length = source.Length,
            IsCanFd = source.IsCanFd,
            BitRateSwitch = source.BitRateSwitch,
            BackgroundColor = source.BackgroundColor,
            ForegroundColor = source.ForegroundColor,
            Sender = sender,
        };

        foreach (var attribute in source.Attributes)
        {
            copy.Attributes.Add(new DbcAttributeValue { Name = attribute.Name, Value = attribute.Value });
        }

        foreach (var signal in source.Signals.Values)
        {
            var clone = new DbcSignal
            {
                Name = signal.Name,
                StartBit = signal.StartBit,
                SignalSize = signal.SignalSize,
                IntelByteOrder = signal.IntelByteOrder,
                IsMultiplexor = signal.IsMultiplexor,
                IsMultiplexed = signal.IsMultiplexed,
                HasExtendedMultiplexing = signal.HasExtendedMultiplexing,
                ValueType = signal.ValueType,
                Factor = signal.Factor,
                Bias = signal.Bias,
                Min = signal.Min,
                Max = signal.Max,
                UnitName = signal.UnitName,
                Comment = signal.Comment,
                Receiver = receiver ?? signal.Receiver,
                ParentMessage = copy,
                FactorText = signal.FactorText,
                BiasText = signal.BiasText,
                MinText = signal.MinText,
                MaxText = signal.MaxText,
            };

            foreach (var range in signal.MultiplexRanges)
            {
                clone.MultiplexRanges.Add(range);
            }

            foreach (var value in signal.ValueDescriptions)
            {
                clone.ValueDescriptions.Add(value);
            }

            foreach (var attribute in signal.Attributes)
            {
                clone.Attributes.Add(new DbcAttributeValue { Name = attribute.Name, Value = attribute.Value });
            }

            copy.Signals[clone.Name] = clone;
            signalMap?[clone.Name] = clone;
        }

        foreach (var signal in source.Signals.Values)
        {
            if (signal.MultiplexParent is not null &&
                signalMap is not null &&
                signalMap.TryGetValue(signal.MultiplexParent.Name, out var parent))
            {
                parent.MultiplexedChildren.Add(signalMap[signal.Name]);
                signalMap[signal.Name].MultiplexParent = parent;
            }
        }

        if (source.MultiplexorSignal is not null && signalMap is not null &&
            signalMap.TryGetValue(source.MultiplexorSignal.Name, out var multiplexor))
        {
            copy.MultiplexorSignal = multiplexor;
        }

        return copy;
    }

    /// <summary>Copies a signal (all editable fields, multiplex ranges and value table) into a message.</summary>
    public static DbcSignal CloneSignal(DbcSignal source, string name, DbcMessage parent)
    {
        var clone = new DbcSignal
        {
            Name = name,
            StartBit = source.StartBit,
            SignalSize = source.SignalSize,
            IntelByteOrder = source.IntelByteOrder,
            IsMultiplexor = source.IsMultiplexor,
            IsMultiplexed = source.IsMultiplexed,
            HasExtendedMultiplexing = source.HasExtendedMultiplexing,
            ValueType = source.ValueType,
            Factor = source.Factor,
            Bias = source.Bias,
            Min = source.Min,
            Max = source.Max,
            UnitName = source.UnitName,
            Comment = source.Comment,
            Receiver = source.Receiver ?? parent.Sender,
            ParentMessage = parent,
            FactorText = source.FactorText,
            BiasText = source.BiasText,
            MinText = source.MinText,
            MaxText = source.MaxText,
        };

        foreach (var range in source.MultiplexRanges)
        {
            clone.MultiplexRanges.Add(range);
        }

        foreach (var value in source.ValueDescriptions)
        {
            clone.ValueDescriptions.Add(value);
        }

        foreach (var attribute in source.Attributes)
        {
            clone.Attributes.Add(new DbcAttributeValue { Name = attribute.Name, Value = attribute.Value });
        }

        clone.MultiplexParent = source.MultiplexParent;
        return clone;
    }

    /// <summary>First free ID at or above <paramref name="start"/>, skipping IDs already in the file.</summary>
    public static uint NextFreeMessageId(DbcFile file, uint start)
    {
        var id = start;
        while (id <= MaxMessageId && file.Messages.ContainsKey(id))
        {
            id++;
        }

        return id > MaxMessageId ? MaxMessageId : id;
    }

    public static string SanitizeName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var simplified = string.Join('_', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return simplified.Replace(' ', '_');
    }
}
