# DBC + Scripting: implementation spec (port from SavvyCAN)

Source files (read-only reference): `C:\Github\SavvyCAN\dbc\dbchandler.cpp`,
`dbc\dbc_classes.cpp`, `dbc\*.h`, `utility.h` (`Utility::processIntegerSignal`),
`scriptcontainer.cpp/.h`, `scriptingwindow.cpp`, `canfilter.cpp`,
`connections\canconmanager.cpp`, `bus_protocols\isotp_handler.*`, `bus_protocols\uds_handler.*`,
`examples\*.js`.

This document describes **observed behaviour**, including bugs and lossy edges. Everything marked
**[quirk]** is a reference bug or limitation: replicate it only where it is observable in saved
files or in script behaviour; otherwise fix it and record the deviation.

---

# 1. DBC data model

```
DBCFile
  fileName: string            // "<Unsaved File>" until saved/loaded
  filePath: string
  assocBuses: int             // -1 = all buses, 0.. = bus index
  isDirty: bool
  nodes:  List<DbcNode>              // Vector__XXX always first
  valueTables: List<DbcValueTable>
  attributes:  List<DbcAttribute>
  messages: MessageHandler          // ordered map keyed by uint32 ID

DbcNode      { name, comment, sourceFileName, attributes: List<DbcAttrValue> }
DbcMessage   { ID:uint32, extendedID:bool, name, comment, len:uint,
               canFD:bool, bitRateSwitch:bool, sender:DbcNode,
               bgColor, fgColor, attributes, signals:SignalHandler,
               multiplexorSignal:DbcSignal }
DbcSignal    { name, startBit:int=1, signalSize:int=1, intelByteOrder:bool=false,
               isMultiplexor:bool, isMultiplexed:bool, hasExtendedMultiplexing:bool,
               multiplexRanges: List<(int min,int max)>,        // private in C++
               multiplexParent:DbcSignal, multiplexedChildren: List<DbcSignal>,
               valType: UnsignedInt|SignedInt|SpFloat|DpFloat|String = UnsignedInt,
               factor:double=1.0, bias:double=0, min:double=0, max:double=1,
               receiver:DbcNode, parentMessage:DbcMessage, unitName, comment,
               cachedValue:object, attributes, valList: List<(int value,string descript)> }
DbcValueTable { name, valList: List<(int,string)> }
DbcAttribute { name, valType: Int|Float|String|Enum,
               attrType: General|Node|Message|Sig|Any, upper:double, lower:double,
               enumVals: List<string>, defaultValue: object }
DbcAttrValue { attrName, value: object }     // int | double | string
```

`messages` and `signals` are **QMap**s (sorted maps) in the reference. Consequences that must be
reproduced:

* messages iterate in **ascending numeric ID** order (save order, list order, lookup iteration);
* signals iterate in **ascending ordinal string** order of name (UTF-16 code-unit order, so `ABC`
  sorts before `abc`), **not** file order (save order, display order);
* inserting a duplicate key (duplicate message ID, duplicate signal name inside one message)
  **replaces** the earlier entry (last one wins).

`MessageHandler` also holds `matchingCriteria` (`Exact | J1939 | GMLAN`) and
`filterLabeling: bool`. Both are DBC-file scoped and persisted through the `matchingcriteria` /
`filterlabeling` message attributes (see section 4).

---

# 2. Parser pipeline

## 2.1 Line handling

* The file is read **line by line** (`QFile::readLine`).
* `rawLine` keeps the original text; `line = rawLine.simplified()` collapses every run of
  whitespace (tabs included) to a single space and trims both ends.
  **[quirk]** This also collapses whitespace *inside* quoted comments, units and enums.
  Consequence: leading indentation is irrelevant everywhere except the multiline `BU_` test
  (section 2.3).
* No comment syntax, no line continuation, no `\` escapes. Input is effectively Latin-1
  (`QString` from `QByteArray` implicit conversion); output is `toUtf8()`.
* Blank lines and unknown statements are silently ignored. `VERSION`, `NS_`, `BS_`, `NS_DESC_`,
  file-level `CM_`, `BA_DEF_REL_`, `BA_REL_`, `BA_DEF_DEF_REL_`, `SIG_GROUP_`, `BO_TX_BU_`,
  `SGTYPE_*`, `EV_*`, `ENVVAR_*`, `CAT_*`, `FILTER` are **not parsed** and therefore **not
  preserved on save**.

## 2.2 Parser state

```
currentMessage: DbcMessage = null      // last successfully parsed BO_
inMultilineBU: bool = false
inMultilineValTable: bool = false
multilineValTable: string
```

Before the loop: clear nodes, value tables and messages; reset `matchingCriteria = Exact`,
`filterLabeling = false`; append the implicit default node:

```
nodes[0] = { name: "Vector__XXX", comment: "Default node if none specified" }
```

Node `sourceFileName` is the DBC base name without extension (used by the node tree UI).

## 2.3 Dispatch table (order matters)

Each entry is an independent `if`, except the `BA_DEF_*` group, which is an `if / else if` chain.
All tests are `StartsWith` on the *simplified* line.

| # | Trigger | Parser |
|---|---|---|
| 0 | raw line starts with `\t` or three spaces, **while `inMultilineBU`** | continuation node: new node whose **whole name is the simplified line** **[quirk: not split on spaces]** |
| 1 | `BO_ ` | message |
| 2 | `SG_ ` | signal (attached to `currentMessage`) |
| 3 | `SG_MUL_VAL_ ` | extended multiplex relation |
| 4 | `SIG_VALTYPE_ ` | float signal type |
| 5 | `BU_:` | node list, also enables multiline mode |
| 6 | `CM_ SG_ ` | signal comment |
| 7 | `CM_ BO_ ` | message comment |
| 8 | `CM_ BU_ ` | node comment |
| 9 | `VAL_TABLE_ ` | value table (single or multi line) |
| 10 | `VAL_ ` | signal value descriptions |
| 11 | `BA_DEF_ SG_ `, `BA_DEF_ BO_ `, `BA_DEF_ BU_ `, `BA_DEF_ ` | attribute definitions |
| 12 | `BA_DEF_DEF_ ` | attribute default |
| 13 | `BA_ ` | attribute value |

Multiline `BU_`: any line starting with `BU_:` sets `inMultilineBU = true`. On the next
iteration, if the raw line is indented (a tab or three spaces) a node is appended with the
*entire* simplified line as its name; otherwise `inMultilineBU` is cleared and the line is
processed normally.

Multiline `VAL_TABLE_`: if the `VAL_TABLE_` line does not end with `;`, the line is stored and
every following simplified line is appended as `" " + line` until a line ends with `;`; then
`ParseValueTableLine` runs on the joined text. While buffering, the loop `continue`s, so any
other statement interleaved in a multi-line table is swallowed.
## 2.4 Grammar accepted (exact regexes)

Qt `QRegularExpression` (PCRE-like). `\w` behaves like `[A-Za-z0-9_]` (plus Unicode letters in
.NET; acceptable). None of these are end-anchored, so trailing text is ignored. `RegexOptions`
defaults everywhere (no Multiline/Singleline; each input is one line).

### BO_ : message

```
^BO\_ (\w+) ([-\w]+) *: (\w+) ([-\w]+)
   1: ID, decimal only
        ID &= 0x1FFFFFFF
        extendedID = (raw & 0x80000000) != 0
   2: message name (hyphens allowed)
   3: length in bytes (parsed as uint)
   4: sender node name, case-insensitive lookup, fallback nodes[0] (Vector__XXX)
```

Both `BO_ 468 Ctrl1: 8 VCM` and `BO_ 468 Ctrl1 : 8 VCM` parse. A duplicate ID overwrites the
previous message.

### SG_ : signal (four variants, tried in this order)

Common tail, with `offset = 1` when the variant has a mux-value group:

```
 : <startBit>|<size>@<byteOrder><sign> (<factor>,<bias>) [<min>|<max>] "<unit>" <receivers>
