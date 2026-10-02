# ShiinaRio engine notes (GRAND†CROSS "Plus" games)

Research notes for the extractor, the SCN tools (`tools/ScnTools`) and the **▶ Play Story**
player. Facts marked *(verified)* were checked against all 11 games' data or the engine code;
everything else is an inference and says so.

## 1. Archives

Each game folder holds `*_D.WAR` (UI graphics), `*_G.WAR` (CG, sprites, backgrounds; Azu Plus also
keeps voice/BGM here), `*_M.WAR` (BGM, SE), `*_S.WAR` (compiled scripts `.SCN`), `*_T.WAR`
(scenario `.TXT`, Shift-JIS) and `*_V.WAR` (voice `.OGV`). Movies are loose `mv\*.mpg` files.
Script paths use prefixes: `e\` and `b\` and `c\` = images in `_G`, `v\` = voice, `m\` = BGM,
`se\` = sound effects, `mv\` = movies, `d\` = `_D`, `p\` = scenario TXT, `t\` = SCN.

## 2. S25 layered images *(verified)*

Frames are grouped by their slot number in the S25 header:

| Slots | Meaning |
|---|---|
| 0-99 | base pictures (alternatives, all with the same size and offset) |
| 100-199, 200-299, ... 900-999 | layer 1, 2, ... (mouth, eyes, arms, effects - meaning depends on the file) |
| 1000+ | not part of the picture (hit masks, ...) |

Each frame is drawn at its own screen offset. UI sprite sheets (`SYSTEM`, `RUBY`, `THSAVE`...) are
not layered: rejected because their "bases" differ in size/offset or slots run across a hundred
boundary (99 and 100 both used).

Which combinations are shown is written in the scenario: `$L_MONT,<plane>,<file>,x,y,?,m,<base>,<l1>,<l2>,...`
(also `$L_CHR ...,m,...` for sprites). Value v at position k = slot k*100+v; -1 or a missing value
switches the layer off. All ~4000 such commands in the 11 games point at existing slots. Zoomed CGs
(`EV02_02L`, `..M`) are never named in the script; the engine swaps them in, and they share the
slot table of the unzoomed file, so they use its combinations. Oreimo's sprite `KIR.S25` is
changed with `$L_MONT,1,,0,0,0,M,101` (upper-case M, no file): these are expression codes,
looked up in `MONTBL.BIN` (in `_T`) *(verified)*:

- 100,000 entries of 8 bytes, then a string table (`st\kir.s25`, `f\0.s25`, `st\kir_L.s25`).
- Entry = u16 offset of the S25 name in the string table, u16 offset of a face picture name,
  u32 with six 5-bit slot values (base, layers 1-5; 31 = layer off). All `FF` = unused code.
- Oreimo: 101-107 = mouth 100 + eyes 200-206, 121-127 = the same with blush (300),
  151-157 / 171-177 = open mouth 101; 2xx = the same on the zoomed `kir_L.s25`.

About 3,700 eye/mouth frames are never used by any script combination. Possibly blink / lip-sync
frames switched by the engine, or simply unused art - **unknown**.

## 3. Scenario TXT

Lines are commands `$NAME,args`, speaker lines `【name】`, dialogue `「...」`, narration, or
comments `;`. Every non-empty text line is one message (one click); no message spans two lines.
Comments carry the writers' notes (`;【...】` = scene title, `;/// 差分：... ///` = CG variant
wanted here, `;※i ...` = director's instructions, `;;$L_MONT...` = disabled command).
`①` in the text is the heart gaiji (`GAIJI.S25` in `_D`). Oreimo Plus: 35 files, ~15,000
lines, 2,842 messages. Command use in Oreimo Plus, as the story player (`Player/`) reads them:

