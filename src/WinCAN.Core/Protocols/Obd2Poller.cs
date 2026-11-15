using System.Diagnostics;

namespace WinCAN.Core.Protocols;

/// <summary>
/// Polls OBD-II PIDs from the ECUs on one bus through an <see cref="IsoTpHandler"/>.
/// Discovery asks every ECU for PID 0x00 and follows the supported-PID bitmaps; polling takes the
/// list in turn weighted by each item's interval, one request outstanding at a time. Only
/// read-only services (Mode 01/09) are ever sent.
/// </summary>
public sealed class Obd2Poller
{
    public enum Addressing
    {
        Standard11Bit,
        Extended29Bit,
    }

    public sealed record PollItem(uint EcuReplyId, byte Pid, int IntervalMs);

    public sealed class Ecu
    {
        public required uint ReplyId { get; init; }

        public List<byte> SupportedPids { get; } = [];

        public HashSet<byte> UnsupportedPids { get; } = [];
    }

    public sealed class Stats
    {
        public int Requests { get; set; }

        public int Replies { get; set; }

        public int Timeouts { get; set; }

        public int NegativeReplies { get; set; }

        public int RepliesLastSecond { get; set; }
    }

    private enum RequestKind
    {
        Discovery,
        Bitmap,
        Poll,
        OneOff,
    }

    private sealed record PollerRequest(uint EcuReplyId, byte[] Payload, RequestKind Kind);

    private sealed class Outstanding
    {
        public bool Active { get; set; }

        public PollerRequest? Request { get; set; }

        public long Deadline { get; set; }

        public bool GotReply { get; set; }
    }

    private const uint Functional11Bit = 0x7DF;
    private const uint ReplyBase11Bit = 0x7E8;
    private const uint ReplyMask11Bit = 0x7F8;
    private const uint Functional29Bit = 0x18DB33F1;
    private const uint Physical29Bit = 0x18DA00F1;
    private const uint ReplyBase29Bit = 0x18DAF100;
    private const uint ReplyMask29Bit = 0x1FFFFF00;

    private const byte NegativeReplyCode = 0x7F;
    private const byte NrcServiceNotSupported = 0x11;
    private const byte NrcSubFunctionNotSupported = 0x12;
    private const byte NrcRequestOutOfRange = 0x31;
    private const byte NrcResponsePending = 0x78;

    private readonly IsoTpHandler _isoTp;
    private readonly Func<long> _clock;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly List<PollerRequest> _queue = [];
    private readonly List<long> _replyTimes = [];
    private readonly Outstanding _outstanding = new();

    private readonly List<PollItem> _pollList = [];
    private long[] _nextDue = [];
    private ulong[] _lastSent = [];
    private ulong _sendCounter;
    private bool _hasSent;
    private long _lastSendTime;

    public Obd2Poller(IsoTpHandler isotp, Func<long>? clock = null)
    {
        _isoTp = isotp;
        _clock = clock ?? (() => _elapsed.ElapsedMilliseconds);
        _isoTp.MessageReceived += OnIsoTpMessage;
        ConfigureHandler();
    }

    public int Bus { get; private set; }

    public Addressing AddressMode { get; private set; } = Addressing.Standard11Bit;

    public int ReplyTimeoutMs { get; private set; } = 50;

    public int PendingTimeoutMs { get; private set; } = 5000;

    public int MaxRequestRate { get; private set; } = 20;

    public IReadOnlyList<Ecu> Ecus => _ecus;

    public Stats Statistics { get; } = new();

    public bool IsRunning { get; private set; }

    public bool IsDiscovering { get; private set; }

    public bool NeedsService => _outstanding.Active || _queue.Count > 0 || IsRunning;

    private readonly List<Ecu> _ecus = [];

    public event EventHandler<uint>? EcuFound;

    public event EventHandler? DiscoveryFinished;

    public event EventHandler<(uint EcuReplyId, byte Pid, byte[] Data, IReadOnlyList<Obd2.Value> Values)>? PidUpdated;

    public event EventHandler<(uint EcuReplyId, byte Pid)>? PidUnsupported;

    public event EventHandler<(uint EcuReplyId, byte Pid, string Text)>? VehicleInfoRead;

    public event EventHandler<(uint EcuReplyId, byte[] Request)>? RequestTimedOut;