```

Numeric groups use the class `[0-9.+\-eE]+`; start bit, size and byte order use `(\d+)`; the sign
class is `[\+|\-]` (it also accepts a literal `|`).

1. Multiplexor:

```
^SG\_ *([-\w]+) +M *: *(\d+)\|(\d+)@(\d+)([\+|\-]) \(([0-9.+\-eE]+),([0-9.+\-eE]+)\) \[([0-9.+\-eE]+)\|([0-9.+\-eE]+)\] \"(.*)\" (.*)
```

   sets `isMultiplexor = true` and, if the message has no multiplexor yet, sets
   `msg.multiplexorSignal = sig`. **Only the plain `M` form sets `multiplexorSignal`.**

2. Multiplexed:

```
^SG\_ *([-\w]+) +m(\d+) *: *(\d+)\|(\d+)@(\d+)([\+|\-]) \(([0-9.+\-eE]+),([0-9.+\-eE]+)\) \[([0-9.+\-eE]+)\|([0-9.+\-eE]+)\] \"(.*)\" (.*)
```

   group 2 is the mux value: `AddMultiplexRange(v, v)` (sets `isMultiplexed`), `offset = 1`.

3. Extended multiplexor (both multiplexed and multiplexor):

```
^SG\_ *([-\w]+) +m(\d+)M *: *(\d+)\|(\d+)@(\d+)([\+|\-]) \(([0-9.+\-eE]+),([0-9.+\-eE]+)\) \[([0-9.+\-eE]+)\|([0-9.+\-eE]+)\] \"(.*)\" (.*)
```

   `isMultiplexor = true`, `AddMultiplexRange(v, v)`, `offset = 1`. Note it does **not** set
   `msg.multiplexorSignal`.

4. Plain:

```
^SG\_ *([-\w]+) *: *(\d+)\|(\d+)@(\d+)([\+|\-]) \(([0-9.+\-eE]+),([0-9.+\-eE]+)\) \[([0-9.+\-eE]+)\|([0-9.+\-eE]+)\] \"(.*)\" (.*)
```

   `isMultiplexed = false`, `isMultiplexor = false`.

Any `SG_` line matching none of the four is recorded as a load fault and dropped.

Group mapping after `offset` is applied (1-based, `off` = 0 or 1):

```
1 name | 2+off startBit | 3+off size | 4+off byteOrder | 5+off sign
6+off factor | 7+off bias | 8+off min | 9+off max | 10+off unit | 11+off receivers
```

Byte order and value type from `@<n><sign>`:

| form | intelByteOrder | valType |
|---|---|---|
| `0+` | false (Motorola) | UnsignedInt |
| `1+` | true (Intel) | UnsignedInt |
| `0-` | false | SignedInt |
| `1-` | true | SignedInt |
| `2` | false | SpFloat |
| `3` | false | DpFloat |
| `4` | false | String |
| `5` | true | SpFloat |
| `6` | true | DpFloat |

For `n >= 2` the sign character is ignored. For `n > 6`, `valType` stays at its default
(UnsignedInt) and the byte order stays false.

Receivers: if the captured text contains `,`, only the text before the first `,` is used, so
**only one receiver is stored**. Lookup is case-insensitive; if it fails the signal gets
`nodes[0]`. The signal is inserted into `msg.signals` keyed by name.

**[quirk]** `factor`, `bias`, `min`, `max` use `QString::toDouble()` (C locale) - in .NET use
`double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)`.

### SG_MUL_VAL_ : extended multiplexing

```
^SG\_MUL\_VAL\_ (\d+) ([-\w]+) ([-\w]+) ((?:\d+\-\d+[\s]*[\,]?[\s]*)*)
   1: message ID (decimal) -> &= 0x1FFFFFFF, exact lookup
   2: child signal name
   3: parent multiplexor signal name
   4: ranges like "13-13" or "0-1, 5-7" (pairs separated by ',' and/or spaces)
```

For each `min-max` in group 4: `child.multiplexParent = parent; child.AddMultiplexRange(min,max)`.
After the loop the child is appended to `parent.multiplexedChildren` **once**. A malformed range
(not exactly two sides) aborts the whole line with no changes. Missing message, child or parent
drops the line (load fault).

### SIG_VALTYPE_

```
^SIG\_VALTYPE\_ *(\d+) *([-\w]+) *: *(\d+);
   1: message ID (decimal)   2: signal name   3: type (1 = SpFloat, 2 = DpFloat)
```
Any other type value fails the line.

### BU_ : node list

```
^BU\_\:(.*)
   captured 1 is split on single spaces; every token with length > 1 becomes a node
```

**[quirk]** one-character node names are skipped. Nodes are appended after `Vector__XXX` in file
order.

### CM_ : comments

```
^CM\_ SG\_ *(\w+) *([-\w]+) *\"(.*)\";        // 1 msgID, 2 signal, 3 comment
^CM\_ BO\_ *(\w+) *\"(.*)\";                  // 1 msgID, 2 comment
^CM\_ BU\_ *([-\w]+) *\"(.*)\";               // 1 node name, 2 comment
```

`(.*)` is greedy, so a comment containing `";` over-captures up to the last `";` on the line.
Comments attach only if the target already exists, so `CM_` must follow its `BO_`/`SG_`/`BU_`.
File-level `CM_ "..."` is ignored. No escaping on load or save.

### VAL_TABLE_

```
^VAL\_TABLE\_ ([-\w]+) (.*);
   1: table name   2: value description list
```

Looks up an existing table by name (case-insensitive): if found, its `valList` is **replaced**;
else the table is appended. A table whose list fails to parse is dropped.

### VAL_ : signal value descriptions

```
^VAL\_ (\w+) ([-\w]+) (.*);
   1: message ID (decimal, \w+ only: no 0x prefix, no sign)
   2: signal name
   3: description list or value-table name
```

* If `group3.Trim()` matches an existing value-table name (case-insensitive), the signal's
  `valList` is **replaced** by a copy of that table's list.
* Else the list is parsed and **appended** (several `VAL_` lines for one signal accumulate).

Value description list (`ParseValueDescriptions`), repeated while the remaining text length > 2:

```
^([+-]?\d+) \"(.*?)\"(.*)
   1: value (decimal, optional sign)   2: description (non-greedy; may contain spaces and commas)
```

Returns false when nothing parsed or a token does not match; the output list is cleared first.

### BA_DEF_ : attribute definitions

The prefix is stripped before parsing, then the `attrType` is assigned by the caller:

* `BA_DEF_ SG_ ` strips 12 chars, `attrType = Sig`
* `BA_DEF_ BO_ ` strips 12 chars, `attrType = Message`
* `BA_DEF_ BU_ ` strips 12 chars, `attrType = Node`
* `BA_DEF_ ` strips 9 chars, `attrType = General`
  **[quirk]** one character too many is stripped; harmless because the leading quote is optional
  in the regex.

```
with bounds:    \"*(\w+)\"* \"*(\w+)\"* (\d+) (\d+)
   1 name, 2 type, 3 lower, 4 upper
without bounds: \"*(\w+)\"* \"*(\w+)\"* (.*)
   1 name, 2 type, 3 remainder (ENUM list)
