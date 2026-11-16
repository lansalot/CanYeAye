# SavvyCAN text log format spec (for the C# port)

Source of truth: `C:\Github\SavvyCAN\framefileio.cpp`. Fixtures: `C:\Github\SavvyCAN\examples`
(read-only). Timestamps in every text loader are stored as `FrameTimestamp.FromMicroseconds`;
the C++ always uses `TimeStamp(0, usec)`.

## Shared helpers (must match C++)

- `simplified()`: trim, collapse every internal whitespace run to one space.
- `split(' ')`: keeps empty parts; guard indexing.
- `ParseStringToNum`: `0x`/`X` hex, `0b`/`B` binary, otherwise decimal; garbage -> 0.
- `unQuote`: split on `"`; if >=3 parts return part[1].
- `GetTimeMS`: milliseconds since local midnight (used as a synthetic µs base).
- Text files are line-oriented; CARBUS is read raw because it uses bare `\r`.
- Loaders append and return `false` on malformed input; the caller must drop partial results.

## Autodetect order

Binary formats first (BLF, pcap, Tesla, CANServer) - not implemented yet. Then text:
NativeCSV, ASC, CRTD, Trace(TAB), VehicleSpy, Candump, CARBUS, CANHacker, Cabana, CANOpen,
BusMaster, PCAN, IXXAT MiniLog, Microchip, CAN-DO, Kvaser(hex then dec), CLX000, Lawicel,
CANWise, IXXAT CSV, GenericCSV.

## GVRET native CSV

Header V2: `Time Stamp,ID,Extended,Dir,Bus,LEN,D1,...,D8`; V1 lacks `Dir`. Detection: line 1
contains `TIME STAMP`,`EXTENDED`,`D1`; char 23 == 'D' means V2; line 2 has >=5 tokens and
V1 LEN (token 4) <= 8 / V2 LEN (token 5) <= 255.

Load: `ts = tokens[0]` if length > 3 (ulong, 0 -> signed retry), else synthetic `+5 µs` from
`GetTimeMS`. ID hex; extended = token 2 contains TRUE, forced when id > 0x7FF.
V1: received, bus token 3, LEN token 4 clamp 0..8 and to available tokens, data from token 5,
Always CANDataFrame (clears FD). V2: Dir token 3 (`R` = received), bus token 4, LEN token 5
clamp to 255 then 64 for non-FlexRay, data from token 6, flags token at `6+max(8,LEN)`:
`FR`/`FR+...` -> FlexRay (+NULL, +CRCERR sets (true,false)), else `FD...` or LEN>8 -> FD,
BRS/ESI from flag text.

Save: V2 header. Row: `µs,ID 8 hex upper,true|false,Rx|Tx,bus,len,` + data (classic padded to 8,
no flags; FD padded to max(8,len) then `FD`, `+BRS`, `+ESI`; FlexRay then `FR`, `+NULL`,
`+CRCERR`), every cell followed by a comma.

## Generic CSV

Detection: discard line 1; line 2 split `,`; ID token hex in 1..0x1FFFFFFF; >=2 tokens; data token
split on `' '` has 2..8 parts. Load: discard line 1, synthetic `+5000 µs` per row from
`GetTimeMS`; ID hex, extended if >0x7FF; bus 0; bytes from simplified data token, up to 8.
Save: `ID,Data Bytes` then `<ID 8 hex>,<BB >...` (trailing space after every byte).

## CRTD / OVMS

Detection: discard line 1 then up to 11 lines; token 1 may have a leading bus digit 1..9; it must
be `R29`,`T29`,`R11` or `T11`.

Load: discard line 1. `multiplier = token0 contains '.' ? 1e6 : 1`; ts = double * multiplier
(no epoch conversion). Leading bus digit `n` -> bus `n-1`. `R`/`T` direction; ID hex token 2;
extended for `29`; payload = all remaining tokens (no length column).
Save: header `<µs/1e6:f6> CXX GVRET-PC Reverse Engineering Tool Output V<version>`, rows
`<µs/1e6:f6> <bus+1><R|T><29|11> <ID 8 hex> <BB >...`.

## BusMaster log

Detection: line 1 contains BUSMASTER; skip `***` lines; next line >=6 space tokens;
token0 has 4 colon parts; token3 minus first 2 chars hex in 1..0x1FFFFFFF; token4 in
{S,X,SR,XR}; token5 <= 8.