    public event EventHandler<(uint EcuReplyId, byte Service, byte Code)>? NegativeReply;

    public event EventHandler<(uint EcuReplyId, byte[] Payload)>? ReplyReceived;

    public event EventHandler? StatsChanged;

    public void SetBus(int bus)
    {
        if (bus == Bus)
        {
            return;
        }

        Bus = bus;
        Reset();
        ConfigureHandler();
    }

    public void SetAddressing(Addressing mode)
    {
        if (mode == AddressMode)
        {
            return;
        }

        AddressMode = mode;
        Reset();
        ConfigureHandler();
    }

    public void SetReplyTimeout(int ms) => ReplyTimeoutMs = Math.Max(1, ms);

    public void SetPendingTimeout(int ms) => PendingTimeoutMs = Math.Max(1, ms);

    public void SetMaxRequestRate(int perSecond) => MaxRequestRate = Math.Clamp(perSecond, 1, 1000);

    public void SetPollList(IEnumerable<PollItem> items)
    {
        _pollList.Clear();
        _pollList.AddRange(items);
        var now = _clock();
        _nextDue = new long[_pollList.Count];
        Array.Fill(_nextDue, now);
        _lastSent = new ulong[_pollList.Count];
    }

    public static bool IsServiceAllowed(byte service) =>
        service is Obd2.ShowCurrentData or Obd2.VehicleInfo;

    public uint FunctionalRequestId() =>
        AddressMode == Addressing.Standard11Bit ? Functional11Bit : Functional29Bit;

    public uint RequestIdFor(uint ecuReplyId)
    {
        if (ecuReplyId == 0)
        {
            return FunctionalRequestId();
        }

        return AddressMode == Addressing.Standard11Bit
            ? ecuReplyId - 8
            : Physical29Bit | ((ecuReplyId & 0xFF) << 8);
    }

    public bool IsReplyId(uint id) =>
        AddressMode == Addressing.Standard11Bit
            ? (id & ReplyMask11Bit) == ReplyBase11Bit
            : (id & ReplyMask29Bit) == ReplyBase29Bit;

    public void Discover()
    {
        _ecus.Clear();
        for (var i = _queue.Count - 1; i >= 0; i--)
        {
            if (_queue[i].Kind is RequestKind.Discovery or RequestKind.Bitmap)
            {
                _queue.RemoveAt(i);
            }
        }

        _queue.Insert(0, new PollerRequest(0, Obd2.Mode01Request(0x00), RequestKind.Discovery));
        IsDiscovering = true;
        Service();
    }

    public void Start()
    {
        IsRunning = true;
        Service();
    }

    public void Stop() => IsRunning = false;

    public bool ReadVin(uint ecuReplyId)
    {
        if (ecuReplyId == 0)
        {
            return false;
        }

        return Request(ecuReplyId, Obd2.Mode09Request(0x02));
    }

    public bool Request(uint ecuReplyId, byte[] payload)
    {
        if (payload.Length == 0 || !IsServiceAllowed(payload[0]))
        {
            return false;
        }

        if (ecuReplyId != 0 && !IsReplyId(ecuReplyId))
        {
            return false;
        }

        _queue.Add(new PollerRequest(ecuReplyId, payload, RequestKind.OneOff));
        Service();
        return true;
    }

    /// <summary>Runs one scheduler step (the reference calls this from a 2 ms timer).</summary>
    public void Service()
    {
        var now = _clock();

        if (_outstanding.Active && now >= _outstanding.Deadline)
        {
            if (_outstanding.Request!.EcuReplyId != 0 || !_outstanding.GotReply)
            {
                Statistics.Timeouts++;
                RequestTimedOut?.Invoke(this, (_outstanding.Request.EcuReplyId, _outstanding.Request.Payload));
                StatsChanged?.Invoke(this, EventArgs.Empty);
            }

            FinishOutstanding();
        }

        if (!_outstanding.Active && RateAllows(now))
        {
            if (_queue.Count > 0)
            {
                var request = _queue[0];
                _queue.RemoveAt(0);
                SendRequest(request, now);
            }
            else if (IsRunning)
            {
                var index = NextDuePollItem(now);
                if (index >= 0)
                {
                    var item = _pollList[index];
                    _nextDue[index] = now + item.IntervalMs;
                    _lastSent[index] = ++_sendCounter;
                    SendRequest(new PollerRequest(item.EcuReplyId, Obd2.Mode01Request(item.Pid), RequestKind.Poll), now);
                }
            }
        }
    }

