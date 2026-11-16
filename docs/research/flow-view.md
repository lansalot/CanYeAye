# Flow View

Flow View is the port of SavvyCAN's `re/flowviewwindow` (`help/flowview.md`): pick one
frame ID and watch its payload change as you step or play through that ID's frames. Three
views share one position: the payload bit grid, a graph of every data byte around the
current frame, and a per-byte table holding the reference, seek and graph settings.

WinCAN keeps the semantics but splits the logic from the drawing:

| Piece | Location |
|---|---|
| Reference / seek / trigger state machine | `src/WinCAN.Core/Analysis/FlowViewSession.cs` |
| Window view model, playback timers, live queue, graph series | `src/WinCAN.App/ViewModels/FlowViewWindowViewModel.cs` |
| Payload bit grid (custom drawn) | `src/WinCAN.App/Controls/FlowBitGrid.cs` |
| Byte graph (custom drawn) | `src/WinCAN.App/Controls/FlowGraphControl.cs` |
| Window layout and keyboard shortcuts | `src/WinCAN.App/Views/FlowViewWindow.axaml(.cs)` |

## SavvyCAN semantics preserved

- **ID list.** The left list holds every ID in the capture, ascending, regardless of the
  main window's frame filters. Selecting an ID shows its frame count and resets the position
  to the first frame.
- **Bit grid.** Bytes run top to bottom, the eight bits of each byte run left to right with
  bit 7 first. Black = 1, white = 0. A bit that just turned on flashes green, a bit that just
  turned off flashes red, then both settle to black/white. Comparison is against the
  reference bytes.
- **Auto reference.** On by default: the previously displayed frame becomes the reference on
  every move, so the grid shows what changed between consecutive frames. Off: the grid
  compares against the editable `Ref` values, which start as the first frame of the ID.
- **Playback.** Back one frame / stop / forward one frame, play backward / pause / play
  forward, loop playback, and a frame interval in milliseconds (25 ms by default, `0` runs
  as fast as the timer allows). Playback stops at either end unless looping.
- **Seek values.** Per-byte seek values default to `-1` (disabled). While playing, a frame
  whose byte matches a non-negative seek value stops playback. Values are edited as hex in
  the byte table.
- **Trigger bits.** Clicking a bit in the grid marks it as a trigger (blue, bold bit
  number). Playback stops when a trigger bit changes state. Clicking it again clears it.
- **Graph.** Every data byte is plotted around the current frame with a marker at the
  current position; the visible range is configurable and scrolls as playback advances.
  Bytes can be excluded per row (`Graph?`), and each plotted byte has a fixed colour swatch.
- **Live mode.** Follows the newest frame of the selected ID as traffic arrives instead of
  replaying the capture.

## WinCAN deviations

- **Full-width bit grid.** SavvyCAN draws an 8×8 square grid of pixel cells. WinCAN fills
  the panel: the bits of each byte are wide bars across the whole width (one byte per row up
  to 8 bytes, two up to 32, four otherwise), with no per-cell border and no byte-group
  separators. Repeats of the `7..0` header and the byte number down the left keep the
  orientation readable.
- **Reference visibility without auto reference.** SavvyCAN only flashes changes; WinCAN
  keeps differences against the static reference visible (green/red) until the reference is
  edited or a new ID is selected, so a static reference stays useful while paused.
- **Custom graph instead of QCustomPlot.** `FlowGraphControl` draws the axes, series, frame
  marker and byte colours directly; the range slider scrolls the window around the current
  frame rather than relying on plot zoom/pan.
- **Byte table.** `Byte`, `Val`, `Ref`, `Seek`, `Graph?`, `Colour` columns are a real
  `DataGrid`, so hex values can be typed in place. `Val` is read-only.
- **Extra controls.** A timeline slider scrubs all frames of the selected ID; `Select all
  graphs` / `Deselect all graphs` toggle every `Graph?` row at once.
- **Keyboard.** `R` toggles play/pause, `T`/`Y` step backward/forward, matching the
  shortcut mnemonics in the window. Text boxes swallow the keys while they have focus.
- **Window behaviour.** One Flow View window at a time (Tools → Flow View… re-activates it);
  geometry is stored in WinCAN's own settings (`FlowWindowWidth/Height/X/Y`), never in
  SavvyCAN's QSettings keys.
- **Limits.** The grid and graph cover up to 64 payload bytes (`FlowViewSession.MaxBytes`);
  FlexRay frames can be longer, but only the first 64 bytes are shown.

## LED / flash timing

`FlowViewWindowViewModel` bumps a sequence number per displayed frame and hands the grid a
snapshot of the current and reference bytes. `FlowBitGrid` records which bits changed and
keeps the flash colour for `FlashDurationMs`, repainting when the flash expires. Trigger
changes are a separate styled property (`TriggerVersion`), so clicking a bit repaints the
grid without restarting the change flash.

## Tests

`tests/WinCAN.Tests/Analysis/FlowViewSessionTests.cs` covers the UI-independent rules:
reference defaults and auto-reference stepping, seek matches (including `-1` being
disabled and values above `0xFF` clamping), trigger toggling and change detection, clamping
at both ends and looping, byte-count tracking, live append behaviour, and capture reset
keeping user seek/trigger state.
