using Jint;
using Jint.Native;
using CanYeAye.Core.Protocols;

namespace CanYeAye.Core.Scripting;

/// <summary>
/// One running SavvyCAN script (ScriptContainer) on a Jint engine. Exposes the same
/// <c>host</c>, <c>can</c>, <c>isotp</c> and <c>uds</c> globals and the positional callback
/// signatures, so existing <c>examples/*.js</c> files run unmodified.
/// </summary>
public sealed class ScriptHost
{
    private readonly Engine _engine;
    private readonly HostApi _hostApi;
    private readonly CanApi _canApi;
    private readonly IsoTpApi _isoTpApi;
    private readonly UdsApi _udsApi;
    private readonly IsoTpHandler _isoTpHandler = new();
    private readonly UdsHandler _udsHandler = new();
    private readonly List<(uint Id, uint Mask, int Bus)> _filters = [];
    private readonly List<string> _parameters = [];
    private readonly List<string> _compileErrors = [];

    private JsValue? _commFrameCallback;
    private JsValue? _isoTpCallback;
    private JsValue? _udsCallback;
    private JsValue? _tickCallback;
    private JsValue? _arrayFactory;

    public ScriptHost()
    {
        _engine = new Engine();
        _hostApi = new HostApi(this);
        _canApi = new CanApi(this);
        _isoTpApi = new IsoTpApi(this);
        _udsApi = new UdsApi(this);

        _isoTpHandler.SetReception(true);
        _isoTpHandler.SetFlowControl(true);
        _isoTpHandler.MessageReceived += (_, frame) => InvokeIsoTp(frame);
        _isoTpHandler.FrameToSend += (_, frame) => FrameToSend?.Invoke(this, frame);

        _udsHandler.SetReception(true);
        _udsHandler.SetFlowControl(true);
        _udsHandler.MessageReceived += (_, message) => InvokeUds(message);
        _udsHandler.FrameToSend += (_, frame) => FrameToSend?.Invoke(this, frame);

        _arrayFactory = _engine.Evaluate("(function () { return []; })");

        _engine.SetValue("host", _hostApi);
        _engine.SetValue("can", _canApi);
        _engine.SetValue("isotp", _isoTpApi);
        _engine.SetValue("uds", _udsApi);
    }

    /// <summary>Script file name, visible to scripts as <c>host.fileName</c>.</summary>
    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string ScriptText { get; set; } = string.Empty;

    /// <summary>Tick interval in milliseconds; 0 stops the tick.</summary>
    public int TickInterval { get; private set; }

    public bool IsCompiled { get; private set; }

    public int BusCount { get; set; } = 8;

    public IReadOnlyList<string> Parameters => _parameters;

    public IReadOnlyList<string> CompileErrors => _compileErrors;

    /// <summary>Raised for <c>host.log(...)</c>.</summary>
    public event EventHandler<string>? LogEmitted;

    /// <summary>Raised for every CAN frame a script or helper wants to transmit.</summary>
    public event EventHandler<CanFrame>? FrameToSend;

    /// <summary>Raised when a script changes its tick interval.</summary>
    public event EventHandler<int>? TickIntervalChanged;

    /// <summary>Compiles and runs the script, then installs callbacks and calls <c>setup</c>.</summary>
    public bool Compile(string scriptText, string fileName = "")
    {
        ScriptText = scriptText;
        if (fileName.Length > 0)
        {
            FileName = fileName;
        }

        IsCompiled = false;
        _compileErrors.Clear();
        TickInterval = 0;
        TickIntervalChanged?.Invoke(this, 0);
        _filters.Clear();
        _isoTpHandler.ClearFilters();
        _udsHandler.ClearFilters();
        _parameters.Clear();

        try
        {
            _engine.Execute(scriptText, FileName);
        }
        catch (Exception ex)
        {
            ReportException(ex);
            return false;
        }

        _commFrameCallback = FindCallable("gotCommFrame") ?? FindCallable("gotCANFrame");
        _isoTpCallback = FindCallable("gotISOTPMessage");
        _udsCallback = FindCallable("gotUDSMessage");
        _tickCallback = FindCallable("tick");

        var setup = FindCallable("setup");
        if (setup is not null)
        {
            try
            {
                _engine.Invoke(setup, Array.Empty<object>());
            }
            catch (Exception ex)
            {
                ReportException(ex);
            }
        }

        IsCompiled = true;
        return true;
    }

    /// <summary>Feeds a received CAN frame to the script and the ISO-TP/UDS helpers.</summary>
    public void DispatchFrame(CanFrame frame)
    {
        if (_commFrameCallback is not null && MatchesAnyFilter(frame))
        {
            _engine.Invoke(
                _commFrameCallback,
                [
                    frame.Bus,
                    (double)frame.FrameId,
                    (double)frame.Length,
                    CreateArray(frame.Payload),
                ]);
        }

        _isoTpHandler.ProcessFrame(frame);
        _udsHandler.ProcessFrame(frame);
    }

