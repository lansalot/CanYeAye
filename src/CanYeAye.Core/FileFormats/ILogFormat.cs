using CanYeAye.Core;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// One CAN log file format (save/load + a cheap detector). Mirrors SavvyCAN's
/// <c>isXxxFile</c> / <c>loadXxxFile</c> / <c>saveXxxFile</c> triple from framefileio.cpp.
/// </summary>
public interface ILogFormat
{
    string Name { get; }

    /// <summary>Extension (with dot) used when the user's chosen name has no extension.</summary>
    string DefaultExtension { get; }

    bool CanSave { get; }

    /// <summary>Cheap structural probe used by autodetect.</summary>
    bool IsMatch(string path);

    /// <summary>
    /// Loads frames in file order. Returns false when the file contained malformed rows;
    /// callers must discard whatever was appended in that case (SavvyCAN semantics).
    /// </summary>
    bool Load(string path, List<CanFrame> frames);

    bool Save(string path, IReadOnlyList<CanFrame> frames);
}

/// <summary>Base class supplying a no-op detector and an "unsupported" save.</summary>
public abstract class LogFormatBase : ILogFormat
{
    public abstract string Name { get; }

    public virtual string DefaultExtension => ".log";

    public virtual bool CanSave => false;

    public virtual bool IsMatch(string path) => false;

    public abstract bool Load(string path, List<CanFrame> frames);

    public virtual bool Save(string path, IReadOnlyList<CanFrame> frames) => false;
}
