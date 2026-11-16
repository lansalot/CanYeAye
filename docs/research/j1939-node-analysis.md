# J1939 node names and the node graph (WinCAN original)

SavvyCAN has no equivalent of this feature; the design is ours. It builds on the J1939
decoder in `WinCAN.Core.Protocols.J1939Id` (source address in the low byte, destination in
PS for PDU1, `IsBroadcast` for PDU2).

## Node names

- Names are keyed by **J1939 source address 0–255**, e.g. `0x01 = Steering module`.
- Unnamed nodes display their default address label `0xNN`.
- The **Frame Filtering** window has a `J1939 Nodes` tab:
  - one row per source seen in the capture (plus any named source restored from settings),
  - editable name per row,
  - a checkbox to include/exclude **all frames from that node** (bulk source filter) with
    `All` / `None` buttons,
  - `Load...` / `Save...` for a standalone node-names file.
- Names are also stored in WinCAN settings (`AppSettings.NodeNames`) so they survive restarts
  without an explicit file.
- J1939 frame-filter labels append the source name: `0x18FEF101 [Steering module]`.

### Node-names file format

Plain text, one node per line, `#` comments, decimal or `0x` addresses:

```
# WinCAN J1939 node names
# <address>=<name>
0x01=Steering module
0x02=ABS
0x03=Engine
```

Parsing skips blank lines, comments, malformed addresses and empty names. Saving writes a
header and entries sorted by address (`J1939NodeNames.Save`).

## Node graph window (Tools → Node Graph…)

Shows J1939 **sources as nodes** and **conversations as edges**.

- **Live vs capture**: `Live` accumulates frames arriving from the connected bus while the
  window is open (starts empty when enabled, shows fps). With `Live` off the graph is a
  one-off snapshot of the main window's current capture/log (`Refresh capture` re-reads it).
- **Edges** combine both directions of a conversation into one line; thickness is
  `1 + 9*sqrt(frames/maxFrames)`. Arrowheads mark the directions that actually carried traffic.
- **Broadcast**: a long bar across the bottom; each source that broadcasts gets a link whose
  thickness is proportional to its broadcast volume (PDU2 or PDU1 to destination `0xFF`).
- **Nodes**: circle radius grows with sent+received involvement; label is the node name or
  `0xNN` (address is always drawn inside the circle).
- **Hover** a node for a card with sent/received counts and its three heaviest partners;
  the footer lists the top talkers.
- **Interaction** (touch/tablet friendly):
  - drag a node to move it,
  - drag empty space to pan (grab),
  - mouse wheel or two-finger pinch to zoom (0.3×–4×, around the pointer/pinch centre),
  - zoom −/+ and `Reset view` buttons,
  - **long-press (600 ms) or double-click a node to name it** (opens a large-button dialog).
- **Persistence**: node positions, zoom/pan and the graph window's size/position are saved in
  `AppSettings` (`NodeGraphLayout`, `NodeGraphZoom/PanX/PanY`, `NodeGraphWindow*`) when the
  window closes and restored next time.

## Core pieces

- `WinCAN.Core.Analysis.J1939NodeNames` — name store, load/save file, `Changed` event.
- `WinCAN.Core.Analysis.NodeTraffic` — totals per source, per ordered conversation and per
  source broadcast (29-bit frames only; standard frames counted as ignored).
- `WinCAN.App.Controls.NodeGraphControl` — custom-drawn graph with wheel/pinch zoom, pan,
  node drag, long-press naming, hover card and layout capture/apply.
- `WinCAN.App.ViewModels.NodeGraphViewModel` — live/capture source selection, rebuild timer,
  summary/top-talkers text.
- `WinCAN.App.Views.NodeGraphWindow` / `NodeNameDialog`.

## Tests

- `tests/WinCAN.Tests/Analysis/NodeNamesAndTrafficTests.cs` — names round-trip/parsing,
  default labels, PDU1/PDU2 broadcast classification, bidirectional aggregation.

## Open ideas / verification notes (2026-11-16)

- Verify pinch-zoom and long-press on a real touchscreen; the control handles touch pointers
  manually (two-pointer centre/distance), so check pointer capture and the 600 ms timer on device.
- Worth adding: time-window filtering for live traffic, graph export (PNG), per-conversation
  hover readout, and a legend for the node size scale.
- Node positions are saved by address in world coordinates, so a node that disappears/returns
  keeps its spot; `Reset view` clears them.
