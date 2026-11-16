# WinCAN connection protocol spec (port of SavvyCAN `connections/`)

Source of truth: `C:\Github\SavvyCAN\connections\{gvretserial,lawicel_serial,socketcand,canserver,canlogserver,canconnection,canbus,canconmanager,canconfactory,mqtt_bus}.{h,cpp}`,
`canconconst.h`, `canbus.cpp`, `can_structs.h`, `utils/lfqueue.h`, plus `connectionwindow.cpp` /
`newconnectiondialog.cpp` for discovery + init parameters. Reference repo is read-only.

Everything below is byte/line exact. "C++ line" comments point at the reference for verification.

---

## 0. Shared framework (`CANConnection` / `CANConManager`)

### 0.1 Enums (must be carried over verbatim)

```c
// canconconst.h
namespace CANCon {
  enum status { NOT_CONNECTED = 0, CONNECTED = 1 };
  enum type {
    GVRET_SERIAL = 0, KVASER = 1, SERIALBUS = 2, REMOTE = 3, KAYAK = 4,
    MQTT = 5, LAWICEL = 6, CANSERVER = 7, CANLOGSERVER = 8,
    PICOFLEXRAY = 9, NONE = 10
  };
}
struct CANConStatus { CANCon::status conStatus; int numHardwareBuses; };
```

`CommFrame::FrameType` (can_structs.h): `CANDataFrame=0, CANFDDataFrame=1, FLEXRAYDataFrame=2, LINDataFrame=3, ErrorFrame=4, RemoteRequestFrame=5, InvalidFrame=6`.
`CommFrame::FrameError`: `NoError=0, TransmissionTimeoutError=1<<0, LostArbitrationError=1<<1, ControllerError=1<<2, ProtocolViolationError=1<<3, TransceiverError=1<<4, MissingAcknowledgmentError=1<<5, BusOffError=1<<6, BusError=1<<7, ControllerRestartError=1<<8, UnknownError=1<<9, AnyError=0x1FFFFFFF`.

Timestamps are `TimeStamp(secs, usecs)` (qint64 each), **never** `DateTime`. `TimeStamp::fromMicroSeconds(us) => (us / 1_000_000, us % 1_000_000)`.

`CANBus` defaults (`canbus.cpp` ctor): `speed=500000, listenOnly=false, singleWire=false, active=false, canFD=false, dataRate=2000000, canFDCapable=false`. `active` defaults **false** — devices must explicitly enable, which is why each driver overrides `setActive(true)` where needed.

### 0.2 Connection object lifecycle

Constructed with `CANConnection(port, driver, type, serialSpeed, busSpeed, canFd, dataRate, numBuses, queueLen, useThread, listenOnly=false, active=true)`.

- One `QThread` (HighPriority) per connection when `useThread`; the object is `moveToThread`'d into it.
- `start/stop/suspend/setBusSettings/getBusSettings/sendFrame/sendFrames` use `Qt::BlockingQueuedConnection` from any other thread → synchronous, called-in-worker semantics.
- `start()` reads `Main/TimeClock` (bool) into `useSystemTime` **once per start**, then calls `piStarted()`.
- `sendFrame()` writes the frame into the LF queue *and* calls `piSendFrame()` — TX frames appear in the capture as local echo (`setLocalEcho(true)` set by the manager) and get timestamps from `mElapsedTimer` (µs since `resetTimeBasis()`), unless `useSystemTime`.
- `suspend(true)` → `piSuspend()` sets a flag and `getQueue().flush()`; frame readers must check `isCapSuspended()` and drop.
- Queue: `LFQueue<CommFrame>` ring buffer, `size = pQueueLen` (4000 for every network/serial driver here; capacity is `size-1` usable), `peek()/dequeue()` by manager, `get()/queue()` by producer. Producer **must** `get()` first and may fail (returns null when full → frame dropped).

Per-driver ctor parameters actually used (table):

