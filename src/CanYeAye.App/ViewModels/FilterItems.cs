using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CanYeAye.Core;

namespace CanYeAye.App.ViewModels;

/// <summary>
/// One entry in the frame-ID filter list. The first checkbox shows/hides the ID's rows, the
/// second (with the colour swatch) tints them.
/// </summary>
public partial class FrameFilterItem : ObservableObject
{
    private readonly Action<FrameFilterItem> _changed;
    private readonly Action<FrameFilterItem> _highlightChanged;

    public FrameFilterItem(
        uint id,
        bool extended,
        string label,
        Action<FrameFilterItem> changed,
        Action<FrameFilterItem> highlightChanged,
        bool isChecked = true,
        IBrush? highlightBrush = null)
    {
        Id = id;
        Extended = extended;
        Label = label;
        _changed = changed;
        _highlightChanged = highlightChanged;
        _isChecked = isChecked;
        _highlightBrush = highlightBrush;
    }

    public uint Id { get; }

    public bool Extended { get; }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private bool _isChecked = true;

    /// <summary>Shown as the swatch next to the highlight checkbox and as the row tint.</summary>
    [ObservableProperty]
    private IBrush? _highlightBrush;

    /// <summary>True when rows with this ID are tinted in the frame grid.</summary>
    [ObservableProperty]
    private bool _isHighlighted;

    partial void OnIsCheckedChanged(bool value) => _changed(this);

    partial void OnIsHighlightedChanged(bool value) => _highlightChanged(this);
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
    /// <summary>Pseudo address used by the Broadcast row (all broadcast destinations).</summary>
    public const int BroadcastAddress = -1;

    private readonly Action<int, string?> _applyName;
    private readonly Action<NodeNameItem> _selectionChanged;

    public NodeNameItem(
        int address,
        string? name,
        Action<int, string?> applyName,
        Action<NodeNameItem> selectionChanged,
        bool isBroadcast = false)
    {
        Address = address;
        IsBroadcast = isBroadcast;
        _name = name ?? string.Empty;
        _applyName = applyName;
        _selectionChanged = selectionChanged;
    }

    public int Address { get; }

    /// <summary>True for the special row that covers every broadcast destination.</summary>
    public bool IsBroadcast { get; }

    public bool IsEditable => !IsBroadcast;

    public string AddressText => IsBroadcast
        ? "Broadcast"
        : CanYeAye.Core.Analysis.J1939NodeNames.FormatAddress(Address);

    [ObservableProperty]
    private string _name;

    /// <summary>True/false select or clear all of this node's IDs; null when they are mixed.</summary>
    [ObservableProperty]
    private bool? _isChecked = true;

    partial void OnNameChanged(string value) => _applyName(Address, value);

    partial void OnIsCheckedChanged(bool? value) => _selectionChanged(this);
}