Load: discard line 1; upper-case; skip `***`; >=6 tokens. Timestamp
`h*3600e6+m*60e6+s*1e6+ms*100` (**the reference's ×100 bug; reproduced for parity**).
Direction token1 starts `R`. ID = token3[2..] hex (assumes 0x prefix). Type: S/X data,
SR/XR remote; remote payload zero-filled to DLC. Bus = int(token2). Data from token6.

## Microchip CAN BUS Analyzer

Detection: first 100 lines; `//` toggles comment mode; data line split `;` >=4 tokens;
ID (token2) 1..0x1FFFFFFF; LEN (token3) 0..8; not `(len+4) < tokens.Length`.
(Fix: tolerate trailing `;`, otherwise SavvyCAN's own output is rejected.)

Load: same comment toggle; drop one trailing empty token. Timestamp token0 int * 1000 (ms -> µs).
Received = token1 starts `R`; ID via ParseStringToNum; bus 0; payload tokens 3+ clamped to 8.
Save: Microchip header, `<µs/1000>;<RX|TX>;0x<ID8>;<LEN>;` + `0x<BB>;` per byte + newline.

## Vector trace (TAB, `*.trace`)

Detection: lines starting `;` are comments; split `\t`; >3 tokens; token1 has 4 colon parts;
token2 hex 1..0x1FFFFFFF; token3 0..8.

Load: timestamp `h*3600e6+m*60e6+s*1e6+field4*100` (100 µs ticks). ID hex, extended >0x7FF,
bus 0, len token3 clamp 8, data token4 split spaces.
Save: comment preamble then `<msgNo,10>\t<HH:MM:SS:pppp>\t<ID8>\t<LEN>\t<BB >...`.

## IXXAT MiniLog

Detection: line 1 contains IXXAT; line 7 contains FORMAT.
Load: discard 7 lines; upper; token0 unquoted `h:m:s.frac` -> `h*3600e6+m*60e6+s*1e6`;
ID token1 hex; format token2 `Std` -> 11-bit, else extended; data token4 split spaces, max 8.
Save: 7-line header, rows `"<h:m:s.zzz>","<ID8>","<Ext|Std>","","<BB >..."`.

## CAN-DO (binary)

12-byte records. `ts = 1e6*(b0>>2) + (((b0&3)<<8)+b1)*1000`; `id = ((b3&0x0F)<<8)|b2`;
len = b3>>4; data bytes 4..11, `0xFF` padding. `b2==0xFF && b3==0xFF` is a global-time header.
Minute wrap adds 60e6. 11-bit only. Save skips extended/len>8 and pads with 0xFF.

## Vehicle Spy CSV (load only)

Detection: first 10 lines contains LINE/TIME/B1, then one more line with >20 comma tokens and
token9 hex > 0. Load: skip until the second LINE header; ts = DateTime.Now + token1 seconds;
direction token5 starts T; ID token9 hex; extended token11 starts T; payload tokens 12..19
until empty (max 8). Save is a stub returning false.

## candump / Kayak

Regexes (line upper, split spaces): time `^\((\S+)\)$`; id `^([0-9A-F]+)(##?)(\S*)$`.
Bus = first digit run inside token1 (`vcan0` -> 0). Timestamp = double seconds * 1e6.
Expanded: `(<t>) iface ID [len] bytes...`; concise: `ID#data`, `ID##Fdata`, `ID#Rlen`.
`##` FD with flag hex digit (BRS bit0, ESI bit1, max 64 bytes); `#R` remote.
Save: `(<seconds:17.6 zero-padded>) vcan0 <ID>#...` (FD: `##<flags><data>`, remote `#R<len>`).

## Lawicel trace

Detection: first 100 lines upper, skip `S...` and len<=4; first 3 chars hex in 1..0x7FF;
remaining length/2 in 0..8. Load: simplified; skip len<=4 and lines starting uppercase `S`;
synthetic +100 µs/row; ID = first 3 hex; 11-bit, received, bus 0; bytes = pairs.
No save.

## PCAN Viewer TRC (text)

Detection: within 25 lines a `;$FILEVERSION=` line and a line containing PCAN.
Load: `;` comments also set version (2.1/2.0/1.3/1.1). v1.1: time token1 *1000 (ms->µs), ID
token3 hex, extended if >0x10000000 or token length>=8, len token4, `R` at token5 = remote.
v1.3: bus token2, ID token4, len token6, data token7. v2.0: type token2, ID token3, FD types
FD/FB/FE/BI, len token5 via DLC table, data token6, remote for RR or `R` token6.
v2.1: bus token3, ID token4, len token7, data token8.
DLC table `[0,1,2,3,4,5,6,7,8,12,16,20,24,32,48,64]`.

## CARBUS Analyzer / CANHacker TRC

Detection: read raw (bare `\r`), line 1 starts `@ TEXT @`.
Load: header version digit after `@ TEXT @ `; split lines on `\r`; rows have
`sec,(ms|µs)  bus  ?  ID  LEN  data...`; ts = sec*1e6 + (v2 ? ms*1000 : µs); minute wrap when
ts+10000 < previous; bus used as-is; ID hex; extended >0x7FF; LEN parsed hex; `#` lines skipped.
Save: header `@ TEXT @ 3 @ 64 @ 0 @ <count> @ <ms> @ <hh:mm:ss.zzz> @\r`, rows
`SS,UUUUUU\t<bus>\t0004\t<ID>\t<len>\t<data padded 23>\t00000000\t<ascii 8>\t\r`.

## CANHacker trace

Detection: line 1 contains CANHACKER, else up to 10 lines with token1 hex and token2 decimal 0..8.
Load: discard line 1; `HH[.|,]MMM ID LEN data...`; multiplier 1e6 when a decimal separator is
present else 1; minute wrap adds 60; LEN parsed as hex (quirk); bus 0, received.

## Cabana

Detection: line 1 contains TIME and ADDR; then non-empty lines with 3-4 comma tokens and decimal
token1 in 1..0x1FFFFFFF. Load: time seconds *1e6 relative to first frame; ID token1 decimal; bus
token2; data token3 as one big-endian hex word, length = chars/2, take last N bytes.
Save: `time,addr,bus,data` rows `<seconds f>.0,<id>,<bus>,<BB>...` (reference writes a malformed
double `.0`; do not reproduce).

## IXXAT CSV export

Detection: scan 100 lines with splitQuotedString; find header
`Bus,No,Time,State,ID (hex),DLC,Data (hex)`; rows have hex/Error ID and DLC matching data count.
Load: optional `Start Time  dddd MMMM dd yyyy HH:mm:ss` sets epoch base; bus from channel name
suffix 1/2; time token2 `HH:mm:ss.zzz`; `Error` rows skipped; ID hex; DLC; data split spaces must
match DLC. Extended flag is never set (reference quirk; fix).

## CANOpen Magic (load only)

Detection: line 1 contains CANOPEN MAGIC; five header lines; rows with >11 comma tokens after
quote replacement. Load: time token1 *1000 (ms -> µs, relative); ID ParseStringToNum(token5);
bus 0; payload token11 split spaces (no 8-byte clamp - guard it).

## CANWise (load only)

Detection: line 2 matches
`^(RX|TX) (\d+) (EFF|SFF) ([\dA-F]+) (\d) HEX +((?:[\dA-F]{2} +)+)(\d+)$` (case-sensitive).
Load: bus 0, direction, extended by EFF, ID hex, LEN must equal data count, timestamp raw
microseconds.

## Kvaser log (load only)

Detection: line 1 contains CHN/FLG/D0; next up to 10 lines >70 chars with DLC at [21..24] 0..8.
Load: fixed columns: bus [0..3), ID [4..14) in hex (autodetect tries hex then decimal),
LEN [21..24) decimal, byte i at 25+i*4, time [57..71) seconds *1e6, direction at 72.
Only lines with length > 70 are parsed.

## Known reference bugs

- BusMaster's ms field is multiplied by 100 in the reference loader (see deviations below).
- Microchip and Generic CSV detectors reject SavvyCAN's own writer output due to trailing
  separators (fix detectors; regression-tested).
- Cabana writer emits `166.064000.0` (fix; loader tolerates it).
- GVRET `GET_ANALOG_INPUTS` never returns to idle (fix in CanYeAye).

## CanYeAye deviations (deliberate fixes)

Implemented deviations, each covered by a test in `tests/CanYeAye.Tests/FileFormats`:

| Format | Reference behaviour | CanYeAye behaviour | Why |
|---|---|---|---|
| BusMaster | ms field multiplied by 100 (100 µs units) | real milliseconds | Writer emits ms, so the reference never round-trips its own files |
| BusMaster | ID column always loses its first two characters (assumes an `0x` prefix) | strip `0x` only when present | Real BusMaster files print bare zero-padded hex; truncation corrupts short IDs |
| BusMaster | writer writes `bus + 1`, loader keeps the raw channel | writer writes the bus as-is | Loader is unchanged (third-party files read identically); round trips are now stable |
| PCAN | writer writes `bus + 1`, loader keeps the raw bus | writer writes the bus as-is | Same reason as BusMaster |
| CARBUS | a `# $LocalTime=...` line marks the whole file as failed | comment rows are skipped | Files with that note otherwise cannot be loaded at all |
| CARBUS | DLC column parsed as hex | parsed as decimal | Hex parsing breaks every CAN FD frame with a DLC above 9 (`16`, `64`) |
| CANHacker | loader indexes past the token list when the DLC exceeds the data | returns false | The reference "successfully" loads unrelated logs (e.g. CANWise) |
| Microchip | detector accepts any line without `;` separators | requires four fields | Otherwise the detector matches every text file placed before it |
| IXXAT CSV | extended flag always false | set when ID > 0x7FF | 29-bit IDs round-trip |

