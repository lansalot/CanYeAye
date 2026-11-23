using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanYeAye.App.Services;
using CanYeAye.Core.Dbc;

namespace CanYeAye.App.ViewModels;

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

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(MessageCount));
        OnPropertyChanged(nameof(SignalCount));
        OnPropertyChanged(nameof(FilePath));
    }
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

    public DbcManagerViewModel(DbcDatabase database, IFileDialogService dialogs, Action changed, DbcWindowState? windows = null)
    {
        _database = database;
        _dialogs = dialogs;
        _changed = changed;
        Windows = windows ?? new DbcWindowState();

        foreach (var file in database.LoadedFiles)
        {
            Files.Add(new DbcFileEntry(file));
        }

        Renumber();
        SelectedFile = Files.FirstOrDefault();
    }

    public DbcWindowState Windows { get; }

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

    /// <summary>Refresh row counts and let the main window pick up edits made in the editor.</summary>
    public void NotifyEdited()
    {
        foreach (var entry in Files)
        {
            entry.Refresh();
        }

        _changed();
    }

    /// <summary>Writes every loaded file back to disk (asking for a path for unsaved files).</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        var errors = new List<string>();
        var saved = 0;

        foreach (var entry in Files.ToArray())
        {
            var file = entry.File;
            var path = string.IsNullOrWhiteSpace(file.FilePath)
                ? await _dialogs.PickSaveDbcAsync(file.FileName)
                : Path.Combine(file.FilePath, file.FileName);

            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            try
            {
                DbcFileWriter.Save(file, path);
                entry.Refresh();
                saved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{file.FileName}: {ex.Message}");
            }
        }

        if (saved > 0)
        {
            _changed();
        }

        if (errors.Count > 0)
        {
            await _dialogs.ShowMessageAsync("DBC save", string.Join(Environment.NewLine, errors));
        }
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
