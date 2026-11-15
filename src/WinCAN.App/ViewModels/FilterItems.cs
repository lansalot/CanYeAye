using CommunityToolkit.Mvvm.ComponentModel;
using WinCAN.Core;

namespace WinCAN.App.ViewModels;

/// <summary>One entry in the frame-ID filter list (checked = show that ID).</summary>
public partial class FrameFilterItem : ObservableObject
{
    private readonly Action<FrameFilterItem> _changed;

    public FrameFilterItem(uint id, bool extended, string label, Action<FrameFilterItem> changed)
    {
        Id = id;
        Extended = extended;
        Label = label;
        _changed = changed;
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
