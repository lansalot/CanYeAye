using Avalonia.Controls;
using Avalonia.Interactivity;
using CanYeAye.App.Services;
using CanYeAye.Core;
using CanYeAye.Core.Dbc;

namespace CanYeAye.App.Views;

/// <summary>
/// Rebase or duplicate every message of a node (dbc/dbcnoderebaseeditor and
/// dbcnodeduplicateeditor share this dialog).
/// </summary>
public partial class NodeBaseWindow : Window
{
    private readonly DbcFile _file;
    private readonly DbcNode _node;
    private readonly bool _duplicate;
    private readonly WindowGeometry _geometry;

    public NodeBaseWindow()
    {
        InitializeComponent();
        _file = null!;
        _node = null!;
        _geometry = null!;
    }

    public NodeBaseWindow(DbcFile file, DbcNode node, bool duplicate, WindowGeometry? geometry = null)
    {
        InitializeComponent();

        _file = file;
        _node = node;
        _duplicate = duplicate;
        _geometry = geometry ?? new WindowGeometry();
        _geometry.Apply(this);
        Closed += (_, _) => _geometry.Capture(this);

        var lowest = file.Messages.Values
            .Where(message => message.Sender == node)
            .Select(message => (uint?)message.Id)
            .Min() ?? 0;

        Title = duplicate ? "Duplicate Node" : "Rebase All Node Messages";
        NewNameLabel.IsVisible = duplicate;
        NewNameBox.IsVisible = duplicate;
        ApplyButton.Content = duplicate ? "Duplicate Node" : "Rebase All Messages in Node";
        NewNameBox.Text = node.Name + "_copy";

        NodeNameBox.Text = node.Name;
        CurrentBaseBox.Text = "0x" + CanIdParser.FormatId(lowest, lowest > 0x7FF);
        NewBaseBox.Text = CurrentBaseBox.Text;
        NewBaseBox.SelectAll();
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        var newBase = (uint)(CanIdParser.Parse(NewBaseBox.Text) & 0x1FFFFFFF);

        var error = _duplicate
            ? DbcEditor.DuplicateNode(_file, _node, NewNameBox.Text ?? string.Empty, newBase, out _)
            : DbcEditor.RebaseNode(_file, _node, newBase);

        if (error is not null)
        {
            ErrorText.Text = error;
            return;
        }

        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
