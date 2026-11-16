# Detailed Frame Information

Detailed Frame Information is the port of SavvyCAN's `re/frameinfowindow` (reference help:
`help/framedetails.md`). It answers "what do the bytes of this one ID actually do?" with
per-byte value histograms, a bit-change heatmap, a bit histogram, an interval histogram and
eight per-byte value graphs.

CanYeAye keeps the reference analysis and splits it from the drawing:

| Piece | Location |
|---|---|
| Snapshot statistics, detail tree, text export | `src/CanYeAye.Core/Analysis/FrameInfoAnalyzer.cs` |
| Window view model, ID list, chart/graph models, save command | `src/CanYeAye.App/ViewModels/FrameInfoWindowViewModel.cs` |
| Bit-change heatmap (custom drawn) | `src/CanYeAye.App/Controls/FrameInfoHeatmap.cs` |
| Bit / interval histogram (custom drawn) | `src/CanYeAye.App/Controls/FrameInfoBarChart.cs` |
| Eight per-byte value graphs (custom drawn) | `src/CanYeAye.App/Controls/FrameInfoByteGraphs.cs` |
| Window layout | `src/CanYeAye.App/Views/FrameInfoWindow.axaml(.cs)` |
| Tests | `tests/CanYeAye.Tests/Analysis/FrameInfoAnalyzerTests.cs` |

## Snapshot, not live

The reference window re-reads the capture on every `framesUpdated` signal and even rebuilds
the details for the selected ID (the call is commented out because it "blasts us out of the
details"). CanYeAye takes a single snapshot when the window opens and never subscribes to live
frames: opening the window again (or closing and reopening it) is how you refresh it. The
header line states the snapshot size, so it is obvious that the numbers are frozen.

Consequences:

- Selecting another ID is a pure analysis of the in-memory snapshot.
- Saving details re-analyses every ID from the same snapshot, so the file always matches what
  was on screen.
- New live traffic does not change the window, by design.

## Analysis (parity with `re/frameinfowindow.cpp`)

Per selected ID:

- **Frames and length.** Frame count plus the shortest and longest payload length. Per-byte
  statistics cover the first eight bytes (`FrameInfoAnalyzer.StatBytes`); CAN-FD, FlexRay and
  LIN frames can be longer and still report their true length.
- **Per byte.** Range (min/max), a `ChangedBits` mask (bits that differ from the ID's first
  frame at least once, shown as hex plus binary), a 256-entry value histogram and the run of
  values across the ID's frames (the byte graph series).
- **Bit histogram.** For every bit 0..63 (`byte * 8 + bit`), how many frames had the bit set.
- **Bit-change heatmap.** Bit transitions between consecutive frames divided by the frame
  count, so a bit that flips on every frame reads 100 %. The rate is the same number the
  reference stores per bit.
- **Intervals.** Average, minimum, maximum, variation (max - min), standard deviation and
  the width of the range that holds 90 % of the intervals (P95 - P5), plus a 20-bin
  histogram. The histogram keeps 21 edge counts like the reference (the first edge is the
  minimum interval, the last bin holds the longest intervals).
- **DBC signals.** When a DBC message matches the ID, each active signal's formatted value is
  counted (`ProcessAsText`), giving a value histogram per signal and one tree section per
  signal.
- **J1939 / GMLAN.** Extended IDs get a J1939 decoding section (broadcast or destination,
  priority, SRC, EDP/DP, PGN, PF, PS) and a GMLAN 29-bit section (priority bits, arbitration
  ID, sender ID), matching the reference's extended-ID branches.

## Layout

Left: the ID list with the unique-ID count, the detail tree (real nested nodes this time),
`Save details to file`, and the interval histogram. Right: the heatmap and bit histogram side
by side, then the `Bytes Graph` block: a 2×4 grid of per-byte value graphs (value 0..255 on
the y axis, frame index of the selected ID on the x axis) with one fixed colour per byte
taken from the Flow View series palette.

The heatmap is always 8 rows × 8 columns (one row per byte, bit 7 on the left). Rows past the
ID's payload length are dimmed, and a footer says which rows have no data. Each cell shows the
absolute bit index in its top-left corner and the change rate as a percentage below it, on a
dark-blue → purple → orange → yellow gradient with black/white text picked by luminance.

Both bar charts use a logarithmic value axis like the reference tickers, labelled x-axis
ticks (bit index; interval in ms) and value labels on the gridlines.

## Save details

`Save details to file` writes every ID in the snapshot (not just the selected one) as
tab-indented text with one blank line between IDs, exactly like the reference's
`dumpNode` loop over `listFrameID`. The tree shown on screen and the text export share the
same `FrameInfoDetailNode` structure, so they cannot drift apart.

## CanYeAye deviations

- **Snapshot semantics** instead of the reference refresh signal (above).
- **Seconds-aware intervals.** SavvyCAN subtracts only the microsecond field of its
  timestamps, which produces nonsense when a capture crosses a second boundary. CanYeAye
  subtracts `Seconds * 1_000_000 + Microseconds` and takes the absolute value.
- **Single-frame IDs.** The reference reports `INT_MAX / 1000` as the minimum interval and a
  bogus average; CanYeAye reports zeroed interval statistics, no histogram and an empty-state
  message ("No inter-frame intervals (only one frame for this ID).").
- **Real nested tree nodes.** J1939/GMLAN blocks, the value histograms and the signal value
  lists are children of their section instead of space-indented siblings, so the tree can be
  expanded and the text export uses one tab per level.
- **Compact tree.** Avalonia's default `TreeViewItem` indents 40 px per level; the window
  overrides `TreeViewItemIndent` and the chevron margin/size and drops `MinHeight` to 19 so
  the detail rows stay near the reference's density.
- **Heatmap.** The reference paints whole cells with the change percentage; CanYeAye adds the
  absolute bit number, a modern gradient, dimmed unused rows and the "no data" footer.
- **Charts.** Custom drawn instead of QCustomPlot, with log value axes and explicit axis
  captions (`Bits`, `Interval (ms)`). Scroll-wheel zoom/pan is not implemented; the charts
  re-fit when the ID changes.
- **Byte graphs.** Only the analysed bytes get a graph (at most eight) instead of the
  reference's 64 widgets, with a fixed 0..255 value axis rather than auto-scaling so graphs
  are comparable at a glance.
- **Window behaviour.** One window at a time (Tools → Detailed Frame Information…
  re-activates it); geometry is stored as `FrameInfoWindowWidth/Height/X/Y` in CanYeAye's own
  settings store.

## Tests

`FrameInfoAnalyzerTests` covers the UI-independent parts: unique-ID collection, per-byte
range/`ChangedBits`/value counts/bit counts/change rates/series, aggregate 64-bit counts,
interval statistics and the 20-bin histogram, intervals across second boundaries, the
single-frame empty case, data-length tracking with mixed lengths, the eight-byte statistics
cap, J1939 and GMLAN detail nodes (PDU1 destination + broadcast PDU2), the reference section
lines (`Changed bits`, `Range`, `Histogram`, `Bitfield Histogram`, `Bit Change Heatmap`), the
tab-indented text export and DBC signal value histograms.
