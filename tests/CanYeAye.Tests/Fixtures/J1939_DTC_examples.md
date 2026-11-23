# J1939 DTC example logs

Two candump logs for checking the J1939-73 decoding in the frame grid. Load either one with
`dotnet run --project src/CanYeAye.App -- tests\CanYeAye.Tests\Fixtures\J1939_DM1_examples.log`,
then enable **Interpret Frames** and **Expand All Rows**.

## `J1939_DM1_examples.log` (single frame, decodes today)

| # | CAN ID | Payload | Interpretation |
|---|---|---|---|
| 1 | `18FECA00` | `C3 FF 00 00 00 00 FF FF` | `DM1: no active DTCs` |
| 2 | `18FECA00` | `04 00 64 00 01 05 FF FF` | `DM1: Amber warning · SPN 100 / FMI 1 (below normal, most severe) · OC 5` |
| 3 | `18FECA03` | `00 00 9A 0C 02 8C FF FF` | `DM1: SPN 3226 / FMI 2 (erratic or intermittent) · OC 12 · CM 1` |
| 4 | `18FECA0B` | `10 00 BE 00 05 03 FF FF` | `DM1: Red stop · SPN 190 / FMI 5 (open circuit) · OC 3` |
| 5 | `18FECA21` | `40 00 6E 00 00 01 FF FF` | `DM1: Malfunction · SPN 110 / FMI 0 (above normal, most severe) · OC 1` |
| 6 | `18FECA12` | `00 00 00 E0 FF FF` | `DM1: SPN 516096 / FMI 31 (condition exists) · OC n/a · CM 1 · proprietary` |
| 7 | `18FECB00` | `00 00 9D 00 12 07 FF FF` | `DM2: SPN 157 / FMI 18 (below normal, moderately severe) · OC 7` |
| 8 | `18FECB21` | `00 00 00 00 00 00 00 00` | `DM2: no previously active DTCs` |
| 9 | `18FED400` | `00 00 9D 00 12 02 FF FF` | `DM12: SPN 157 / FMI 18 (below normal, moderately severe) · OC 2` |
| 10 | `18F00400` | `F0 FF FF FF 00 FF FF 00` | EEC1, not a diagnostic message (no DTC text) |
| 11 | `18FEF100` | `00 00 00 00 00 00 00 00` | CCVS, not a diagnostic message (no DTC text) |

Source addresses: `0x00` engine, `0x03` transmission, `0x0B` brake, `0x12` manufacturer-specific,
`0x21` instrument cluster. Name them in **Frame Filtering → J1939 Nodes** to see the names in the
grid; until then the Node column shows `0xNN`.

Select a row and use the frame grid context menu:

- **Look up SPN/FMI…** opens `SPN 100 FMI 1` in the browser.
- **Copy DTC** puts the full multi-line decode (lamps + full FMI wording + OC/CM notes) on the clipboard.

## `J1939_DM1_multiframe_bam.log` (needs transport-protocol reassembly)

This is a three-DTC DM1 from source `0x00` sent with the J1939 Broadcast Announce Message:

```
TP.CM  #20 0E 00 02 FF CA FE 00   (BAM, 14 bytes, 2 packets, PGN 0xFECA)
TP.DT  #01 00 00 64 00 01 03 6E
TP.DT  #02 00 00 01 BE 00 02 07
```

Expected codes once J1939 TP reassembly exists: SPN 100/FMI 1/OC 3, SPN 110/FMI 0/OC 1 and
SPN 190/FMI 2/OC 7. Today the TP frames are shown raw and do not produce an interpretation;
the fourth frame is a normal single-frame DM1 that still decodes.
