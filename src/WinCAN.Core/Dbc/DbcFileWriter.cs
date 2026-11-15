using System.Globalization;
using System.Text;

namespace WinCAN.Core.Dbc;

/// <summary>
/// Ports DBCFile::saveFile. Output is byte-identical to SavvyCAN: the fixed header, the node /
/// value-table / message blocks, then comments, attribute definitions, defaults and value
/// sections in the reference flush order. Messages write in ascending ID, signals in ascending
/// ordinal name.
/// </summary>
public static class DbcFileWriter{
    public static void Save(DbcFile file, string path)
    {
        var content = Serialize(file);
        File.WriteAllText(path, content, new UTF8Encoding(false));

        file.IsDirty = false;
        file.FileName = Path.GetFileName(path);
        file.FilePath = Path.GetDirectoryName(path) ?? string.Empty;
    }

    public static string Serialize(DbcFile file)
    {
        SyncCanFdAttributes(file);

        var builder = new StringBuilder(4096);
        WriteHeader(builder);
        WriteNodes(file, builder);
        WriteValueTables(file, builder);
        WriteMessages(file, builder);

        var comments = new StringBuilder();
        var defaults = new StringBuilder();
        var attributeValues = new StringBuilder();
        var valueDescriptions = new StringBuilder();
        var extendedMultiplex = new StringBuilder();
        var signalValueTypes = new StringBuilder();
        var attributeDefinitions = new StringBuilder();
        var extendedMultiplexNeeded = false;

        CollectNodes(file, comments, attributeValues);
        CollectMessages(file, comments, attributeValues, valueDescriptions, signalValueTypes, ref extendedMultiplexNeeded);
        CollectAttributeDefinitions(file, attributeDefinitions, defaults);
        if (extendedMultiplexNeeded)
        {
            CollectExtendedMultiplex(file, extendedMultiplex);
        }

        builder.Append(comments);
        builder.Append(attributeDefinitions);
        builder.Append(defaults);
        builder.Append(attributeValues);
        builder.Append(valueDescriptions);
        builder.Append(extendedMultiplex);
        builder.Append(signalValueTypes);
        return builder.ToString();
    }

    private static void WriteHeader(StringBuilder builder)
    {
        builder.Append("VERSION \"\"\n\n\nNS_ :\n");
        foreach (var entry in new[]
        {
            "NS_DESC_", "CM_", "BA_DEF_", "BA_", "VAL_", "CAT_DEF_", "CAT_", "FILTER",
            "BA_DEF_DEF_", "EV_DATA_", "ENVVAR_DATA_", "SGTYPE_", "SGTYPE_VAL_",
            "BA_DEF_SGTYPE_", "BA_SGTYPE_", "SIG_TYPE_REF_", "VAL_TABLE_", "SIG_GROUP_",
            "SIG_VALTYPE_", "SIGTYPE_VALTYPE_", "BO_TX_BU_", "BA_DEF_REL_", "BA_REL_",
            "BA_DEF_DEF_REL_", "BU_SG_REL_", "BU_EV_REL_", "BU_BO_REL_", "SG_MUL_VAL_",
        })
        {
            builder.Append("    ").Append(entry).Append('\n');
        }

        builder.Append("\nBS_: \n");
    }

