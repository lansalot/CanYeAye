using WinCAN.Core;
using WinCAN.Core.Formatting;

namespace WinCAN.App.ViewModels;

/// <summary>Formatted row for the frame grid. Keeps the underlying frame for editing/export.</summary>
public sealed class FrameRowViewModel
{
    public FrameRowViewModel(CanFrame frame)
    {
        Frame = frame;
    }

    public CanFrame Frame { get; }

    public string Time => Frame.Timestamp.ToString();

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
}

