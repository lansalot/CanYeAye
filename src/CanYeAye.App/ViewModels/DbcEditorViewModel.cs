using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CanYeAye.Core;
using CanYeAye.Core.Dbc;

namespace CanYeAye.App.ViewModels;

public enum DbcTreeItemKind
{
    Node,
    Message,
    Signal,
}

/// <summary>One row of the DBC editing tree (dbc/dbcmaineditor).</summary>
public partial class DbcTreeItem : ObservableObject
{
    public DbcTreeItem(DbcTreeItemKind kind, string text, object model)
    {
        Kind = kind;
        Text = text;
        Model = model;
    }

    public DbcTreeItemKind Kind { get; }

    public string Text { get; }

    public object Model { get; }

    public string Badge => Kind switch
    {
        DbcTreeItemKind.Node => "N",
        DbcTreeItemKind.Message => "M",
        _ => "S",
    };

    public IBrush BadgeBrush => Kind switch
    {
        DbcTreeItemKind.Node => NodeBrush,
        DbcTreeItemKind.Message => MessageBrush,
        _ => SignalBrush,
    };

    private static readonly IBrush NodeBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x7C, 0xB1));
    private static readonly IBrush MessageBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x8A, 0x4C));
    private static readonly IBrush SignalBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x6B, 0xAE));

    public ObservableCollection<DbcTreeItem> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    public DbcNode? Node => Model as DbcNode;

    public DbcMessage? Message => Model as DbcMessage;

    public DbcSignal? Signal => Model as DbcSignal;
}

/// <summary>
/// Drives the DBC editing window: the node → message → signal tree plus the create / delete and
/// search operations from DBCMainEditor. Every operation mutates the shared <see cref="DbcFile"/>
/// in place so the frame grid picks the edits up as soon as the file is marked dirty.
/// </summary>
public partial class DbcEditorViewModel : ObservableObject
{
    private readonly DbcFile _file;
    private readonly Action? _changed;
    private readonly Random _random = new();

    private List<DbcTreeItem> _searchResults = [];
    private int _searchPosition = -1;

    public DbcEditorViewModel(DbcFile file, Action? changed = null)
    {
        _file = file;
        _changed = changed;

        DbcEditor.EnsureDefaultNode(file);
        Rebuild();
    }

    public DbcFile File => _file;

    public string Title => $"DBC Editing Window - {_file.FileName}";

    public ObservableCollection<DbcTreeItem> Roots { get; } = [];

    [ObservableProperty]
    private DbcTreeItem? _selectedItem;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _searchStatus = "Search Results: 0 of 0";

    public bool IsNodeSelected => SelectedItem?.Kind == DbcTreeItemKind.Node;

    public bool IsMessageSelected => SelectedItem?.Kind == DbcTreeItemKind.Message;

    public bool IsSignalSelected => SelectedItem?.Kind == DbcTreeItemKind.Signal;

    public bool HasSelection => SelectedItem is not null;

    public bool CanAddSignal => SelectedItem?.Kind is DbcTreeItemKind.Message or DbcTreeItemKind.Signal;

    public bool CanAddMessage => SelectedItem is not null;

    partial void OnSelectedItemChanged(DbcTreeItem? value)
    {
        OnPropertyChanged(nameof(IsNodeSelected));
        OnPropertyChanged(nameof(IsMessageSelected));
        OnPropertyChanged(nameof(IsSignalSelected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanAddSignal));
        OnPropertyChanged(nameof(CanAddMessage));
    }

    /// <summary>Rebuilds the whole tree, optionally reselecting the item that wraps <paramref name="model"/>.</summary>
    public void Rebuild(object? model = null)
    {
        Roots.Clear();

        foreach (var node in _file.Nodes)
        {
            var nodeItem = new DbcTreeItem(DbcTreeItemKind.Node, DescribeNode(node), node);
            Roots.Add(nodeItem);

            foreach (var message in _file.Messages.Values.Where(message =>
                         ReferenceEquals(message.Sender ?? _file.DefaultNode, node)))
            {
                var messageItem = new DbcTreeItem(DbcTreeItemKind.Message, DescribeMessage(message), message);
                nodeItem.Children.Add(messageItem);

                var visited = new HashSet<DbcSignal>();
                foreach (var signal in message.Signals.Values.Where(signal => signal.MultiplexParent is null))
                {
                    AddSignal(messageItem, signal, visited);
                }

                // Defensive: a signal whose multiplex parent went away is still reachable.
                foreach (var signal in message.Signals.Values.Where(signal => !visited.Contains(signal)))
                {
                    AddSignal(messageItem, signal, visited);
                }
            }
        }

        var selected = model is null ? null : FindItem(Roots, model);
        SelectedItem = selected;
        if (selected is not null)
        {
            ExpandAncestors(selected);
        }
    }