    /// <summary>Runs <c>tick</c> and advances pending ISO-TP transmissions.</summary>
    public void Tick()
    {
        _isoTpHandler.Tick();
        _udsHandler.Tick();

        if (_tickCallback is null)
        {
            return;
        }

        try
        {
            _engine.Invoke(_tickCallback, Array.Empty<object>());
        }
        catch (Exception ex)
        {
            ReportException(ex);
        }
    }

    /// <summary>Sets a script global from the UI parameter table (as a string, like the reference).</summary>
    public void UpdateParameter(string name, string value) => _engine.SetValue(name, value);

    public string GetParameterValue(string name)
    {
        try
        {
            return ToJsString(_engine.GetValue(name));
        }
        catch
        {
            return string.Empty;
        }
    }

    public void SetTickInterval(int milliseconds)
    {
        TickInterval = Math.Max(0, milliseconds);
        TickIntervalChanged?.Invoke(this, TickInterval);
    }

    /// <summary>Stops the tick and clears filters (used when a script is deleted).</summary>
    public void Stop()
    {
        SetTickInterval(0);
        _filters.Clear();
        _isoTpHandler.ClearFilters();
        _udsHandler.ClearFilters();
        IsCompiled = false;
    }

    private JsValue? FindCallable(string name)
    {
        try
        {
            var value = _engine.GetValue(name);
            return value.IsCallable() ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private void InvokeIsoTp(CanFrame frame)
    {
        if (_isoTpCallback is null)
        {
            return;
        }

        _engine.Invoke(
            _isoTpCallback,
            [frame.Bus, (double)frame.FrameId, (double)frame.Length, CreateArray(frame.Payload)]);
    }

    private void InvokeUds(UdsMessage message)
    {
        if (_udsCallback is null)
        {
            return;
        }

        var frame = message.Frame;
        _engine.Invoke(
            _udsCallback,
            [
                frame.Bus,
                (double)frame.FrameId,
                (double)message.Service,
                (double)message.SubFunc,
                (double)frame.Length,
                CreateArray(frame.Payload),
            ]);
    }

    private bool MatchesAnyFilter(CanFrame frame)
    {
        foreach (var filter in _filters)
        {
            if (frame.Bus == filter.Bus && (frame.FrameId & filter.Mask) == filter.Id)
            {
                return true;
            }
        }

        return false;
    }

    private JsValue CreateArray(ReadOnlySpan<byte> bytes)
    {
        var array = _engine.Invoke(_arrayFactory!, Array.Empty<object>());
        for (var i = 0; i < bytes.Length; i++)
        {
            array.Set(
                JsValue.FromObject(_engine, (double)i),
                JsValue.FromObject(_engine, (double)bytes[i]),
                array);
        }

        return array;
    }

    private void ReportException(Exception ex)
    {
        _compileErrors.Add("SCRIPT EXCEPTION!");
        if (ex is Jint.Runtime.JavaScriptException jsException && jsException.Location is { } location)
        {
            _compileErrors.Add("Line: " + location.Start.Line);
        }

        _compileErrors.Add(ex.Message);
        _compileErrors.Add("Stack:");
        _compileErrors.Add(ex.StackTrace ?? string.Empty);
        foreach (var line in _compileErrors)
        {
            LogEmitted?.Invoke(this, line);
        }
    }

    private double ToNumber(JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return 0;
        }

        try
        {
            return Convert.ToDouble(value.ToObject(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private double GetElement(JsValue data, int index)
    {
        try
        {
            return ToNumber(data.Get(JsValue.FromObject(_engine, (double)index)));
        }
        catch
        {
            return 0;
        }
    }

    private static string ToJsString(JsValue value) => value.ToObject()?.ToString() ?? string.Empty;

    private sealed class HostApi
    {
        private readonly ScriptHost _host;

        public HostApi(ScriptHost host) => _host = host;

        public void log(JsValue value) => _host.LogEmitted?.Invoke(_host, ToJsString(value));

        public void setTickInterval(JsValue milliseconds) =>
            _host.SetTickInterval((int)_host.ToNumber(milliseconds));

        public void addParameter(JsValue name) => _host._parameters.Add(ToJsString(name));

        public void updateParameter(JsValue name, JsValue value) =>
            _host._engine.SetValue(ToJsString(name), ToJsString(value));

        public string fileName
        {
            get => _host.FileName;
            set => _host.FileName = value;
        }

        public string filePath
        {
            get => _host.FilePath;
            set => _host.FilePath = value;
        }

        public string scriptText
        {
            get => _host.ScriptText;
            set => _host.ScriptText = value;
        }

        public bool compileScript() => _host.Compile(_host.ScriptText, _host.FileName);

        public void updateValuesTable(JsValue widget)
        {
        }
    }

    private sealed class CanApi
    {
        private readonly ScriptHost _host;

        public CanApi(ScriptHost host) => _host = host;

        public void setFilter(JsValue id, JsValue mask, JsValue bus) =>
            _host._filters.Add(
                ((uint)_host.ToNumber(id), (uint)_host.ToNumber(mask), (int)_host.ToNumber(bus)));

        public void clearFilters() => _host._filters.Clear();

        public void sendFrame(JsValue bus, JsValue id, JsValue length, JsValue data)
        {
            var count = Math.Max(0, (int)_host.ToNumber(length));
            var payload = new byte[count];
            for (var i = 0; i < count; i++)
            {
                payload[i] = (byte)_host.GetElement(data, i);
            }

            var frame = new CanFrame();
            frame.SetFrameType(FrameType.CanDataFrame);
            frame.SetFrameId((uint)_host.ToNumber(id));
            frame.SetExtended(false);
            frame.Bus = (byte)_host.ToNumber(bus);
            frame.SetPayload(payload);
            if (frame.FrameId > 0x7FF)
            {
                frame.SetExtended(true);
            }

            _host.FrameToSend?.Invoke(_host, frame);
        }

        public void sendFDFrame(JsValue bus, JsValue id, JsValue length, JsValue data, JsValue brs)
        {
            var requested = Math.Min(Math.Max(0, (int)_host.ToNumber(length)), 64);
            int[] validLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];
            var frameLength = validLengths.FirstOrDefault(candidate => candidate >= requested, 64);

            var payload = new byte[frameLength];
            for (var i = 0; i < requested; i++)
            {
                payload[i] = (byte)_host.GetElement(data, i);
            }

            var frame = new CanFrame();
            frame.SetFrameId((uint)_host.ToNumber(id));
            frame.Bus = (byte)_host.ToNumber(bus);
            frame.SetFrameType(FrameType.CanFdDataFrame);
            frame.SetFlexibleDataRate(true);
            frame.SetBitrateSwitch(_host.ToNumber(brs) != 0);
            frame.SetPayload(payload);
            if (frame.FrameId > 0x7FF)
            {
                frame.SetExtended(true);
            }

            _host.FrameToSend?.Invoke(_host, frame);
        }
    }

    private sealed class IsoTpApi
    {
        private readonly ScriptHost _host;

        public IsoTpApi(ScriptHost host) => _host = host;

        public void setFilter(JsValue id, JsValue mask, JsValue bus) =>
            _host._isoTpHandler.AddFilter(
                (int)_host.ToNumber(bus), (uint)_host.ToNumber(id), (uint)_host.ToNumber(mask));

        public void clearFilters() => _host._isoTpHandler.ClearFilters();

        public void setPaddingByte(JsValue value) =>
            _host._isoTpHandler.PaddingByte = (byte)_host.ToNumber(value);

        public void sendISOTP(JsValue bus, JsValue id, JsValue length, JsValue data)
        {
            var count = Math.Max(0, (int)_host.ToNumber(length));
            var payload = new byte[count];
            for (var i = 0; i < count; i++)
            {
                payload[i] = (byte)_host.GetElement(data, i);
            }

            _host._isoTpHandler.SendIsoTp((int)_host.ToNumber(bus), (uint)_host.ToNumber(id), payload);
        }

        public void setRxCallback(JsValue callback)
        {
        }
    }

    private sealed class UdsApi
    {
        private readonly ScriptHost _host;

        public UdsApi(ScriptHost host) => _host = host;

        public void setFilter(JsValue id, JsValue mask, JsValue bus) =>
            _host._udsHandler.AddFilter(
                (int)_host.ToNumber(bus), (uint)_host.ToNumber(id), (uint)_host.ToNumber(mask));

        public void clearFilters() => _host._udsHandler.ClearFilters();

        public void sendUDS(
            JsValue bus,
            JsValue id,
            JsValue service,
            JsValue subLength,
            JsValue subFunc,
            JsValue length,
            JsValue data)
        {
            var count = Math.Max(0, (int)_host.ToNumber(length));
            var payload = new byte[count];
            for (var i = 0; i < count; i++)
            {
                payload[i] = (byte)_host.GetElement(data, i);
            }

            _host._udsHandler.SendUds(
                (int)_host.ToNumber(bus),
                (uint)_host.ToNumber(id),
                (int)_host.ToNumber(service),
                (int)_host.ToNumber(subLength),
                (long)_host.ToNumber(subFunc),
                payload);
        }

        public void setRxCallback(JsValue callback)
        {
        }
    }
}
