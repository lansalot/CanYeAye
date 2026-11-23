using CommunityToolkit.Mvvm.ComponentModel;
using CanYeAye.App.Formatting;
using CanYeAye.Core;
using CanYeAye.Core.Analysis;
using CanYeAye.Core.Formatting;
using CanYeAye.Core.Protocols;

namespace CanYeAye.App.ViewModels;

/// <summary>
/// Formatted row for the frame grid. Keeps the underlying frame for editing/export.
/// Mutable so overwrite mode can update a row in place without losing selection.
/// </summary>
public partial class FrameRowViewModel : ObservableObject
{
    private CanFrame _frame;
    private J1939NodeNames? _nodeNames;

    public FrameRowViewModel(CanFrame frame)
    {
        _frame = frame;
    }

    public CanFrame Frame => _frame;

    public void AttachNodeNames(J1939NodeNames names) => _nodeNames = names;

    public void RefreshNodes() => OnPropertyChanged(nameof(NodeText));

    /// <summary>J1939 source → destination, using configured node names when available.</summary>
    public string NodeText
    {
        get
        {
            if (_nodeNames is null || !Frame.IsExtended)
            {
                return string.Empty;
            }

            var id = J1939Id.Decode(Frame.FrameId);
            var source = _nodeNames.GetLabel(id.Source);
            var destination = id.IsBroadcast || id.Destination == 0xFF
                ? "Broadcast"
                : _nodeNames.GetLabel(id.Destination);
            return $"{source} → {destination}";
        }
    }

    public void SetFrame(CanFrame frame)
    {
        _frame = frame;
        OnPropertyChanged(string.Empty); // every displayed property changed
    }

    [ObservableProperty]
    private string _interpretation = string.Empty;

    partial void OnInterpretationChanged(string value) => OnPropertyChanged(nameof(HasInterpretation));

    public bool HasInterpretation => Interpretation.Length > 0;

    /// <summary>Full decoded detail (e.g. every DTC with its failure mode) shown as a tooltip.</summary>
    [ObservableProperty]
    private string? _interpretationTip;

    public string Ascii => ToAscii(Frame.Payload);

    public string Time => TimeFormat.LocalTime(Frame.Timestamp);

    public string Bus => Frame.Bus.ToString();

    public string Type => Frame.FrameType switch
    {
        FrameType.CanDataFrame => "CAN",
        FrameType.CanFdDataFrame => "CAN FD",
        FrameType.FlexRayDataFrame => "FlexRay",
        FrameType.LinDataFrame => "LIN",
        FrameType.ErrorFrame => "Error",
        FrameType.RemoteRequestFrame => "RTR",
        _ => "Invalid",
    };

    public string Id => CanIdParser.FormatId(Frame.FrameId, Frame.IsExtended);

    public string Length => Frame.Length.ToString();

    public string Data => HexFormat.SpaceDelimited(Frame.Payload);

    public bool Extended => Frame.IsExtended;

    /// <summary>Frame count for overwrite mode (empty in normal mode).</summary>
    public string Count => Frame.FrameCount > 0 ? Frame.FrameCount.ToString() : string.Empty;

    /// <summary>Time since the previous frame of this ID in overwrite mode.</summary>
    public string Delta => Frame.TimeDelta > 0
        ? (Frame.TimeDelta / 1_000_000.0).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)
        : string.Empty;

    public string Flags
    {
        get
        {
            var flags = new List<string>(4);
            if (Frame.IsExtended)
            {
                flags.Add("EXT");
            }

            if (Frame.HasBitrateSwitch)
            {
                flags.Add("BRS");
            }

            if (Frame.HasErrorStateIndicator)
            {
                flags.Add("ESI");
            }

            if (Frame.IsLocalEcho)
            {
                flags.Add("TX");
            }

            return string.Join(' ', flags);
        }
    }

    public string Detail =>
        $"{Frame.Timestamp}  {Type}  {Id}  [{Length}]  {Data}  bus {Bus}" +
        (string.IsNullOrEmpty(Flags) ? string.Empty : $"  {Flags}");

    private static string ToAscii(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        return string.Create(payload.Length, payload.ToArray(), static (span, bytes) =>
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                span[i] = bytes[i] is >= 0x20 and < 0x7F ? (char)bytes[i] : '.';
            }
        });
    }
}