    public DbcTreeItem? NewNode()
    {
        var node = DbcEditor.AddNode(_file, "Unnamed" + _random.Next(50000));
        Rebuild(node);
        _changed?.Invoke();
        return FindItem(Roots, node);
    }

    /// <summary>
    /// Adds a message under the selected node (or the selected message/signal's node) and returns
    /// its tree item (DBCMainEditor::newMessage).
    /// </summary>
    public DbcTreeItem? NewMessage()
    {
        var selected = SelectedItem;
        if (selected is null)
        {
            return null;
        }

        var node = selected.Kind switch
        {
            DbcTreeItemKind.Node => selected.Node!,
            DbcTreeItemKind.Message => selected.Message!.Sender ?? _file.DefaultNode,
            _ => selected.Signal!.ParentMessage.Sender ?? _file.DefaultNode,
        };

        uint id;
        string name;
        uint length = 8;
        bool canFd = false;
        bool brs = true;
        string background = string.Empty;
        string foreground = string.Empty;

        var previous = selected.Kind == DbcTreeItemKind.Message
            ? selected.Message
            : selected.Kind == DbcTreeItemKind.Signal
                ? selected.Signal!.ParentMessage
                : null;

        if (previous is not null)
        {
            name = "Msg" + _random.Next(500);
            id = DbcEditor.NextFreeMessageId(_file, previous.Id + 1);
            length = previous.Length;
            canFd = previous.IsCanFd;
            brs = previous.BitRateSwitch;
            background = previous.BackgroundColor;
            foreground = previous.ForegroundColor;
        }
        else
        {
            name = DbcEditor.SanitizeName(node.Name) + "Msg" + _random.Next(500);
            id = DbcEditor.NextFreeMessageId(_file, 0x004);
        }

        var message = new DbcMessage
        {
            Id = id,
            Name = name,
            Length = length,
            IsCanFd = canFd,
            BitRateSwitch = brs,
            Sender = node,
            BackgroundColor = background,
            ForegroundColor = foreground,
        };

        if (!DbcEditor.AddMessage(_file, message))
        {
            return null;
        }

        Rebuild(message);
        _changed?.Invoke();
        return FindItem(Roots, message);
    }

    /// <summary>
    /// Adds a signal to the selected message. Selecting a signal copies it, matching
    /// DBCMainEditor::newSignal.
    /// </summary>
    public DbcTreeItem? NewSignal()
    {
        var selected = SelectedItem;
        var message = selected?.Kind switch
        {
            DbcTreeItemKind.Message => selected.Message,
            DbcTreeItemKind.Signal => selected.Signal?.ParentMessage,
            _ => null,
        };

        if (message is null)
        {
            return null;
        }

        DbcSignal signal;
        DbcSignal? sourceParent = null;
        if (selected?.Signal is { } source)
        {
            var name = UniqueSignalName(message, source.Name + _random.Next(100));
            signal = DbcEditor.CloneSignal(source, name, message);
            sourceParent = source.MultiplexParent;
        }
        else
        {
            var baseName = string.IsNullOrWhiteSpace(message.Name) ? "Signal" : message.Name;
            var name = UniqueSignalName(message, baseName + "Sig" + _random.Next(500));
            signal = new DbcSignal
            {
                Name = name,
                ParentMessage = message,
                Receiver = message.Sender ?? _file.DefaultNode,
            };
        }

        DbcEditor.AddSignal(message, signal);
        if (sourceParent is not null)
        {
            DbcEditor.SetMultiplexParent(signal, sourceParent);
        }

        _file.IsDirty = true;
        Rebuild(signal);
        _changed?.Invoke();
        return FindItem(Roots, signal);
    }

    /// <summary>Confirmation text for the current selection (DBCMainEditor::deleteCurrentTreeItem).</summary>
    public string? DescribeDeletion() => SelectedItem?.Kind switch
    {
        DbcTreeItemKind.Node =>
            $"Delete node {SelectedItem.Node!.Name}? Its messages and signals are deleted too.",
        DbcTreeItemKind.Message =>
            $"Delete message {DescribeMessage(SelectedItem.Message!)}? Its signals are deleted too.",
        DbcTreeItemKind.Signal => $"Delete signal {SelectedItem.Signal!.Name}?",
        _ => null,
    };

