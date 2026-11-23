using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CanYeAye.Core.Dbc;

namespace CanYeAye.App.ViewModels;

/// <summary>
/// The message editor (dbc/dbcmessageeditor): ID, extended flag, name, length, CAN FD / bit rate
/// switch, text and background colours (GenMsgForegroundColor / GenMsgBackgroundColor), sender
/// and comment, with the coloured sample line.
/// </summary>
public partial class MessageEditorViewModel : ObservableObject
{
    private readonly DbcFile _file;

    public MessageEditorViewModel(DbcFile file, DbcMessage message)
    {
        _file = file;
        Message = message;
        RefreshSample();
    }

    public event Action? Changed;

    public DbcMessage Message { get; }

    public IReadOnlyList<DbcNode> Nodes => _file.Nodes;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private IBrush _sampleForeground = Brushes.Black;

    [ObservableProperty]
    private IBrush _sampleBackground = Brushes.White;

    public void CommitId(string text)
    {
        var id = (uint)(CanYeAye.Core.CanIdParser.Parse(text) & 0x1FFFFFFF);
        if (id == 0)
        {
            ErrorText = "The frame ID must be greater than zero.";
            return;
        }

        if (!DbcEditor.ChangeMessageId(_file, Message, id))
        {
            ErrorText = $"Message 0x{id:X} already exists.";
            return;
        }

        ErrorText = string.Empty;
        Changed?.Invoke();
    }

    public void CommitName(string text)
    {
        var name = DbcEditor.SanitizeName(text);
        if (name.Length == 0)
        {
            return;
        }

        Message.Name = name;
        _file.IsDirty = true;
        Changed?.Invoke();
    }

    public void CommitLength(string text)
    {
        var length = (uint)CanYeAye.Core.CanIdParser.Parse(text);
        if (length > 64)
        {
            length = 64;
        }

        Message.Length = length;
        if (length > 8)
        {
            Message.IsCanFd = true;
        }

        _file.IsDirty = true;
        Changed?.Invoke();
    }

    public void CommitComment(string text)
    {
        Message.Comment = text;
        _file.IsDirty = true;
        Changed?.Invoke();
    }

    public void SetExtended(bool extended)
    {
        if (Message.IsExtended == extended)
        {
            return;
        }

        Message.IsExtended = extended;
        _file.IsDirty = true;
        Changed?.Invoke();
    }

    public void SetCanFd(bool canFd)
    {
        if (Message.IsCanFd == canFd && (!canFd || Message.Length <= 8))
        {
            return;
        }

        Message.IsCanFd = canFd;
        if (!canFd && Message.Length > 8)
        {
            // Classic CAN carries at most 8 bytes.
            Message.Length = 8;
        }

        _file.IsDirty = true;
        Changed?.Invoke();
    }

    public void SetBitRateSwitch(bool switchEnabled)
    {
        if (Message.BitRateSwitch == switchEnabled)
        {
            return;
        }

        Message.BitRateSwitch = switchEnabled;
        _file.IsDirty = true;
    }

    public void CommitSender(string? name)
    {
        var text = DbcEditor.SanitizeName(name);
        if (text.Length == 0)
        {
            return;
        }

        var node = DbcEditor.FindNode(_file, text);
        if (node is null)
        {
            // An unknown sender name becomes a new node, as in the reference combo box.
            node = new DbcNode { Name = text };
            _file.Nodes.Add(node);
        }

        if (node == Message.Sender)
        {
            return;
        }

        Message.Sender = node;
        _file.IsDirty = true;
        Changed?.Invoke();
    }

    public void SetColor(bool foreground, Color color)
    {
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        var attributeName = foreground ? "GenMsgForegroundColor" : "GenMsgBackgroundColor";

        if (foreground)
        {
            Message.ForegroundColor = hex;
        }
        else
        {
            Message.BackgroundColor = hex;
        }

        var attribute = Message.Attributes.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, attributeName, StringComparison.OrdinalIgnoreCase));
        if (attribute is null)
        {
            Message.Attributes.Add(new DbcAttributeValue { Name = attributeName, Value = hex });
        }
        else
        {
            attribute.Value = hex;
        }

        _file.IsDirty = true;
        RefreshSample();
        Changed?.Invoke();
    }

    public void RefreshSample()
    {
        SampleForeground = ParseColor(Message.ForegroundColor, GetDefault("GenMsgForegroundColor"), Colors.Black);
        SampleBackground = ParseColor(Message.BackgroundColor, GetDefault("GenMsgBackgroundColor"), Colors.White);
    }

    private string? GetDefault(string attributeName) =>
        _file.FindAttribute(attributeName)?.DefaultValue as string;

    private static IBrush ParseColor(string? value, string? fallback, Color ultimateFallback)
    {
        if (Color.TryParse(value, out var colour))
        {
            return new SolidColorBrush(colour);
        }

        if (Color.TryParse(fallback, out var defaultColour))
        {
            return new SolidColorBrush(defaultColour);
        }

        return new SolidColorBrush(ultimateFallback);
    }
}