    private static void WriteNodes(DbcFile file, StringBuilder builder)
    {
        builder.Append("BU_: ");
        var nodeNumber = 1;
        foreach (var node in file.Nodes)
        {
            if (node.Name.Equals(DbcFile.DefaultNodeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = node.Name;
            if (name.Length < 1)
            {
                name = "NODE" + nodeNumber++;
                node.Name = name;
            }

            builder.Append(name).Append(' ');
        }

        builder.Append('\n');
    }

    private static void WriteValueTables(DbcFile file, StringBuilder builder)
    {
        foreach (var table in file.ValueTables)
        {
            builder.Append("VAL_TABLE_ ").Append(table.Name);
            foreach (var entry in table.Entries)
            {
                builder.Append(' ')
                    .Append(entry.Value.ToString(CultureInfo.InvariantCulture))
                    .Append(" \"")
                    .Append(entry.Description)
                    .Append('"');
            }

            builder.Append(";\n");
        }
    }

    private static void WriteMessages(DbcFile file, StringBuilder builder)
    {
        var messageNumber = 1;
        foreach (var message in file.Messages.Values)
        {
            var name = message.Name;
            if (name.Length < 1)
            {
                name = "MSG" + messageNumber++;
                message.Name = name;
            }

            var id = MessageIdOnWire(message);

            builder.Append("BO_ ")
                .Append(id.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(name)
                .Append(": ")
                .Append(message.Length.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(message.Sender?.Name ?? DbcFile.DefaultNodeName)
                .Append('\n');

            WriteSignals(file, message, id, builder);

            builder.Append('\n');
        }
    }

    private static void WriteSignals(DbcFile file, DbcMessage message, uint id, StringBuilder builder)
    {
        var signalNumber = 1;
        foreach (var signal in message.Signals.Values)
        {
            var name = signal.Name;
            if (name.Length < 1)
            {
                name = "SIG" + signalNumber++;
                signal.Name = name;
            }

            builder.Append("   SG_ ").Append(name);

            if (signal.IsMultiplexed)
            {
                builder.Append(" m").Append(signal.GetSimpleMultiplexValue().ToString(CultureInfo.InvariantCulture));
            }

            if (signal.IsMultiplexor)
            {
                if (!signal.IsMultiplexed)
                {
                    builder.Append(' ');
                }

                builder.Append('M');
            }

            builder.Append(" : ")
                .Append(signal.StartBit.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(signal.SignalSize.ToString(CultureInfo.InvariantCulture))
                .Append('@')
                .Append(ByteOrderCode(signal))
                .Append(" (")
                .Append(FormatNumber(signal.Factor))
                .Append(',')
                .Append(FormatNumber(signal.Bias))
                .Append(") [")
                .Append(FormatNumber(signal.Min))
                .Append('|')
                .Append(FormatNumber(signal.Max))
                .Append("] \"")
                .Append(signal.UnitName)
                .Append("\" ")
                .Append(signal.Receiver?.Name ?? DbcFile.DefaultNodeName)
                .Append('\n');
        }
    }

    private static void CollectNodes(DbcFile file, StringBuilder comments, StringBuilder attributeValues)
    {
        foreach (var node in file.Nodes)
        {
            if (node.Name.Equals(DbcFile.DefaultNodeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (node.Comment.Length > 0)
            {
                comments.Append("CM_ BU_ ").Append(node.Name).Append(" \"").Append(node.Comment).Append("\";\n");
            }

            foreach (var value in node.Attributes)
            {
                attributeValues.Append("BA_ \"").Append(value.Name).Append("\" BU_ ")
                    .Append(node.Name).Append(' ')
                    .Append(FormatAttributeValue(value.Value)).Append(";\n");
            }
        }
    }

    private static void CollectMessages(
        DbcFile file,
        StringBuilder comments,
        StringBuilder attributeValues,
        StringBuilder valueDescriptions,
        StringBuilder signalValueTypes,
        ref bool extendedMultiplexNeeded)
    {
        foreach (var message in file.Messages.Values)
        {
            var id = MessageIdOnWire(message);

            if (message.Comment.Length > 0)
            {
                comments.Append("CM_ BO_ ").Append(id.ToString(CultureInfo.InvariantCulture))
                    .Append(" \"").Append(message.Comment).Append("\";\n");
            }

            foreach (var value in message.Attributes)
            {
                attributeValues.Append("BA_ \"").Append(value.Name).Append("\" BO_ ")
                    .Append(id.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(FormatAttributeValue(value.Value)).Append(";\n");
            }

            foreach (var signal in message.Signals.Values)
            {
                if (signal.IsMultiplexed && signal.IsMultiplexor)
                {
                    extendedMultiplexNeeded = true;
                }
                else if (signal.HasExtendedMultiplexing)
                {
                    extendedMultiplexNeeded = true;
                }

                if (signal.ValueType is DbcSignalValueType.SingleFloat or DbcSignalValueType.DoubleFloat)
                {
                    signalValueTypes.Append("SIG_VALTYPE_ ")
                        .Append(id.ToString(CultureInfo.InvariantCulture))
                        .Append(' ')
                        .Append(signal.Name)
                        .Append(" : ")
                        .Append(signal.ValueType == DbcSignalValueType.SingleFloat ? "1" : "2")
                        .Append(";\n");
                }

                if (signal.Comment.Length > 0)
                {
                    comments.Append("CM_ SG_ ").Append(id.ToString(CultureInfo.InvariantCulture))
                        .Append(' ').Append(signal.Name).Append(" \"").Append(signal.Comment).Append("\";\n");
                }

                foreach (var value in signal.Attributes)
                {
                    attributeValues.Append("BA_ \"").Append(value.Name).Append("\" SG_ ")
                        .Append(id.ToString(CultureInfo.InvariantCulture)).Append(' ')
                        .Append(signal.Name).Append(' ')
                        .Append(FormatAttributeValue(value.Value)).Append(";\n");
                }

                if (signal.ValueDescriptions.Count > 0)
                {
                    valueDescriptions.Append("VAL_ ").Append(id.ToString(CultureInfo.InvariantCulture))
                        .Append(' ').Append(signal.Name);
                    foreach (var entry in signal.ValueDescriptions)
                    {
                        valueDescriptions.Append(' ')
                            .Append(entry.Value.ToString(CultureInfo.InvariantCulture))
                            .Append(" \"")
                            .Append(entry.Description)
                            .Append('"');
                    }

                    valueDescriptions.Append(";\n");
                }
            }
        }
    }

    private static void CollectAttributeDefinitions(DbcFile file, StringBuilder definitions, StringBuilder defaults)
    {
        var wroteMatchingCriteria = false;
        var wroteFilterLabeling = false;

        foreach (var attribute in file.Attributes)
        {
            if (attribute.Target == DbcAttributeTarget.Message)
            {
                if (attribute.Name.Equals("matchingcriteria", StringComparison.OrdinalIgnoreCase))
                {
                    if (wroteMatchingCriteria)
                    {
                        continue;
                    }

                    wroteMatchingCriteria = true;
                }
                else if (attribute.Name.Equals("filterlabeling", StringComparison.OrdinalIgnoreCase))
                {
                    if (wroteFilterLabeling)
                    {
                        continue;
                    }

                    wroteFilterLabeling = true;
                }
            }

            definitions.Append("BA_DEF_ ");
            definitions.Append(attribute.Target switch
            {
                DbcAttributeTarget.Node => "BU_ ",
                DbcAttributeTarget.Message => "BO_ ",
                DbcAttributeTarget.Signal => "SG_ ",
                _ => string.Empty,
            });

            definitions.Append('"').Append(attribute.Name).Append("\" ");

            switch (attribute.ValueKind)
            {
                case DbcAttributeValueKind.Int:
                    definitions.Append("INT ")
                        .Append(FormatInteger(attribute.Lower)).Append(' ')
                        .Append(FormatInteger(attribute.Upper));
                    break;
                case DbcAttributeValueKind.Float:
                    definitions.Append("FLOAT ")
                        .Append(FormatNumber(attribute.Lower)).Append(' ')
                        .Append(FormatNumber(attribute.Upper));
                    break;
                case DbcAttributeValueKind.String:
                    definitions.Append("STRING ");
                    break;
                case DbcAttributeValueKind.Enum:
                    definitions.Append("ENUM ");
                    foreach (var enumValue in attribute.EnumValues)
                    {
                        definitions.Append('"').Append(enumValue).Append("\",");
                    }

                    if (attribute.EnumValues.Count > 0)
                    {
                        definitions.Length--; // remove trailing comma
                    }

                    break;
            }

            definitions.Append(";\n");

            if (attribute.DefaultValue is not null)
            {
                defaults.Append("BA_DEF_DEF_ \"").Append(attribute.Name).Append("\" ");
                switch (attribute.ValueKind)
                {
                    case DbcAttributeValueKind.String:
                        defaults.Append('"').Append(attribute.DefaultValue).Append("\";\n");
                        break;
                    case DbcAttributeValueKind.Enum:
                        var index = Convert.ToInt32(attribute.DefaultValue, CultureInfo.InvariantCulture);
                        var enumName = index >= 0 && index < attribute.EnumValues.Count
                            ? attribute.EnumValues[index]
                            : string.Empty;
                        defaults.Append('"').Append(enumName).Append("\";\n");
                        break;
                    case DbcAttributeValueKind.Int:
                        defaults.Append(Convert.ToInt64(attribute.DefaultValue, CultureInfo.InvariantCulture)
                            .ToString(CultureInfo.InvariantCulture)).Append(";\n");
                        break;
                    case DbcAttributeValueKind.Float:
                        defaults.Append(FormatNumber(Convert.ToDouble(attribute.DefaultValue, CultureInfo.InvariantCulture)))
                            .Append(";\n");
                        break;
                }
            }
        }
    }

    private static void CollectExtendedMultiplex(DbcFile file, StringBuilder output)
    {
        foreach (var message in file.Messages.Values)
        {
            var id = MessageIdOnWire(message);
            foreach (var signal in message.Signals.Values)
            {
                if (!signal.IsMultiplexed || signal.MultiplexParent is null)
                {
                    continue;
                }

                output.Append("SG_MUL_VAL_ ")
                    .Append(id.ToString(CultureInfo.InvariantCulture))
                    .Append(' ')
                    .Append(signal.Name)
                    .Append(' ')
                    .Append(signal.MultiplexParent.Name)
                    .Append(' ');

                foreach (var range in signal.MultiplexRanges)
                {
                    output.Append(range.Min.ToString(CultureInfo.InvariantCulture))
                        .Append('-')
                        .Append(range.Max.ToString(CultureInfo.InvariantCulture))
                        .Append(' ');
                }

                output.Append(";\n");
            }
        }
    }

    private static string ByteOrderCode(DbcSignal signal)
    {
        return signal.ValueType switch
        {
            DbcSignalValueType.UnsignedInt => signal.IntelByteOrder ? "1+" : "0+",
            DbcSignalValueType.SignedInt => signal.IntelByteOrder ? "1-" : "0-",
            DbcSignalValueType.SingleFloat => signal.IntelByteOrder ? "1-" : "0-",
            DbcSignalValueType.DoubleFloat => signal.IntelByteOrder ? "1-" : "0-",
            DbcSignalValueType.String => "4-",
            _ => "0-",
        };
    }

    private static uint MessageIdOnWire(DbcMessage message) =>
        (message.Id > 0x7FF || message.IsExtended)
            ? message.Id + 0x80000000u
            : message.Id;

    private static string FormatAttributeValue(object? value) => value switch
    {
        string text => "\"" + text + "\"",
        double d => FormatNumber(d),
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        null => "0",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
    };

    private static string FormatNumber(double value)
    {
        var text = value.ToString("g6", CultureInfo.InvariantCulture);
        return text.Replace('E', 'e');
    }

    private static string FormatInteger(double value) =>
        ((long)Math.Round(value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    // ---- CAN FD sync (section 6) ----------------------------------------

    private static void SyncCanFdAttributes(DbcFile file)
    {
        var anyFd = file.Messages.Values.Any(m => m.IsCanFd);

        if (file.FindAttribute("VFrameFormat", DbcAttributeTarget.Message) is null)
        {
            if (!anyFd)
            {
                return;
            }

            var attribute = new DbcAttribute
            {
                Name = "VFrameFormat",
                Target = DbcAttributeTarget.Message,
                ValueKind = DbcAttributeValueKind.Enum,
                DefaultValue = 0,
            };
            attribute.EnumValues.AddRange(
            [
                "StandardCAN", "ExtendedCAN", "reserved", "J1939PG",
                "reserved", "reserved", "reserved", "reserved", "reserved", "reserved",
                "reserved", "reserved", "reserved", "reserved",
                "StandardCAN_FD", "ExtendedCAN_FD",
            ]);
            file.Attributes.Add(attribute);
        }

        if (anyFd && file.FindAttribute("CANFD_BRS", DbcAttributeTarget.Message) is null)
        {
            var attribute = new DbcAttribute
            {
                Name = "CANFD_BRS",
                Target = DbcAttributeTarget.Message,
                ValueKind = DbcAttributeValueKind.Enum,
                DefaultValue = 1,
            };
            attribute.EnumValues.AddRange(["0", "1"]);
            file.Attributes.Add(attribute);
        }

        var frameFormat = file.FindAttribute("VFrameFormat", DbcAttributeTarget.Message);
        var brs = file.FindAttribute("CANFD_BRS", DbcAttributeTarget.Message);
        string[] plainFormats = ["StandardCAN", "ExtendedCAN", "StandardCAN_FD", "ExtendedCAN_FD"];

        foreach (var message in file.Messages.Values)
        {
            var extended = message.IsExtended || message.Id > 0x7FF;
            var wanted = (extended ? "ExtendedCAN" : "StandardCAN") + (message.IsCanFd ? "_FD" : "");
            var current = frameFormat is null ? string.Empty : MessageAttributeText(message, frameFormat);

            if (frameFormat is not null &&
                (FrameFormatIsFd(message, frameFormat) != message.IsCanFd ||
                 (HasAttributeValue(message, frameFormat.Name) &&
                  plainFormats.Contains(current, StringComparer.OrdinalIgnoreCase) &&
                  !current.Equals(wanted, StringComparison.OrdinalIgnoreCase))))
            {
                var index = frameFormat.EnumValues.FindIndex(v =>
                    v.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    SetMessageAttribute(message, frameFormat.Name, index, DbcAttributeValueKind.Enum);
                }
            }

            if (message.IsCanFd && brs is not null)
            {
                var wantedBrs = message.BitRateSwitch ? "1" : "0";
                if (!MessageAttributeText(message, brs).Equals(wantedBrs, StringComparison.Ordinal) ||
                    !HasAttributeValue(message, brs.Name))
                {
                    var index = brs.EnumValues.FindIndex(v => v == wantedBrs);
                    if (index >= 0)
                    {
                        SetMessageAttribute(message, brs.Name, index, DbcAttributeValueKind.Enum);
                    }
                }
            }
        }
    }

    private static string MessageAttributeText(DbcMessage message, DbcAttribute attribute)
    {
        var value = FindMessageAttribute(message, attribute.Name);
        if (value is not null)
        {
            if (attribute.ValueKind == DbcAttributeValueKind.Enum)
            {
                var index = Convert.ToInt32(value.Value, CultureInfo.InvariantCulture);
                var text = index >= 0 && index < attribute.EnumValues.Count
                    ? attribute.EnumValues[index]
                    : string.Empty;
                return text.Trim();
            }

            return Convert.ToString(value.Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        }

        if (attribute.ValueKind == DbcAttributeValueKind.Enum)
        {
            var defaultIndex = Convert.ToInt32(attribute.DefaultValue ?? 0, CultureInfo.InvariantCulture);
            var text = defaultIndex >= 0 && defaultIndex < attribute.EnumValues.Count
                ? attribute.EnumValues[defaultIndex]
                : string.Empty;
            return text.Trim();
        }

        return Convert.ToString(attribute.DefaultValue, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }

    private static bool FrameFormatIsFd(DbcMessage message, DbcAttribute attribute)
    {
        var text = MessageAttributeText(message, attribute);
        return attribute.ValueKind == DbcAttributeValueKind.Enum
            ? text.EndsWith("CAN_FD", StringComparison.OrdinalIgnoreCase)
            : text is "14" or "15";
    }

    private static DbcAttributeValue? FindMessageAttribute(DbcMessage message, string name)
    {
        foreach (var value in message.Attributes)
        {
            if (string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static bool HasAttributeValue(DbcMessage message, string name) => FindMessageAttribute(message, name) is not null;

    private static void SetMessageAttribute(DbcMessage message, string name, object value, DbcAttributeValueKind kind)
    {
        var existing = FindMessageAttribute(message, name);
        if (existing is not null)
        {
            existing.Value = value;
            return;
        }

        message.Attributes.Add(new DbcAttributeValue { Name = name, Value = value });
    }
}
