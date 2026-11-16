using CommunityToolkit.Mvvm.ComponentModel;
using WinCAN.Core;

namespace WinCAN.App.ViewModels;

/// <summary>One entry in the frame-ID filter list (checked = show that ID).</summary>
public partial class FrameFilterItem : ObservableObject
{
    private readonly Action<FrameFilterItem> _changed;

    public FrameFilterItem(
        uint id,
        bool extended,
        string label,
        Action<FrameFilterItem> changed,
        bool isChecked = true)
    {
        Id = id;
        Extended = extended;
        Label = label;
        _changed = changed;
        _isChecked = isChecked;
    }

    public uint Id { get; }

    public bool Extended { get; }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private bool _isChecked = true;

    partial void OnIsCheckedChanged(bool value) => _changed(this);
}

/// <summary>One entry in the bus filter list (checked = show that bus).</summary>
public partial class BusFilterItem : ObservableObject
{
    private readonly Action<BusFilterItem> _changed;

    public BusFilterItem(byte bus, Action<BusFilterItem> changed)
    {
        Bus = bus;
        Label = bus.ToString();
        _changed = changed;
    }

    public byte Bus { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isChecked = true;

    partial void OnIsCheckedChanged(bool value) => _changed(this);
}

/// <summary>Editable name for a J1939 source address; its checkbox selects the node's IDs.</summary>
public partial class NodeNameItem : ObservableObject
{
    private readonly Action<int, string?> _applyName;
    private readonly Action<NodeNameItem> _selectionChanged;

    public NodeNameItem(int address, string? name, Action<int, string?> applyName, Action<NodeNameItem> selectionChanged)
    {
        Address = address;
        _name = name ?? string.Empty;
        _applyName = applyName;
        _selectionChanged = selectionChanged;
    }

    public int Address { get; }

    public string AddressText => WinCAN.Core.Analysis.J1939NodeNames.FormatAddress(Address);

    [ObservableProperty]
    private string _name;

    /// <summary>True/false select or clear all of this node's IDs; null when they are mixed.</summary>
    [ObservableProperty]
    private bool? _isChecked = true;

    partial void OnNameChanged(string value) => _applyName(Address, value);

    partial void OnIsCheckedChanged(bool? value) => _selectionChanged(this);
}
