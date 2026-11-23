# J1939 node names and the node graph (CanYeAye original)

SavvyCAN has no equivalent of this feature; the design is ours. It builds on the J1939
decoder in `CanYeAye.Core.Protocols.J1939Id` (source address in the low byte, destination in
PS for PDU1, `IsBroadcast` for PDU2).

## Node names

- Names are keyed by **J1939 source address 0–255**, e.g. `0x01 = Steering module`.
- Unnamed nodes display their default address label `0xNN`.
- The **Frame Filtering** window has two tabs:
  - `Frame IDs` — the per-ID include/exclude list (unchanged; the single source of truth for
    which frames are displayed).
  - `J1939 Nodes` — one row per source address seen in the capture (plus any named source
    restored from settings) and a leading **Broadcast** pseudo-row:
    - an editable name (`0x02` → `ABS`) for real nodes; the Broadcast row is not nameable,
    - a check box that **only selects/clears all IDs of that node on the `Frame IDs` tab**
      (it is a bulk shortcut, not a second filter layer). Mixed ID selections show a dash
      (`IsThreeState`); the Broadcast row covers every broadcast destination (PDU2 or PDU1 to
      `0xFF`) so all broadcast traffic can be hidden in one click,
    - `All` / `None` for the whole node list,
    - `Load...` / `Save...` for a standalone node-names file, and `Reset` to remove every
      label.
- Names are also stored in CanYeAye settings (`AppSettings.NodeNames`) so they survive restarts
  without an explicit file.
- J1939 frame-filter labels append the source name: `0x18FEF101 [Steering module]`.
- The main frame grid has a **`Nodes`** column showing `source → destination` with names when
  available (`Steering module → ABS`, `Engine → Broadcast`; unnamed nodes use `0xNN`). It is
  blank for standard 11-bit frames and can be hidden/reordered like any other column.

### Node-names file format

Plain text, one node per line, `#` comments, decimal or `0x` addresses:

```
# CanYeAye J1939 node names
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
  the footer lists the top talkers as `Name (0xNN): frames` (address only when unnamed).
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

- `CanYeAye.Core.Analysis.J1939NodeNames` — name store, load/save file, `Changed` event.
- `CanYeAye.Core.Analysis.NodeTraffic` — totals per source, per ordered conversation and per
  source broadcast (29-bit frames only; standard frames counted as ignored).
- `CanYeAye.App.Controls.NodeGraphControl` — custom-drawn graph with wheel/pinch zoom, pan,
  node drag, long-press naming, hover card and layout capture/apply.
- `CanYeAye.App.ViewModels.NodeGraphViewModel` — live/capture source selection, rebuild timer,
  summary/top-talkers text.
- `CanYeAye.App.Views.NodeGraphWindow` / `NodeNameDialog`.

## Tests

- `tests/CanYeAye.Tests/Analysis/NodeNamesAndTrafficTests.cs` — names round-trip/parsing,
  default labels, PDU1/PDU2 broadcast classification, bidirectional aggregation.

## Open ideas / verification notes (2026-11-16)

- Verify pinch-zoom and long-press on a real touchscreen; the control handles touch pointers
  manually (two-pointer centre/distance), so check pointer capture and the 600 ms timer on device.
- Worth adding: time-window filtering for live traffic, graph export (PNG), per-conversation
  hover readout, and a legend for the node size scale.
- Node positions are saved by address in world coordinates, so a node that disappears/returns
  keeps its spot; `Reset view` clears them.

## DM1/DM2/DM12 decoding in the frame grid (2026-11-23)

`Interpret Frames` also decodes the J1939-73 diagnostic messages that carry trouble codes, with
no DBC loaded. DM1 (`PGN 65226`), DM2 (`65227`) and DM12 (`65236`) are fixed broadcast PGNs, so
they are recognised from the 29-bit identifier alone.

What is decoded from the SAE-defined layout:

- The two lamp header bytes: MIL, red stop, amber warning and protect, each 2 bits
  (`00` off, `01` on, `10` reserved, `11` not available) plus the matching flash bits
  (`00` slow 1 Hz, `01` fast 2 Hz, `10`/`11` reserved).
- Four-byte DTC records: SPN 19 bits, FMI 5 bits, CM 1 bit (`CM 1` = pre-1996 SPN encoding),
  OC 7 bits (126 max, 127 = not available).
- SPN 516096-524287 is flagged as manufacturer-defined.
- FMI 0-31 is translated to the J1939-73 wording (full text in the tooltip/clipboard, a short
  form in the one-line grid summary).

The SPN *name* (e.g. "Engine Oil Pressure") is not decoded: that list is only available in the
paid SAE J1939 Digital Annex / J1939-71. Instead the grid shows the exact SPN and FMI so the code
can be looked up, and the frame context menu offers **Look up SPN/FMI...** (opens a web search)
and **Copy DTC** (full multi-line decode to the clipboard).

Limits:

- The decoder handles a **single frame**. Two or more DTCs make DM1 longer than 8 bytes, which
  J1939-73 transmits with the transport protocol (BAM or CMDT, `PGN 60416`/`60160`). CanYeAye does
  not reassemble J1939 TP yet, so multi-DTC messages show only their raw TP frames.
- DM2 is request-only, so it appears in a capture only when something requested it. Decoding it
  when present is still useful for historic faults.
- Lamp encodings differ between ECUs for the "off" case (0 vs 3 seen in the same vehicle); the
  decoder reports what the bits say rather than guessing.

Core: `src/CanYeAye.Core/Protocols/J1939Diagnostics.cs` (`TryDecode`, `DecodeDtcs`,
`DescribeFmi`, `DescribeCompact`, `Describe`). Tests:
`tests/CanYeAye.Tests/Protocols/J1939DiagnosticsTests.cs`.