| Driver | type | serialSpeed | busSpeed | numBuses | queueLen | useThread |
|---|---|---|---|---|---|---|
| GVRetSerial | GVRET_SERIAL | 0 | 0 | 0 (becomes 2) | 4000 | yes |
| LAWICELSerial | LAWICEL | dialog (default 500000 list) | dialog bus speed | 3 | 4000 | yes |
| SocketCANd | KAYAK | 0 | 0 | 1 (then #ids) | 4000 | yes |
| CANserver | CANSERVER | 0 | 0 | 3 | 4000 | yes |
| CanLogServer | CANLOGSERVER | 0 | 0 | 1 | 4000 | yes |
| MQTT_BUS | MQTT | 0 | 0 | 1 | 4000 | yes |

### 0.3 Bus numbering / routing (`CANConManager`)

- Global bus = sum of `getNumBuses()` of all preceding connections + driver-local bus. Drivers always work in *local* bus indices; manager adds/removes `busBase` in `sendFrame`/`refreshConnection`.
- `refreshConnection` drains each connection's queue on a **20 ms** main-thread timer (50 Hz), adds `busBase`, emits `framesReceived(conn, frames)`.
- `getTimeBasis()` returns `mTimestampBasis` = epoch-µs captured in `resetTimeBasis()`; connection drivers use it to align device clocks (GVRET).
- `autoReconnectCheck` ticks every **1 s** from the main thread; NOT_CONNECTED connections get `autoReconnect()` queued (every second); CONNECTED ones whose frame count was 0 over a 5 s window get it on every 5th tick. Default `autoReconnect()` is a no-op; only SerialBus overrides it in SavvyCAN, but port the hook.
- Targeted-frame filters: per-bus list of `{id, mask, observer}`; match is `frame.frameId() & mask == id`; observer receives `gotTargettedFrame(CommFrame)` queued under a mutex.
- UI parameters (newconnectiondialog / connectionwindow)

- CAN bus speed dropdown: `10000, 20000, 50000, 83333, 100000, 125000, 250000, 500000, 1000000` (default 500000).
- CAN FD data-rate dropdown: `1000000, 2000000, 4000000, 5000000` (default 2000000).
- Lawicel serial speed dropdown: `115200, 150000, 250000, 500000, 1000000, 2000000, 3000000`.
- GVRET serial vs TCP decision (`canconfactory.cpp`): port string is **serial** if it starts with `/`, or contains `tty`/`serial`, or matches a system port name/system location; otherwise, if it contains `.`, it is TCP (`useTcp = true`). `REMOTE` type always means TCP.
- Discovery: UDP listener on **17222** (`ShareAddress`, AnyIPv4) collects GVRET sender IPs (device broadcasts; no probe is sent). UDP listener on **42000** parses socketcand/kayak XML beacons: `<CANBeacon name=...><URL>can://ip:port</URL><Bus name=.../></CANBeacon>` → entry string `bus1,bus2@name (can://ip:port)`.
- Persistence keys (SavvyCAN QSettings; WinCAN keeps its own store but same field names are fine): `Main/ValidateComm`, `Main/TimeClock`, `Remote/User`, `Remote/Host`, `Remote/Port`, `Remote/Pass`, `connections/portNames`, `connections/driverNames`, `connections/devTypes`, `connections/serialSpeeds`, `connections/busSpeeds`, `connections/isCanFds`, `connections/DataRates`, `connections/listenOnlys`, `connections/actives`.

---

## 1. GVRET serial / TCP (`GVRetSerial`)

Two transports, identical byte protocol. Binary, framed by `0xF1` command marker.

### 1.1 Transport setup

**Serial** (`connectDevice`, gvretserial.cpp:331-416)
- Open 8 data bits, no parity, 1 stop bit, `BaudRate = 1_000_000` (**most GVRET devices ignore it; ESP32 boards require exactly 1 Mbit**).
- `espSerialMode = true` by default:
  - `Handshake = None`; on success force `DTR = false`, `RTS = false` (ESP32 uses them for boot/reset), then **wait 2000 ms** (timer of 3000 ms measured from open, see note) before `deviceConnected()`.
  - On failure/timeout the mode toggles to **standard**: `Handshake = RequestToSend` (hardware flow control), `deviceConnected()` immediately after open (no delay).
- Port open: if the name is a known system serial port, open by port info; otherwise open by name (allows `/dev/...` links and `\\\\.\\COMx`).
- C#: `new SerialPort(name, 1_000_000, Parity.None, 8, StopBits.One) { Handshake = Handshake.None /* or RequestToSend */, DtrEnable = false, RtsEnable = false }`.
- Exact delay in code: ESP branch calls `QTimer::singleShot(3000, ...)`; treat as **≈3 s after successful open**.

**TCP** (`useTcp = true`, i.e. `REMOTE` or name containing `.`): `connectToHost(host, 23)`; no TLS, no socket timeout; handshake is sent the moment `connected` fires. (A UDP path to port 17222 exists but is commented out — do not port it.)

### 1.2 Serial state machine (`SERIALSTATE`, gvretserial.h:15-32)

```
IDLE=0, GET_COMMAND=1, BUILD_CAN_FRAME=2, TIME_SYNC=3, GET_DIG_INPUTS=4,
GET_ANALOG_INPUTS=5, SET_DIG_OUTPUTS=6, SETUP_CANBUS=7, GET_CANBUS_PARAMS=8,
GET_DEVICE_INFO=9, SET_SINGLEWIRE_MODE=10, GET_NUM_BUSES=11, GET_EXT_BUSES=12,
BUILD_FD_FRAME=13, GET_FD_SETTINGS=14
```

`IDLE`: only byte `0xF1` is meaningful → `GET_COMMAND`. Every other byte is ignored.

`GET_COMMAND` dispatch on the byte after `0xF1`:

| byte | meaning | next state |
|---|---|---|
| 0 (0x00) | CAN frame follows | BUILD_CAN_FRAME (step 0) |
| 1 | time sync reply | TIME_SYNC |
| 2 | digital inputs reply | GET_DIG_INPUTS |
| 3 | analog inputs reply | GET_ANALOG_INPUTS |
| 4 | set digital outputs (no reply expected) | IDLE |
| 5 | setup canbus (no reply) | IDLE |
| 6 | CAN bus params reply | GET_CANBUS_PARAMS |
| 7 | device info reply | GET_DEVICE_INFO |
| 9 | comm validation reply | reset `validationCounter = 10`, IDLE |
| 12 (0x0C) | number-of-buses reply | GET_NUM_BUSES (1 byte) |
| 13 (0x0D) | extended-buses reply | GET_EXT_BUSES (15 bytes) |
| 20 (0x14) | CAN FD frame follows | BUILD_FD_FRAME |
| 22 (0x16) | CAN FD settings reply | GET_FD_SETTINGS |
| other | ignored | IDLE (state unchanged in ref: no default) |

### 1.3 RX frame grammar

**Classic frame, opcode 0** — 10 + N bytes total (`0xF1 0x00` then):
```
off 0..3  timestamp  uint32 little-endian, microseconds since device boot
off 4..7  id         uint32 little-endian; bit31 = extended flag (mask 0x7FFFFFFF)
off 8     len|bus    low nibble  = payload length 0..8
                     high nibble = bus number 0..15
off 9..    data[len]
```
No trailing byte. A zero-length frame completes immediately after offset 8 (`emitBuiltFrame(CANDataFrame)`). If the bus nibble is `>= getNumBuses()` the frame is **dropped**.

**CAN FD frame, opcode 20 (0x14)** — 11 + N + 1 bytes:
```
off 0..3  timestamp uint32 LE, µs since boot
off 4..7  id        uint32 LE, bit31 = extended
off 8     length | BRS   bits0-6 = payload length (0..64); bit7 = bitrate-switch
                         if (len & 0x7F) > 64 → drop frame, resync on next 0xF1
off 9     bus       full byte
off 10..  data[len]
off 10+len  one trailing byte (ignored) completes the frame
```
FD frames are emitted as `CANFDDataFrame` with `setFlexibleDataRateFormat(true)` and BRS from bit 7.

**Timestamp assignment** (both frame types, at the end of the 4 timestamp bytes):
```
buildTimestamp = (int64)(uint32 raw) + timeBasis            // timeBasis is int32
if (useSystemTime) ts = TimeStamp.fromMicroSeconds(epochNowUs)
else               ts = TimeStamp(0, buildTimestamp)
```

**Time sync, opcode 1** (4 bytes, uint32 LE µs since boot):
```
timeAtGVRETSync = epochNowUs
rebuildLocalTimeBasis():
    lastSystemTimeBasis = CANConManager.getTimeBasis()
    systemDelta = (int64)timeAtGVRETSync - lastSystemTimeBasis
    localDelta  = (int32)buildTimeBasis - systemDelta        // wrapping int32 math
    timeBasis   = -localDelta
continuousTimeSync = false
```

**CAN bus params, opcode 6** (10 bytes):
```
[0] can0Enabled = b & 0x0F ; can0ListenOnly = b >> 4
[1..4] can0 baud, uint32 LE
[5] can1Enabled = b & 0x0F ; can1ListenOnly = b >> 4 ; deviceSingleWireMode = b >> 6
[6..9] can1 baud, uint32 LE
```

**Device info, opcode 7** (6 bytes): `buildNum` uint16 LE (bytes 0-1), byte 2 eeprom version (ignored), byte 3 file type (ignored), byte 4 auto-log flag (ignored), byte 5 single-wire mode.

**Number of buses, opcode 12** (1 byte): sets `mNumBuses`, resizes per-bus state (new buses inherit bus 0 config, marked configured), then **sends `0xF1 0x0D`** (get extended buses), emits status.

**Extended buses, opcode 13** (15 bytes): SWCAN + LIN1 + LIN2 settings (see above).

**CAN FD settings, opcode 22 (0x16)**:
```
byte 0: busCount (0 or >16 ⇒ ignore the reply entirely, back to IDLE)
then per bus i = 0..busCount-1: 5 bytes
   [0] flags: bit0 = bus is CAN FD capable, bit1 = FD mode currently on
   [1..4] data rate uint32 LE (0 ⇒ keep current)
```
After processing the last bus: `deviceHasFDSetup = true` (allows sending opcode 21), emit status.
Note `setCanFD(flags & 0x03) == 0x03`.

**Digital inputs, opcode 2**: 2 bytes (state + checksum), values unused, back to IDLE.
**Analog inputs, opcode 3**: 9 bytes (2 per input + checksum).
⚠️ Reference bug: `GET_ANALOG_INPUTS` never transitions back to IDLE and keeps swallowing bytes forever. **Do not port that**: consume 9 bytes then return to IDLE.
`SET_DIG_OUTPUTS` / `SETUP_CANBUS` / `SET_SINGLEWIRE_MODE` always fall through to IDLE.

### 1.4 TX frame grammar (`piSendFrame`)

Error frames are refused: `if (frame.frameId() & 0x20000000) return true;` (no bytes).

**Classic** (payload clamped to 8; a longer payload would desync the device parser):
```
F1 00 id0 id1 id2 id3 bus len d0 .. dN-1 00
```
- `id` = `frame.frameId()`, with bit31 set when extended.
- `bus` = `frame.bus & 0x03`.
- `len` = payload length (0..8), no flags.
- Trailing `0x00` terminator.

**CAN FD** (payload clamped to 64):
```
F1 14 id0 id1 id2 id3 bus (len | (BRS ? 0x80 : 0)) d0 .. dN-1 00
```
Same layout as the device's opcode-20 frames: len bits 0-6, BRS bit 7, byte 9 = bus, trailing byte.

### 1.5 Bus configuration commands

`piSetBusSettings(idx, bus)` → `setBusConfig` then, for `idx < 2`:
```
F1 05 can0[4 LE] can1[4 LE] 00
```
where each 32-bit word is `speed | 0x80000000 | (active ? 0x40000000:0) | (listenOnly ? 0x20000000:0)` and for bus 1 additionally `| (singleWire ? 0x10000000:0)`.
For `idx >= 2` (SWCAN/LIN):
```
F1 0E swcan[4 LE] lin1[4 LE] lin2[4 LE] 00
```
(LIN rates come from the device's ext-bus reply and are stored in `lin1Baud/lin2Baud`; they are not settable from UI in the ref.)
For **any** bus index, if `deviceHasFDSetup && bus.isCanFDCapable()`:
```
F1 15 <busIdx> <flags> rate[4 LE]        // flags = 0x02 when FD on, else 0x00; rate 0 = keep
```
(The `deviceHasFDSetup` guard exists because an old device would misinterpret unknown opcode payload bytes as further commands.)

### 1.6 Init sequence, validation, lifecycle

On connect (`deviceConnected`), one write of:
```
E7 E7              → put device into binary comm mode (sent twice, once per arg)
F1 0C              → get number of truly implemented buses (M2RET only); mNumBuses pre-set to 2
F1 06              → get CAN bus parameters
F1 07              → get device information
F1 16              → get CAN FD settings (opcode 22; older firmware skips it safely)
F1 01              → time sync
F1 09              → comm validation
```

- `doValidation = Main/ValidateComm` (default **true**).
- If validation on: `QTimer::singleShot(5000, connectionTimeout)`.
  - `connectionTimeout`: if status is still NOT_CONNECTED → **toggle `espSerialMode`**, disconnect, reconnect (unbounded retry loop, 5 s per attempt). Else start the 250 ms tick timer.
  - In practice status turns CONNECTED earlier (opcode 6/13 replies) so the timer path mostly starts the tick.
- If validation off: set CONNECTED immediately after the init write and emit status.
- `handleTick()` (250 ms): if validation on and CONNECTED, `validationCounter--`; at 0 (10 missed = 2.5 s) → send debug "Comm validation failed", set NOT_CONNECTED, `disconnectDevice()` (no auto-restart). Also sends `F1 09` on every tick while connected (4/s).
- `validationCounter` resets to 10 on every opcode-9 reply.
- Serial errors: `DeviceNotFound, Permission, Open, Resource, UnsupportedOperation, Unknown, NotOpen` → disconnect; `Write, Read` → `piStop()`; `NoError` → ignore; parity/framing/break only logged (Qt ≤ 6.0 only).
- `piStop()`: stop timer, close socket/port, delete objects, status NOT_CONNECTED, emit status.
- UDP broadcast discovery is a UI concern only (17222 as described in §0.4); there is **no** protocol-level handshake request from SavvyCAN besides `0xE7 0xE7`.

### 1.7 CAN FD and FlexRay

- CAN FD: RX opcode 20, TX opcode 20, config opcodes 21/22 as above. BRS carried on RX and TX; ESI is not transported by GVRET.
- **FlexRay: none.** GVRET's "extended buses" (opcode 13/14) are SWCAN + LIN1 + LIN2. FlexRay only exists in the separate `PICOFLEXRAY` driver (libusb, VID `0x3801`, PID `0xddcc`, serial prefix `"picoflex"`, bulk-in `0x81`, vendor-out ops `0x90`/`0x91`), which is out of scope here and should get its own spec.

### 1.8 Device firmware cross-check (`collin80/GVRET`)

The firmware (`GVRET.ino`, `sys_io.cpp`) is the other half of the protocol and confirms
the client-side grammar above:

- Opcodes are the `CAN_COMMAND` enum: 0 build frame, 1 time sync, 2 digital inputs, 3 analog
  inputs, 4 set digital outputs, 5 setup CAN bus, 6 CAN bus params, 7 device info, 8 single-wire
  mode, 9 keepalive, 10 system type, 11 echo frame, 12 number of buses, 13 extended buses,
  14 setup extended buses, 20 build FD frame, 21 setup FD, 22 get FD settings.
- Frame and command replies always end with an XOR checksum byte. The client parser reads the
  fixed payload length and ignores the checksum because it is not `0xF1`.
- Keepalive reply is **4 bytes**: `F1 09 DE AD` (opcode + 2 fixed bytes). Only `F1 09` matters
  to the client; the two trailing bytes are discarded by the idle scanner.
- Device info reply is **8 bytes**: `F1 07 buildLo buildHi eepromVer fileType autoLog singleWire`.
- Extended-buses reply is **17 bytes**: `F1 0D` plus the 15-byte SWCAN/LIN payload.
- `GET_NUMBUSES` on the stock Due firmware always answers **3** (CAN0, CAN1, SWCAN) regardless
  of whether SWCAN is enabled, so the reported bus count is an upper bound.
- `SETUP_CANBUS` consumes exactly 8 bytes after the opcode (bus0 word, bus1 word); the client's
  trailing `0x00` is harmless and ignored by the idle scanner.
- CAN bus params flags: bit0 = enabled, bit4 = listen-only, bit6 (bus 1 only) = single-wire.

**Verified live device** (`192.168.1.57:23`, TCP; single client, so SavvyCAN must be closed):

```
→ E7 E7 F1 0C F1 06 F1 07 F1 16 F1 01 F1 09
← F1 0C 01                                  number of buses = 1
← F1 06 01 20 A1 07 00  00 00 00 00 00       CAN0 enabled @ 500 kbit/s, CAN1 disabled
← F1 07 6A 02 20 00 00 00                    build 618, EEPROM v0x20, file type 0, single-wire 0
← F1 01 71 56 1D 6B                          time sync (device µs)
```

The transport reports CONNECTED after the bus-parameters/validation reply, exposes one bus, and
the bus range in the UI follows the device-reported count (`GvretConnection.BusesChanged` →
`CanConnectionManager` renumbering). Deterministic coverage lives in
`tests/WinCAN.Tests/Transports/GvretConnectionTests.cs` (mock TCP device: init preamble, bus
config, frame reception, no-reply timeout) and `CanConnectionManagerTests.cs`.

---

## 2. Lawicel ASCII serial (`LAWICELSerial`)

### 2.1 Transport

- Serial, `BaudRate = serialSpeed` from the dialog (115200…3000000), `DataBits = 8`, `Handshake = RequestToSend` (hardware), parity/stop bits left at defaults (none/one).
- Open failure is silently swallowed (debug string commented out); `deviceConnected()` only runs on success.
- **No validation timer**: `connectionTimeout()` exists but is never scheduled and `mTimer` is never started; `handleTick()` is empty. There is no keepalive and no reconnect loop.

### 2.2 Init sequence (`deviceConnected`)

```
"C\r"       close channel (in case it was already open)
"S<code>\r" set bit rate
["Y<n>\r"]  only when bus.isCanFD()  → data phase rate
"O\r"       open channel
```
`S` code mapping (bus speed → digit): `10000→0, 20000→1, 50000→2, 100000→3, 125000→4, 250000→5, 500000→6, 800000→7, 1000000→8`, **default `6`** for anything else.
`Y` mapping (data rate): `1000000→Y1, 2000000→Y2, 4000000→Y4, 5000000→Y5`, default `Y2` with a warning.

Then: `mNumBuses = 1`, bus 0 marked configured + active, status CONNECTED, emit `CANConStatus{1}`.
`piStop()` writes `"C\r"` before closing. `piStop` writes `"C\r"` before closing. `piSetBusSettings` only stores config (no serial traffic).

### 2.3 RX grammar (line at a time, terminator `\r` = 0x0D)

Buffer chars into a line; on CR parse by first char:

```
tIIIL DD..   classic, 11-bit ID: "t" + 3 hex ID + 1 decimal DLC + 2*N hex data
TIIIIIIIIL DD..  classic, 29-bit ID: "T" + 8 hex ID + 1 decimal DLC + 2*N hex data
dIIIL DD..   CAN FD, 11-bit, no BRS: "d" + 3 hex ID + 1 hex DLC-code + data
bIIIL DD..   CAN FD, 11-bit, BRS:    "b" + 3 hex ID + 1 hex DLC-code + data
DIIIIIIIIL DD..  CAN FD, 29-bit, no BRS
BIIIIIIIIL DD..  CAN FD, 29-bit, BRS
```
- Classic DLC is read as **decimal** (`"8"`); FD DLC is read as **hex** and expanded via the DLC code table:
  `1..8→1..8, 9→12, 10→16, 11→20, 12→24, 13→32, 14→48, 15→64` (others → 0).
- Extended flag set for `T/D/B` only (and `setFrameId` auto-sets extended for IDs > 0x7FF).
- Frame type: `CANDataFrame` for t/T; `CANFDDataFrame` for d/b/D/B (`setFlexibleDataRateFormat(true)`, `setBitrateSwitch(true)` for b/B).
- Unknown first chars are ignored (parsed as if `t`).

**Timestamps.** The frame timestamp is first set to **wall clock**: `TimeStamp(0, epochNowUs)`. When `useSystemTime == false` and the line carries the optional 4-hex-digit hardware-ms suffix, it is replaced:
```
offset, lenIndex =  (5,4) for t/d/b ; (10,9) for T/D/B
dlc        = hex(line[lenIndex])
byteCount  = FD ? dlcCodeToBytes(dlc) : dlc
frameLen   = offset + byteCount*2
if line.length > frameLen + 1:
    hwMs = hex(line[frameLen .. frameLen+3])
    if lastHWTimestamp >= 0 && hwMs < lastHWTimestamp: wrapAdder += 60000   // 60 s wrap
    lastHWTimestamp = hwMs
    unwrapped = wrapAdder + hwMs
    if timeBasis == 0: timeBasis = epochNowMs - unwrapped
    ts = TimeStamp(0, (timeBasis + unwrapped) * 1000)
```
`rebuildLocalTimeBasis()` resets `timeBasis = 0, lastHWTimestamp = -1, wrapAdder = 0` (called on connect).

### 2.4 TX grammar (`piSendFrame`)

```
t<ID 3 hex><DLC 1 hex><data 2*N hex upper>\r      classic 11-bit
T<ID 8 hex><DLC 1 hex><data>\r                    classic 29-bit
d/b<3 hex ID><DLC><data>\r                        FD 11-bit, no BRS / BRS
D/B<8 hex ID><DLC><data>\r                        FD 29-bit, no BRS / BRS
```
- ID masked to `0x7FF` (11-bit) or `0x1FFFFFFF` (29-bit); the command letter encodes the ID width — **bit 31 must not be set** in the printed ID.
- DLC is one hex digit from `bytesToDlcCode`: `1..8→1..8, 12→9, 16→10, 20→11, 24→12, 32→13, 48→14, 64→15`, other lengths → 0 (send 0 bytes; SavvyCAN does not pad or reject).
- Data bytes are uppercase hex (byte separator none).
- Error frames (`frameId() & 0x20000000`) return true without writing.
- `debugInput` bytes are written raw to the port (console passthrough).

---

## 3. socketcand TCP (`SocketCANd`, driver name "kayak")

### 3.1 Port-string format and setup

Port string: `<bus1,bus2,...>@can://<ip>:<port>` (as produced from the UDP beacon, see §0.4).
Reference parsing (socketcand.cpp:21-26):
```
hostCanIDs = portName.left(indexOf('@')).split(',')
tail       = portName.mid(indexOf("can://")+6, length - indexOf("can://") - 7)   // strips 'can://' + one trailing char
ip         = tail.left(indexOf(':'))
port       = tail after last ':'  (toInt)
mNumBuses  = hostCanIDs.count()
```
In WinCAN parse with `Uri` after trimming the trailing `)`/`)` artefact, then split the prefix on `,`.
Each bus is initialised to `active=true, listenOnly=false, speed=500000` and marked configured. Status starts NOT_CONNECTED.

### 3.2 Lifecycle / threading

- One `QTcpSocket` per bus in a `QVarLengthArray<QTcpSocket*>`, all owned by the worker thread.
- `piStarted()` → `connectDevice()` (create/connect every socket) → `checkConnection()` → start the **2000 ms** reconnect timer.
- `connectDevice()` skips sockets already in `ConnectedState`/`ConnectingState`; other sockets are closed/deleted/recreated and `rx_state[i] = IDLE`.
- `checkConnection()`: connected iff every socket is in `ConnectedState`. If not: `reconnecting = true`, status NOT_CONNECTED, reconnect all. If yes: status CONNECTED; emit status only on the transition out of `reconnecting`.
- `readyRead` → `invokeReadTCPData()` (queues `readTCPData(busNum)` in the worker) → `procRXData`.
- `piStop()` stops the timer and closes all sockets; status NOT_CONNECTED, emit status.
- No timeouts other than the 2 s poll. `piSetBusSettings` stores config (and then overwrites speed to 250000 — a ref bug; do not port, just store).

### 3.3 Handshake (per bus, state machine `KAYAKSTATE`)

```
IDLE=0, BCM=1, SWITCHING2RAW=2, RAWMODE=3, ISOTP=4
```

| state | received datagram | action | next |
|---|---|---|---|
| IDLE | exactly `< hi >` | send `< open <busName> >` | BCM |
| IDLE | anything else | log "host did not greet with `< hi >`" | IDLE |
| BCM | exactly `< ok >` | send `< rawmode >`, clear RX buffer | SWITCHING2RAW |
| BCM | anything else | log "did not respond with `< ok >`" | BCM |
| SWITCHING2RAW | exactly `< ok >` | — | RAWMODE |
| SWITCHING2RAW | starts with `< ok >` (case-insensitive) | `buffer = decodeFrames(data)` | RAWMODE |
| SWITCHING2RAW | contains `< ok >` at index > 0 | `buffer = decodeFrames(data)` | RAWMODE |
| RAWMODE | any | `buffer = decodeFrames(buffer + data)`; if `buffer.Length > 128` → clear buffer | RAWMODE |

Strings sent: `"< open can0 >"`, `"< rawmode >"` (with surrounding spaces exactly as shown, `>` last, no CR/LF).

### 3.4 Frame line grammar

```
< frame <ID hex> <TIMESTAMP seconds> <DATA hex> >
```
e.g. `< frame 1A2B 1234567.890123 0102030405060708 >`

Parsing (`decodeFrames`, socketcand.cpp:271-354):
- Discard anything before the first `< frame ` (logged); datagrams without `<` return empty.
- Take the first `>`-terminated block, strip leading `<` and trailing `>`, `simplified()` (collapse whitespace), split on space.
- `< 3` tokens → incomplete, keep the buffer for the next read.
- `id = hex(token[1])`; `extended = id > 0x7FF`; `bus = busNum` (local bus index).
- `timestamp = token[2] as double * 1_000_000` → `TimeStamp(0, us)` (device seconds → µs; note this is float seconds, so ~µs precision is all you get).
- Length is derived from the hex string when exactly 4 tokens: `len = token[3].Length / 2`; with 3 tokens the length is 0.
- Frame type `CANDataFrame`, `received = true`; `capSuspended` drops.
- Recurses on the remainder to consume multiple frames.

### 3.5 TX grammar (`piSendFrame`)

Requires `rx_state[bus] == RAWMODE` and an open socket, else returns false:
```
< send <ID hex lowercase> <len decimal> <byte hex> <byte hex> ... >
```
- `ID = frame.frameId()`, with bit31 set when extended (`| 1 << 31`); printed lowercase hex, no padding.
- `len` decimal payload length; every data byte printed as lowercase hex (`QString::number(b,16)`, no zero padding) separated by single spaces; a space precedes the closing `>`.
- Error frames short-circuit to `true`; no CR/LF is appended.

---

## 4. CANserver (UDP, PANDA "v1" stream)

### 4.1 Lifecycle

- Construction: 3 buses, each `active=true, listenOnly=true, speed=500000`; server address = the port string (plain IPv4/hostname).
- `connectToDevice()`: `bind(1338)` (local port; failure is logged and ignored), send one `hello`, then a **3500 ms** heartbeat timer, status CONNECTED, `CANConStatus{3}`.
- `disconnectFromDevice()`: stop timer, send `"bye"`, close socket. `piStop` calls this. No reconnect logic, no timeouts.
- Socket is created in the worker-thread object, and `readyRead` runs in it; no extra thread hops.
- TX is unsupported: `piSendFrame` returns true and discards.

### 4.2 Wire format

UDP datagrams to/from server port **1338**. Requests are 5-byte ASCII (`hello`, `bye`). Responses are a stream of **16-byte packets**, `packetCount = datagram.Length / 16` (trailing remainder ignored):

```
off 0..7  header1 : uint32 LE   →  frameId = header1 >> 21   (11-bit effective)
off 8..15 header2 : uint32 LE   →  length  = header2 & 0x0F
                                   busId   = header2 >> 4      (0..15)
```
Note the reference reads `header1`/`header2` from bytes 0..3 and 4..7 of each 16-byte slot but takes payload from `i*16 + 8` and length from the *second* header word: i.e. slot layout = `[header1:4][header2:4][data:8]`, and the "uint32 LE" reads are of consecutive byte pairs 0..3 and 4..7 — implement exactly as `header1 = LE32(p,0)`, `header2 = LE32(p,4)`, `len = header2 & 0xF`, `bus = header2 >> 4`, `data = p[8 .. 8+len)`.

Frame mapping:
- `frameId = header1 >> 21` (29-bit IDs are **not** supported; `setExtendedFrameFormat(false)` always).
- `busId == 15` is a synthetic "special" bus → mapped to local bus **2**.
- `FrameType = CANDataFrame`, `received = true`, timestamp = **wall clock** (`epochNowUs`), unaffected by `useSystemTime` in the ref (keep that behaviour).
- Suspended capture: the datagram is read and discarded without parsing.

---

## 5. CANlogserver (TCP, ASCII)

### 5.1 Lifecycle

- 1 bus (`active=true, listenOnly=true, speed=500000`).
- `connectToDevice()`: parse `QUrl("http://" + address)` → `host`, `port`; set `NoProxy`; `connectToHost(host, port)`. Status is set NOT_CONNECTED before connecting and CONNECTED on the `connected` signal (`numHardwareBuses = 1`). `disconnected` only logs — no reconnect, no heartbeat, no timeout.
- `readNetworkData()` uses `canReadLine()` in a loop (LF-delimited); each line is `trimmed()`, so CR/LF are tolerated.
- TX unsupported (returns true). Suspended capture: early return if `isCapSuspended()`.

### 5.2 Line grammar

```
(<seconds>.<fraction>) <canID hex> <id>#<payload hex>
```
Three space-separated fields required.

- Field 0 timestamp: remove `(`, `.`, `)` and take the remaining digit string as an **unsigned integer of microseconds** (`toULongLong`). i.e. `(12345.678901)` → `12345678901` µs. (Naive concatenation — port it as-is for compatibility.)
- Field 1 can ID token: ignored (bus is hard-coded to local bus 0).
- Field 2 split on `#`:
  - classic: `id#payload` (2 parts).
  - CAN FD: `id##<flags><payload>` (3 parts where part[1] is empty): `flags` is **one hex digit**, bit0 = BRS, bit1 = ESI; the payload starts at the second character.
- ID length check: accepted when `id.Length <= 4` or `id.Length == 8` (hex). `extended = id > 0x7FF`.
- Frame type: `CANDataFrame`, or `CANFDDataFrame` (+ `setBitrateSwitch(flags&1)`, `setErrorStateIndicator(flags&2)`) for the `##` form.
- Timestamp: `TimeStamp(0, parsedUs)` (device time, not `useSystemTime`).
- Payload: `fromHex(payload)`.

---

## 6. MQTT bus (`MQTT_BUS`, QMQTT)

### 6.1 Connection

- The "port" string is the **topic base name**; one CAN bus.
- Broker settings (QSettings; WinCAN store equivalents):
  `Remote/User` default `"Anonymous"`, `Remote/Host` default `"api.savvycan.com"`, `Remote/Port` default `8883`, `Remote/Pass` default empty and **encrypted with SimpleCrypt key `0xdeadbeefface6285`** (port the decryption or, better, use Windows DPAPI/ProtectedData with the same "value is opaque bytes" contract).
- Port `8883` → TLS using default `QSslConfiguration`; any other port → plain TCP with the first resolved `QHostAddress` from `QHostInfo::fromName(host)`.
- Client ID: 12 random chars from `[A-Za-z0-9]` (`0..25→A-Z, 26..51→a-z, 52..61→0-9`).
- Username/password sent only when non-empty. No will, no keepalive override (QMQTT default), no clean-session override.
- `connectToHost()` in `piStarted()`; `clientConnected` → subscribe `topicName + "/+"` at **QoS 0**, then attach the message handler, status CONNECTED, `CANConStatus{1}`.
- Errors are only logged (full QMQTT error enum is translated to debug strings; see mqtt_bus.cpp:95-190). No reconnect, no timer (`mTimer` unused). `piStop` stops the timer and sets NOT_CONNECTED.

### 6.2 Topics

- Subscribe: `<topic> / +` — one wildcard level, so frame topics are `<topic>/<frameId>`.
- Publish: `<topic> / s / <frameId decimal>` (prefix `s` marks a send/echo channel). RX parses the frame ID from **topic part index 1** with `toInt()` (decimal), so both `<topic>/<id>` and `<topic>/s/<id>` are accepted on receive.

### 6.3 Payload (both directions)

```
off 0..7   timestamp : uint64 little-endian, microseconds
off 8      flags     : uint8
off 9..    data      : 0..N payload bytes (rest of message)
```
Flags bit values: `0x01` extended ID, `0x02` remote request, `0x04` CAN FD, `0x08` error frame, `0x10` BRS, `0x20` ESI. Composite flags are allowed on TX (`flags += ...` per condition); on RX the frame type is chosen by precedence **error (8) → remote (2) → FD (4) → data**.

RX mapping: `bus = 0`, `frameId = topic id` (decimal, no width validation), `received = true`; BRS/ESI only applied when the FD bit is set. Timestamp = payload µs unless `useSystemTime`, in which case wall clock is used.
TX: `frameId() & 0x20000000` (error frames) short-circuit without publishing; extended/remote/FD/error/BRS/ESI flags derived from the frame; timestamp is always wall clock.

---

## 7. Cross-cutting implementation notes for C#

- **Timestamp type**: `readonly record struct Timestamp(long Seconds, int Microseconds)` with `FromMicroseconds(long)`; keep microsecond precision end-to-end and never convert capture time to `DateTimeOffset` except for display.
- **Per-connection worker**: one long-running `Task`/dedicated `Thread` per connection owning the transport, mirroring the `moveToThread` model. Call `Start/Stop/SendFrame/SetBusSettings` into it synchronously (e.g. `Channel` + `TaskCompletionSource`) so `sendFrame` stays blocking like `BlockingQueuedConnection`.
- **Receive queue**: bounded ring buffer (`Channel.CreateBounded<CommFrame>(4000)` with `BoundedChannelFullMode.DropWrite` approximates `LFQueue::get() == null`), drained by a 20 ms app-level pump that applies the bus base offset.
- **Bus base offsetting** must be reproduced exactly (`sum of numBuses of preceding connections`) — it is user-visible in the frame table and in DBC filter binding.
- **Suspension**: check the `CaptureSuspended` flag at the top of every parse path and flush on suspend.
- **Error-frame sentinel**: `frameId() & 0x20000000` is the "not a real ID" test used by every TX path.
- **`setFrameId` auto-extended rule**: IDs with any bit in `0x1FFFF800` set force `extended = true`; IDs `>= 0x20000000` are invalid (id 0, valid flag false).
- **Serial specifics**: .NET `SerialPort` needs explicit `DtrEnable/RtsEnable` for the GVRET ESP path; the 3 s settle delay is protocol-relevant (ESP32 may be rebooting), and hardware handshake for Lawicel and standard-mode GVRET.
- **socketcand** and **CANlogserver** both assume line/`>`-delimited text; use a byte-oriented accumulator per socket, not `StreamReader` line reads (socketcand frames have no newline).
- **CANserver/MQTT/GVRET-FD** carry binary payloads up to 64 bytes (GVRET FD), 254 is only reachable via FlexRay (not in these drivers).
- Reproduce the reference's **deliberate quirks** (Lawicel decimal classic DLC, CANserver's naive timestamp concatenation, GVRET's drop on `len > 64`/`len > 8` clamping, socketcand's `token[2] as double` seconds), and **fix** the reference's clear bugs: GVRET `GET_ANALOG_INPUTS` never leaving the state, socketcand overwriting bus speed with 250000 in `piSetBusSettings`, and Lawicel DLC `toInt()` fallback reading mid-payload.

## 8. WinCAN protocol deviations (deliberate fixes)

Implemented in `src/WinCAN.Connections/Protocols`, each covered by tests in
`tests/WinCAN.Tests/Protocols`:

| Driver | Reference behaviour | WinCAN behaviour | Why |
|---|---|---|---|
| Lawicel TX | error-frame guard `frameId() & 0x20000000` can never fire (the accessor masks the bit off), so error frames are sent as ID 0 | error frames are not sent | Matches the guard's stated intent |
| socketcand TX | extended IDs set bit 31 in a signed `int` and print with a minus sign (`-1f0e0000`) | ID printed as unsigned 32-bit hex with bit 31 set (`98daf110`) | The host parses the ID as unsigned; the reference's output can never be understood |
| socketcand `piSetBusSettings` | stores the bus config, then overwrites the speed with 250000 | stores the config unchanged | Reference bug with no protocol meaning |

The Lawicel and socketcand **receive** paths keep the reference grammar exactly (including
Lawicel's DECIMAL classic DLC, HEX CAN FD DLC codes, 4-hex-digit hardware counter with the
60 s wrap, and socketcand's `< hi >`/`< ok >` handshake with its partial-datagram buffer).

---

## 9. PCAN-USB (`PcanConnection`, WinCAN native)

SavvyCAN reaches PEAK adapters through Qt SerialBus's `peakcan` plugin. WinCAN calls PEAK's
**PCAN-Basic** API directly (`PCANBasic.dll`), so USB enumeration is owned by the PEAK driver
and channel polling in the device manager is not needed.

- USB identity: **VID `0x0C72`, PID `0x000C`** (PCAN-USB). The driver binds the device; the
  application addresses it by channel handle.
- Channels: `CAN_Initialize(channel, BTR0BTR1, 0, 0, 0)` with
  `PCAN_USBBUS1 = 0x0051` … `PCAN_USBBUS16 = 0x0060`.
- Detection: `CAN_GetValue(ch, PCAN_CHANNEL_CONDITION = 0x0D)` → `AVAILABLE(1)` or
  `OCCUPIED(2)`; `CAN_GetValue(ch, PCAN_HARDWARE_NAME = 0x0E)` returns `"PCAN-USB"`.
- Bit timing constants used by the connection dialog:

  | Bit rate | BTR0/BTR1 | Bit rate | BTR0/BTR1 |
  |---|---|---|---|
  | 1 Mbit/s | `0x0014` | 100 kbit/s | `0x432F` |
  | 800 kbit/s | `0x0016` | 95 kbit/s | `0xC34E` |
  | 500 kbit/s | `0x001C` | 83.333 kbit/s | `0x852B` |
  | 250 kbit/s | `0x011C` | 50 kbit/s | `0x472F` |
  | 125 kbit/s | `0x031C` | 33.333 kbit/s | `0x8B2F` |

- Receive: poll `CAN_Read` roughly every 3 ms; `PCAN_ERROR_QRCVEMPTY` (0x20) means no frame.
  The driver timestamp (`millis` + `millis_overflow` + `micros`) is anchored to the epoch on
  the first frame so capture timestamps stay in microseconds.
- Transmit: `CAN_Write` with standard 11-bit, extended 29-bit and RTR flags, classic payload
  up to 8 bytes. **CAN-FD transmission is not implemented** in this transport yet.
- Common status codes: `QRCVEMPTY 0x20`, `BUSLIGHT 0x4`, `BUSHEAVY 0x8`, `BUSOFF 0x10`,
  `NODRIVER 0x200`, `HWINUSE 0x400` (another application owns the channel),
  `ILLHW 0x1400` (no hardware on that channel), `INITIALIZE 0x4000000`.

Live verification on the development machine: PCAN-USB detected on USB1 at 500 kbit/s,
`CAN_Initialize` succeeds, and the connection reports `Connected` (the test bus was idle, so
no frames were received).
