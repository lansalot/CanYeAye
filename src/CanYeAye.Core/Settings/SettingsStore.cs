using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanYeAye.Core.Settings;

/// <summary>Saved geometry and visibility of one frame-grid column.</summary>
public sealed record ColumnLayout(string Header, double Width, string Unit, int DisplayIndex, bool Visible);

/// <summary>Saved J1939 node name (source address + display name).</summary>
public sealed record NodeNameEntry(int Address, string Name);

/// <summary>Saved node position in the J1939 node graph.</summary>
public sealed record NodeGraphPosition(int Address, double X, double Y);

/// <summary>
/// CanYeAye's own settings store. Deliberately independent of SavvyCAN's QSettings
/// registry keys (see AGENTS.md): same file formats, separate preferences.
/// </summary>
public sealed record AppSettings
{
    public string Theme { get; init; } = "Default";

    public List<string> RecentFiles { get; init; } = [];

    public string? LastConnectionType { get; init; }

    public string? LastSerialPort { get; init; }

    public int LastBaudRate { get; init; } = 115200;

    public uint LastBusSpeed { get; init; } = 500_000;

    public string? SocketCandHost { get; init; }

    public int SocketCandPort { get; init; } = 29536;

    public int GvretTcpPort { get; init; } = 23;

    public int MqttPort { get; init; } = 1883;

    public int CanLogServerPort { get; init; } = 23;

    /// <summary>PCAN-USB channel (1 = USB1).</summary>
    public int PcanChannel { get; init; } = 1;

    /// <summary>Open new connections in listen-only (no transmit) mode; on by default.</summary>
    public bool LastListenOnly { get; init; } = true;

    public bool TimestampAbsolute { get; init; }

    public bool ShowDecimalIds { get; init; }

    public int WindowWidth { get; init; } = 1280;

    public int WindowHeight { get; init; } = 800;

    public int WindowX { get; init; } = int.MinValue;

    public int WindowY { get; init; } = int.MinValue;

    public bool AutoScrollWindow { get; init; } = true;

    public bool OverwriteMode { get; init; }

    public bool InterpretFrames { get; init; }

    public bool KeepFiltersWhenClearing { get; init; }

    public bool RowsExpanded { get; init; }

    public double UiScale { get; init; } = 1.0;

    public List<string> DbcFiles { get; init; } = [];

    public List<NodeNameEntry> NodeNames { get; init; } = [];

    public List<NodeGraphPosition> NodeGraphLayout { get; init; } = [];

    /// <summary>Last frame-filter host (Left, Right, Float, FullHeight).</summary>
    public string FrameFilterHost { get; init; } = "Right";

    public double NodeGraphZoom { get; init; } = 1.0;

    public double NodeGraphPanX { get; init; }

    public double NodeGraphPanY { get; init; }

    public double NodeGraphWindowWidth { get; init; } = 1050;

    public double NodeGraphWindowHeight { get; init; } = 720;

    public int NodeGraphWindowX { get; init; } = int.MinValue;

    public int NodeGraphWindowY { get; init; } = int.MinValue;

    public double FlowWindowWidth { get; init; } = 1220;

    public double FlowWindowHeight { get; init; } = 820;

    public int FlowWindowX { get; init; } = int.MinValue;

    public int FlowWindowY { get; init; } = int.MinValue;

    public double FrameInfoWindowWidth { get; init; } = 1280;

    public double FrameInfoWindowHeight { get; init; } = 860;

    public int FrameInfoWindowX { get; init; } = int.MinValue;

    public int FrameInfoWindowY { get; init; } = int.MinValue;

    public double DbcEditorWindowWidth { get; init; } = 680;

    public double DbcEditorWindowHeight { get; init; } = 800;

    public int DbcEditorWindowX { get; init; } = int.MinValue;

    public int DbcEditorWindowY { get; init; } = int.MinValue;

    public double DbcSignalWindowWidth { get; init; } = 1150;

    public double DbcSignalWindowHeight { get; init; } = 930;

    public int DbcSignalWindowX { get; init; } = int.MinValue;

    public int DbcSignalWindowY { get; init; } = int.MinValue;

    public double DbcMessageWindowWidth { get; init; } = 430;

    public double DbcMessageWindowHeight { get; init; } = 620;

    public int DbcMessageWindowX { get; init; } = int.MinValue;

    public int DbcMessageWindowY { get; init; } = int.MinValue;

    public double DbcNodeWindowWidth { get; init; } = 400;

    public double DbcNodeWindowHeight { get; init; } = 210;

    public int DbcNodeWindowX { get; init; } = int.MinValue;

    public int DbcNodeWindowY { get; init; } = int.MinValue;

    public double DbcNodeBaseWindowWidth { get; init; } = 480;

    public double DbcNodeBaseWindowHeight { get; init; } = 280;

    public int DbcNodeBaseWindowX { get; init; } = int.MinValue;

    public int DbcNodeBaseWindowY { get; init; } = int.MinValue;

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
        "CanYeAye",
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
