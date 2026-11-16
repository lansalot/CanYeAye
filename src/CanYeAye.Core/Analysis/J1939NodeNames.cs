using System.Globalization;
using System.Text;

namespace CanYeAye.Core.Analysis;

/// <summary>
/// User-assigned names for J1939 source addresses (nodes). Names are kept in CanYeAye's
/// settings but can also be exchanged as a small text file:
/// <code>
/// # CanYeAye J1939 node names
/// 0x01=Steering module
/// 0x02=ABS
/// </code>
/// </summary>
public sealed class J1939NodeNames
{
    private readonly Dictionary<int, string> _names = [];

    public event EventHandler? Changed;

    public int Count => _names.Count;

    public string? this[int address] =>
        _names.TryGetValue(address & 0xFF, out var name) ? name : null;

    /// <summary>Configured name, or the default <c>0xNN</c> address label.</summary>
    public string GetLabel(int address) => this[address] ?? FormatAddress(address);

    public static string FormatAddress(int address) =>
        "0x" + (address & 0xFF).ToString("X2", CultureInfo.InvariantCulture);

    public IReadOnlyDictionary<int, string> Snapshot() => new Dictionary<int, string>(_names);

    public void SetName(int address, string? name)
    {
        address &= 0xFF;
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            if (_names.Remove(address))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (_names.TryGetValue(address, out var existing) &&
            string.Equals(existing, trimmed, StringComparison.Ordinal))
        {
            return;
        }

        _names[address] = trimmed;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_names.Count == 0)
        {
            return;
        }

        _names.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces all names (used when loading a file or restoring settings).</summary>
    public void Replace(IEnumerable<KeyValuePair<int, string>> names)
    {
        _names.Clear();
        foreach (var (address, name) in names)
        {
            var trimmed = name?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                _names[address & 0xFF] = trimmed;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Parses a node-names file; returns false when the file could not be read.</summary>
    public bool Load(string path)
    {
        var names = new Dictionary<int, string>();
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                if (!TryParseAddress(line[..separator].Trim(), out var address))
                {
                    continue;
                }

                var name = line[(separator + 1)..].Trim();
                if (name.Length > 0)
                {
                    names[address] = name;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        Replace(names);
        return true;
    }

    public void Save(string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# CanYeAye J1939 node names");
        builder.AppendLine("# <address>=<name>");
        foreach (var (address, name) in _names.OrderBy(pair => pair.Key))
        {
            builder.Append(FormatAddress(address));
            builder.Append('=');
            builder.AppendLine(name);
        }

        File.WriteAllText(path, builder.ToString());
    }

    private static bool TryParseAddress(string text, out int address)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out address);
    }
}
