using System.Globalization;
using System.Text.RegularExpressions;

namespace WinCAN.Core.Dbc;

public sealed record DbcParseFault(int LineNumber, string Message);

/// <summary>
/// Ports DBCFile::loadFile. Line-by-line, whitespace-simplified, order-sensitive dispatch. The
/// accepted grammar is the exact set SavvyCAN accepts; the same statements are silently ignored
/// (see the research doc), but are preserved in <see cref="DbcFile.UnparsedStatements"/> for a
/// lossless save.
/// </summary>
public static partial class DbcFileReader
{
    private sealed record SignalShape(
        string Name,
        int StartBit,
        int Size,
        int ByteOrder,
        string Sign,
        string Factor,
        string Bias,
        string Min,
        string Max,
        string Unit,
        string Receivers);

    private sealed record SignalMatch(Regex Regex, bool IsMultiplexor, bool IsMultiplexed, bool HasMuxValue);

    private static readonly SignalMatch[] SignalVariants =
    [
        new(SignalMultiplexorRegex(), IsMultiplexor: true, IsMultiplexed: false, HasMuxValue: false),
        new(SignalMultiplexedRegex(), IsMultiplexor: false, IsMultiplexed: true, HasMuxValue: true),
        new(SignalExtendedMuxRegex(), IsMultiplexor: true, IsMultiplexed: true, HasMuxValue: true),
        new(SignalPlainRegex(), IsMultiplexor: false, IsMultiplexed: false, HasMuxValue: false),
    ];

    public static DbcFile Load(string path, out IReadOnlyList<DbcParseFault> faults)
    {
        var text = File.ReadAllText(path);
        var file = Parse(text, out faults);
        file.FilePath = path;
        file.FileName = Path.GetFileName(path);
        foreach (var node in file.Nodes)
        {
            node.SourceFileName = Path.GetFileNameWithoutExtension(path);
        }

        file.IsDirty = false;
        return file;
    }

    public static DbcFile Parse(string text, out IReadOnlyList<DbcParseFault> faults)
    {
        var rawLines = text.Split('\n');
        var faultList = new List<DbcParseFault>();
        faults = faultList;

        var file = new DbcFile();
        file.Nodes.Add(new DbcNode
        {
            Name = DbcFile.DefaultNodeName,
            Comment = "Default node if none specified",
        });

        var currentMessage = (DbcMessage?)null;
        var inMultilineBu = false;
        var inMultilineValueTable = false;
        var multilineValueTable = string.Empty;

        for (var index = 0; index < rawLines.Length; index++)
        {
            var raw = rawLines[index].TrimEnd('\r');
            var lineNumber = index + 1;

            var simplified = Simplify(raw);

            if (inMultilineValueTable)
            {
                multilineValueTable += " " + simplified;
                if (simplified.EndsWith(';'))
                {
                    ParseValueTableLine(file, Simplify(multilineValueTable));
                    inMultilineValueTable = false;
                    multilineValueTable = string.Empty;
                }

                continue;
            }

            if (inMultilineBu)
            {
                if (raw.StartsWith('\t') || raw.StartsWith("   "))
                {
                    AppendContinuationNodes(file, simplified);
                    continue;
                }

                inMultilineBu = false;
            }

            if (StartsWithWord(simplified, "BO_ "))
            {
                if (TryParseMessage(file, simplified, out var message))
                {
                    file.Messages[message.Id] = message;
                    currentMessage = message;
                }
                else
                {
                    faultList.Add(new DbcParseFault(lineNumber, simplified));
                }
            }
            else if (StartsWithWord(simplified, "SG_ "))
            {
                if (currentMessage is not null && TryParseSignal(simplified, currentMessage, file))
                {
                    // ok
                }
                else
                {
                    faultList.Add(new DbcParseFault(lineNumber, simplified));
                }
            }
            else if (StartsWithWord(simplified, "SG_MUL_VAL_ "))
            {
                ParseExtendedMultiplex(file, simplified, lineNumber, faultList);
            }
            else if (StartsWithWord(simplified, "SIG_VALTYPE_ "))
            {
                ParseSignalValueType(file, simplified);
            }
            else if (StartsWithWord(simplified, "BU_:"))
            {
                ParseNodeList(file, simplified);
                inMultilineBu = true;
            }
            else if (StartsWithWord(simplified, "CM_ SG_ "))
            {
                ParseSignalComment(file, simplified);
            }
            else if (StartsWithWord(simplified, "CM_ BO_ "))
            {
                ParseMessageComment(file, simplified);
            }
            else if (StartsWithWord(simplified, "CM_ BU_ "))
            {
                ParseNodeComment(file, simplified);
            }
            else if (StartsWithWord(simplified, "VAL_TABLE_ "))
            {
                ParseOrBufferValueTable(ref inMultilineValueTable, ref multilineValueTable, file, simplified);
            }
            else if (StartsWithWord(simplified, "VAL_ "))
            {
                ParseValueDescriptionsLine(file, simplified);
            }
            else if (StartsWithWord(simplified, "BA_DEF_ "))
            {
                ParseAttributeDefinition(file, simplified);
            }
            else if (StartsWithWord(simplified, "BA_DEF_DEF_ "))
            {
                ParseAttributeDefault(file, simplified);
            }
            else if (StartsWithWord(simplified, "BA_ "))
            {
                ParseAttributeValue(file, simplified);
            }
            else if (simplified.Length > 0)
            {
                file.UnparsedStatements.Add((simplified, raw));
            }
        }

        ApplyPostLoadFixups(file);
        return file;
    }

