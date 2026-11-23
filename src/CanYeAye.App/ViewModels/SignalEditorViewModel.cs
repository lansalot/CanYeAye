using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CanYeAye.Core;
using CanYeAye.Core.Dbc;

namespace CanYeAye.App.ViewModels;

/// <summary>One row of the signal editor's value table; the last row is always blank.</summary>
public partial class DbcValueRow : ObservableObject
{
    [ObservableProperty]
    private string _valueText = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    public bool IsBlank => string.IsNullOrWhiteSpace(ValueText) && string.IsNullOrWhiteSpace(Description);

    public int ParsedValue =>
        int.TryParse(ValueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : (int)CanIdParser.Parse(ValueText);
}

/// <summary>
/// The signal editor (dbc/dbcsignaleditor): name, bit length, byte order, type, scaling, limits,
/// unit, receiver, multiplexing and the value table. Edits are applied to the shared model
/// immediately, as the reference does, and <see cref="Changed"/> tells the window to redraw.
/// </summary>
public partial class SignalEditorViewModel : ObservableObject
{
    private readonly DbcFile _file;
    private readonly DbcMessage _message;
    private bool _suppress;

    public SignalEditorViewModel(DbcFile file, DbcMessage message, DbcSignal signal)
    {
        _file = file;
        _message = message;
        Signal = signal;
        FillRows();
    }

    public event Action? Changed;

    public DbcSignal Signal { get; private set; }

    public DbcMessage Message => _message;

    public DbcFile File => _file;

    public IReadOnlyList<DbcNode> Nodes => _file.Nodes;

    public ObservableCollection<DbcValueRow> Values { get; } = [];

    public bool HexMode { get; private set; }

    [ObservableProperty]
    private string _errorText = string.Empty;

    public bool IsExtendedMultiplexing => Signal.IsMultiplexor && Signal.IsMultiplexed;

    public void SetSignal(DbcSignal signal)
    {
        Signal = signal;
        FillRows();
        OnPropertyChanged(nameof(Signal));
        OnPropertyChanged(nameof(IsExtendedMultiplexing));
        Changed?.Invoke();
    }

    public void ClearError() => ErrorText = string.Empty;

    public void CommitName(string text)
    {
        var name = DbcEditor.SanitizeName(text);
        if (name.Length == 0 || name == Signal.Name)
        {
            return;
        }

        if (!DbcEditor.RenameSignal(_message, Signal, name))
        {
            ErrorText = $"A signal named {name} already exists in this message.";
            return;
        }

        _file.IsDirty = true;
        ClearError();
        Changed?.Invoke();
    }

    public void CommitByteOrder(bool littleEndian)
    {
        if (Signal.IntelByteOrder == littleEndian)
        {
            return;
        }

        Signal.IntelByteOrder = littleEndian;
        _file.IsDirty = true;
        Changed?.Invoke();
    }

    public void CommitBitLength(string text)
    {
        var size = (int)CanIdParser.Parse(text);
        if (size < 1 || size > Math.Max(1, (int)_message.Length * 8))
        {
            return;
        }

        // Float signals have a fixed width (DBCSignalEditor).
        if (Signal.ValueType == DbcSignalValueType.SingleFloat)
        {
            size = 32;
        }
        else if (Signal.ValueType == DbcSignalValueType.DoubleFloat)
        {
            size = 64;
        }

        if (Signal.SignalSize == size)
        {
            return;
        }

        Signal.SignalSize = size;
        _file.IsDirty = true;
        ClearError();
        Changed?.Invoke();
    }

    public void CommitValueType(int index)
    {
        var type = index switch
        {
            1 => DbcSignalValueType.SignedInt,
            2 => DbcSignalValueType.SingleFloat,
            3 => DbcSignalValueType.DoubleFloat,
            4 => DbcSignalValueType.String,
            _ => DbcSignalValueType.UnsignedInt,
        };

        if (Signal.ValueType == type)
        {
            return;
        }

        Signal.ValueType = type;
        switch (type)
        {
            case DbcSignalValueType.SingleFloat:
            {
                var maxBit = Math.Max(0, (int)_message.Length * 8 - 32 + 7);
                if (Signal.StartBit > maxBit)
                {
                    Signal.StartBit = maxBit;
                }

                Signal.SignalSize = 32;
                break;
            }

            case DbcSignalValueType.DoubleFloat:
            {
                var maxBit = Math.Max(0, (int)_message.Length * 8 - 64 + 7);
                if (Signal.StartBit > maxBit)
                {
                    Signal.StartBit = maxBit;
                }

                Signal.SignalSize = 64;
                break;
            }
        }

        _file.IsDirty = true;
        ClearError();
        Changed?.Invoke();
    }

    public void CommitDouble(string text, Action<DbcSignal, double> apply)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return;
        }

