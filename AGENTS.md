# AGENTS.md — WinCAN

WinCAN is a .NET 10 port of [SavvyCAN](https://github.com/collin80/SavvyCAN) (Qt6/C++17).
Goal: full feature parity, equivalent UX (modernized visuals, not a Qt pixel clone), same
connectivity and same log/DBC file formats.

Reference implementation lives at `C:\Github\SavvyCAN`.

**Hard rule: never modify `C:\Github\SavvyCAN` or anything outside `C:\Github\WinCAN` without
explicit permission.** The reference repo is read-only; read it freely.

## Locked decisions

| Area | Decision |
|---|---|
| UI | **Avalonia** (equivalent UX, modernized look; not a Qt clone) |
| Architecture | **Clean .NET layers** — not a 1:1 file/class mapping of the C++ |
| Scope | **Full parity in one pass** (no partial shipping milestones) |
| Bus connections | GVRET serial, Lawicel serial, socketcand (TCP), MQTT, PCAN-Basic, Vector XL via P/Invoke. No Qt SerialBus dependency. |
| Scripting | **Jint**; existing SavvyCAN JS scripts must run unmodified |
| Charts | **ScottPlot** (replaces QCustomPlot, incl. hex axis ticker) |
| NuGet | Standard NuGet allowed: maintained, permissive licenses, versions pinned centrally |
| Tests | **xUnit, new tests** (do not port QtTest suites mechanically) |
| SavvyCAN compat | Same log/DBC/project file formats; **own** settings store (do not touch SavvyCAN's QSettings registry keys) |

Because scope is full parity in one pass, build bottom-up: frame model + timestamp semantics →
file I/O → connection abstraction/transports → DBC + scripting → bus protocols → UI/RE windows.
Design layer boundaries before filling them in.

## Planned solution layout

```
WinCAN.sln
src/WinCAN.Core          frame model, timestamps, filters, file I/O, DBC, scripting host
src/WinCAN.Connections   transport abstraction + GVRET, Lawicel, socketcand, MQTT, PCAN, Vector
src/WinCAN.App           Avalonia UI, MVVM (CommunityToolkit.Mvvm)
tests/WinCAN.Tests       xUnit: parser round-trips, protocol framing, DBC, filter logic
```

Update this section if the layout changes during scaffolding.

## Reference repo map (read before porting anything)

- `can_structs.h` — `CommFrame`: `secs` + `usecs` timestamp, `FrameType` (CAN, CAN-FD,
  FlexRay, LIN, Error, RemoteRequest, Invalid), payload up to 254 bytes. Port these exact
  semantics; every format and transport depends on them.
- `framefileio.cpp` — all log formats (BusMaster, Microchip, CRTD/OVMS, GVRET, CSV, Vector
  TRC/BLF, IXXAT, CAN-DO, Vehicle Spy, candump, PCAN, pcap). `examples/` has fixtures.
- `connections/` — GVRET binary serial protocol, Lawicel, socketcand, MQTT, canserver /
  canlogserver, connection window, `canconmanager` (bus/frame routing).
- `bus_protocols/` — ISO-TP, J1939, OBD2 (PIDs, poller, DBC), UDS.
- `dbc/` — DBC parse/edit/save (node, message, signal editors).
- `re/` — sniffer, graphing/temporal/flow views, fuzzing, UDS scan + firmware upload, OBD2.
- `scriptcontainer.cpp` + `examples/*.js` — JS surface: `host`, `can`, `isotp`, `uds` globals.
  Preserve this API so existing scripts run unchanged under Jint.
- `test/`, `unit_tests/` — QtTest suites; treat as behavioral specs, write fresh xUnit tests.
- `main.cpp`, `mainwindow.cpp` — app wiring, docks/panes, settings, shortcuts (`shortcutcheck`).

## Gotchas that will bite a naive port

- Capture timestamps are **seconds + microseconds** (not `DateTime`). Keep microsecond
  precision end-to-end; don't convert capture timing to `DateTime`.
- Frames are not all classic 8-byte CAN: CAN-FD up to 64 bytes, FlexRay/LIN up to 254,
  plus remote/error frames. Model them first-class.
- IDs are 11-bit or 29-bit; preserve extended/remote flags through file formats.
- File-format compatibility is semantic, not just parseable: validate with round-trip tests
  against `C:\Github\SavvyCAN\examples` (read-only), including timestamps and flags.
- Settings are QSettings (registry on Windows) in SavvyCAN. WinCAN uses its own store; do not
  read/write SavvyCAN keys even for "compatibility".
- SavvyCAN vendors everything; here NuGet is allowed. Prefer maintained, permissively
  licensed packages and pin versions centrally.
- `pico-flexray` (libusb) is optional in SavvyCAN; don't block parity work on it.
- Licensing: SavvyCAN's `LICENSE` is MIT, but `can_structs.h` is derived from Qt's
  `QCanBusFrame` (LGPL/GPL family) per its own comment. Keep attribution and flag licensing
  before any distribution.

## Commands

The solution is scaffolded (`WinCAN.slnx`, SDK pinned by `global.json`). Always run from the
repo root so the pinned SDK is used:

```powershell
dotnet build
dotnet test --nologo -v q
dotnet run --project src/WinCAN.App
dotnet test --filter FullyQualifiedName~FileFormats
```

## Progress so far

- `WinCAN.Core`: `CanFrame` / `FrameTimestamp` (CommFrame semantics), `CanFilter`, `CanIdParser`,
  `FrameStore`, settings store, and the log-format layer
  (`src/WinCAN.Core/FileFormats`, `LogFormatRegistry`).
- Log formats implemented: native GVRET CSV (V1/V2 incl. FD/FlexRay), CRTD, BusMaster,
  Microchip, Vector trace, IXXAT MiniLog, CAN-DO, Vehicle Spy, candump, Lawicel, PCAN,
  CANalyzer ASC, CARBUS, CANHacker, Generic CSV, Cabana, CANOpen Magic, IXXAT CSV, CANWise,
  Kvaser (decimal/hex), CANalyzer BLF (containers + zlib), pcap/pcapng (SocketCAN and Linux
  cooked captures), Tesla AP snapshots, CANServer binary and CLX000.
- `WinCAN.Connections`: `ICanConnection`, `CanConnectionManager`, option records, text
  protocols (`GvretProtocol`, `LawicelProtocol`, `SocketCanDProtocol`, `CanServerProtocol`,
  `CanLogServerProtocol`, `MqttFrameCodec`), and transport implementations over serial/TCP/UDP
  (`GvretConnection`, `LawicelConnection`, `SocketCanDConnection`, `CanServerConnection`,
  `CanLogServerConnection`, `MqttConnection`) plus passive UDP discovery
  (`ConnectionDiscovery`: GVRET beacons on 17222, socketcand CANBeacon XML on 42000).
- `WinCAN.Core.Dbc`: model (`DbcFile`, `DbcMessage`, `DbcSignal`, nodes/attributes/value
  tables), `SignalCodec` (sawtooth extract/insert + float/string decode), `DbcFileReader`
  (SavvyCAN grammar with fault list), `DbcFileWriter` (byte-stable save incl. CAN FD
  `VFrameFormat`/`CANFD_BRS` sync) and `DbcDatabase` (multi-file lookup, J1939/GMLAN).
  Validated against SavvyCAN's `examples/*.dbc` and the 17 real files in `DBCsamples`
  (copied to `tests/WinCAN.Tests/Fixtures/DbcSamples`).
- `WinCAN.Core.Protocols`: ISO-TP transport (`IsoTpHandler`, single/multi-frame, flow control,
  segmented TX), UDS (`UdsHandler`) and OBD-II (full Mode 01 PID table, `Obd2Dbc` generator,
  `Obd2Poller`). `WinCAN.Core.Scripting.ScriptHost` runs SavvyCAN `.js` files on Jint with the
  `host`/`can`/`isotp`/`uds` globals and positional callbacks; all four `examples/*.js` run
  unmodified.
- `WinCAN.App` (Avalonia): main window with the SavvyCAN right-hand frame panel (total/FPS,
  suspend, normalize, clear, keep-filters, auto-scroll, overwrite mode with counts, interpret
  frames, expand/collapse, bus and per-ID filtering), reorderable/hideable grid columns,
  optional log pane and status bar. Connection dialog, non-modal send-frame window, DBC manager
  (multi-file load/remove/reorder), log-loading progress dialog, scripting window, OBD-II poller
  window and sniffer window. `dotnet run -- <log>` opens a log at startup. Verified with screen
  captures, including a 324k-frame load.
- Fixtures copied from SavvyCAN's `examples` live in `tests/WinCAN.Tests/Fixtures`; deviations from
  the reference are listed in `docs/research/log-formats.md`.


Avalonia + vendor SDKs: PCAN-Basic / Vector XL DLLs are runtime-only dependencies on Windows;
keep them behind interfaces so the app builds and tests run without hardware or those DLLs.
