using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CanYeAye.App.Services;
using CanYeAye.App.ViewModels;

namespace CanYeAye.App.Views;

/// <summary>
/// The DBC editing window (dbc/dbcmaineditor): tree of nodes → messages → signals, the new /
/// delete operations, search and the node rebase / duplicate dialogs. Double-clicking an item
/// opens the matching editor; edits apply to the shared DBC file immediately.
/// </summary>
public partial class DbcEditorWindow : Window
{
    private readonly DbcEditorViewModel _viewModel;
    private readonly IFileDialogService _dialogs;
    private readonly Action? _changed;
    private readonly DbcWindowState _windows;
    private Views.NodeEditorWindow? _nodeEditor;

    public DbcEditorWindow()
    {
        InitializeComponent();
        _viewModel = null!;
        _dialogs = null!;
        _windows = null!;
    }

    public DbcEditorWindow(CanYeAye.Core.Dbc.DbcFile file, Action? changed = null, DbcWindowState? windows = null)
    {
        InitializeComponent();

        _changed = changed;
        _windows = windows ?? new DbcWindowState();
        _viewModel = new DbcEditorViewModel(file, changed);
        _dialogs = new FileDialogService(() => this);
        DataContext = _viewModel;
        Title = _viewModel.Title;
        SearchPosText.Text = _viewModel.SearchStatus;
        _windows.Editor.Apply(this);

        Loaded += (_, _) => _viewModel.Rebuild();
        Closed += (_, _) =>
        {
            _windows.Editor.Capture(this);
            _nodeEditor?.Close();
        };
    }

    private void OnNewNode(object? sender, RoutedEventArgs e)
    {
        _viewModel.NewNode();
        SearchPosText.Text = _viewModel.SearchStatus;
    }

    private void OnNewMessage(object? sender, RoutedEventArgs e)
    {
        _viewModel.NewMessage();
        SearchPosText.Text = _viewModel.SearchStatus;
    }

    private void OnNewSignal(object? sender, RoutedEventArgs e)
    {
        _viewModel.NewSignal();
        SearchPosText.Text = _viewModel.SearchStatus;
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        var message = _viewModel.DescribeDeletion();
        if (message is null)
        {
            return;
        }

        if (await _dialogs.ConfirmAsync("Delete", message + Environment.NewLine + Environment.NewLine + "Continue?"))
        {
            _viewModel.DeleteSelected();
            SearchPosText.Text = _viewModel.SearchStatus;
        }
    }

    private void OnSearch(object? sender, RoutedEventArgs e)
    {
        _viewModel.SearchText = SearchBox.Text ?? string.Empty;
        _viewModel.Search();
        SearchPosText.Text = _viewModel.SearchStatus;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        OnSearch(sender, e);
        e.Handled = true;
    }

    private void OnSearchNext(object? sender, RoutedEventArgs e)
    {
        _viewModel.SearchForward();
        SearchPosText.Text = _viewModel.SearchStatus;
    }

    private void OnSearchPrevious(object? sender, RoutedEventArgs e)
    {
        _viewModel.SearchBackward();
        SearchPosText.Text = _viewModel.SearchStatus;
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e) => EditSelected();

    private void OnEditItem(object? sender, RoutedEventArgs e) => EditSelected();

    private async void EditSelected()
    {
        if (_viewModel.SelectedItem is not { } item)
        {
            return;
        }

        switch (item.Kind)
        {
            case DbcTreeItemKind.Node:
            {
                if (_nodeEditor is { IsVisible: true })
                {
                    _nodeEditor.Close();
                }

                _nodeEditor = new NodeEditorWindow(_viewModel.File, item.Node!, _windows.Node);
                await _nodeEditor.ShowDialog(this);
                _nodeEditor = null;
                break;
            }

            case DbcTreeItemKind.Message:
            {
                var editor = new MessageEditorWindow(_viewModel.File, item.Message!, _windows.Message);
                await editor.ShowDialog(this);
                break;
            }

            case DbcTreeItemKind.Signal:
            {
                var signal = item.Signal!;
                var editor = new SignalEditorWindow(_viewModel.File, signal.ParentMessage, signal, _windows.Signal);
                await editor.ShowDialog(this);
                break;
            }
        }

        _viewModel.Rebuild(item.Model);
        _changed?.Invoke();
    }

    private async void OnRebaseNode(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem?.Node is not { } node)
        {
            await _dialogs.ShowMessageAsync("Rebase", "Select a node first.");
            return;
        }

        var window = new NodeBaseWindow(_viewModel.File, node, duplicate: false, geometry: _windows.NodeBase);
        await window.ShowDialog(this);
        _viewModel.Rebuild(node);
        _changed?.Invoke();
    }

    private async void OnDuplicateNode(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem?.Node is not { } node)
        {
            await _dialogs.ShowMessageAsync("Duplicate", "Select a node first.");
            return;
        }

        var window = new NodeBaseWindow(_viewModel.File, node, duplicate: true, geometry: _windows.NodeBase);
        await window.ShowDialog(this);
        _viewModel.Rebuild(node);
        _changed?.Invoke();
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Tree).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.Source is Control source &&
            source.FindAncestorOfType<TreeViewItem>()?.DataContext is DbcTreeItem item)
        {
            _viewModel.SelectedItem = item;
        }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _viewModel.SelectedItem is null)
        {
            return;
        }

        e.Handled = true;
        OnDelete(sender, new RoutedEventArgs());
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