        apply(Signal, value);
        _file.IsDirty = true;
        ClearError();
        Changed?.Invoke();
    }

    public void CommitUnit(string text)
    {
        Signal.UnitName = DbcEditor.SanitizeName(text);
        _file.IsDirty = true;
    }

    public void CommitComment(string text)
    {
        Signal.Comment = DbcEditor.SanitizeName(text);
        _file.IsDirty = true;
    }

    public void CommitReceiver(string? name)
    {
        var node = _file.Nodes.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (node is null || node == Signal.Receiver)
        {
            return;
        }

        Signal.Receiver = node;
        _file.IsDirty = true;
        ClearError();
        Changed?.Invoke();
    }

    public void CommitMultiplexMode(DbcMultiplexMode mode)
    {
        DbcEditor.SetMultiplexMode(_message, Signal, mode);

        _file.IsDirty = true;
        ClearError();
        OnPropertyChanged(nameof(IsExtendedMultiplexing));
        Changed?.Invoke();
    }

    public void CommitMultiplexValues(string text)
    {
        if (!DbcEditor.TryParseMultiplexValues(text, out var ranges, out var error))
        {
            ErrorText = "The multiplex values field contains errors: " + error;
            return;
        }

        DbcEditor.SetMultiplexRanges(Signal, ranges);
        _file.IsDirty = true;
        ClearError();
        Changed?.Invoke();
    }

    public void CommitMultiplexParent(string? name)
    {
        var parent = _message.Signals.Values.FirstOrDefault(signal =>
            signal != Signal && string.Equals(signal.Name, name, StringComparison.Ordinal) && signal.IsMultiplexor);
        if (parent is null)
        {
            return;
        }

        if (DbcEditor.SetMultiplexParent(Signal, parent))
        {
            _file.IsDirty = true;
            ClearError();
            Changed?.Invoke();
        }
    }

    public void ClearMultiplexParent()
    {
        if (DbcEditor.SetMultiplexParent(Signal, null))
        {
            _file.IsDirty = true;
            Changed?.Invoke();
        }
    }

    public void SetStartBit(int bit)
    {
        bit = Math.Max(0, bit);
        if (Signal.ValueType == DbcSignalValueType.SingleFloat)
        {
            var maxBit = Math.Max(0, (int)_message.Length * 8 - 32 + 7);
            bit = Math.Min(bit, maxBit);
        }
        else if (Signal.ValueType == DbcSignalValueType.DoubleFloat)
        {
            var maxBit = Math.Max(0, (int)_message.Length * 8 - 64 + 7);
            bit = Math.Min(bit, maxBit);
        }

        Signal.StartBit = bit;
        _file.IsDirty = true;
        Changed?.Invoke();
    }

    /// <summary>Right-click on the grid edits the signal that owns that bit, if any.</summary>
    public DbcSignal? FindSignalAtBit(int bit)
    {
        foreach (var signal in _message.Signals.Values)
        {
            if (DbcEditor.BitPositions(signal.StartBit, signal.SignalSize, signal.IntelByteOrder).Contains(bit))
            {
                return signal;
            }
        }

        return null;
    }

    public void SetHexMode(bool hex)
    {
        if (HexMode == hex)
        {
            return;
        }

        HexMode = hex;
        foreach (var row in Values)
        {
            if (row.IsBlank)
            {
                continue;
            }

            row.ValueText = hex
                ? "0x" + row.ParsedValue.ToString("X", CultureInfo.InvariantCulture)
                : row.ParsedValue.ToString(CultureInfo.InvariantCulture);
        }
    }

    public void DeleteRow(DbcValueRow row)
    {
        if (Values.Contains(row))
        {
            Values.Remove(row);
        }

        SyncRows();
    }

    /// <summary>Writes the table back to the signal, keeping one blank row at the end.</summary>
    public void SyncRows()
    {
        if (_suppress)
        {
            return;
        }

        _suppress = true;
        try
        {
            var entries = Values
                .Where(row => !row.IsBlank)
                .Select(row => new DbcValueEntry(
                    row.ParsedValue,
                    row.Description.Length == 0 ? "No Description" : DbcEditor.SanitizeName(row.Description)))
                .ToList();

            Signal.ValueDescriptions.Clear();
            Signal.ValueDescriptions.AddRange(entries);

            if (Values.Count == 0 || !Values[^1].IsBlank)
            {
                Values.Add(new DbcValueRow());
            }

            _file.IsDirty = true;
            ClearError();
        }
        finally
        {
            _suppress = false;
        }
    }

    private void FillRows()
    {
        _suppress = true;
        try
        {
            Values.Clear();
            foreach (var entry in Signal.ValueDescriptions)
            {
                Values.Add(new DbcValueRow
                {
                    ValueText = HexMode
                        ? "0x" + entry.Value.ToString("X", CultureInfo.InvariantCulture)
                        : entry.Value.ToString(CultureInfo.InvariantCulture),
                    Description = entry.Description,
                });
            }

            Values.Add(new DbcValueRow());
        }
        finally
        {
            _suppress = false;
        }
    }
}
