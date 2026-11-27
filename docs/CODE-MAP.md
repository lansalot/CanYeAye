# CanYeAye code map

Purpose: get from "I need to change X" to the right files in one hop, then `grep` the named
symbol. Don't read whole files to orient yourself.

Conventions: this file names **types and members, never line numbers** (line numbers rot, symbols
don't). Every path in backticks is checked by `DocMapTests` — if a rename breaks one, the test
fails instead of the doc lying. Deep-dive design notes live in `docs/research/`.

## 1. Task → files

### Frame grid and main window

| I need to... | Start here |
|---|---|
| Add/remove a grid column | `src/CanYeAye.App/MainWindow.axaml` (`DataGridTextColumn` list + "Show X column" menu items), `src/CanYeAye.App/MainWindow.axaml.cs` (`FindColumn`, `CaptureColumns`, `ApplyColumnLayout`), `src/CanYeAye.App/ViewModels/FrameRowViewModel.cs` |
| Change a formatted cell value | `src/CanYeAye.App/ViewModels/FrameRowViewModel.cs`; hex/length/ID helpers in `src/CanYeAye.Core/Formatting/HexFormat.cs` and `src/CanYeAye.App/Formatting/TimeFormat.cs` |
| Change row/cell colouring | `src/CanYeAye.App/MainWindow.axaml` — row styles are the zebra `DataGridRow:nth-child(even)` selector plus the `DataGridRow.highlight` tint selectors; brushes are per-theme in `src/CanYeAye.App/App.axaml` (zebra) and `src/CanYeAye.App/Services/FrameHighlightPalette.cs` (ID tints). `FrameRowViewModel.HighlightBrush` is the per-row tint; `src/CanYeAye.App/MainWindow.axaml.cs` (`ApplyRowHighlightClass`) maintains the `highlight` class on realised rows. |
| Add a seat on the right-hand panel (checkboxes, labels) | `src/CanYeAye.App/MainWindow.axaml`, bound properties on `src/CanYeAye.App/ViewModels/MainWindowViewModel.cs` |
| Change what "Interpret Frames" prints | `MainWindowViewModel.Describe` / `DescribeDiagnostics`, DBC lookups via `src/CanYeAye.Core/Dbc/DbcDatabase.cs` |
| Change filtering behaviour | `src/CanYeAye.Core/Frames/CanFilter.cs` (logic), `MainWindowViewModel.PassesFilters` / `RegisterFilterEntries` / `SetAllFilters` (wiring), `src/CanYeAye.App/Views/FrameFilterView.axaml` + `src/CanYeAye.App/ViewModels/FilterItems.cs` (pane) |
| Add a menu item | `src/CanYeAye.App/MainWindow.axaml` only — macOS mirrors it automatically via `src/CanYeAye.App/Services/NativeMenuBridge.cs`; wire the `Click` handler in `src/CanYeAye.App/MainWindow.axaml.cs` |
| Add a window | `src/CanYeAye.App/Views/` (`.axaml` + `.axaml.cs`), `src/CanYeAye.App/ViewModels/`, plus menu item and an `On…Click` handler in `MainWindow.axaml.cs` |
| Persist a new UI value | `AppSettings` record in `src/CanYeAye.Core/Settings/SettingsStore.cs`, apply in the `MainWindow` constructor, save in `MainWindow.axaml.cs` `SaveSettings` (runs on `Closing`). Note: some fields are inert — `ShowDecimalIds` is never read. |
| Add a custom-drawn view | `src/CanYeAye.App/Controls/` (Avalonia `Control` subclasses; GDI/Qt-style painting lives there, not in XAML) |
| Change CLI startup arguments | `src/CanYeAye.App/Program.cs`, `src/CanYeAye.App/App.axaml.cs` |

### Frames, files, protocols, connections

| I need to... | Start here |
|---|---|
| Change frame/timestamp semantics | `src/CanYeAye.Core/Frames/CanFrame.cs`, `FrameTimestamp.cs`, `FrameStore.cs` |
| Parse user-entered IDs (`0x1F3`, `1893`, binary) | `src/CanYeAye.Core/Frames/CanIdParser.cs` (`Parse`, `ParseId`, `FormatId`). **Single IDs only** — no comma-separated list parser exists anywhere in the repo; a multi-ID field needs one built on top. |
| Add or fix a log format | `src/CanYeAye.Core/FileFormats/` (one class per format implementing `src/CanYeAye.Core/FileFormats/ILogFormat.cs`) + register in `src/CanYeAye.Core/FileFormats/LogFormatRegistry.cs`; fixtures and expectations in `tests/CanYeAye.Tests/FileFormats/` |
| Change log loading in the app | `MainWindowViewModel.LoadLogFileAsync` / `PopulateLoadedAsync` |
| DBC parsing or saving | `src/CanYeAye.Core/Dbc/DbcFileReader.cs`, `DbcFileWriter.cs`, model in `DbcModel.cs`, bit maths in `SignalCodec.cs` |
| DBC mutation operations | `src/CanYeAye.Core/Dbc/DbcEditor.cs` (used by both the editors and tests) |
| DBC editor UI | `src/CanYeAye.App/Views/DbcEditorWindow.axaml`, `SignalEditorWindow.axaml`, `MessageEditorWindow.axaml`, `NodeEditorWindow.axaml` + `src/CanYeAye.App/Controls/DbcBitGrid.cs` |
| ISO-TP / UDS / OBD-II | `src/CanYeAye.Core/Protocols/IsoTpHandler.cs`, `UdsHandler.cs`, `Obd2Pids.cs`, `Obd2Poller.cs`, `Obd2Dbc.cs` |
| J1939 ID or diagnostics decoding | `src/CanYeAye.Core/Protocols/J1939Id.cs`, `J1939Diagnostics.cs`, `src/CanYeAye.Core/Analysis/J1939NodeNames.cs`, `NodeTraffic.cs` |
| Script API (`host`, `can`, `isotp`, `uds`) | `src/CanYeAye.Core/Scripting/ScriptHost.cs`; must stay source-compatible with SavvyCAN scripts in `C:\Github\SavvyCAN\examples` |
| Add or fix a transport | `src/CanYeAye.Connections/Transports/` + textual codec in `src/CanYeAye.Connections/Protocols/`; contract in `src/CanYeAye.Connections/ICanConnection.cs`; base class `src/CanYeAye.Connections/Transports/CanConnectionBase.cs` |
| Routing between buses/connections, listen-only guard | `src/CanYeAye.Connections/CanConnectionManager.cs` (`SendFrameAsync`, `FrameReceived`) |
| Connection dialog fields | `src/CanYeAye.App/Views/ConnectionWindow.axaml` + `MainWindowViewModel.CreateConnection` |
| PCAN / Vector native interop | `src/CanYeAye.Connections/Transports/PcanConnection.cs`, `PcanNative.cs` (must stay behind interfaces; tests run without the DLLs) |
| UDP discovery of adapters | `src/CanYeAye.Connections/Discovery/ConnectionDiscovery.cs` |
| Analysis data for Sniffer / Flow View / Frame Info / Node Graph | `src/CanYeAye.Core/Sniffing/SnifferEntry.cs`, `src/CanYeAye.Core/Analysis/FlowViewSession.cs`, `FrameInfoAnalyzer.cs`, `NodeTraffic.cs` |

## 2. Data flows

**A. Live frame → grid.** `ICanConnection.FrameReceived` → `CanConnectionManager.FrameReceived`
→ `MainWindowViewModel` (enqueues into a `ConcurrentQueue`) → `FlushPendingFrames` on the UI timer
→ `CreateRow` (new ID) or `UpdateOverwriteRow` (overwrite mode) → `FrameGrid` rows. Every frame is
also in `FrameStore` (`_store`), which is what analysis windows snapshot.

**B. Load a log.** `OpenLogAsync` → `LoadLogFileAsync` → format chosen by
`LogFormatRegistry` → `FrameStore` replaced → `ClearFilterState` (ID/bus filters are deliberately
reset) → `PopulateLoadedAsync` in batches with the progress window.

**C. Menu click.** `MainWindow.axaml` `MenuItem` → `On…Click` in `MainWindow.axaml.cs` → build
the window and its ViewModel → `Show`. On macOS the same click is replayed by `NativeMenuBridge`;
never wire the same action twice.

**D. Settings round-trip.** `SettingsStore.Load` in the `MainWindow` constructor → values applied
to the ViewModel and grid → `SaveSettings` on `Closing` writes `_settings with { … }`. Window
geometry for child windows follows the `src/CanYeAye.App/Services/DbcWindowState.cs` pattern.

**E. Transmit.** `SendFrameWindow` → `MainWindowViewModel.SendFrameAsync` →
`CanConnectionManager.SendFrameAsync`, which refuses everything when the connection is listen-only;
frames for a bus with no owning connection are echoed into the log (SavvyCAN `buslessFrames`
behaviour).

## 3. Layer rules

- `CanYeAye.Core` — no Avalonia, no `CanYeAye.Connections` reference. Pure logic, file I/O, parsing.
- `CanYeAye.Connections` — no Avalonia. References Core for `CanFrame` only.
- `CanYeAye.App` — UI, MVVM (`CommunityToolkit.Mvvm`), custom drawing in `Controls/`.
- Capture time is `FrameTimestamp` (seconds + microseconds). Never convert capture timing to
  `DateTime`.
- `CanFrame` carries `FrameType` (CAN, CAN-FD, FlexRay, LIN, error, remote) and up to 254 payload
  bytes; 11-bit and 29-bit IDs with extended/remote flags preserved through every format.
- Settings are CanYeAye's own JSON store. Never read or write SavvyCAN's QSettings keys.
- `C:\Github\SavvyCAN` is a read-only reference; nothing outside this repo is modified.

## 4. Landmines

- **Row colour is mostly XAML-only.** Zebra stripes come from one `:nth-child(even)` selector; the
  ID-tint `DataGridRow.highlight` selectors must have equal or greater specificity, and
  `:selected` / `:pointerover` must be excluded or the effect vanishes. The `highlight` class is
  set in code because virtualised rows cannot bind `Classes.*`; re-add (not just set) it when a
  recycled row gets a new DataContext, or the bound brush can be missed.
- **Five row-production paths**: `PopulateLoadedAsync`, `FlushPendingFrames`, `RebuildDisplay`,
  `BuildOverwriteRows`, `UpdateOverwriteRow`. Per-row state must be applied in all of them.
- **Overwrite mode mutates rows in place** through `FrameRowViewModel.SetFrame`, which raises
  `OnPropertyChanged(string.Empty)` — state computed once at construction drifts.
- **Never call `RebuildDisplay()` for a visual-only change**; it recreates up to 50k row view models.
- The grid is virtualised: keep per-row work out of property getters on the hot path.
- **Bindings fail silently.** `MainWindow.axaml` sets `x:CompileBindings="False"`: a wrong binding
  path raises no build error and no exception, the value just never appears.
- **`CanIdParser` traps.** `Parse` returns 0 for invalid text and 0 is a legal CAN ID; `ParseId`
  reads bare digits as hex while `Parse` reads them as decimal — pick one deliberately.
- **`Frame.FrameId` does not identify a frame alone** — an 11-bit `0x123` and a 29-bit `0x00000123`
  collide. Key on `(FrameId, IsExtended)` for anything user-visible.
- Expanded rows (`RowsExpanded`) put Data + interpretation in one tall cell — row-level background
  survives that, cell-level foreground does not.
- `ClearFrames` and a new log load both clear filter state; decide explicitly whether new UI state
  should survive them.
- PCAN-Basic / Vector XL are Windows runtime-only DLLs; keep them behind interfaces so
  `dotnet build` and `dotnet test` work without hardware.

## 5. Where new code goes

- New analysis: logic in `src/CanYeAye.Core/Analysis/` (unit-testable), rendering in
  `src/CanYeAye.App/Controls/`, window in `src/CanYeAye.App/Views/`.
- New persisted value: `AppSettings` property (init-only) + constructor apply + `SaveSettings`.
- New tests: mirror the folder, `tests/CanYeAye.Tests/<Area>/`.

## 6. Deep-dive docs

| Topic | File |
|---|---|
| Log formats and deviations from SavvyCAN | `docs/research/log-formats.md` |
| Connection protocols (GVRET, Lawicel, socketcand, MQTT, CANserver) | `docs/research/connection-protocols.md` |
| DBC model, editors, scripting surface | `docs/research/dbc-and-scripting.md` |
| Flow View | `docs/research/flow-view.md` |
| Detailed Frame Information | `docs/research/frame-info.md` |
| J1939 node analysis and Node Graph | `docs/research/j1939-node-analysis.md` |