    public bool DeleteSelected()
    {
        if (SelectedItem is not { } item)
        {
            return false;
        }

        switch (item.Kind)
        {
            case DbcTreeItemKind.Node:
                DbcEditor.DeleteNode(_file, item.Node!);
                break;
            case DbcTreeItemKind.Message:
                DbcEditor.DeleteMessage(_file, item.Message!);
                break;
            case DbcTreeItemKind.Signal:
                DbcEditor.DeleteSignal(item.Signal!.ParentMessage, item.Signal);
                _file.IsDirty = true;
                break;
            default:
                return false;
        }

        Rebuild();
        _changed?.Invoke();
        return true;
    }

    public void Search()
    {
        var query = SearchText;
        _searchResults = string.IsNullOrEmpty(query)
            ? []
            : Flatten(Roots).Where(item => item.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _searchPosition = _searchResults.Count > 0 ? 0 : -1;
        ShowSearchResult();
    }

    public void SearchForward()
    {
        if (_searchResults.Count == 0)
        {
            return;
        }

        _searchPosition = (_searchPosition + 1) % _searchResults.Count;
        ShowSearchResult();
    }

    public void SearchBackward()
    {
        if (_searchResults.Count == 0)
        {
            return;
        }

        _searchPosition = _searchPosition <= 0 ? _searchResults.Count - 1 : _searchPosition - 1;
        ShowSearchResult();
    }

    private void ShowSearchResult()
    {
        if (_searchPosition < 0 || _searchPosition >= _searchResults.Count)
        {
            SearchStatus = "Search Results: 0 of 0";
            return;
        }

        var item = _searchResults[_searchPosition];
        SearchStatus = $"Search Results: {_searchPosition + 1} of {_searchResults.Count}";
        ExpandAncestors(item);
        foreach (var candidate in Flatten(Roots))
        {
            candidate.IsSelected = ReferenceEquals(candidate, item);
        }

        SelectedItem = item;
    }

    public DbcTreeItem? FindItem(IEnumerable<DbcTreeItem> items, object model)
    {
        foreach (var item in items)
        {
            if (ReferenceEquals(item.Model, model))
            {
                return item;
            }

            if (item.Children.Count > 0 && FindItem(item.Children, model) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void AddSignal(DbcTreeItem parent, DbcSignal signal, HashSet<DbcSignal> visited)
    {
        if (!visited.Add(signal))
        {
            return;
        }

        var item = new DbcTreeItem(DbcTreeItemKind.Signal, DescribeSignal(signal), signal);
        parent.Children.Add(item);
        foreach (var child in signal.MultiplexedChildren)
        {
            AddSignal(item, child, visited);
        }
    }

    private static IEnumerable<DbcTreeItem> Flatten(IEnumerable<DbcTreeItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Children))
            {
                yield return child;
            }
        }
    }

    private void ExpandAncestors(DbcTreeItem item)
    {
        foreach (var candidate in Flatten(Roots))
        {
            if (candidate.Children.Contains(item))
            {
                candidate.IsExpanded = true;
                ExpandAncestors(candidate);
                return;
            }
        }
    }

    private string UniqueSignalName(DbcMessage message, string candidate)
    {
        var name = DbcEditor.SanitizeName(candidate);
        if (name.Length == 0)
        {
            name = "Signal";
        }

        var unique = name;
        var suffix = 2;
        while (message.Signals.ContainsKey(unique))
        {
            unique = name + suffix++;
        }

        return unique;
    }

    private static string DescribeNode(DbcNode node) =>
        node.Comment.Length > 0 ? $"{node.Name} - {node.Comment}" : node.Name;

    private static string DescribeMessage(DbcMessage message)
    {
        var text = $"0x{CanIdParser.FormatId(message.Id, message.IsExtended)} {message.Name}";
        return message.Comment.Length > 0 ? text + " - " + message.Comment : text;
    }

    private static string DescribeSignal(DbcSignal signal)
    {
        var text = signal.IsMultiplexed ? $"({DbcEditor.FormatMultiplexValues(signal)}) " : string.Empty;
        text += signal.Name;
        text += signal.IntelByteOrder
            ? $" [{signal.StartBit}i {signal.SignalSize}]"
            : $" [{signal.StartBit}m {signal.SignalSize}]";
        return signal.Comment.Length > 0 ? text + " - " + signal.Comment : text;
    }
}