| Command | Uses | Arguments and meaning |
|---|---|---|
| VOICE | 1116 | `file, ?` - voice of the next message |
| DRAW_EX | 592 | `kind (0/1/2/47), rule S25 or empty, ms, wait` - show the prepared picture: cross-fade, or wipe along the rule mask (dark areas first) |
| L_BG | 352 | `file, x, y, ?` - background plane 0; **also clears planes 1+** (face overlays like `ev01_01` would otherwise stay) |
| L_CHR | 372 | `plane, file (empty = clear), x, y, ?[, m, slots...]` |
| A_CHR | 334 | `code, plane, ...` - plane animation started by the next DRAW, see below |
| DRAW | 200 | show the prepared picture at once (or with the A_CHR 152 cross-fade) |
| WAITA | 152 | wait until animations and a non-looping movie end *(a movie with loop 0 is followed by WAITA)* |
| WAIT | 120 | `ms` |
| WINDOW | 118 | `0` = hide the message window (the next message shows it) |
| EFECT | 67 | `n` (0, 1, 2, 12) - screen effect in `EFCLIB.SCN`; **not decoded**, shown as shakes / a flash |
| L_MONT | 54 | `plane, file, x, y, ?, m/M, ...` - section 2 |
| EX | 36 | `9,0,dir,width` / `9,1,layer,file` / `9,2,speed` / `9,4` = background scroll setup, picture, start, end (dir 1 = picture moves right, the character walks left; speed unit unknown); `10,2,var,value` = set `_Dvar`; `2,0` = wait for a key; `4` = ? |
| L_MOVIE / WAIT_L_MOVIE | 36 / 18 | `plane, mv\file.mpg (empty = stop), loop, ?` / `plane` = wait for the end of the current round |
| MUSIC / MUSIC_FADE | 31 / 32 | `file, ?` (empty = stop) / `[ms]` |
| SE / SE_FADE | 25 / 7 | `file (empty = stop), plays (0 = loop), channel` / `ms, channel` |
| PRELOAD | 18 | `file` - cache hint |
| LABEL / CJUMP / EVENT_BLOCK | 4 | `n` / `_D710==0, label` / `1, label` = skipping inside the block jumps to the label |

`A_CHR` codes *(guesses from the scripts' context unless marked)*: 00 stop and reset the plane;
01 / 06 looping bounce / sway (`x, y, period`); 40 screen rect the plane is drawn into, 41 source
rect shown in it (zoom / pan start, `x, y, w, h`), 42 pan / zoom the source rect (`x, y, w, h,
ms, wait`) - checked against 1600x1200 CGs; 62 fade in through a rule (`rule, ms`); 114 / 128
move to `x, y` (`ms, wait`); 150 fade out, 151 fade in (`ms, wait`); 152 cross-fade the plane's
new picture (`ms`, after an expression change); 90 / 91 around expression changes (lip sync?).

The message window is `SYSTEM.S25` (in `_D`) slot 0 at (70,451). Name plates are slot 29 + n,
where n comes from `NWINTBL.BIN` (0x24-byte entries: Shift-JIS name, u32 n): 俺 = 30, 桐乃 = 31,
ＰＣ = 32. Route menu buttons are slots 400 + 10·i (+1 highlighted), text choices use slot 311
with the text drawn on it.

Every resource Oreimo Plus references exists (1,515 files).

## 4. SCN bytecode *(verified on all 11 SRC_MAIN.SCN, 0 decode errors)*

The engine interprets **TXT at run time**; the SCN files are the engine's own script code:

| File | Role |
|---|---|
| START.SCN | engine library: TXT command implementations, text system, UI, saves, CG list |
| SRC_MAIN.SCN | game flow: which TXT runs, choice menus, ending, staff roll |
| PLAUNCH.SCN | script number -> TXT file (save loading) |
| LAUNCH.SCN | event-number dispatcher |
| TOPMENU.SCN | logo, caution, title screen |
| EFCLIB.SCN | visual effects, calls GDI (`gdi32.dll`) directly |

**Instruction** = u16 opcode + operands. **Operand** = type byte + payload (self-delimiting):

| Type (low bits) | Payload | Meaning |
|---|---|---|
| 0x02/0x06/0x08/0x0A/0x0C/0x0E (+1 = dereference) | u16 index | variable in one of six areas (printed g/s/l/a/f/b) |
| 0x04 (0x05 = deref) | i32 | immediate |
| 0x10 | NUL-terminated string | string literal |
| 0x11 | optional '.', NUL-terminated string | expression text, e.g. `_D780|(1<<_D990)` |
| 0x12 (0x13 = deref) | name up to NUL or `}` | named variable |
| flag 0x80 | | address relative to the script base (jump targets are `0x84 <i32>`) |
| flag 0x40 | | take the address (GetVarAdr) |

Opcode tables (`tools/ScnTools/tables/ops_*.tsv`) give each opcode's operand signature:
`V` = operand, `bN` = N raw bytes. Variable-length instructions handled by hand:

| Opcode | Layout |
|---|---|
| 0x3CE / 0x3CF | global / local declarations: u16 count + count operands |
| 0x283 callmod | V V, u16 argc, argc operands |
| 0x259 switch | u32 table end, index V, case targets V... up to the end |
| 0x209 case | u32 address of the index operand (inside a 0x208), u32 target, values V... , 0xFF, u32 next case |
| 0x2DB | printf-like message: operands until a 0xFF byte |
| 0x1F4 if | V, cmp byte (0 == 1 != 2 >= 3 > 4 <= 5 < 6 & 7 \|, unsigned), V, u32 target taken when the condition is FALSE |

No fall-through after 0x0000 (end), 0x0258 (goto), 0x026C (ret), 0x026D (ret value), 0x0209, and
`if` with constant operands that is always false. Code embeds data after jumps: choice texts,
u32 address tables (`f = idx<<2; f += TABLE; f = *f; goto f`), callback records, even native x86.

Named opcodes (handler read): 0000 end, 0001 loadmod, 01F4 if, 0208 caseidx, 0209 case,
0258 goto, 0259 switch, 0267 gosub, 026C ret, 026D retv, 0283 callmod, 02D1 strcpy,
02D5 lea (address of string), 0302 load (`dst = *src`), 0303 store, 0316/0317 stack alloc/free,
038E mov (`src, dst`), 038F addr, 0393 add, 0394 sub, 0396 and, 0397 or, 0399 neg, 039A shr,
039B shl, 03CE global, 03CF local, 03DE eval.

### Engine builds

Opcode tables come from the unpacked executables by `ScnTools opscan`: emulate the dispatcher
for all 65,536 values, then count operand reads (GetVar / SetVar / GetVarAdr) and raw reads of
the script pointer `[ctx+10h]` along each handler.

| | Sena Plus dump | Oreimo Plus dump |
|---|---|---|
| Interpreter | FUN_00428c30 | FUN_00423940 |
| GetVar / SetVar / GetVarAdr | 416000 / 415ab0 / 415e60 | 414e30 / 4148e0 / 414ca0 |
| Context register in handlers | EBP | EBX (EDI = &ctx->pc) |
| Valid opcodes | 1,658 | 704 |

Of the 704 opcodes both builds know, 699 have the same signature; 0x02DB (varargs, overridden),
0x04E7, 0x05D5, 0x05D6, 0x0C30 differ (Oreimo is the older engine). Either table decodes Oreimo's
SRC_MAIN identically; use the game's own table when one exists.

Decoding coverage of Oreimo's SCN: LAUNCH 100 %, PLAUNCH 97 %, EFCLIB 95 %, SRC_MAIN 83 % (rest is
data), START 54 %, TOPMENU 56 % - START and TOPMENU still have unhandled special instructions.

