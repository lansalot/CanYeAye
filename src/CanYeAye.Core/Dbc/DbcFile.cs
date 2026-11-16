namespace CanYeAye.Core.Dbc;

/// <summary>
/// A parsed DBC database (DBCFile). Messages iterate by ascending ID and signals by ascending
/// ordinal name (both sorted maps in the reference), so save order and lookup order are stable.
/// </summary>
public sealed class DbcFile
{
    public const string DefaultNodeName = "Vector__XXX";

    public string FileName { get; set; } = "<Unsaved File>";

    public string FilePath { get; set; } = string.Empty;

    /// <summary>-1 = all buses, otherwise a concrete bus index.</summary>
    public int AssociatedBus { get; set; } = -1;

    public bool IsDirty { get; set; }

    public List<DbcNode> Nodes { get; } = [];

    public List<DbcValueTable> ValueTables { get; } = [];

    public List<DbcAttribute> Attributes { get; } = [];

    /// <summary>Messages ascending numeric ID; duplicate IDs overwrite (last wins).</summary>
    public SortedDictionary<uint, DbcMessage> Messages { get; } = [];

    public MatchingCriteria MatchingCriteria { get; set; } = MatchingCriteria.Exact;

    public bool FilterLabeling { get; set; }

    /// <summary>Statements the reader does not model, preserved for a lossless save.</summary>
    public List<(string Statement, string Line)> UnparsedStatements { get; } = [];

    public DbcNode DefaultNode => Nodes.Count > 0 ? Nodes[0] : null!;

    public DbcNode FindNode(string name)
    {
        foreach (var node in Nodes)
        {
            if (string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return DefaultNode;
    }

    public DbcAttribute? FindAttribute(string name, DbcAttributeTarget? target = null)
    {
        foreach (var attribute in Attributes)
        {
            if (!string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (target is null || attribute.Target == target)
            {
                return attribute;
            }
        }

        return null;
    }

    public DbcValueTable? FindValueTable(string name)
    {
        foreach (var table in ValueTables)
        {
            if (string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }
        }

        return null;
    }

    /// <summary>
    /// Exact ID lookup first, then J1939 / GMLAN pseudo-matching against ascending IDs
    /// (MessageHandler::findMsgByID).
    /// </summary>
    public DbcMessage? FindMessageById(uint id)
    {
        if (Messages.TryGetValue(id, out var exact))
        {
            return exact;
        }

        if (MatchingCriteria == MatchingCriteria.Exact)
        {
            return null;
        }

        var pgn = (id & 0x3FFFF00u) >> 8;
        var arbId = id & 0x3FFE000u;
        foreach (var pair in Messages)
        {
            var key = pair.Key;
            if (MatchingCriteria == MatchingCriteria.J1939)
            {
                if ((pgn & 0xFF00) <= 0xEF00)
                {
                    // PDU1: match destination-less PGN over the source-address bits.
                    var masked = pgn & 0x3FF00;
                    if ((key & 0x3FF0000u) == (masked << 8))
                    {
                        return pair.Value;
                    }
                }
                else if ((key & 0x3FFFF00u) == (pgn << 8))
                {
                    return pair.Value;
                }
            }
            else if (arbId != 0 && (key & 0x3FFE000u) == arbId)
            {
                return pair.Value;
            }
        }

        return null;
    }

    public DbcMessage? FindMessageByName(string name)
    {
        foreach (var message in Messages.Values)
        {
            if (string.Equals(message.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return message;
            }
        }

        return null;
    }
}