    private void ConfigureHandler()
    {
        _isoTp.SetReception(false);
        _isoTp.ClearFilters();
        _isoTp.ProcessAll = false;
        _isoTp.ExtendedAddressing = false;
        if (AddressMode == Addressing.Standard11Bit)
        {
            _isoTp.AddFilter(Bus, ReplyBase11Bit, ReplyMask11Bit);
        }
        else
        {
            _isoTp.AddFilter(Bus, ReplyBase29Bit, ReplyMask29Bit);
        }

        _isoTp.SetFlowControl(true);
        _isoTp.SetReception(true);
    }

    private void Reset()
    {
        _queue.Clear();
        _ecus.Clear();
        _outstanding.Active = false;
        _outstanding.Request = null;
        _outstanding.GotReply = false;
        IsDiscovering = false;
    }

    private bool RateAllows(long now) => !_hasSent || (now - _lastSendTime) * MaxRequestRate >= 1000;

    private int NextDuePollItem(long now)
    {
        var best = -1;
        for (var i = 0; i < _pollList.Count; i++)
        {
            var item = _pollList[i];
            if (_nextDue[i] > now)
            {
                continue;
            }

            if (item.EcuReplyId != 0 &&
                _ecus.Any(e => e.ReplyId == item.EcuReplyId && e.UnsupportedPids.Contains(item.Pid)))
            {
                continue;
            }

            if (best < 0 || _nextDue[i] < _nextDue[best] ||
                (_nextDue[i] == _nextDue[best] && _lastSent[i] < _lastSent[best]))
            {
                best = i;
            }
        }

        return best;
    }