```

Type token, case-insensitive: `INT` (bounds -> int, both default 0 when the no-bounds form is
used), `FLOAT` (bounds -> double), `STRING` (bounds ignored), `ENUM` (**only** the no-bounds
form; `group3.Split(',')`, each element unquoted with `Utility::unQuote`).

`DBC_ATTRIBUTE()` is reset before every definition, so enum lists never accumulate across
definitions. Definitions are appended, never de-duplicated; `FindAttributeByName` returns the
**first** match (optionally filtered by `attrType`).

`Utility::unQuote(s)`: `s.Split('"')`; if there are 3 or more parts return part 1, else return `s`.

### BA_DEF_DEF_ : attribute default value

```
^BA\_DEF\_DEF\_ \"*([-\w]+)\"* \"*([#\w]*)\"*
   1: attribute name   2: default value  (character class [#\w] only)
```

Applied to the first matching definition:

* `String` -> the raw captured text
* `Float` -> float parse
* `Int` -> int parse
* `Enum` -> case-insensitive search of `enumVals`, store the index; **0** when not found

**[quirk]** `[#\w]*` cannot capture `.`, `-`, `+` or `e`, so `BA_DEF_DEF_ "x" 1.5;` yields `1`,
`-3` yields an empty capture (`0`), `1e3` yields `1`. `#RRGGBB` strings survive only because `#`
is in the class.

### BA_ : attribute values

Tried in this order (independent `if`s). Each requires that the attribute definition and the
target object already exist; otherwise the line is ignored.

```
message: ^BA\_ \"*([-\w]+)\"* BO\_ (\d+) \"*([#\w]+)\"*            // 1 attr, 2 msgID, 3 value
signal : ^BA\_ \"*([-\w]+)\"* SG\_ (\d+) \"*([-\w]+)\"* \"*([#\w]+)\"*
                                                                    // 1 attr, 2 msgID, 3 signal, 4 value
node   : ^BA\_ \"*([-\w]+)\"* BU\_ \"*([-\w]+)\"* \"*([#\w]+)\"*    // 1 attr, 2 node, 3 value
```

Conversion uses the **definition's** `valType`: `String` -> raw text, `Float` -> float,
`Int`/`Enum` -> int (an enum value is stored as its index). The value replaces an existing
`DbcAttrValue` with the same name (case-insensitive) or is appended.

**[quirk, do not replicate]** in the `SG_` branch the "replace existing" path passes group 3 (the
*signal name*) instead of group 4 (the value) to the converter, so re-declaring a signal
attribute overwrites its value with `0` (or the signal name for string attributes). The `BO_`
branch uses the correct group.
---

# 3. Numeric parsing rules

| Field | Rule |
|---|---|
| `BO_` ID | decimal only (`toULong`, base 10) |
| ID normalisation | `& 0x1FFFFFFF`; extended = bit 31 of the raw value |
| message length | decimal `toUInt` |
| `SG_` start bit / size / byte order | decimal `toInt` |
| factor / bias / min / max | `toDouble` (invariant culture); accepts `1e-3`, `+0.5`, `.5` |
| IDs in `VAL_`, `BA_`, `CM_`, `SIG_VALTYPE_`, `SG_MUL_VAL_` | decimal only |
| attribute INT bounds / values / defaults | `toInt` (saved through `toLongLong`) |
| attribute FLOAT bounds | `toFloat` when parsed with bounds, `toDouble` in the no-bounds form |
| attribute FLOAT values / defaults | `toFloat` on load |
| parse failure | Qt returns `0` silently; the line still counts as parsed |

Load faults (message/signal lines that fail to parse) are collected and shown in a message box
after the load. They do not abort the load and are **not** written back on save.

---

# 4. Post-load fix-ups (mandatory)

1. If the file has no `GenMsgBackgroundColor` **message** attribute, append
   `{ attrType: Message, valType: String, lower: 0, upper: 0, name: "GenMsgBackgroundColor",
      defaultValue: <UI Base colour name, e.g. "#ffffff"> }`.
2. Same for `GenMsgForegroundColor` (UI `WindowText`, e.g. `"#000000"`).
   Both definitions are always written by `saveFile`, so loading a file that lacked them and
   saving it again gains two `BA_DEF_` / `BA_DEF_DEF_` pairs.
3. `matchingCriteria = (MatchingCriteria)attr("matchingcriteria").defaultValue` when present,
   else `Exact`; `filterLabeling = attr("filterlabeling").defaultValue != 0` when present, else
   `false`.
4. Per message: `bgColor` / `fgColor` start at the palette defaults and are overridden by the
   message's own `GenMsgBackgroundColor` / `GenMsgForegroundColor` values (`QColor(name)`).
5. `msg.canFD = FrameFormatIsFD(VFrameFormat, msg) || msg.len > 8`, where `FrameFormatIsFD` is
   true when the enum text ends with `CAN_FD` (case-insensitive) or, for non-enum attributes,
   when the text is `"14"` or `"15"`. Attribute text = message value if present, else the
   attribute default.
6. `msg.bitRateSwitch` = (`CANFD_BRS` message attribute text == `"1"`) when that attribute
   exists, else `true` (the `DBC_MESSAGE` default).
7. Multiplex parent repair, per message and per signal:
   * `if (sig.isMultiplexed && sig.multiplexParent == null && msg.multiplexorSignal != null)`
     -> `sig.multiplexParent = msg.multiplexorSignal; msg.multiplexorSignal.multiplexedChildren.Add(sig);`
     (may duplicate an entry already added by `SG_MUL_VAL_`; a `List` is used, so duplicates are
     possible)
   * `if (sig.isMultiplexed && msg.multiplexorSignal == null)` -> `sig.isMultiplexed = false;`
     (`isMultiplexor` is left untouched)
8. `assocBuses = -1`, `isDirty = false`.
9. `fileName` / `filePath` are split on **`/` only** **[quirk: on Windows a `C:\...` path makes
   fileName the whole path]** - use `Path.GetFileName` / `Path.GetDirectoryName` in the port.

---

# 5. Save format (exact)

Order below is the exact write order. Line endings are `\n` (never `\r\n`). Encoding UTF-8.

## 5.1 Fixed header (verbatim, 28 namespace entries)

```
VERSION ""


NS_ :
    NS_DESC_
    CM_
    BA_DEF_
    BA_
    VAL_
    CAT_DEF_
    CAT_
    FILTER
    BA_DEF_DEF_
    EV_DATA_
    ENVVAR_DATA_
    SGTYPE_
    SGTYPE_VAL_
    BA_DEF_SGTYPE_
    BA_SGTYPE_
    SIG_TYPE_REF_
    VAL_TABLE_
    SIG_GROUP_
    SIG_VALTYPE_
    SIGTYPE_VALTYPE_
    BO_TX_BU_
    BA_DEF_REL_
    BA_REL_
    BA_DEF_DEF_REL_
    BU_SG_REL_
    BU_EV_REL_
    BU_BO_REL_
    SG_MUL_VAL_

BS_: 
```

Notes: `VERSION ""` is followed by **two** blank lines; the namespace block is preceded by
`NS_ :` and followed by one blank line; `BS_: ` has a trailing space; `FILTER` is the only entry
without a trailing underscore.

## 5.2 Node line

`"BU_: "`, then for each node in list order, **skipping any node named `Vector__XXX`**
(case-insensitive):

* empty name -> assign `"NODE" + n` (nodes, messages and signals each have their own counter, all starting at 1, incremented only when a blank name is filled in);
* append `name + " "` (trailing space after every name, including the last);
* if the node has a comment, buffer `CM_ BU_ <name> "<comment>";\n`;
* for each node attribute value, buffer `BA_ "<attr>" BU_ <value>;\n` where

```
String -> "\"" + text + "\""
Bool   -> "1" or "0"
other  -> value.ToString()
```

then one `\n`.

## 5.3 Value tables

For each table, in list order: `"VAL_TABLE_ " + name`, then per entry
`" " + value + " \"" + descript + "\""`, then `";\n"`. The block is written only when at least
one table exists (no separating blank line).

## 5.4 Messages (ascending ID)

For each message:

```
BO_ <ID> <name>: <len> <senderName>\n
```

* `ID = msg.ID + (msg.extendedID || msg.ID > 0x7FF ? 0x80000000 : 0)`
* empty name -> `"MSG" + n`
* non-empty comment -> buffer `CM_ BO_ <ID> "<comment>";\n`
* message attributes -> buffer `BA_ "<attr>" BO_ <ID> <value>;\n` (same value formatting as above)

then for each signal, **in name-sorted order**:

```
   SG_ <name>[ m<k>][ M] : <startBit>|<size>@<bo><sign> (<factor>,<bias>) [<min>|<max>] "<unit>" <receiver>\n
```

* three leading spaces; empty name -> `"SIG" + n`
* ` m<k>` appended when `isMultiplexed`, where `k = multiplexRanges[0].min` (the first stored
  range's lower bound)
* `M` appended when `isMultiplexor`; when the signal is not also multiplexed a space is inserted
  before it. The four possible shapes are ` m3`, ` M`, ` m3 M`, and no marker at all.
* `@` code: `UnsignedInt` -> `1+` (Intel) / `0+` (Motorola); `SignedInt`, `SpFloat`, `DpFloat`
  -> `1-` / `0-`; `String` -> `4-`; anything else `0-`.
* numbers are printed with `QString::number(double)`, i.e. **format "g" with 6 significant
  digits** (`0.01`, `0.392`, `1e-07`, `0.333333`, `1e+10`). This is lossy (section 9).
* receiver name is used verbatim (`sig.receiver.name`; never null after a load)
* `SpFloat` / `DpFloat` additionally buffer `SIG_VALTYPE_ <ID> <name> : 1;` or `: 2;`
* non-empty comment -> buffer `CM_ SG_ <ID> <sig> "<comment>";\n`
* signal attributes -> buffer `BA_ "<attr>" SG_ <ID> <sig> <value>;\n`
* non-empty `valList` -> buffer `VAL_ <ID> <sig>` + ` <value> "<descript>"` per entry + `;\n`

After each message block exactly one blank line (`\n`) is written.

## 5.5 Comments

All buffered `CM_` lines are written here, in the order they were collected: **all node comments
first**, then per message (ascending ID) its `CM_ BO_`, then that message's `CM_ SG_` lines in
signal-name order.

## 5.6 Attribute definitions, then defaults and values

For each `DbcAttribute` in list order, one line:

```
BA_DEF_ [BU_ |BO_ |SG_ ]"<name>" <type spec>;\n
```

* prefix omitted for `General` and `Any`
* `INT <lower> <upper>`, `FLOAT <lower> <upper>`, `STRING ` (note the trailing space before the
  `;`), `ENUM "a","b","c"` (no bounds, comma separated, no trailing comma)
* `matchingcriteria` and `filterlabeling` definitions are emitted **once each** even when the
  list contains duplicates (dedupe only these two names, message type only)

When `defaultValue` is set, buffer:

```
BA_DEF_DEF_ "<name>" <default>;
```

`String` -> quoted; `Enum` -> quoted `enumVals[defaultIndex]` **[quirk: crashes when the index is
out of range]**; `Int` -> decimal (long); `Float` -> `defaultValue.ToString()`.

## 5.7 Extended multiplexing block

Only when `hasExtendedMultiplexing` is true for the file (any signal is both multiplexed and
multiplexor, or any signal has a range with `min != max`): for each message (ascending ID) and
each signal (name order) with `isMultiplexed`:

```
SG_MUL_VAL_ <ID> <sigName> <parentSignalName> <ranges>;\n
```

`ranges` = `"min-max"` pairs joined by **single spaces** (the DBC-file format). The UI format
of the same method joins with `;` and collapses `min == max` to a single number.

## 5.8 Final flush order

```
<all BA_DEF_DEF_ lines>
<all BA_ value lines>       (node attrs, then per message: message attrs, then per signal attrs)
<all VAL_ lines>
<all SG_MUL_VAL_ lines>
<all SIG_VALTYPE_ lines>
```

`syncCanFDAttributes()` (section 6) runs **before** the header is written. Finally `isDirty` is
cleared and `fileName` / `filePath` are updated from the target path.

---

# 6. CAN FD attribute synchronisation (save-time)

Runs on every save; it only changes files that contain CAN FD messages.

1. If there is no `VFrameFormat` **message** attribute: if the file has no FD message, return
   immediately; otherwise create
   `{ Enum, lower 0, upper 0, default 0, enumVals = ["StandardCAN","ExtendedCAN","reserved",
   "J1939PG", ten more "reserved", "StandardCAN_FD", "ExtendedCAN_FD"] }`
   (16 entries; indices 14 and 15 are the FD formats).
2. If the file has FD messages and no `CANFD_BRS` message attribute: create
   `{ Enum, enumVals = ["0","1"], default 1 }`.
3. Both attributes are re-looked-up after appending (the list may have moved in C++).
4. Per message:
   * `extended = msg.extendedID || msg.ID > 0x7FF` (same rule `saveFile` uses)
   * `wanted = (extended ? "ExtendedCAN" : "StandardCAN") + (msg.canFD ? "_FD" : "")`
   * the value is rewritten when `FrameFormatIsFD(attr,msg) != msg.canFD`, **or** when the
     message has an explicit value that is one of the four plain formats and differs from
     `wanted`. `J1939PG` and other formats are preserved, and classic messages that only use
     the default are not given an explicit value.
   * enum attribute -> index of `wanted` (case-insensitive); non-enum -> numeric
     `(extended ? 1 : 0) + (canFD ? 14 : 0)`
   * for FD messages with a `CANFD_BRS` attribute: ensure the text is `"1"` or `"0"` per
     `bitRateSwitch` (enum -> index of `"1"`/`"0"`, otherwise numeric).
---

# 7. Bit extraction (the core codec)

`Utility::processIntegerSignal(data, startBit, sigSize, littleEndian, isSigned) -> int64`

Bit numbering ("sawtooth"): a bit index `b` lives in `data[b / 8]` at mask `1 << (b % 8)`.

```
      7  6  5  4  3  2  1  0
byte0  7  6  5  4  3  2  1  0
byte1 15 14 13 12 11 10  9  8
byte2 23 22 21 20 19 18 17 16
...
```

* **Intel / little endian**: start at `startBit`, advance by 1 per extracted bit; the first bit is
  the **LSB** of the value.
* **Motorola / big endian**: start at `startBit`; after consuming a bit,
  `bit += ((bit % 8) == 0) ? 15 : -1`. The first bit is the **MSB** of the value. Start bit 12
  with size 8 gives bits 12, 11, 10, 9, 8, 23, 22, 21 with 12 as MSB.

Port of the reference (with the hardening noted below):

```csharp
static ulong ExtractBits(ReadOnlySpan<byte> data, int startBit, int size, bool littleEndian, bool isSigned)
{
    if ((startBit + size) / 8 > data.Length) return 0;       // pre-check -> raw 0
    ulong result = 0;
    int bit = startBit;
    for (int bitpos = 0; bitpos < size; bitpos++)
    {
        if (bit < 2048)                                      // reference guard, 256 bytes
        {
            int bytePos = bit / 8;
            if (bytePos >= data.Length) return 0;            // per-bit bound check -> raw 0
            if ((data[bytePos] & (1 << (bit % 8))) != 0)
                result |= 1UL << (littleEndian ? bitpos : size - bitpos - 1);
        }
        bit += littleEndian ? 1 : ((bit % 8) == 0 ? 15 : -1);
    }
    if (isSigned)
    {
        ulong mask = 1UL << (size - 1);
        if ((result & mask) == mask)
            result |= ~((1UL << size) - 1);                  // sign-extend to 64 bits
    }
    return result;
}
```

Hardening: special-case `size >= 64` (mask `0x8000000000000000`, extension `~(mask-1)`) so
`1UL << 64` is not evaluated; the reference has undefined behaviour there. `startBit + size`
never exceeds the payload of a valid DBC, but a negative or oversized definition must be rejected
early.

`ProcessIntegerSignal` returns `0` (not an error) when the signal does not fit in the payload.

## 7.1 Decode paths

Let `raw = (long)ExtractBits(...)`.

### Integer (`UnsignedInt`, `SignedInt`)

```
isSigned  = valType == SignedInt
raw       = ExtractBits(data, startBit, signalSize, intelByteOrder, isSigned)
value     = raw * factor + bias          // double arithmetic
intValue  = (long)value                  // C truncation toward zero
isInteger = (factor == Math.Floor(factor))
```

`ProcessAsInt` returns the same computation as an `int` and fails (returns false) for `String`,
`SpFloat` and `DpFloat`. `ProcessAsDouble` first rejects when
`LastByteOfSignal(startBit, size, intel) >= payload.Length` and returns `false`, leaving the out
parameter untouched.

```
LastByteOfSignal: intel    -> (startBit + size - 1) / 8
                  motorola -> msb = (startBit / 8) * 8 + (7 - startBit % 8);
                              (msb + size - 1) / 8
```

### Single precision float (`SpFloat`)

```
bits32 = ExtractBits(data, startBit, 32, intelByteOrder, isSigned: false)
f      = BitConverter.Int32BitsToSingle((int)bits32)          // bit reinterpretation
value  = f * factor + bias
```

The reference reinterprets the low 4 bytes of the 64-bit integer result, which produces the same
value on little-endian hosts.
**[quirk]** `ProcessAsDouble` passes `littleEndian: false` for both float paths while
`ProcessAsText` passes `intelByteOrder`; use `intelByteOrder` consistently.
Bounds: `ProcessAsText` does **not** bound-check floats (garbage or 0 on short payloads);
`ProcessAsDouble` requires `payload.Length * 8 >= startBit + 32`.

### Double precision float (`DpFloat`)

```
if (payload.Length < 8) return false;                     // both paths
bits64 = ExtractBits(data, startBit, 64, intelByteOrder, isSigned: false)
d      = BitConverter.Int64BitsToDouble((long)bits64)
value  = d * factor + bias
```

### String

```
startByte = startBit / 8
count     = signalSize / 8
text      = payload[startByte .. startByte + count - 1] as Latin-1 characters
```

No NUL termination or trimming, no byte-order handling, no bounds check (clamp in the port).
`cachedValue` is set to the decoded `string`, `int` or `double` on every successful decode.

## 7.2 Pretty output (frame list / detail views)

```
if (outputName) s = name + ": "
if (valList is non-empty):
    if (valList has an entry whose value == intValue)  s += entry.descript
    else                                              s += intValue.ToString()
else:
    s += isInteger ? intValue.ToString() : floatValue.ToString()   // QString::number(double)
if (outputUnit) s += " " + unitName                                // always, even when empty
```

**[quirk]** the unit is appended unconditionally when `outputUnit`, so an empty unit leaves a
**trailing space**. `QString::number(double)` is 6 significant digits - use
`value.ToString("g6", CultureInfo.InvariantCulture)` to match byte-for-byte.
`GetValueString(intValue)` is the valList-only lookup (returns true when found).

## 7.3 Encode (needed for TX, the signal editor and the fuzzer)

The reference has no encoder, but the same bit walk gives one:

```
for bitpos in 0..size-1:
    valueBit = (littleEndian ? bitpos : size - 1 - bitpos)
    if ((raw >> valueBit) & 1) data[bit / 8] |= (byte)(1 << (bit % 8))
    advance bit exactly as in ExtractBits
```

Floats: write `BitConverter.SingleToInt32Bits` / `DoubleToInt64Bits` as a 32/64-bit unsigned
value through the same walk. Strings: copy the bytes starting at `startBit / 8` (Motorola
`sawtooth` walk is not used). Clear the target bits before writing a signal so overlapping
definitions behave deterministically.

---

# 8. Multiplexing

State per signal: `isMultiplexor`, `isMultiplexed`, `multiplexRanges: List<(int min,int max)>`,
`multiplexParent`, `multiplexedChildren`, `hasExtendedMultiplexing`.

`AddMultiplexRange(min, max)`:

* `if (min != max) hasExtendedMultiplexing = true;`
* `isMultiplexed = true;`
* append the pair only when an identical pair is not already stored (exact pair equality)

Presence test:

```
IsSignalInMessage(frame):
  if (isMultiplexor && !isMultiplexed) return true;            // the root muxor is always present
  if (!isMultiplexed) return true;
  if (parentMessage.multiplexorSignal == null) return false;
  if (!multiplexParent.IsSignalInMessage(frame)) return false;
  int v;
  if (!multiplexParent.ProcessAsInt(frame, out v)) return false;
  return IsValueMatchingMultiplex(v);                          // any range contains v, inclusive
```

Rendering a message's active signals:

* `ProcessSignalTree(frame)` is called on the root multiplexor: decode the muxor as an int; for
  each child whose ranges contain the value, append `child.ProcessAsText(frame)` (separated by
  `"\n"`), and when the child `isMultiplexor`, recurse and append its subtree output.
* `ProcessAvailableSignals(frame, map)`: same walk but stores `map[child] = text`.
  **[quirk]** it calls `child->processSignalTree(frame)` and discards the result, so grandchildren
  are never collected - fix by recursing into the same map.
* `MultiplexDbcString(DbcFile)` -> `"min-max"` pairs joined by spaces (save format).
  `MultiplexDbcString(UI)` -> joined by `;`, collapsing `min == max` to a single number.
* `ParseDbcMultiplexUiString("1;2;5-7")` -> `(1,1),(2,2),(5,7)`; fails on a part with more than
  one `-` or a non-integer, and on failure leaves the existing ranges untouched; on success it
  **replaces** them. Used by the signal editor.
* `MultiplexesIdenticalToSignal(other)` = multiset equality of both range lists.
* `GetSimpleMultiplexValue()` = `ranges[0].min` (throws when empty; only called for multiplexed
  signals, which always have at least one range).

---

# 9. Lookup semantics (message resolution)

`DBCHandler` (port as `DbcDatabase`) holds `List<DBCFile> loadedFiles` in load order.

```
FindMessage(CommFrame f):
    foreach file in loadedFiles:
        if (file.assocBus == -1 || f.Bus == file.assocBus):
            m = file.Messages.FindById(f.FrameId);
            if (m != null) return m;
    return null;

FindMessage(uint id):   first file whose FindById(id) is non-null
FindMessage(string name): first file whose message name matches case-insensitively
FindMessage(msgName, nodeName, fileNameNoExt): exact name and sender name, file by base name
FindMessage(msgName, "file::node"): split on Utility.fullyQualifiedNameSeperator (which is "::")
FindMessageForFilter(id, out criteria): only files with filterLabeling enabled
```

`MessageHandler.FindById(id)`:

```
if (map contains exact id) return it;                      // an exact match always wins
if (criteria == Exact) return null;
pgn   = (id & 0x3FFFF00) >> 8;
arbId = (id & 0x3FFE000);
foreach (key, msg) in ascending key order:
    if (criteria == J1939):
        if ((pgn & 0xFF00) <= 0xEF00) { pgn &= 0x3FF00; if ((key & 0x3FF0000) == (pgn << 8)) return msg; }  // PDU1
        else                          { if ((key & 0x3FFFF00) == (pgn << 8)) return msg; }                  // PDU2
    else /* GMLAN */:
        if (arbId != 0 && (key & 0x3FFE000) == arbId) return msg;
return null;
```

`FindByName` is a linear scan with `StringComparison.OrdinalIgnoreCase` equality;
`FindByPartialName` uses case-insensitive `Contains`. Both scan in ascending ID order.

`DBCHandler` is a process singleton; on construction it restores previously loaded files from
settings keys `DBC/FileCount`, `DBC/Filename_<i>`, `DBC/AssocBus_<i>`,
`DBC/MatchingCriteria_<i>`, `DBC/FilterLabeling_<i>`, creating the `matchingcriteria` and
`filterlabeling` attributes when absent. **CanYeAye must use its own settings store** (AGENTS.md
forbids touching SavvyCAN keys).

Load/save dialogs offer `DBC File (*.dbc)` (index 0), `Tesla JSON File (*.json)` (1) and
`Secret CSV Signal Defs (*.csv)` (2); `LoadFileAs` appends the extension when the name has no
`.`.

## 9.1 Secondary loaders (needed for parity)

**Tesla JSON**: root `{"messages": {"<MsgName>": {...}}}` per message with `message_id`,
`length_bytes`, `originNode`, and `signals: {"<Sig>": {...}}` per signal with `scale`, `offset`,
`min`, `max`, `start_position`, `units`, `width`, `mux_id`, `is_muxer`,
`endianness` (`"BIG"` -> Motorola, anything else -> Intel), `signedness` (`"UNSIGNED"` ->
UnsignedInt, else SignedInt), `receivers` (array; first entry, node created on demand),
`value_description` (object: description -> number). Starts from `createBlankFile()` (which
pre-creates `GenMsgBackgroundColor`, `GenMsgForegroundColor`, `matchingcriteria`,
`filterlabeling` and the `Vector__XXX` node) and marks the file dirty.

**Secret CSV**: 10 columns, the first two lines (header) are skipped, every line upper-cased,
then split on `,`:
`Message,CAN ID,Signal,Short Name,Start Byte,Start Bit,Len,Data,Range,Conversion`

* `tokens[0].Length > 2` -> new message (`ID = int(tokens[1].Substring(1), 16)`, `len = 8`,
  sender = node 0) plus its first signal
* `tokens[2].Length > 2` with an empty first column -> another signal in the current message
* otherwise `tokens[9]` holds an extra `$value=Text` entry appended to the current signal
* signal: `startBit = tokens[4]*8 + tokens[5]`, always Motorola, `Range` parsed as `"min-max"`,
  `Conversion` `E = N * k`, `E = N * k + b`, `E = N * k - b`
  **[quirk]** the `-` case multiplies the bias by `1.0` instead of negating it; fix in the port.
---

# 10. Known-lossy edges and port decisions

| # | Reference behaviour | Decision for CanYeAye |
|---|---|---|
| 1 | factor/bias/min/max saved with `QString::number(double)` (g6) | Keep g6 output for byte-identical round trips. Additionally store the original literal text (`FactorText`, `BiasText`, `MinText`, `MaxText`) on load and reuse it while the field is unmodified, so `1/3` or long decimals survive. Tests that compare against SavvyCAN output must accept `abs(a-b) <= abs(a)*1e-6`. |
| 2 | `[#\w]` class truncates `BA_` / `BA_DEF_DEF_` values at `.`, `-`, `e` | Parse the full token up to `;` for FLOAT/INT attributes; keep the truncating behaviour only if a fixture proves it matters. |
| 3 | `BA_ ... SG_` overwrite bug (section 2.4) | Do not replicate. |
| 4 | File-level `CM_`, `NS_` contents, `SIG_GROUP_`, `BO_TX_BU_`, `BA_REL_*`, `EV_*`, `CAT_*` dropped | Preserve unparsed statements verbatim in `List<string> UnparsedStatements` and re-emit them before the attribute block, gated by a setting. Strict SavvyCAN-identical output remains the default for round-trip tests. |
| 5 | Multiline `BU_` continuation stores the whole line as one node name | Split on spaces (correct behaviour). |
| 6 | `CM_` greedy `(.*)` over-capture | Match non-greedily up to the last `";` on the line; no unescaping. |
| 7 | Path split on `/` | Use `Path.GetFileName` / `Path.GetDirectoryName`. |
| 8 | `sort()` is a no-op because QMap is already ordered | Use `SortedDictionary<uint, DbcMessage>` and `SortedDictionary<string, DbcSignal>(StringComparer.Ordinal)`. |
| 9 | Duplicate signal names silently replace the earlier signal | Keep "last wins", log a diagnostic. |
| 10 | `DBC_SIGNAL::self` is uninitialised | Drop the field. |
| 11 | Save crashes on null `multiplexParent`, out-of-range enum default index, null sender/receiver | Throw a descriptive exception; the editors must keep these non-null. |
| 12 | `ProcessAsDouble` uses big-endian for floats | Use `intelByteOrder`. |
| 13 | `ProcessAsText` does not bounds-check ints/floats | Return `false` consistently and never read outside the payload. |
| 14 | No escaping of `"` in comments, units or enum text | Keep unescaped for byte-identical output; treat round trips of such files as best effort and log a warning. |
| 15 | `DBCFile` copy ctor/assignment are broken (shares the message handler, shallow-copies nodes) | Do not port; deep clone or make files immutable-by-convention and never copy. |
| 16 | Message/signal names must match `[-\w]+`, so real files containing `(`, `)` or `?` lose those entries | Accept any non-space, non-colon name; validated locally against 17 real DBCs in `DBCsamples` (git-ignored, not committed) |
| 17 | `BA_DEF_` bounds match `(\d+)` only, so FLOAT bounds and 7-digit INT bounds break the round trip | Parse bounds as full numeric tokens and print INT bounds as integers |
| 18 | BA_/VAL_/CM_/SIG_VALTYPE_/SG_MUL_VAL_ message ids are written with bit 31 but masked on load (`& 0x1FFFFFFF`) | Same masking on load, otherwise extended-message attributes/comments/values are dropped on re-read |

---

# 11. Test plan (DBC)

Read-only fixtures from `C:\Github\SavvyCAN\examples`: `bms.dbc` (small, clean),
`LeafPowertrainBus.dbc` (contains the empty message `BO_ 498 Ctrl2: 8 VCM` with no signals),
`ThinkCity.dbc` (275 KB, tab-indented namespace, single-space `SG_` indentation, real-world noise).

1. **Grammar**: parse each fixture; assert message count, node list (`Vector__XXX` first), signal
   counts, and that no message or signal is reported as a fault. `LeafPowertrainBus` must yield
   the empty message.
2. **Bit extraction** (table driven, both byte orders):
   * Intel `0|16` over `{0x10,0x27}` -> `0x2710` = 10000
   * Motorola `39|8` with byte 4 = `0xFF` -> 255 (start bit is the MSB of that byte)
   * Motorola `16|16` signed over `{0,0,0xFF,0xFE}` -> -2
   * Intel `0|12` signed over `{0xFE,0x0F}` -> -2
   * Motorola `23|12` (`LeafPowertrainBus` `RequestedTorque`, signed, crosses a byte boundary)
   * Intel `12|8` (crosses a byte boundary)
   * Motorola `7|16` (`LeafPowertrainBus` `LeftWheel`, start bit at the LSB of byte 0, so the
     value is the big-endian 16-bit word of bytes 0-1)
3. **Scaling**: `10000 * 0.01 = 100.0` with non-integer factor -> text `"100"`; `255 * 0.392 =
   99.96` -> `"99.96"`; integer factor 1 -> integer formatting; bias is applied before the
   truncation to `intValue`.
4. **Floats**: `SIG_VALTYPE_ ... 1` (SpFloat) and `... 2` (DpFloat) decode known IEEE bit
   patterns; DP with a payload shorter than 8 bytes returns `false`.
5. **Strings**: a `4-` signal of 8 bytes; assert the current behaviour (embedded NULs kept, no
   trimming) so the port is intentionally identical.
6. **Multiplex**: simple `m0`/`m1`; extended `m1M` chains; `SG_MUL_VAL_` with `13-13`, `1-2` and
   multi-range `0-1, 5-7`; a signal marked multiplexed in a message with no multiplexor (must end
   up `isMultiplexed == false` after load).
7. **Save round trip**: load -> save -> load -> save. The second and third outputs must be
   **byte-identical** (stable ordering, g6 numbers, fixed header verbatim).
8. **Ordering**: messages ascending by ID; signals ascending ordinal by name; node comments
   before message comments; `SG_MUL_VAL_` before `SIG_VALTYPE_`.
9. **CAN FD**: a file with `VFrameFormat` 14/15 round trips unchanged; a classic message with
   `len > 8` gains the FD attributes on save; `CANFD_BRS` defaults to 1; `bitRateSwitch` false
   round trips as `"0"`.
10. **Faults**: truncated `BO_`, an `SG_` with no preceding `BO_`, a `VAL_` for a missing signal,
    a `BA_` for an undefined attribute -> dropped, no exception, load still returns success.
11. **J1939/GMLAN**: `FindById` for PDU1 and PDU2 ids, and the "exact match wins" case.
12. **CSV/JSON**: craft small fixtures per section 9.1 (the files in `examples` with those
    extensions are logs, not definitions).
13. **Attributes**: enum defaults, `matchingcriteria`/`filterlabeling` round trip, the
    `GenMsg*Color` injection on load, and the "emit matchingcriteria once" dedupe on save.

---

# 12. Scripting: engine, globals, lifecycle

Reference: `scriptcontainer.cpp/.h` (engine + helpers) and `scriptingwindow.cpp` (UI host).
The engine is `QJSEngine` (ECMAScript 5.1 plus Qt extensions such as `Number.prototype.toString`).
Port target: **Jint** with ECMAScript 5.1+ compatibility; do not enable ES modules.

## 12.1 Object graph

```
ScriptContainer                      // one per loaded script
  engine: QJSEngine
  canHelper:  CANScriptHelper        // -> global "can"
  isoHelper:  ISOTPScriptHelper      // -> global "isotp"  (own ISOTP_HANDLER)
  udsHelper:  UDSScriptHelper        // -> global "uds"    (own ISOTP_HANDLER + UDS_HANDLER)
  timer: QTimer (single shot? no, repeating, ms)
  scriptParams: Vector<QString>
  fileName, filePath, scriptText     // public; visible as JS properties of "host"
  compiledScript, setupFunction, tickFunction
```

Each helper is registered with `scriptEngine->newQObject(helper)`, so **every public slot and
every public property** becomes a JS member. Signatures take/return `QJSValue`, which is how
untyped JS values pass through.

## 12.2 `host` (the ScriptContainer object)

| Script-visible member | C++ signature | Behaviour |
|---|---|---|
| `host.log(text)` | `void log(QJSValue)` | converts with `toString()` and emits `sendLog`; the window prefixes `<elapsed ms>(<script fileName>): ` |
| `host.setTickInterval(ms)` | `void setTickInterval(QJSValue)` | `int v = toInt(); if (v > 0) { timer.setInterval(v); timer.start(); } else timer.stop();` |
| `host.addParameter(name)` | `void addParameter(QJSValue)` | `scriptParams.append(name.toString())`; the parameter table then polls the global of that name |
| `host.updateParameter(name, value)` | `void updateParameter(QString, QString)` | sets the **global** property `name` to a **string** `value` (used by the UI when the user edits a cell; scripts may call it too) |
| `host.compileScript()` | `void compileScript()` | recompiles the container from `scriptText` (public slot, reachable from JS) |
| `host.updateValuesTable(widget)` | `void updateValuesTable(QTableWidget*)` | UI plumbing; not usable from JS. Skip in CanYeAye or expose as a no-op. |
| `host.fileName`, `host.filePath`, `host.scriptText` | public fields | readable **and writable** from JS |

Logging is a signal (`sendLog(QString)`) connected by `setScriptWindow()`; the log list is shared
by all scripts.

## 12.3 `can`

| Member | Signature | Behaviour |
|---|---|---|
| `can.setFilter(id, mask, bus)` | `(QJSValue id, QJSValue mask, QJSValue bus)` | stores a `CANFilter{ID = id.toUInt(), mask = mask.toUInt(), bus = bus.toInt()}` in the helper's list **and** registers a targeted-frame observer with the connection manager: `addTargettedFrame(bus, id, mask, this)` |
| `can.clearFilters()` | `()` | removes every stored filter's observer, then clears the list |
| `can.sendFrame(bus, id, length, data)` | `(bus, id, length, data)` | builds a classic frame (see below) and calls `CANConManager::sendFrame` |
| `can.sendFDFrame(bus, id, length, data, brs)` | `(bus, id, length, data, brs)` | CAN FD variant |
| `can.setRxCallback(cb)` | `(QJSValue cb)` | internal; used by `compileScript` to install `gotCommFrame` |

`sendFrame`:
1. `frame.setExtendedFrameFormat(false)`, `frame.setFrameId((uint32)id.toInt())`
2. `bytes = new byte[length.toUInt()]` (zeros); `for i in 0..length-1: bytes[i] = (byte)data.property(i).toInt()`
   (an undefined property converts to 0; a non-array argument logs but is still indexed)
3. `frame.setPayload(bytes)`, `frame.setBus((uint32)bus.toInt())`
4. `if (frame.frameId() > 0x7FF) frame.setExtendedFrameFormat(true)`
5. `CANConManager::sendFrame(frame)`

Length is **not** clamped to 8 (the documentation says it cannot exceed 8 but nothing enforces
it). Negative `bus` becomes a huge `uint32`.

`sendFDFrame`:
1. `requested = min(length.toUInt(), 64)`
2. `fdLen` = the smallest entry of `{0,1,2,3,4,5,6,7,8,12,16,20,24,32,48,64}` that is `>= requested`,
   defaulting to 64
3. payload of `fdLen` zeros, the first `requested` bytes copied from `data`
4. `setFrameType(CANFDDataFrame)`, `setFlexibleDataRateFormat(true)`,
   `setBitrateSwitch(brs.toBool())`
5. extended when `id > 0x7FF`

### Sends with no owning connection

`CANConManager::sendFrame` queues frames in `buslessFrames` when no connection exists and the
connection timer later replays them through `framesReceived` (two-arg overload with a null
connection). Script sends therefore never fail and show up in the frame log even on a machine
with no adapter attached. CanYeAye `CanConnectionManager.SendFrameAsync` behaves the same way:
if no attached connection covers the global bus, the frame is published through `FrameReceived`
instead of throwing, so the script log stays free of "no connection owns bus" warnings. The
echo is stamped with the manager's elapsed time and is marked as received (SavvyCAN leaves
`localEcho` false for busless frames).

## 12.4 `isotp`

| Member | Behaviour |
|---|---|
| `isotp.setFilter(id, mask, bus)` | `handler->addFilter(bus, id.toUInt(), mask.toUInt())` on that helper's own `ISOTP_HANDLER` |
| `isotp.clearFilters()` | `handler->clearAllFilters()` |
| `isotp.sendISOTP(bus, id, length, data)` | builds an ISOTP message (payload from the array) and calls `handler->sendISOTPFrame(bus, id, payload)`; the handler fragments, pads with `padByte` and handles flow control |
| `isotp.setPaddingByte(byte)` | `handler->setPadByte((char)byte.toInt())`; default `0xAA` |
| `isotp.setRxCallback(cb)` | internal; installs `gotISOTPMessage` |

The handler is created with reception **on** and flow control **on**. Reception-side filters are
evaluated inside the ISOTP handler against the *CAN* ID and bus of each received frame.

## 12.5 `uds`

| Member | Behaviour |
|---|---|
| `uds.setFilter(id, mask, bus)` | `handler->addFilter(bus, id, mask)`, which forwards to that helper's own ISOTP handler |
| `uds.clearFilters()` | `handler->clearAllFilters()` |
| `uds.sendUDS(bus, id, service, sublen, subfunc, length, data)` | sets `service`, `subFuncLen`, `subFunc`, payload from the array, then `handler->sendUDSFrame(msg)` |
| `uds.setRxCallback(cb)` | internal; installs `gotUDSMessage` |

`UDS_HANDLER::sendUDSFrame` validates `bus >= 0`, `bus < busCount`, `service <= 0xFF`, then
builds `[service][subFunc big-endian over subFuncLen bytes][payload]` and hands it to the ISOTP
handler (so `data` is only the bytes *after* the sub-function).

## 12.6 Frame object shape

There is **no frame object**. Callbacks receive positional arguments:

```
gotCommFrame(bus, id, len, data)        // data is a JS array of numbers 0..255
gotCANFrame(bus, id, len, data)         // legacy alias, used only when gotCommFrame is absent
gotISOTPMessage(bus, id, len, data)     // reassembled ISO-TP payload
gotUDSMessage(bus, id, service, subFunc, len, data)
```

* `bus` - `frame.getBus()`, a number
* `id` - `frame.frameId()`, a number; **the extended flag is not exposed**
* `len` - payload length (`uint`), a number
* `data` - a fresh JS array; elements are set with `QJSValue((unsigned char)byte)`, so they are
  numbers 0..255 (`data.length === len`)
* UDS: `service` is the service byte (for a negative reply `0x7F` the handler substitutes the
  *original* service byte and flags `isErrorReply`), `subFunc` is the first payload byte after
  the service (0 when absent). `subFuncLen` is **not** exposed.

Delivery is asynchronous: the connection manager invokes `gotTargettedFrame` with
`Qt::QueuedConnection`, so callbacks run on the main thread from the event loop. In CanYeAye:
dispatch through the UI dispatcher (Avalonia) or a queued synchronization context, never from
the receive thread.

## 12.7 Filters

`CANFilter::CheckFilter(id, bus)`:

```
if (bus == -1 || bus == filter.Bus)     // note: the FRAME's bus is compared with the filter bus
    return (id & filter.Mask) == filter.ID;
return false;
```

* Registration is split: the connection stores an observer per bus (`id & mask == filter.id`),
  and `CANScriptHelper::gotTargettedFrame` re-checks its own filter list before invoking the
  callback. It **returns after the first matching filter** (only one callback per frame, even
  with overlapping filters).
* `bus` must be a concrete bus index for delivery: `CANConManager::addTargettedFrame` registers
  with the connection that owns `pBusId`; `-1` means "all buses" only inside `CANFilter`, and
  `CANConManager` handles `-1` by registering on every connection's every bus, but the helper's
  own re-check (`bus == -1 || frame.Bus == filter.Bus`) then always passes.
* Filters registered before any connection exists are stored in the helper but never handed to a
  connection (the manager iterates its current connections). CanYeAye should keep filters in the
  script host and re-apply them when a connection is added.
* Bus numbering: connections renumber frames to their local bus index before dispatch, so
  `bus` in the callback is only identical to the global bus number when a single connection is
  present.
* ISO-TP/UDS filters are bus+ID+mask triplets evaluated inside their handlers on the received
  CAN frame; only frames that parse as ISO-TP (and then UDS) reach the callbacks.

## 12.8 Lifecycle

State machine per script:

```
Created       -> (editor text loaded)      -> Compile()
Compile():
    result = engine.evaluate(scriptText, fileName)       // fileName is the script source name
    log "Compiling script..."
    canHelper.clearFilters(); isoHelper.clearFilters(); udsHelper.clearFilters()
    if (result.isError()):
        log "SCRIPT EXCEPTION!"
        log "Line: "  + result.lineNumber
        log           result.message
        log "Stack:"
        log           result.stack
        return                                            // globals NOT installed, callbacks unchanged
    compiledScript = result
    global.host  = newQObject(container)
    global.can   = newQObject(canHelper)
    global.isotp = newQObject(isoHelper)
    global.uds   = newQObject(udsHelper)
    canHelper.setRxCallback(  global.gotCommFrame, else legacy global.gotCANFrame )
    isoHelper.setRxCallback(  global.gotISOTPMessage )
    udsHelper.setRxCallback(  global.gotUDSMessage )
    tickFunction  = global.tick
    setupFunction = global.setup
    if (setupFunction is callable) setupFunction.call()    // errors logged with line number, not fatal
Stop()/Dispose():
    timer.stop(); clearFilters() on all three helpers; disconnect; delete helpers
```

Important, faithful details:

* The script body is evaluated **before** `host`/`can`/`isotp`/`uds` are installed. Top-level code
  that touches them throws a ReferenceError; only `setup()` can use them.
  Recommendation: install the globals *before* evaluating (a strict superset of the reference
  behaviour - no existing script relies on the failure) but still call `setup()` afterwards.
* `setup`/`tick`/callbacks are resolved **once** at compile time; redefining them later has no
  effect until the next compile.
* `compileScript()` does **not** stop the tick timer. After a recompile with a new script that
  never calls `setTickInterval`, the old interval keeps firing (harmless no-op when `tick` is
  absent). CanYeAye: stop the timer at the start of compile, then let the new `setup` restart it.
* Compile errors are reported as four log lines (header, `Line: n`, message, `Stack:`, stack) -
  keep this shape so users recognize it.
* Deleting a script stops its timer and clears filters; pending queued deliveries must be
  discarded (the reference calls `removePostedEvents` before deleting the helpers to avoid a
  crash). In CanYeAye: unsubscribe from the frame bus first, then dispose the engine.
* Scripts run on the UI thread; a long `tick` blocks the UI.
* One `QJSEngine` per script, so globals are per script and never shared.
## 12.9 Public parameters (the "Public Variables" table)

* `host.addParameter("name")` only records the name (a plain string). The current script's table
  is refreshed by a window-level timer every **1000 ms** (`valuesTimer.start(1000)`; the help
  page says 250 ms - the code is 1 s).
* Refresh (current script only, because the signal is connected/disconnected when the selection
  changes): for each registered name read `engine.globalObject().property(name).toString()`;
  update column 1 of the row whose column 0 equals the name, **unless that cell is selected**
  (so a value being edited is not stomped); create the row when missing. Column 0 items are
  `ItemIsEnabled` only (not editable).
* When the user edits column 1 of a selected row, the window emits
  `updatedParameter(name, text)` -> `ScriptContainer::updateParameter`, which sets the global
  property to a **string**. A numeric parameter therefore becomes a JS string after a manual
  edit; `testingVar++` still works because JS coerces. Keep this behaviour (string assignment)
  for compatibility.
* Only the currently selected script's table is polled; switching scripts clears and rebuilds the
  table.

## 12.10 Window/UI behaviour that the port must preserve

* A list of loaded scripts (file names), one `ScriptContainer` per entry, all running
  simultaneously.
* Buttons: Load, New (random `UNNAMED_<n>.js`), Delete (with confirmation), Save, Save As,
  Revert (restore the last compiled text), Reload from disk (then recompile), Recompile
  (copies the editor text into `scriptText` first), Clear Log, Save Log.
* Shared log list; each entry is prefixed with elapsed milliseconds and the script name.
  Auto-scroll checkbox.
* Editing does **not** affect the running script until Recompile/Reload; `scriptText` is updated
  from the editor when the selection changes and before Recompile.
* A new script is not compiled until Recompile is pressed; a loaded script is compiled
  immediately after loading.
* Optional extras in the reference: the editor is a JS syntax-highlighting widget; F1 opens
  `help/scriptingwindow.md`.

---

# 13. The four example scripts (compatibility targets)

All four must run **unmodified**.

### 13.1 `examples/GetCANandTick.js`

```js
function setup ()
{
    host.log("Example script 1.0");
    host.setTickInterval(20000);
    can.sendFrame(0, 0x7E0, 8, [0x0d, 1, 0, 0, 0, 0, 0, 0]);
    can.setFilter(0x100, 0x700, 0);
}
function gotCommFrame (bus, id, len, data)
{
    host.log("Bus: " + bus + "  id: " + id.toString(16));
}
function tick ()
{
    host.log("TICK!");
}
```

Purpose: send one frame at startup and tick (log) every 20 s; log every received frame whose
`(id & 0x700) == 0x100` on bus 0. Exercises: `host.log`, `host.setTickInterval`, `can.sendFrame`
with a hex-literal array, `can.setFilter`, `Number.prototype.toString(16)`, string concatenation.

### 13.2 `examples/CaptureAllMsgTypes.js`

```js
function setup ()
{
    host.log("Example script 1.0");
    host.setTickInterval(20000);
    can.sendFrame(0, 0x200, 4, [1,30,20,10]);
    uds.sendUDS(0, 0x604, 0x3E, 1, 0, 0, 0);
    can.setFilter(0x100, 0x700, 0);
    uds.setFilter(0x600, 0x7F0, 0);
}
function gotCommFrame (bus, id, len, data)        { host.log("Bus: " + bus + "  id: " + id.toString(16)); }
function gotISOTPMessage(bus, id, len, data)      { host.log("ISOTP bus " + bus + "  ID: " + id.toString(16)); }
function gotUDSMessage(bus, id, service, subFunc, len, data)
{
    host.log("UDS Bus: " + bus + "  ID: " + id.toString(16) + "    Sv: " + service.toString(16));
}
function tick () { host.log("TICK!"); }
```

Purpose: the smoke test for all four callbacks (raw CAN, ISO-TP, UDS) plus TX. Notes for the port:
* `uds.sendUDS(0, 0x604, 0x3E, 1, 0, 0, 0)` - the 7th argument is the number `0`, not an array;
  the helper only logs a warning. `length.toUInt()` is 0, so no elements are read. Jint must not
  throw when a non-array is indexed.
* Three different callback names in one script must all be registered.

### 13.3 `examples/RLEC.js`

Purpose: an "RLEC" (module ID change) challenge/response sequence over raw CAN (not ISO-TP):
send the challenge request `0D 01 ...` to `0x7E0` on bus 0, watch for `0D 01 AA` (security
challenge), compute the complemented response with bitwise ops, then for `0D 02 AA` send
`04 15 <newID>`. `var newID = 0` at the top is the value to change.

```js
var newID = 0;
function setup () { host.log("RLEC ID Changer"); can.setFilter(0x0, 0x0F, 0); can.sendFrame(0, 0x7E0, 8, [0x0d,1,0,0,0,0,0,0]); }
function gotCommFrame (bus, id, len, data)
{
     var dataBytes = [];
     if (len == 8)
     {
         if (data[0] == 0xd && data[1] == 1 && data[2] == 0xAA) { ... can.sendFrame(0, 0x7E0, 8, dataBytes); }
         if (data[0] == 0xd && data[1] == 2 && data[2] == 0xAA) { ... can.sendFrame(0, 0x7E0, 8, dataBytes); }
         if (data[0] == 4 && data[1] == 0x15 && data[2] == 0xAA) { host.log("ID Reprogramming Successful!"); }
     }
}
```

Exercises: module-level `var` (a mutable global), sparse array creation
(`var dataBytes = []` then index assignment), `~` (bitwise NOT -> 32-bit int), `&`, `<<`, `>>`,
loose `==`, `Number.prototype.toString(16)`, `String + number` coercion, and **undefined array
elements** passed to `sendFrame` (`dataBytes[3..7]` are never set in the second branch) - the
host converts `undefined` to `0` via `toInt()`; Jint must do the same (`undefined -> 0`,
`null -> 0`, non-numeric -> 0).

### 13.4 `examples/UpdateParameter.js`

```js
var testingVar = 10;
function setup() { host.addParameter("testingVar"); host.setTickInterval(2000); }
function tick()  { testingVar++; }
```

Exercises: `host.addParameter` with a quoted name, a 2 s tick that mutates a global that the UI
polls, and the round trip user-edit -> `updateParameter` (string assignment) -> `++`.

## 13.5 JavaScript surface actually required

| Feature | Used by |
|---|---|
| `function name (args) { }` declarations at top level | all |
| top-level `var` globals, mutated later | RLEC, UpdateParameter |
| `+` string concatenation with numbers | all |
| `Number.prototype.toString(16)` | all frame loggers, RLEC |
| array literals `[1, 0x0d, ...]` | GetCANandTick, CaptureAllMsgTypes, RLEC |
| array length / index assignment, sparse arrays | RLEC |
| `~`, `&`, `<<`, `>>` (32-bit semantics) | RLEC |
| `==` loose equality, `&&` | RLEC |
| `if`, nested `if` | RLEC |
| method call on an injected host object, mixed argument types (number/string/boolean) | all |
| reading a global property by name from C# (`engine.GetValue`) | parameter table |

Not used: `this`, closures/IIFEs, prototypes, `let`/`const`, arrow functions, template literals,
classes, `JSON`, `Math`, Promises, `setTimeout`, `require`, `print`, exceptions, regex, `Date`.
Keep those available anyway (Jint gives them for free), but **do not** expose browser/DOM APIs
and **do not** change the four argument shapes.

---

# 14. CanYeAye mapping notes

| SavvyCAN | CanYeAye |
|---|---|
| `DBC_MESSAGE`, `DBC_SIGNAL`, `DBC_NODE`, `DBC_VAL_TABLE`, `DBC_ATTRIBUTE`, `DBC_ATTRIBUTE_VALUE`, `DBC_VAL_ENUM_ENTRY` | `CanYeAye.Core.Dbc` records/classes, one file per type; keep `ID`/`extendedID`/`len`/`canFD`/`bitRateSwitch` as first-class members |
| `DBCSignalHandler` / `DBCMessageHandler` (QMap) | `SortedDictionary<string, DbcSignal>(StringComparer.Ordinal)` / `SortedDictionary<uint, DbcMessage>` inside `DbcFile` |
| `DBCFile::loadFile` / `saveFile` | `DbcFileReader` / `DbcFileWriter` (pure, no UI); faults returned as `IReadOnlyList<DbcParseFault>` instead of a message box |
| `DBCHandler` singleton + QSettings restore | `DbcDatabase` registered in DI, backed by CanYeAye's own settings store |
| `Utility::processIntegerSignal` | `SignalCodec.ExtractBits` / `SignalCodec.InsertBits` + `DbcSignal.Decode(frame)` returning a `SignalValue { Raw, Value, IntValue, Text }` |
| `ScriptContainer` + 3 helpers | `ScriptHost` in `CanYeAye.Core.Scripting` with `FrameBus` subscription, `IJsEngine` = Jint; keep `host`, `can`, `isotp`, `uds` member names and argument orders identical |
| `ScriptingWindow` | Avalonia view + `ScriptWorkspace` view model; same buttons/lifecycle |

Cross-cutting constraints from AGENTS.md that apply here: timestamps stay `secs + usecs` (never
`DateTime`); frames are not limited to 8 bytes (`len` up to 254); do not read or write SavvyCAN
registry keys; scripting must run existing SavvyCAN `.js` files unchanged.

---

# 15. Editor windows

The editing surface (`dbc\dbcmaineditor.cpp`, `dbcsignaleditor.cpp`, `dbcmessageeditor.cpp`,
`dbcnodeeditor.cpp`, `dbcnoderebaseeditor.cpp`, `dbcnodeduplicateeditor.cpp`) is ported as:

| Reference | CanYeAye |
|---|---|
| `DBCMainEditor` tree + toolbar + search | `Views/DbcEditorWindow.axaml`, `ViewModels/DbcEditorViewModel.cs` |
| `DBCSignalEditor` + `CANDataGrid` signal view | `Views/SignalEditorWindow.axaml`, `ViewModels/SignalEditorViewModel.cs`, `Controls/DbcBitGrid.cs` |
| `DBCMessageEditor` | `Views/MessageEditorWindow.axaml`, `ViewModels/MessageEditorViewModel.cs` |
| `DBCNodeEditor` | `Views/NodeEditorWindow.axaml` |
| `DBCNodeRebaseEditor` / `DBCNodeDuplicateEditor` | one `Views/NodeBaseWindow.axaml` with a direction flag |
| `DBCFile` mutation helpers | `Core/Dbc/DbcEditor.cs` (add/delete node/message/signal, ID re-key, clone, multiplex links, rebase/duplicate, bit positions) |

Behaviour notes and deviations:

* Edits apply to the shared `DbcFile` immediately and mark it dirty; saving is explicit through
  **Save** in the DBC files window (`DbcFileWriter.Save`), which also asks for a path for files
  that were never saved. SavvyCAN has no save control in the editor either.
* The tree shows nodes → messages → signals, with multiplex hierarchies nested under their
  multiplexor. Double-click opens the matching editor. The context menu offers Edit, Rebase,
  Duplicate and Delete; Delete also answers the Delete key and asks for confirmation.
* Search is the reference's contains + Search/Next/Previous with a "Search Results: x of y" label.
* The node and message editors have no OK button in the reference (live edits, close to finish);
  CanYeAye adds an explicit Close button. The signal editor likewise commits on focus loss / Enter.
* The multiplex form has **one** "Multiplex value" field (`1;2;5-7`, semicolons), matching the
  current `txtMultiplexValues` code rather than the older two-field screenshot.
* The bit grid draws 8-byte blocks with bit 7 leftmost, hatches the current signal green, rings the
  start bit, and fills bits owned by other signals with a stable per-signal colour. Left-click sets
  the start bit; right-click switches editing to the signal that owns the clicked bit.
* Value tables keep a trailing blank row; editing it appends the entry. Row delete is on the grid
  context menu, hex/decimal display on the value column header context menu, exactly as the
  reference.
* Text fields are simplified to underscore-separated identifiers (name/unit/comment/value text),
  matching `QString::simplified().replace(' ', '_')`.
* Colours use `Avalonia.Controls.ColorPicker` rather than `QColorDialog` and write
  `GenMsgForegroundColor` / `GenMsgBackgroundColor` attributes. The missing attribute is created
  on first use.
* Geometry of the editor and its child dialogs persists in the app settings
  (`DbcEditorWindow*`, `DbcSignalWindow*`, `DbcMessageWindow*`, `DbcNodeWindow*`,
  `DbcNodeBaseWindow*`), like the other tool windows.
* Deviation: the signal undo stack (Ctrl+Z) is not ported yet.
