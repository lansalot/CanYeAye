using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCAN.App.Services;
using WinCAN.Core.Dbc;

namespace WinCAN.App.ViewModels;

public partial class DbcFileEntry : ObservableObject
{
    public DbcFileEntry(DbcFile file) => File = file;

    public DbcFile File { get; }

    [ObservableProperty]
    private int _order;

    public string Name => File.FileName;

    public int MessageCount => File.Messages.Count;

    public int SignalCount => File.Messages.Values.Sum(message => message.Signals.Count);

    public string FilePath => File.FilePath;
}

/// <summary>
/// DBC management: load several files, remove them, and order them. The database resolves
/// messages file by file in this order, so the order is the priority.
/// </summary>
public partial class DbcManagerViewModel : ObservableObject
{
    private readonly DbcDatabase _database;
    private readonly IFileDialogService _dialogs;
    private readonly Action _changed;

    public DbcManagerViewModel(DbcDatabase database, IFileDialogService dialogs, Action changed)
    {
        _database = database;
        _dialogs = dialogs;
        _changed = changed;

        foreach (var file in database.LoadedFiles)
        {
            Files.Add(new DbcFileEntry(file));
        }

        Renumber();
        SelectedFile = Files.FirstOrDefault();
    }

    public ObservableCollection<DbcFileEntry> Files { get; } = [];

    [ObservableProperty]
    private DbcFileEntry? _selectedFile;

    [RelayCommand]
    private async Task AddAsync()
    {
        var paths = await _dialogs.PickOpenDbcsAsync();
        if (paths.Count == 0)
        {
            return;
        }

        var errors = new List<string>();
        var added = 0;
        foreach (var path in paths)
        {
            try
            {
                Files.Add(new DbcFileEntry(_database.Load(path)));
                added++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Renumber();
        if (added > 0)
        {
            _changed();
        }

        if (errors.Count > 0)
        {
            await _dialogs.ShowMessageAsync("DBC load", string.Join(Environment.NewLine, errors));
        }
    }

    [RelayCommand]
    private void Remove()
    {
        if (SelectedFile is null)
        {
            return;
        }

        var index = Files.IndexOf(SelectedFile);
        _database.LoadedFiles.Remove(SelectedFile.File);
        Files.Remove(SelectedFile);
        Renumber();
        SelectedFile = Files.Count > 0 ? Files[Math.Clamp(index, 0, Files.Count - 1)] : null;
        _changed();
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedFile is null)
        {
            return;
        }

        var index = Files.IndexOf(SelectedFile);
        if (index <= 0)
        {
            return;
        }

        (Files[index - 1], Files[index]) = (Files[index], Files[index - 1]);
        Renumber();
        SyncOrder();
        _changed();
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedFile is null)
        {
            return;
        }

        var index = Files.IndexOf(SelectedFile);
        if (index < 0 || index >= Files.Count - 1)
        {
            return;
        }

        (Files[index + 1], Files[index]) = (Files[index], Files[index + 1]);
        Renumber();
        SyncOrder();
        _changed();
    }

    [RelayCommand]
    private void Clear()
    {
        Files.Clear();
        _database.LoadedFiles.Clear();
        SelectedFile = null;
        _changed();
    }

    private void Renumber()
    {
        for (var i = 0; i < Files.Count; i++)
        {
            Files[i].Order = i + 1;
        }
    }

    private void SyncOrder()
    {
        _database.LoadedFiles.Clear();
        _database.LoadedFiles.AddRange(Files.Select(entry => entry.File));
    }
}