    private bool SendRequest(PollerRequest request, long now)
    {
        if (request.Payload.Length == 0 || !IsServiceAllowed(request.Payload[0]))
        {
            return false;
        }

        _outstanding.Active = true;
        _outstanding.Request = request;
        _outstanding.Deadline = now + ReplyTimeoutMs;
        _outstanding.GotReply = false;
        _hasSent = true;
        _lastSendTime = now;
        Statistics.Requests++;

        _isoTp.SendIsoTp(Bus, RequestIdFor(request.EcuReplyId), request.Payload);
        StatsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void FinishOutstanding()
    {
        _outstanding.Active = false;
        _outstanding.Request = null;
        _outstanding.GotReply = false;
        CheckDiscoveryDone();
    }

    private void CheckDiscoveryDone()
    {
        if (!IsDiscovering)
        {
            return;
        }

        if (_outstanding.Active && _outstanding.Request is { } active &&
            active.Kind is RequestKind.Discovery or RequestKind.Bitmap)
        {
            return;
        }

        if (_queue.Any(r => r.Kind is RequestKind.Discovery or RequestKind.Bitmap))
        {
            return;
        }

        IsDiscovering = false;
        DiscoveryFinished?.Invoke(this, EventArgs.Empty);
    }

    private Ecu EcuFor(uint replyId)
    {
        foreach (var ecu in _ecus)
        {
            if (ecu.ReplyId == replyId)
            {
                return ecu;
            }
        }

        var created = new Ecu { ReplyId = replyId };
        _ecus.Add(created);
        EcuFound?.Invoke(this, replyId);
        return created;
    }

    private void CountReply(long now)
    {
        Statistics.Replies++;
        _replyTimes.Add(now);
        while (_replyTimes.Count > 0 && now - _replyTimes[0] >= 1000)
        {
            _replyTimes.RemoveAt(0);
        }

        Statistics.RepliesLastSecond = _replyTimes.Count;
        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool ReplyMatchesOutstanding(uint ecuReplyId, byte[] payload)
    {
        if (!_outstanding.Active || _outstanding.Request is not { } request)
        {
            return false;
        }

        if (request.EcuReplyId != 0 && request.EcuReplyId != ecuReplyId)
        {
            return false;
        }

        if (payload[0] != request.Payload[0] + Obd2.PositiveReplyOffset)
        {
            return false;
        }

        return request.Payload.Length < 2 || (payload.Length >= 2 && payload[1] == request.Payload[1]);
    }

    private void OnIsoTpMessage(object? sender, CanFrame message)
    {
        if (!message.IsReceived || message.Bus != Bus)
        {
            return;
        }

        var id = message.FrameId;
        if (!IsReplyId(id))
        {
            return;
        }

        var payload = message.Payload;
        if (payload.Length == 0)
        {
            return;
        }

        var now = _clock();
        EcuFor(id);
        ReplyReceived?.Invoke(this, (id, payload));

        var replyService = payload[0];
        if (replyService == NegativeReplyCode)
        {
            HandleNegative(id, payload, now);
        }
        else
        {
            var matches = ReplyMatchesOutstanding(id, payload);
            if (matches)
            {
                _outstanding.GotReply = true;
            }

            if (replyService == Obd2.ShowCurrentData + Obd2.PositiveReplyOffset)
            {
                HandleMode01(id, payload);
            }
            else if (replyService == Obd2.VehicleInfo + Obd2.PositiveReplyOffset && payload.Length >= 2)
            {
                var pid = payload[1];
                if (pid is 0x02 or 0x0A && Obd2.TryDecodeMode09Text(payload, pid, out var text))
                {
                    VehicleInfoRead?.Invoke(this, (id, pid, text));
                }
            }

            CountReply(now);

            if (matches && _outstanding.Request is { } request && request.EcuReplyId != 0)
            {
                FinishOutstanding();
            }
        }

        Service();
    }

    private void HandleMode01(uint ecuReplyId, byte[] payload)
    {
        if (!Obd2.TryParseMode01Reply(payload, out var replies))
        {
            return;
        }

        foreach (var reply in replies)
        {
            if (Obd2.IsSupportBitmapPid(reply.Pid))
            {
                var supported = Obd2.SupportedPids(reply.Pid, reply.Data);
                var ecu = EcuFor(ecuReplyId);
                foreach (var pid in supported)
                {
                    if (!Obd2.IsSupportBitmapPid(pid) && !ecu.SupportedPids.Contains(pid))
                    {
                        ecu.SupportedPids.Add(pid);
                    }
                }

                ecu.SupportedPids.Sort();
                var next = reply.Pid + 0x20;
                if (IsDiscovering && next <= 0xE0 && supported.Contains((byte)next))
                {
                    _queue.Add(new PollerRequest(ecuReplyId, Obd2.Mode01Request((byte)next), RequestKind.Bitmap));
                }
            }

            IReadOnlyList<Obd2.Value> values = [];
            var info = Obd2.FindMode01(reply.Pid);
            if (info is not null)
            {
                Obd2.TryDecode(info, reply.Data, out values);
            }

            PidUpdated?.Invoke(this, (ecuReplyId, reply.Pid, reply.Data, values));
        }
    }

    private void HandleNegative(uint ecuReplyId, byte[] payload, long now)
    {
        if (payload.Length < 3)
        {
            return;
        }

        var service = payload[1];
        var code = payload[2];
        var forOutstanding = _outstanding.Active &&
                             _outstanding.Request is { } request &&
                             request.Payload.Length > 0 &&
                             request.Payload[0] == service &&
                             (request.EcuReplyId == 0 || request.EcuReplyId == ecuReplyId);

        if (code == NrcResponsePending)
        {
            if (forOutstanding)
            {
                _outstanding.Deadline = now + PendingTimeoutMs;
            }

            return;
        }

        Statistics.NegativeReplies++;
        NegativeReply?.Invoke(this, (ecuReplyId, service, code));
        StatsChanged?.Invoke(this, EventArgs.Empty);
        if (!forOutstanding || _outstanding.Request is not { } outstandingRequest)
        {
            return;
        }

        if (service == Obd2.ShowCurrentData && outstandingRequest.Payload.Length >= 2 &&
            code is NrcServiceNotSupported or NrcSubFunctionNotSupported or NrcRequestOutOfRange)
        {
            var pid = outstandingRequest.Payload[1];
            var ecu = EcuFor(ecuReplyId);
            if (ecu.UnsupportedPids.Add(pid))
            {
                PidUnsupported?.Invoke(this, (ecuReplyId, pid));
            }
        }

        _outstanding.GotReply = true;
        if (outstandingRequest.EcuReplyId != 0)
        {
            FinishOutstanding();
        }
    }
}

