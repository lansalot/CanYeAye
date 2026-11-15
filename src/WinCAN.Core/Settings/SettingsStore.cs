using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinCAN.Core.Settings;

/// <summary>Saved geometry and visibility of one frame-grid column.</summary>
public sealed record ColumnLayout(string Header, double Width, string Unit, int DisplayIndex, bool Visible);

/// <summary>
/// WinCAN's own settings store. Deliberately independent of SavvyCAN's QSettings
/// registry keys (see AGENTS.md): same file formats, separate preferences.
/// </summary>
public sealed record AppSettings
{
    public string Theme { get; init; } = "Default";

    public List<string> RecentFiles { get; init; } = [];

    public string? LastConnectionType { get; init; }

    public string? LastSerialPort { get; init; }

    public int LastBaudRate { get; init; } = 115200;

    public string? SocketCandHost { get; init; }

    public int SocketCandPort { get; init; } = 29536;

    public bool TimestampAbsolute { get; init; }

    public bool ShowDecimalIds { get; init; }

    public int WindowWidth { get; init; } = 1280;

    public int WindowHeight { get; init; } = 800;

    public int WindowX { get; init; } = int.MinValue;

    public int WindowY { get; init; } = int.MinValue;

    public bool ShowLog { get; init; }

    public bool AutoScrollWindow { get; init; } = true;

    public bool OverwriteMode { get; init; }

    public bool InterpretFrames { get; init; }

    public bool KeepFiltersWhenClearing { get; init; }

    public bool RowsExpanded { get; init; }

    public List<string> DbcFiles { get; init; } = [];

    public List<ColumnLayout> Columns { get; init; } = [];
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultFilePath;
    }

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinCAN",
        "settings.json");

    public string FilePath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, SerializerOptions));
    }

    public AppSettings AddRecentFile(AppSettings settings, string path)
    {
        var recent = settings.RecentFiles
            .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
            .Prepend(path)
            .Take(10)
            .ToList();
        return settings with { RecentFiles = recent };
    }
}
