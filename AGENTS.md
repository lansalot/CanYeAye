# AGENTS.md — CanYeAye

CanYeAye is a .NET 10 port of [SavvyCAN](https://github.com/collin80/SavvyCAN) (Qt6/C++17).
Goal: full feature parity, equivalent UX (modernized visuals, not a Qt pixel clone), same
connectivity and same log/DBC file formats.

Reference implementation lives at `C:\Github\SavvyCAN`.

**Hard rule: never modify `C:\Github\SavvyCAN` or anything outside `C:\Github\CanYeAye` without
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
CanYeAye.sln
src/CanYeAye.Core          frame model, timestamps, filters, file I/O, DBC, scripting host
src/CanYeAye.Connections   transport abstraction + GVRET, Lawicel, socketcand, MQTT, PCAN, Vector
src/CanYeAye.App           Avalonia UI, MVVM (CommunityToolkit.Mvvm)
tests/CanYeAye.Tests       xUnit: parser round-trips, protocol framing, DBC, filter logic
build/macos              package-app.sh: osx publish -> CanYeAye.app bundle + zip
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
- Settings are QSettings (registry on Windows) in SavvyCAN. CanYeAye uses its own store; do not
  read/write SavvyCAN keys even for "compatibility".
- SavvyCAN vendors everything; here NuGet is allowed. Prefer maintained, permissively
  licensed packages and pin versions centrally.
- `pico-flexray` (libusb) is optional in SavvyCAN; don't block parity work on it.
- Licensing: SavvyCAN's `LICENSE` is MIT, but `can_structs.h` is derived from Qt's
  `QCanBusFrame` (LGPL/GPL family) per its own comment. Keep attribution and flag licensing
  before any distribution.

## Commands

The solution is scaffolded (`CanYeAye.slnx`, SDK pinned by `global.json`). Always run from the
repo root so the pinned SDK is used:

```powershell
dotnet build
dotnet test --nologo -v q
dotnet run --project src/CanYeAye.App
dotnet test --filter FullyQualifiedName~FileFormats
```

## CI / releases

`.github/workflows/build.yml` runs **only when a tag matching `v*` is pushed** — branch pushes
and pull requests build nothing. A tag only builds when its commit is on `main`: tags on
unmerged branches or side commits fail the `tag-on-main` gate and never reach the build,
publish or release jobs. Cut a release by tagging the commit and pushing the tag:

```powershell
git tag v0.1.3
git push origin v0.1.3
```

- **tag-on-main**: gate that fails the run unless the tagged commit is reachable from `main`
  (`git merge-base --is-ancestor $GITHUB_SHA origin/main`, with `fetch-depth: 0`). Tags on
  unmerged branches or side commits never reach the build; merge into `main` first, then
  move/re-push the tag.
- **build-test** (windows-latest, .NET 10.0.404): restore (NuGet cache), Release build and
  `dotnet test` (308 tests). Needs `tag-on-main`.
- **publish**: self-contained `win-x64` (zip), `linux-x64` (tar.gz) and `osx-arm64`
  (`CanYeAye.app` bundle, ad-hoc signed, zipped by `build/macos/package-app.sh`) artifacts, one
  runner per RID. The tag (without the leading `v`) becomes the assembly/package version.
- **release**: creates the GitHub Release for the pushed tag with the generated notes and both
  packages.

`publish` and `release` require the gate **and** the previous job to succeed, so a tag that
fails the gate produces no build, package or release.

## Progress so far

- `CanYeAye.Core`: `CanFrame` / `FrameTimestamp` (CommFrame semantics), `CanFilter`, `CanIdParser`,
  `FrameStore`, settings store, and the log-format layer
  (`src/CanYeAye.Core/FileFormats`, `LogFormatRegistry`).
- Log formats implemented: native GVRET CSV (V1/V2 incl. FD/FlexRay), CRTD, BusMaster,
  Microchip, Vector trace, IXXAT MiniLog, CAN-DO, Vehicle Spy, candump, Lawicel, PCAN,
  CANalyzer ASC, CARBUS, CANHacker, Generic CSV, Cabana, CANOpen Magic, IXXAT CSV, CANWise,
  Kvaser (decimal/hex), CANalyzer BLF (containers + zlib), pcap/pcapng (SocketCAN and Linux
  cooked captures), Tesla AP snapshots, CANServer binary and CLX000.
- `CanYeAye.Connections`: `ICanConnection`, `CanConnectionManager`, option records, text
  protocols (`GvretProtocol`, `LawicelProtocol`, `SocketCanDProtocol`, `CanServerProtocol`,
  `CanLogServerProtocol`, `MqttFrameCodec`), and transport implementations over serial/TCP/UDP
  (`GvretConnection`, `LawicelConnection`, `SocketCanDConnection`, `CanServerConnection`,
  `CanLogServerConnection`, `MqttConnection`) plus `PcanConnection` (PEAK PCAN-USB over
  PCAN-Basic, classic CAN, channel detection via `PCAN_CHANNEL_CONDITION`) and passive UDP
  discovery (`ConnectionDiscovery`: GVRET beacons on 17222, socketcand CANBeacon XML on 42000).
- `CanYeAye.Core.Dbc`: model (`DbcFile`, `DbcMessage`, `DbcSignal`, nodes/attributes/value
  tables), `SignalCodec` (sawtooth extract/insert + float/string decode), `DbcFileReader`
  (SavvyCAN grammar with fault list), `DbcFileWriter` (byte-stable save incl. CAN FD
  `VFrameFormat`/`CANFD_BRS` sync) and `DbcDatabase` (multi-file lookup, J1939/GMLAN).
  Validated against SavvyCAN's `examples/*.dbc` and the 17 real files in `DBCsamples`
  (copied to `tests/CanYeAye.Tests/Fixtures/DbcSamples`); both folders are git-ignored, so
  `DbcSampleValidationTests` only exercises whatever is present locally and is a no-op in a
  fresh clone or on CI.
- DBC editing (parity with `dbc/dbcmaineditor` + the child editors): `Core.Dbc.DbcEditor` holds
  the mutation operations (add/delete node/message/signal, ID re-key, signal clone, multiplex
  links, rebase/duplicate, sawtooth bit positions) with 26 xUnit tests. The UI is
  `DbcEditorWindow` (tree, New Node/Message/Signal, Delete with confirmation, Find/Next/Previous,
  context menu with Edit/Rebase/Duplicate/Delete, double-click to edit), `SignalEditorWindow`
  (custom `DbcBitGrid`: 8-byte blocks, LSB0 numbering, green hatch for the current signal, start
  bit ring, per-signal colours and names, left-click start bit, right-click switch signal; all
  form fields; multiplex mode + values + parent; value table with trailing blank row, hex/decimal
  header menu and row delete), `MessageEditorWindow` (ID/name/length/CAN FD/BRS/extended/sender
  with free-text node creation, colour pickers writing `GenMsgForegroundColor`/
  `GenMsgBackgroundColor`, coloured sample) and `NodeEditorWindow` plus the shared rebase /
  duplicate dialog. The DBC files window gains Edit (double-click) and Save; editor geometry is
  saved with the other window geometry. Design notes: `docs/research/dbc-and-scripting.md`
  section 15.
- `CanYeAye.Core.Protocols`: ISO-TP transport (`IsoTpHandler`, single/multi-frame, flow control,
  segmented TX), UDS (`UdsHandler`) and OBD-II (full Mode 01 PID table, `Obd2Dbc` generator,
  `Obd2Poller`). `CanYeAye.Core.Scripting.ScriptHost` runs SavvyCAN `.js` files on Jint with the
  `host`/`can`/`isotp`/`uds` globals and positional callbacks; all four `examples/*.js` run
  unmodified.
- `CanYeAye.App` (Avalonia): main window with the SavvyCAN right-hand frame panel (total/FPS,
  suspend, normalize, clear, keep-filters, auto-scroll, overwrite mode with counts, interpret
  frames, expand/collapse, bus filtering), reorderable/hideable grid columns (incl. ASCII),
  in-row DBC interpretation, zebra-striped rows, vertically centred Data cells, status bar, and
  10%-step UI scaling (View menu). Connection dialog split by connection type (GVRET serial/TCP,
  Lawicel, QT SerialBus placeholder, socketcand, MQTT, CANserver, CANlogserver); GVRET TCP uses
  port 23 and the CAN bus speed is a drop-down of standard speeds, persisted in settings.
  Non-modal send-frame window, DBC manager (multi-file load/remove/reorder), log-loading
  progress dialog, scripting window, OBD-II poller window, sniffer window, flow-view window,
  and a compact frame-filter pane (per-ID list plus a J1939 node-name/bulk-selection tab) that
  docks into the main window on the left or right (the grid resizes around it) and only uses a
  separate window in Float/Full Height mode; the last host mode is remembered. ID filter
  labels include DBC message names when interpreting, and loading a new log resets the
  ID/bus filters. Layout
  (position, columns, toggles, scale, loaded DBCs) persists across restarts. `dotnet run --
  <log> [dbc...]` opens files at startup. Verified with screen captures, including a 324k-frame
  load.
- Sniffer (parity with `re/sniffer`): custom-drawn grid with Delta / Frequency / ID / byte
  columns, changed-byte green/red colouring, notching (per notch interval), mute notched bits,
  byte fading, bit view (each bit is a larger green=1 / red=0 cell with a white outline),
  configurable expire interval and a minimum-rate (Hz) filter that hides IDs while their
  observed rate is below the threshold. Header stays fixed while scrolling; the ID filter list
  is seeded from the main window's ID filters when it opens, and IDs that arrive later are
  added checked by default.
- Flow View (parity with `re/flowviewwindow`): `CanYeAye.Core.Analysis.FlowViewSession` holds the
  frames of the selected ID plus the auto-reference, seek and trigger state (unit tested); the
  window lists every capture ID while ignoring the main window's filters, steps one frame
  (`T`/`Y`) or plays forward/backward (`R`) at a configurable interval with looping, follows
  live traffic, and scrubs with a timeline slider. Set bits are black and clear bits white in a
  full-width grid (wide bars, no per-cell borders) where 0→1 flashes green and 1→0 red; static
  reference bytes are editable or auto-set to the previous frame. Seek bytes (`-1` disabled)
  stop playback on a match, and clicking a bit makes it a trigger that stops playback when it
  changes. The byte graph is a custom windowed control with a range slider, per-byte `Graph?`
  toggle and an eight-colour palette. Geometry persists; design notes:
  `docs/research/flow-view.md`.
- Detailed Frame Information (parity with `re/frameinfowindow`): `CanYeAye.Core.Analysis`
  `FrameInfoAnalyzer` builds a snapshot report for one ID (per-byte range/`ChangedBits`/value
  histogram/series, 64-bit set counts and change rates, seconds-aware interval statistics with
  a 20-bin histogram, DBC signal value histograms, J1939/GMLAN decoding, and a detail-node tree
  shared with the tab-indented text export). `FrameInfoWindowViewModel` snapshots the capture
  when the window opens - it never follows live traffic - and drives custom-drawn heatmap, bit
  and interval histograms and eight per-byte value graphs. ID list, detail tree, save-all-
  details, last-window geometry and Tools → Detailed Frame Information… wiring are in place;
  13 analyzer tests. Design notes: `docs/research/frame-info.md`.
- J1939 node analysis (CanYeAye original, no SavvyCAN equivalent): `CanYeAye.Core.Analysis`
  (`J1939NodeNames` with load/save file and settings persistence, `NodeTraffic` per source /
  conversation / broadcast). The Frame Filtering window keeps the per-ID list on its
  `Frame IDs` tab and adds a `J1939 Nodes` tab for renaming nodes and bulk-selecting a node's
  IDs (All/None, mixed = dash, plus a leading Broadcast row covering all broadcast
  destinations) plus Load/Save/Reset. The main grid has a `Nodes` column showing
  `source → destination` with names. The Node Graph window (Tools → Node Graph…) shows live or
  captured traffic: nodes sized by involvement, bidirectional edges weighted by volume,
  broadcast bar, hover details and top talkers (`Name (0xNN): frames`), drag, wheel/pinch zoom,
  pan, long-press/double-click node naming. Node positions, zoom/pan and window geometry
  persist. Design notes: `docs/research/j1939-node-analysis.md`.
- macOS (Apple silicon) support: builds, tests and runs natively. On macOS the main window's
  XAML `Menu` is mirrored into the global menu bar by `Services/NativeMenuBridge` (clicks are
  forwarded to the original `MenuItem`, so Command/Click wiring stays in one place; Exit maps
  to the app menu's Quit) and shortcuts use Cmd (`NativeMenuBridge.SetShortcut`). PCAN uses a
  `DllImportResolver` in `PcanNative` that maps `PCANBasic.dll` to MacCAN's `libPCBUSB.dylib`
  (macOS) or `libpcanbasic.so` (Linux) - same PCAN-Basic C API. Vector XL has no macOS driver.
  The About box is `Views/AboutWindow` (version from `AssemblyInformationalVersion`) and is
  reachable from macOS's application menu and from Tools → About CanYeAye… on every platform.
- Fixtures copied from SavvyCAN's `examples` live in `tests/CanYeAye.Tests/Fixtures`; deviations from
  the reference are listed in `docs/research/log-formats.md`.

### Next session

- DBC editor follow-up: port the signal editor undo stack (Ctrl+Z) from dbcsignaleditor.cpp.
- Verify the node graph's pinch-zoom and long-press naming on a real touchscreen (the control
  tracks touch pointers manually).
- Optional Flow View follow-ups: hover readout on the byte graph, remembering the last selected
  ID and playback options across restarts, and validating seek/ref input inline.
- Optional Frame Info follow-ups: chart zoom/pan, remembering the last selected ID, and a
  per-ID DBC signal drill-down.
- Optional graph follow-ups: time-window filtering for live traffic, PNG export, per-conversation
  hover readout, node-size legend.


Avalonia + vendor SDKs: PCAN-Basic / Vector XL DLLs are runtime-only dependencies on Windows;
keep them behind interfaces so the app builds and tests run without hardware or those DLLs.
