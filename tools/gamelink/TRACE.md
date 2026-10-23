# Logic and property feeds (gamelink `trace` / `prop` / `overlay`)

Two live feeds out of a running game, through FrostyGameLink (127.0.0.1:31500), and an
in-game overlay that shows them.

| feed | shows | hooks |
|---|---|---|
| `trace` | every logic event travelling along a wire | `FullEntityBus::internalFireEventOnConnection`, game+0x73285E0, via its jmp stub game+0x114C860 (finding #53) |
| `prop` | every property value arriving at a listener | `PropertyModificationQueue::internalCallListeners` game+0x1D3780 (its 6 call sites) and `internalRegisterCallListeners` game+0x543E610 (its jmp stub game+0x1D4C30) (findings #56, #57) |

## Reading a line

```
S EventGate[33].Out -> LogicPrefabReferenceObject#32914a11.TriggerUpdate  @AI/BattleAI/Prefabs/Skirmish/PF_Skirmish_AI_Ability_Button_Logic
S InterfaceDescriptor[4].DistanceToTarget -> CompareFloat[3].A = 153.515  @AI/BattleAI/.../PF_Skirmish_AI_Ability_Hunter_Logic
```

- `S` / `C` - server or client realm. `net` - networked event connection.
- `EventGate` - the entity data type (`...EntityData` dropped).
- `[33]` - the instance's index in its EBX file = Frosty's placeholder GUID `00000000-0000-0000-0000-000000000033`.
  `#32914a11` - an exported instance's real GUID (Data1). `trace guid 2` prints full GUIDs, `trace guid 0` none.
- `.Out`, `.TriggerUpdate` - event / field names. Unknown ones stay 8 hex digits; a `~` name is a
  guess from word combinations (see Names).
- `@...` - the blueprint (prefab, sub-world, level) that owns the wire. `trace where 0` hides it.
- `prop`: `Source.Field -> Target.Field = value`. `(+1)` = more than one wire feeds that field.
  `?` = no wire in that blueprint feeds it (set from engine code). `relay` = an internal relay
  between buses, not an entity. Transforms print their position (`at (x, y, z)`).

## Commands

From the CLI: `frostycli game <command>` (e.g. `frostycli game trace on GameModes_Flow`,
`frostycli game follow --prop MosEspa`, `frostycli game status`); `frostycli game send <raw>` for
anything else. Raw gamelink commands:

```
trace on [filter]   trace off   trace [n]   trace status   trace filter <f>   trace clear   trace dump
prop on [filter]    prop off    prop [n]    prop status    prop filter <f>    prop clear
trace pause         trace resume                                (freeze / resume both feeds)
trace guid 0|1|2    trace where 0|1    trace addr 0|1           (apply to both feeds)
overlay on [filter] overlay off   overlay filter <f>   overlay feed events|props|both
wires [--events|--props] <blueprint filter>     (every connection of matching loaded blueprints)
fire    <Type[tag]>.<Event> @<blueprint> [S|C] [all]   (the instance emits the event)
deliver <Type[tag]>.<Event> @<blueprint> [S|C] [all]   (the event arrives at the instance)
inspect <Type[tag]> @<blueprint> [S|C]                  (every field as the live entity sees it now)
write   <Type[tag]>.<Field> = <value> @<blueprint> [S|C] [all]   (a wired field, listeners notified)
```

`fire` calls the engine's own `EntityBus::fireEvent` (game+0x114AB50) for the instance, so every wire
from it runs; `deliver` hands a plain `EntityEvent` to `registerEvent` (game+0x1180770), as a wire
would. Both are queued and carried out inside the event or property hook on the realm's logic thread
(so a feed must be on), never from the command thread. Default realm: server if the instance lives
there. A blueprint placed N times has N entities - the first is used, or all with `all`.

`inspect` lists the instance's reflected fields with the value the entity reads right now - from its
bus where something is wired to or overrides the field (marked `live`), else the EBX value - then its
wire-only fields (an Or's `In1`/`Out`, a Math input: every PropertyConnection into or out of it, read
through the bus) and its last wires in the event buffer. It asks the engine's own
`PropertyReaderBase::set`/`get` (game+0x5442490 / game+0x543AD40) for (bus, instance, field), the same
reader the entity has; an output is read through its first target's reader, which shares its CacheData.

`write` sets such a field through `PropertyWriterBase::set` (game+0x543E700 -> registerWrite) on that
CacheData, so every listener is notified as if the wire had carried the value. Booleans (`true`,
`false`, `toggle`), whole numbers, enums (number or member name), floats, `Vec2/3/4`, and a
LinearTransform's position (rotation kept). A field nothing is wired to is refused: the entity reads
its EBX value directly and a live write can't reach it. The wire's source overwrites the value the next
time it writes. Verified on Cloud City: `CompareBool[d].Bool = false` then `deliver CompareBool[d].In`
answered `OnFalse`, `= true` answered `OnTrue`. Addresses as in ArmchairDevelopers/Kyber's
NativeEntityManager.h, checked by disassembly against this build. Both run on the realm's logic
thread like `fire`, so a feed must be on.

`wires` lists each connection in feed-line form (`E` event, `P` property, no realm or value).
The CLI builds on it: `frostycli game record` saves a feed with timestamps, `game diff` compares
two recordings (first divergence, wires only one run fired), and `game wires --against <recording>`
marks every connection of a blueprint `fired` or `NEVER`.

Filters: comma-separated, case-insensitive; plain terms must match (any), `!` terms must not:
`MosEspa`, `GameModes_Flow`, `OnTrue,!Sound`, `!Phantom,!relay`.

`python gl.py follow [--prop] [filter]` streams the event feed, or the property feed with
`--prop`; Ctrl+C turns it off. The overlay and `follow` can run together.

## Overlay

`overlay on` draws the newest ~22 event lines and ~16 property lines over the game (top left),
refreshed 4 times a second, server lines orange, client lines blue. Display only - it takes no
mouse or keyboard input. After the first `overlay on`, with the game window in front:
**F9** shows/hides it, **F10** pauses/resumes both feeds. (Hotkeys untested as of 2026-10-08.)
It draws from the swap chain's Present (a dxgi vtable patch, not game code) with Dear ImGui
1.78 (MIT, `src/imgui`). DX11 only - in DX12 mode it logs that and stays off.

## Crashes

With a feed on, a crash writes the last 40 logic events and 24 property writes to
`C:\ProgramData\FrostyGameLink\gamelink.log` as `feed:` lines, after the fault report.
`trace dump` writes the same thing on demand. In the harness, `"feeds": true` in
`autotest-args.json` turns both feeds on right after LOADED.

Never earlier: patching the game's code at injection or the main menu stalled the game behind
a fake "activation" window or made it exit, in all four tries. Both feeds refuse to patch until a
level is loaded - `trace on` / `prop on` at the menu answers `deferred` and arms itself when a
level is up.

## Coverage

- Events: complete. Every wire is evaluated on a FullEntityBus; proxy buses (single entities)
  hand outbound events to their parent bus's `fireEvent` and receive inbound ones from a wire
  the parent already fired, so nothing travels a wire unseen.
- Properties: every value write - immediate, batched (queued listener calls) and written-entry
  flushes. Not shown: transform propagation down spatial hierarchies (the queue's "from parent"
  entries, used only by EntityTransformSpace) - that is engine transform plumbing, not wiring.
- Volume: unfiltered, ~7k events/s and ~7k property writes/s. Buffers hold 262k events (~37 s)
  and 131k writes (~20 s) before dropping; filter, read often, or `trace pause`.
- The hook addresses are for this exact build (module size 0xE45C000); gamelink refuses to run
  against anything else, and every patch refuses bytes it doesn't expect.

## Names

`event_hashes_extra.csv` adds to the frostbite-research `field_hashes.csv`:
- `python harvest_hashes.py` - exact names from the exe's strings, the registry and the EBX dumps.
- `python harvest_source.py` - exact names from `event_*` / `fieldhash_*` in the indexed engine source.
- `python harvest_combo.py` - two/three-word CamelCase combinations, marked `~` (guesses: about 1 in 10
  pairs and 1 in 4 triples is a chance hash collision).