    private static string Simplify(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        return SimplifiedWhitespace().Replace(trimmed, " ");
    }

    private static bool StartsWithWord(string line, string prefix) =>
        line.StartsWith(prefix, StringComparison.Ordinal);

    // ---- Node list ------------------------------------------------------

    private static void ParseNodeList(DbcFile file, string line)
    {
        var match = NodeListRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        AppendNodeTokens(file, match.Groups[1].Value);
    }

    private static void AppendContinuationNodes(DbcFile file, string line)
    {
        AppendNodeTokens(file, line);
    }

    private static void AppendNodeTokens(DbcFile file, string text)
    {
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 1 && !ExistingNode(file, token))
            {
                file.Nodes.Add(new DbcNode { Name = token });
            }
        }
    }

    private static bool ExistingNode(DbcFile file, string name)
    {
        return file.Nodes.Any(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Message / signal ----------------------------------------------

    private static bool TryParseMessage(DbcFile file, string line, out DbcMessage message)
    {
        message = null!;
        var match = MessageRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        var rawId = ulong.Parse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var messageId = (uint)(rawId & 0x1FFFFFFF);
        var extended = (rawId & 0x80000000) != 0;

        var length = uint.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var senderName = match.Groups[4].Value;

        message = new DbcMessage
        {
            Id = messageId,
            IsExtended = extended,
            Name = match.Groups[2].Value,
            Length = length,
            Sender = file.FindNode(senderName),
        };
        return true;
    }

    private static bool TryParseSignal(string line, DbcMessage message, DbcFile file)
    {
        foreach (var variant in SignalVariants)
        {
            var match = variant.Regex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var signal = new DbcSignal
            {
                Name = match.Groups["name"].Value,
                IsMultiplexor = variant.IsMultiplexor,
                IsMultiplexed = variant.IsMultiplexed,
                StartBit = int.Parse(match.Groups["startBit"].Value, CultureInfo.InvariantCulture),
                SignalSize = int.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture),
            };

            ApplyByteOrderAndType(signal, int.Parse(match.Groups["byteOrder"].Value, CultureInfo.InvariantCulture), match.Groups["sign"].Value);

            signal.FactorText = match.Groups["factor"].Value;
            signal.BiasText = match.Groups["bias"].Value;
            signal.MinText = match.Groups["min"].Value;
            signal.MaxText = match.Groups["max"].Value;
            signal.Factor = ParseDouble(match.Groups["factor"].Value);
            signal.Bias = ParseDouble(match.Groups["bias"].Value);
            signal.Min = ParseDouble(match.Groups["min"].Value);
            signal.Max = ParseDouble(match.Groups["max"].Value);
            signal.UnitName = match.Groups["unit"].Value;

            signal.Receiver = ResolveReceiver(file, match.Groups["receivers"].Value);
            signal.ParentMessage = message;

            if (variant.HasMuxValue)
            {
                var muxValue = int.Parse(match.Groups["muxValue"].Value, CultureInfo.InvariantCulture);
                signal.AddMultiplexRange(muxValue, muxValue);
            }

            message.Signals[signal.Name] = signal;

            if (signal.IsMultiplexor && message.MultiplexorSignal is null && !variant.IsMultiplexed)
            {
                message.MultiplexorSignal = signal;
            }

            return true;
        }

        return false;
    }

    private static DbcNode ResolveReceiver(DbcFile file, string receivers)
    {
        var receiverName = receivers.Contains(',') ? receivers[..receivers.IndexOf(',')] : receivers;
        return file.FindNode(receiverName.Trim());
    }

    private static void ApplyByteOrderAndType(DbcSignal signal, int byteOrder, string sign)
    {
        switch (byteOrder)
        {
            case 0:
                signal.IntelByteOrder = false;
                signal.ValueType = sign == "-" ? DbcSignalValueType.SignedInt : DbcSignalValueType.UnsignedInt;
                break;
            case 1:
                signal.IntelByteOrder = true;
                signal.ValueType = sign == "-" ? DbcSignalValueType.SignedInt : DbcSignalValueType.UnsignedInt;
                break;
            case 2:
                signal.IntelByteOrder = false;
                signal.ValueType = DbcSignalValueType.SingleFloat;
                break;
            case 3:
                signal.IntelByteOrder = false;
                signal.ValueType = DbcSignalValueType.DoubleFloat;
                break;
            case 4:
                signal.IntelByteOrder = false;
                signal.ValueType = DbcSignalValueType.String;
                break;
            case 5:
                signal.IntelByteOrder = true;
                signal.ValueType = DbcSignalValueType.SingleFloat;
                break;
            case 6:
                signal.IntelByteOrder = true;
                signal.ValueType = DbcSignalValueType.DoubleFloat;
                break;
            default:
                signal.IntelByteOrder = false;
                signal.ValueType = DbcSignalValueType.UnsignedInt;
                break;
        }
    }

    // ---- Extended multiplex --------------------------------------------

    private static void ParseExtendedMultiplex(DbcFile file, string line, int lineNumber, List<DbcParseFault> faults)
    {
        var match = ExtendedMultiplexRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        var idText = match.Groups[1].Value;
        var messageId = uint.Parse(idText, CultureInfo.InvariantCulture) & 0x1FFFFFFF;
        var childName = match.Groups[2].Value;
        var parentName = match.Groups[3].Value;
        var rangesText = match.Groups[4].Value;

        if (!file.Messages.TryGetValue(messageId, out var message) ||
            !message.Signals.TryGetValue(childName, out var child) ||
            !message.Signals.TryGetValue(parentName, out var parent))
        {
            faults.Add(new DbcParseFault(lineNumber, line));
            return;
        }

        foreach (Match range in RangePairRegex().Matches(rangesText))
        {
            var min = int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
            var max = int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
            child.MultiplexParent = parent;
            child.AddMultiplexRange(min, max);
        }

        if (child.MultiplexParent is not null && !parent.MultiplexedChildren.Contains(child))
        {
            parent.MultiplexedChildren.Add(child);
        }
    }

    private static void ParseSignalValueType(DbcFile file, string line)
    {
        var match = SignalValueTypeRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        var messageId = uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) & 0x1FFFFFFF;
        var type = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (type is not 1 and not 2)
        {
            return;
        }

        if (!file.Messages.TryGetValue(messageId, out var message))
        {
            return;
        }

        if (message.Signals.TryGetValue(match.Groups[2].Value, out var signal))
        {
            signal.ValueType = type == 1 ? DbcSignalValueType.SingleFloat : DbcSignalValueType.DoubleFloat;
        }
    }

    // ---- Comments -------------------------------------------------------

    private static void ParseSignalComment(DbcFile file, string line)
    {
        var match = SignalCommentRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        if (!file.Messages.TryGetValue(uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) & 0x1FFFFFFF, out var message))
        {
            return;
        }

        if (message.Signals.TryGetValue(match.Groups[2].Value, out var signal))
        {
            signal.Comment = match.Groups[3].Value;
        }
    }

    private static void ParseMessageComment(DbcFile file, string line)
    {
        var match = MessageCommentRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        if (file.Messages.TryGetValue(uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) & 0x1FFFFFFF, out var message))
        {
            message.Comment = match.Groups[2].Value;
        }
    }

    private static void ParseNodeComment(DbcFile file, string line)
    {
        var match = NodeCommentRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        var node = file.FindNode(match.Groups[1].Value);
        if (string.Equals(node.Name, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
        {
            node.Comment = match.Groups[2].Value;
        }
    }

    // ---- Value tables ---------------------------------------------------

    private static void ParseOrBufferValueTable(
        ref bool inMultiline,
        ref string buffer,
        DbcFile file,
        string line)
    {
        if (line.EndsWith(';'))
        {
            ParseValueTableLine(file, line);
            return;
        }

        inMultiline = true;
        buffer = line;
    }

    private static void ParseValueTableLine(DbcFile file, string line)
    {
        var match = ValueTableRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        var name = match.Groups[1].Value;
        var list = new List<DbcValueEntry>();
        if (!ParseValueDescriptions(match.Groups[2].Value, list))
        {
            return;
        }

        var existing = file.FindValueTable(name);
        if (existing is not null)
        {
            existing.Entries.Clear();
            existing.Entries.AddRange(list);
            return;
        }

        file.ValueTables.Add(new DbcValueTable { Name = name });
        file.ValueTables[^1].Entries.AddRange(list);
    }

    // ---- VAL_ -----------------------------------------------------------

    private static void ParseValueDescriptionsLine(DbcFile file, string line)
    {
        var match = ValueDescriptionsLineRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        var messageId = uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) & 0x1FFFFFFF;
        if (!file.Messages.TryGetValue(messageId, out var message))
        {
            return;
        }

        if (!message.Signals.TryGetValue(match.Groups[2].Value, out var signal))
        {
            return;
        }

        var remainder = match.Groups[3].Value.Trim();
        var table = file.FindValueTable(remainder);
        if (table is not null)
        {
            signal.ValueDescriptions.Clear();
            signal.ValueDescriptions.AddRange(table.Entries);
            return;
        }

        var parsed = new List<DbcValueEntry>();
        if (ParseValueDescriptions(remainder, parsed))
        {
            signal.ValueDescriptions.AddRange(parsed);
        }
    }

    private static bool ParseValueDescriptions(string text, List<DbcValueEntry> entries)
    {
        var remaining = text.Trim();
        while (remaining.Length > 2)
        {
            var match = ValueEntryRegex().Match(remaining);
            if (!match.Success)
            {
                return false;
            }

            entries.Add(new DbcValueEntry(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                match.Groups[2].Value));
            remaining = match.Groups[3].Value.Trim();
        }

        return entries.Count > 0;
    }

    // ---- Attributes -----------------------------------------------------

    private static void ParseAttributeDefinition(DbcFile file, string line)
    {
        DbcAttributeTarget target;
        string rest;
        if (StartsWithWord(line, "BA_DEF_ SG_ "))
        {
            target = DbcAttributeTarget.Signal;
            rest = line[12..];
        }
        else if (StartsWithWord(line, "BA_DEF_ BO_ "))
        {
            target = DbcAttributeTarget.Message;
            rest = line[12..];
        }
        else if (StartsWithWord(line, "BA_DEF_ BU_ "))
        {
            target = DbcAttributeTarget.Node;
            rest = line[12..];
        }
        else
        {
            target = DbcAttributeTarget.General;
            rest = line[9..];
        }

        var attribute = new DbcAttribute { Target = target };

        var withBounds = AttributeDefinitionBoundsRegex().Match(rest);
        if (withBounds.Success)
        {
            attribute.Name = withBounds.Groups[1].Value;
            attribute.ValueKind = ParseValueKind(withBounds.Groups[2].Value);
            attribute.Lower = ParseDouble(withBounds.Groups[3].Value);
            attribute.Upper = ParseDouble(withBounds.Groups[4].Value);

            file.Attributes.Add(attribute);
            return;
        }

        var noBounds = AttributeDefinitionEnumRegex().Match(rest);
        if (!noBounds.Success)
        {
            return;
        }

        attribute.Name = noBounds.Groups[1].Value;
        attribute.ValueKind = ParseValueKind(noBounds.Groups[2].Value);
        var remainder = noBounds.Groups[3].Value;
        if (attribute.ValueKind == DbcAttributeValueKind.Enum)
        {
            foreach (var enumToken in remainder.Split(','))
            {
                attribute.EnumValues.Add(Unquote(enumToken.Trim()));
            }
        }

        file.Attributes.Add(attribute);
    }

    private static DbcAttributeValueKind ParseValueKind(string typeToken) => typeToken.ToUpperInvariant() switch
    {
        "INT" => DbcAttributeValueKind.Int,
        "FLOAT" => DbcAttributeValueKind.Float,
        "STRING" => DbcAttributeValueKind.String,
        "ENUM" => DbcAttributeValueKind.Enum,
        _ => DbcAttributeValueKind.Int,
    };

    private static void ParseAttributeDefault(DbcFile file, string line)
    {
        var match = AttributeDefaultRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        var attribute = file.FindAttribute(match.Groups[1].Value);
        if (attribute is null)
        {
            return;
        }

        attribute.DefaultValue = ConvertAttributeDefault(match.Groups[2].Value, attribute);
    }

    private static void ParseAttributeValue(DbcFile file, string line)
    {
        var messageMatch = AttributeValueMessageRegex().Match(line);
        if (messageMatch.Success)
        {
            var attribute = file.FindAttribute(messageMatch.Groups[1].Value);
            if (attribute is null)
            {
                return;
            }

            var messageId = uint.Parse(messageMatch.Groups[2].Value, CultureInfo.InvariantCulture) & 0x1FFFFFFF;
            if (!file.Messages.TryGetValue(messageId, out var message))
            {
                return;
            }

            var value = ConvertAttributeValue(messageMatch.Groups[3].Value, attribute);
            ReplaceOrAdd(message.Attributes, attribute.Name, value);
            return;
        }

        var signalMatch = AttributeValueSignalRegex().Match(line);
        if (signalMatch.Success)
        {
            var attribute = file.FindAttribute(signalMatch.Groups[1].Value);
            if (attribute is null)
            {
                return;
            }

            var messageId = uint.Parse(signalMatch.Groups[2].Value, CultureInfo.InvariantCulture) & 0x1FFFFFFF;
            if (!file.Messages.TryGetValue(messageId, out var message))
            {
                return;
            }

            if (!message.Signals.TryGetValue(signalMatch.Groups[3].Value, out var signal))
            {
                return;
            }

            // Decision #3: use the value group, not the signal name.
            var value = ConvertAttributeValue(signalMatch.Groups[4].Value, attribute);
            ReplaceOrAdd(signal.Attributes, attribute.Name, value);
            return;
        }

        var nodeMatch = AttributeValueNodeRegex().Match(line);
        if (nodeMatch.Success)
        {
            var attribute = file.FindAttribute(nodeMatch.Groups[1].Value);
            if (attribute is null)
            {
                return;
            }

            var node = file.FindNode(nodeMatch.Groups[2].Value);
            if (!string.Equals(node.Name, nodeMatch.Groups[2].Value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var value = ConvertAttributeValue(nodeMatch.Groups[3].Value, attribute);
            ReplaceOrAdd(node.Attributes, attribute.Name, value);
        }
    }

    private static object ConvertAttributeValue(string raw, DbcAttribute attribute)
    {
        var token = raw.Trim();
        if (attribute.ValueKind == DbcAttributeValueKind.String)
        {
            return Unquote(token);
        }

        var text = Unquote(token);
        switch (attribute.ValueKind)
        {
            case DbcAttributeValueKind.Float:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
            case DbcAttributeValueKind.Int:
            case DbcAttributeValueKind.Enum:
                // Attribute *values* for enums are stored as their numeric index.
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0L;
        }

        return 0;
    }

    private static object ConvertAttributeDefault(string raw, DbcAttribute attribute)
    {
        var token = raw.Trim();
        if (attribute.ValueKind == DbcAttributeValueKind.String)
        {
            return Unquote(token);
        }

        var text = Unquote(token);
        switch (attribute.ValueKind)
        {
            case DbcAttributeValueKind.Float:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
            case DbcAttributeValueKind.Int:
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0L;
            case DbcAttributeValueKind.Enum:
                // Defaults are written as the enum *name*.
                return IndexOfEnum(attribute.EnumValues, text);
        }

        return 0;
    }

    private static int IndexOfEnum(List<string> values, string text)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], text, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static string Unquote(string text)
    {
        var parts = text.Split('"');
        return parts.Length >= 3 ? parts[1] : text;
    }

    private static void ReplaceOrAdd(List<DbcAttributeValue> list, string name, object value)
    {
        foreach (var existing in list)
        {
            if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                existing.Value = value;
                return;
            }
        }

        list.Add(new DbcAttributeValue { Name = name, Value = value });
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0d;

    // ---- Post-load ------------------------------------------------------

    private static void ApplyPostLoadFixups(DbcFile file)
    {
        EnsureColorAttribute(file, "GenMsgBackgroundColor", "#ffffff");
        EnsureColorAttribute(file, "GenMsgForegroundColor", "#000000");

        var criteriaAttribute = file.FindAttribute("matchingcriteria");
        if (criteriaAttribute?.DefaultValue is long criteria and >= 0 and <= 2)
        {
            file.MatchingCriteria = (MatchingCriteria)criteria;
        }

        var labelingAttribute = file.FindAttribute("filterlabeling");
        if (labelingAttribute?.DefaultValue is long labeling)
        {
            file.FilterLabeling = labeling != 0;
        }

        foreach (var message in file.Messages.Values)
        {
            message.BackgroundColor = FindMessageAttributeValue(file, message, "GenMsgBackgroundColor") ?? string.Empty;
            message.ForegroundColor = FindMessageAttributeValue(file, message, "GenMsgForegroundColor") ?? string.Empty;

            var format = FrameFormatText(file, message);
            message.IsCanFd = FrameFormatIsFd(format) || message.Length > 8;
            message.BitRateSwitch = FindMessageAttributeValue(file, message, "CANFD_BRS") is null
                ? true
                : MessageAttributeText(message, "CANFD_BRS") == "1";

            RepairMultiplex(message);
        }

        file.AssociatedBus = -1;
        file.IsDirty = false;
    }

    private static void EnsureColorAttribute(DbcFile file, string name, string defaultColor)
    {
        if (file.FindAttribute(name) is not null)
        {
            return;
        }

        file.Attributes.Add(new DbcAttribute
        {
            Name = name,
            Target = DbcAttributeTarget.Message,
            ValueKind = DbcAttributeValueKind.String,
            DefaultValue = defaultColor,
        });
    }

    private static string? FindMessageAttributeValue(DbcFile file, DbcMessage message, string name)
    {
        foreach (var value in message.Attributes)
        {
            if (string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return value.Value?.ToString();
            }
        }

        var attribute = file.FindAttribute(name, DbcAttributeTarget.Message);
        return attribute?.DefaultValue?.ToString();
    }

    private static string? MessageAttributeText(DbcMessage message, string name)
    {
        foreach (var value in message.Attributes)
        {
            if (string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return value.Value?.ToString();
            }
        }

        return null;
    }

    private static string FrameFormatText(DbcFile file, DbcMessage message) =>
        MessageAttributeText(message, "VFrameFormat")
        ?? file.FindAttribute("VFrameFormat", DbcAttributeTarget.Message)?.DefaultValue?.ToString()
        ?? string.Empty;

    private static bool FrameFormatIsFd(string text) =>
        text.EndsWith("CAN_FD", StringComparison.OrdinalIgnoreCase) || text is "14" or "15";

    private static void RepairMultiplex(DbcMessage message)
    {
        foreach (var signal in message.Signals.Values)
        {
            if (!signal.IsMultiplexed)
            {
                continue;
            }

            if (signal.MultiplexParent is null && message.MultiplexorSignal is not null)
            {
                signal.MultiplexParent = message.MultiplexorSignal;
                message.MultiplexorSignal.MultiplexedChildren.Add(signal);
            }

            if (signal.MultiplexParent is null && message.MultiplexorSignal is null)
            {
                signal.IsMultiplexed = false;
            }
        }
    }

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex SimplifiedWhitespace();

    [GeneratedRegex(@"^BU_\:(.*)", RegexOptions.Compiled)]
    private static partial Regex NodeListRegex();

    [GeneratedRegex(@"^BO_ (\w+) ([^\s:]+) *: (\w+) ([^\s]+)", RegexOptions.Compiled)]
    private static partial Regex MessageRegex();

    [GeneratedRegex(@"^SG_ *(?<name>[^\s:]+) +M *: *(?<startBit>\d+)\|(?<size>\d+)@(?<byteOrder>\d+)(?<sign>[\+\|\-]) \((?<factor>[0-9.+\-eE]+),(?<bias>[0-9.+\-eE]+)\) \[(?<min>[0-9.+\-eE]+)\|(?<max>[0-9.+\-eE]+)\] ""(?<unit>.*)"" (?<receivers>.*)", RegexOptions.Compiled)]
    private static partial Regex SignalMultiplexorRegex();

    [GeneratedRegex(@"^SG_ *(?<name>[^\s:]+) +m(?<muxValue>\d+) *: *(?<startBit>\d+)\|(?<size>\d+)@(?<byteOrder>\d+)(?<sign>[\+\|\-]) \((?<factor>[0-9.+\-eE]+),(?<bias>[0-9.+\-eE]+)\) \[(?<min>[0-9.+\-eE]+)\|(?<max>[0-9.+\-eE]+)\] ""(?<unit>.*)"" (?<receivers>.*)", RegexOptions.Compiled)]
    private static partial Regex SignalMultiplexedRegex();

    [GeneratedRegex(@"^SG_ *(?<name>[^\s:]+) +m(?<muxValue>\d+)M *: *(?<startBit>\d+)\|(?<size>\d+)@(?<byteOrder>\d+)(?<sign>[\+\|\-]) \((?<factor>[0-9.+\-eE]+),(?<bias>[0-9.+\-eE]+)\) \[(?<min>[0-9.+\-eE]+)\|(?<max>[0-9.+\-eE]+)\] ""(?<unit>.*)"" (?<receivers>.*)", RegexOptions.Compiled)]
    private static partial Regex SignalExtendedMuxRegex();

    [GeneratedRegex(@"^SG_ *(?<name>[^\s:]+) *: *(?<startBit>\d+)\|(?<size>\d+)@(?<byteOrder>\d+)(?<sign>[\+\|\-]) \((?<factor>[0-9.+\-eE]+),(?<bias>[0-9.+\-eE]+)\) \[(?<min>[0-9.+\-eE]+)\|(?<max>[0-9.+\-eE]+)\] ""(?<unit>.*)"" (?<receivers>.*)", RegexOptions.Compiled)]
    private static partial Regex SignalPlainRegex();

    [GeneratedRegex(@"^SG_MUL_VAL_ (\d+) ([-\w]+) ([-\w]+) ((?:\d+\-\d+[\s]*[,]?[\s]*)*)", RegexOptions.Compiled)]
    private static partial Regex ExtendedMultiplexRegex();

    [GeneratedRegex(@"(\d+)-(\d+)", RegexOptions.Compiled)]
    private static partial Regex RangePairRegex();

    [GeneratedRegex(@"^SIG_VALTYPE_ *(\d+) *([-\w]+) *: *(\d+);", RegexOptions.Compiled)]
    private static partial Regex SignalValueTypeRegex();

    [GeneratedRegex(@"^CM_ SG_ *(\w+) *([-\w]+) *""(.*)"";", RegexOptions.Compiled)]
    private static partial Regex SignalCommentRegex();

    [GeneratedRegex(@"^CM_ BO_ *(\w+) *""(.*)"";", RegexOptions.Compiled)]
    private static partial Regex MessageCommentRegex();

    [GeneratedRegex(@"^CM_ BU_ *([-\w]+) *""(.*)"";", RegexOptions.Compiled)]
    private static partial Regex NodeCommentRegex();

    [GeneratedRegex(@"^VAL_TABLE_ ([-\w]+) (.*);", RegexOptions.Compiled)]
    private static partial Regex ValueTableRegex();

    [GeneratedRegex(@"^VAL_ (\w+) ([-\w]+) (.*);", RegexOptions.Compiled)]
    private static partial Regex ValueDescriptionsLineRegex();

    [GeneratedRegex(@"^([+-]?\d+) ""(.*?)""(.*)", RegexOptions.Compiled)]
    private static partial Regex ValueEntryRegex();

    [GeneratedRegex(@"\""*(\w+)\""* \""*(\w+)\""* ([0-9.+\-eE]+) ([0-9.+\-eE]+)", RegexOptions.Compiled)]
    private static partial Regex AttributeDefinitionBoundsRegex();

    [GeneratedRegex(@"\""*(\w+)\""* \""*(\w+)\""* (.*)", RegexOptions.Compiled)]
    private static partial Regex AttributeDefinitionEnumRegex();

    [GeneratedRegex(@"^BA_DEF_DEF_ \""*([-\w]+)\""* \""*([^;]*?)\""*;", RegexOptions.Compiled)]
    private static partial Regex AttributeDefaultRegex();

    [GeneratedRegex(@"^BA_ \""*([-\w]+)\""* BO_ (\d+) \""*([^;]*?)\""*;", RegexOptions.Compiled)]
    private static partial Regex AttributeValueMessageRegex();

    [GeneratedRegex(@"^BA_ \""*([-\w]+)\""* SG_ (\d+) \""*([-\w]+)\""* \""*([^;]*?)\""*;", RegexOptions.Compiled)]
    private static partial Regex AttributeValueSignalRegex();

    [GeneratedRegex(@"^BA_ \""*([-\w]+)\""* BU_ \""*([-\w]+)\""* \""*([^;]*?)\""*;", RegexOptions.Compiled)]
    private static partial Regex AttributeValueNodeRegex();
}