## 5. Oreimo Plus game flow (SRC_MAIN.SCN)

1. Start: `b[250]` (scenario number) indexes a 40-entry address table - resumes a saved game.
2. Run a TXT: set `filename = "p\oreXX.txt"`, `msg_no`, `msg_gno`, `save_no`, then `gosub 240`.
3. `ore01.txt`, then the menu: `callmod 203` with the 8 choice texts (頑張って考える, 公園に行く,
   コンビニに行く, 桐乃の学校を見学, 桐乃の部活を見学, ラブホに行きたい, レンタルルームへ, アキバへ行く);
   `a[780]` is the bitmask of routes already played (the menu shows only the rest), the
   result `g$sel` goes to `a[990]`, `eval "_D780|(1<<_D990)"` marks it, `switch` jumps to the route.
4. Each route runs `oreNN-01/-02/-03.txt` and returns to the menu. Routes 04, 06, 07 have a
   choice 中に出す / 外に出す after `-02` leading to `-02b` / `-02c` (`-02d` also exists).
5. When `a[780] == 255`: `ore10.txt` (ending), staff roll (`staff0-5.s25` + `vor02-07.ogv`,
   native-code effect), then back to `topmenu.scn`.

## 6. Play Story - status

`GrandCrossExtractor/Player/` plays Oreimo Plus from beginning to end (all 35 files, every
choice), reading the archives of the game folder:

| File | Role |
|---|---|
| `StoryFlow.cs` | game flow from SRC_MAIN (opening, route menu, choices, ending) and the chapter list |
| `ScenarioScript.cs` | TXT parser |
| `GameData.cs` | archives, lookup by stem, S25 cache, MONTBL / NWINTBL / SYSTEM.S25 |
| `Stage.cs` | 800x600 planes, snapshot-based DRAW / DRAW_EX (cross-fade, rule wipe), plane animations, scroll, movie (WPF MediaElement plays the MPEG-1 files) |
| `AudioEngine.cs` | NAudio mixer: BGM, voice, SE channels, loops, fades |
| `PlayerWindow.*` | text window, name plates, choices, backlog, auto / skip, chapter list |

Approximated: A_CHR codes (above), EFECT, the scroll speed, DRAW_EX kinds. Not done: saves,
ruby, the engine's own lip sync / blinking, other games' flows (each needs its SRC_MAIN read).

## 7. Tools

```
dotnet run --project tools/ScnTools -- opscan oreimoplus <OREIMOPLUS_dump_SCY.exe> ops.tsv
dotnet run --project tools/ScnTools -- dis tools/ScnTools/tables/ops_oreimoplus.tsv out.txt SRC_MAIN.SCN
```
