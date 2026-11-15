using WinCAN.Core;

namespace WinCAN.Core.Dbc;

/// <summary>
/// The process-wide DBC collection (DBCHandler). Holds loaded files in load order and resolves a
/// frame to the first matching message. WinCAN persists this through its own settings store
/// rather than touching SavvyCAN's QSettings keys.
/// </summary>
public sealed class DbcDatabase
{
    public List<DbcFile> LoadedFiles { get; } = [];

    public DbcFile Load(string path)
    {
        var file = DbcFileReader.Load(path, out _);
        LoadedFiles.Add(file);
        return file;
    }

    public bool Remove(DbcFile file) => LoadedFiles.Remove(file);

    public DbcMessage? FindMessage(CanFrame frame)
    {
        foreach (var file in LoadedFiles)
        {
            if (file.AssociatedBus != -1 && frame.Bus != file.AssociatedBus)
            {
                continue;
            }

            var message = file.FindMessageById(frame.FrameId);
            if (message is not null)
            {
                return message;
            }
        }

        return null;
    }

    public DbcMessage? FindMessage(uint id)
    {
        foreach (var file in LoadedFiles)
        {
            var message = file.FindMessageById(id);
            if (message is not null)
            {
                return message;
            }
        }

        return null;
    }

    public DbcMessage? FindMessage(string name)
    {
        foreach (var file in LoadedFiles)
        {
            var message = file.FindMessageByName(name);
            if (message is not null)
            {
                return message;
            }
        }

        return null;
    }

    public DbcMessage? FindMessageForFilter(uint id, out MatchingCriteria criteria)
    {
        criteria = MatchingCriteria.Exact;
        foreach (var file in LoadedFiles)
        {
            if (!file.FilterLabeling)
            {
                continue;
            }

            var message = file.FindMessageById(id);
            if (message is not null)
            {
                criteria = file.MatchingCriteria;
                return message;
            }
        }

        return null;
    }
}
