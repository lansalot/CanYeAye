using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CanYeAye.App.Services;
using CanYeAye.Core.Dbc;

namespace CanYeAye.App.Views;

/// <summary>The node editor (dbc/dbcnodeeditor): name and comment.</summary>
public partial class NodeEditorWindow : Window
{
    private readonly DbcFile _file;
    private readonly DbcNode _node;
    private readonly WindowGeometry _geometry;

    public NodeEditorWindow()
    {
        InitializeComponent();
        _file = null!;
        _node = null!;
        _geometry = null!;
    }

    public NodeEditorWindow(DbcFile file, DbcNode node, WindowGeometry? geometry = null)
    {
        InitializeComponent();

        _file = file;
        _node = node;
        _geometry = geometry ?? new WindowGeometry();
        NameBox.Text = node.Name;
        CommentBox.Text = node.Comment;
        _geometry.Apply(this);
        Closed += (_, _) => _geometry.Capture(this);
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e) => CommitName();

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitName();
            e.Handled = true;
        }
    }

    private void CommitName()
    {
        var name = DbcEditor.SanitizeName(NameBox.Text);
        if (name.Length == 0 || name == _node.Name)
        {
            return;
        }

        _node.Name = name;
        _file.IsDirty = true;
    }

    private void OnCommentLostFocus(object? sender, RoutedEventArgs e)
    {
        _node.Comment = CommentBox.Text ?? string.Empty;
        _file.IsDirty = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
