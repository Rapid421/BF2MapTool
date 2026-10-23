# FrostyCLI

A headless command-line version of Frosty Editor for Star Wars Battlefront II (and SWBF1). Instead of clicking through the Frosty GUI, you run commands or write JSON pipeline scripts that execute mod operations automatically.

**Ambient audio/SFX:** `audio inventory`, `inspect`, `dependencies`, `export`, `import`, and `place`
provide lossless SoundWave packages and destination-game sound placement. Packages preserve channels,
variations, start/loop/stop segments and seek data. See [Audio porting](docs/audio-porting.md) for the
cross-game workflow, supported formats, trigger wiring and save/reopen verification.
**Sound graphs:** `audio graph-inspect`, `graph-validate`, `graph-export`, `graph-plan`
and `graph-import` preserve graph structure and apply explicit destination schema/reference
mappings. See [Graph migration](docs/audio-graphs.md); compiled topology is retained, not regenerated.

**Current state:** the core mod pipeline is operational and validated on **both SWBF2 and SWBF1**. You can inspect, export, search, and make scripted changes to assets — set scalar properties, set cross-asset references, **add/remove list elements**, create/clone/delete/revert assets, edit level entities, scaffold SubWorld structures, edit MeshVariationDatabase entries, and localized strings. You can import textures (2D / cube / 2D-array / volume), meshes (rigid/composite, skinned via `--skeleton`, and new-from-template), sounds (PCM, or compressed EALayer3 when ffmpeg is present, into any runtime variation), and resource-backed assets (AtlasTexture/SvgImage/IesResource, replace or new-from-template); import raw Res/Chunk data; and build, diff, and apply `.fbmod` mods — including running per-type **custom action handlers** on both build and apply (ShaderBlockDepot out of the box; localization / network-registry / mesh-variation handlers via `--handler-plugins`). You can list bundles, add assets (with their res+chunks via `--with-resources`), and inspect blueprint connection wiring. Remaining gaps versus the GUI are mostly visual/interactive plus connection *authoring* and from-scratch mesh authoring — see [What's not done yet](#whats-not-done-yet).

---

## Setup

### Requirements
- Windows 10/11 x64
- [.NET Framework 4.8 runtime](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) — already installed on most Windows machines
- Star Wars Battlefront II installed (or SWBF1 for SWBF1 work)
- A Frosty install compatible with the bundled SDK DLLs — see [DEPENDENCIES.md](DEPENDENCIES.md) (the release bundles the **FE‑1063 (sersavagefork)** Frosty SDK; other builds may differ)

### Install (recommended: download a release)
1. Grab `frostycli-vX.Y.Z.zip` from the repo's **Releases** page.
2. `Expand-Archive frostycli-vX.Y.Z.zip -DestinationPath C:\Tools\frostycli`
3. Run it — the folder is self-contained (exe + all DLLs + `Adapters/` + `thirdparty/`):
   ```
   C:\Tools\frostycli\FrostyCLI.exe man
   C:\Tools\frostycli\FrostyCLI.exe --game-path "C:\Program Files (x86)\Steam\steamapps\common\STAR WARS Battlefront II" ls s2/
   ```
No build step or extra downloads needed. See [Config & environment](#config--environment) to stop typing `--game-path`.

### Build from source (alternative)
```
git clone https://github.com/alexiahartzell/frostycli
dotnet build FrostyCLI.sln -c Release -p:Platform=x64
```
Output: `FrostyCLI/bin/x64/Release/FrostyCLI.exe` (self-contained — the exe needs the DLLs next to it). `package.ps1` produces the release zip. `setup.ps1` only matters if you're refreshing the bundled `lib/` DLLs from a different Frosty build.

### Basic usage
Every command needs to know where your game is installed. Pass `--game-path` each time:

```
FrostyCLI.exe --game swbf2 --game-path "C:\Program Files\EA Games\STAR WARS Battlefront II" <command> [args]
```

For SWBF1:
```
FrostyCLI.exe --game swbf1 --game-path "C:\Program Files\EA Games\STAR WARS Battlefront" <command> [args]
```

…or stop repeating it — see **Config & environment** below.

### Config & environment
So you don't pass `--game-path` on every call, the CLI resolves unset options from (in precedence order) the explicit flag → a `frostycli.json` in the working directory → environment variables:

- **Env:** `FROSTYCLI_GAME_PATH_SWBF2` / `FROSTYCLI_GAME_PATH_SWBF1` (chosen by `--game`).
- **`frostycli.json`** (working dir, then exe dir):
  ```json
  {
    "game-path": "C:/Program Files (x86)/Steam/steamapps/common/STAR WARS Battlefront II",
    "project": "cryo-chamber.fbproject",
    "handler-plugins": "C:/.../FE-1063 (New)/Plugins"
  }
  ```
  With this in your mod folder, `frostycli ls s2/...` just works, and a `project` set here is **auto-loaded** so subsequent commands operate on it. (`--game` itself stays a flag; default `swbf2`.)

### Help & completion
- `frostycli man` lists every command; `frostycli man <command>` shows its usage. `frostycli <command> --help` (or `-h`) routes to the same.
- Tab-completion: dot-source `completions/frostycli.ps1` (PowerShell) or `source completions/frostycli.bash` (bash) to complete command + subcommand names.

### Global flags
| Flag | Description |
|---|---|
| `--game swbf1\|swbf2` | Which game to load (default: swbf2) |
| `--game-path <path>` | Path to the game install folder |
| `--handler-plugins <dir>` | Load the curated merge-handler plugins (localization / SWBF2-merger incl. network registry) from an FE editor `Plugins` folder, so `mod build`/`mod apply` merge those types |
| `--json` | Output results as JSON instead of a table |
| `--quiet` | Suppress progress output, only show errors |
| `--dry-run` | Mutating commands validate + report what they *would* do, but skip the write. A pre-write sanity check (asset/property/target resolves), not a full simulation. |

---

## Commands

Run `frostycli man` for a one-line synopsis of every command, or `frostycli man <command>` for that command's usage. (`man` needs no game — it's instant.)

### List all commands — `man`

```bash
FrostyCLI.exe man              # every command + one-line synopsis
FrostyCLI.exe man bundle       # usage detail for one command
```

The list is generated from the live command registry, so it never goes stale.

### Browse assets — `ls`

List EBX assets in the game by path prefix or type.

```
# List everything under s2/characters/heroes/
FrostyCLI.exe --game-path "..." ls s2/characters/heroes/

# List only SoldierBlueprint assets
FrostyCLI.exe --game-path "..." ls --type SoldierBlueprint

# Just count matches
FrostyCLI.exe --game-path "..." ls s2/levels/cloudcity_01 --count
```

---

### Inspect an asset — `ebx dump`

Dump the full data of any EBX asset as readable JSON. This is the main tool for understanding what's inside a game asset.

```
# Dump Han Solo's blueprint to the console
FrostyCLI.exe --game-path "..." ebx dump s2/characters/heroes/hero_han

# Save to a file
FrostyCLI.exe --game-path "..." ebx dump s2/characters/heroes/hero_han --out han.json

# Fast dump without resolving asset references to paths
FrostyCLI.exe --game-path "..." ebx dump s2/characters/heroes/hero_han --no-resolve

# Dump ONE partition object by its exported instance GUID (shallow; internal refs are named, not expanded).
# Use this on huge assets such as Gameplay/SoldierStateMachine/DefaultSoldierStateMachine (~45 MB), where
# --deep serialises the whole graph.
FrostyCLI.exe --game-path "..." ebx dump Gameplay/SoldierStateMachine/DefaultSoldierStateMachine --object 586f6c58-7578-2694-53df-dddef83b9207

# Dump a raw EBX partition from disk, e.g. a payload pulled out of a .fbmod (CAS-compressed or plain)
FrostyCLI.exe --game-path "..." ebx dump --file streamedlevel.bin --deep --out streamedlevel.json
```

The JSON output uses these special fields:
- `__type` — the Frosty type name of the object
- `__guid` — the object's instance GUID
- `__path` — for cross-asset references (PointerRefs), the resolved EBX path of the target
- `__fileGuid` / `__classGuid` — raw GUIDs on cross-asset references
- `__targetType` / `__targetName` / `__targetGuid` — on an internal ref that is not expanded (no `--deep`), what it
  points at inside the same asset

---

### Read a specific property — `ebx get`

Read one property value from an EBX asset using a dot-path.

```
FrostyCLI.exe --game-path "..." ebx get s2/characters/heroes/hero_han
FrostyCLI.exe --game-path "..." ebx get s2/characters/heroes/hero_han --format xml
```

---

### Search across all assets — `ebx search`

Find assets where a specific property has a specific value. Searches across all ~100k game assets (builds a local cache on first run — takes a few seconds).

```
# Find all SoldierBlueprint assets where Name is "han_solo"
FrostyCLI.exe --game-path "..." ebx search --prop Name --value han_solo --type SoldierBlueprint

# Regex search — find assets where MaxHealth is 150 or 140
FrostyCLI.exe --game-path "..." ebx search --prop MaxHealth --value "1[45]0" --regex

# Limit results and save to file
FrostyCLI.exe --game-path "..." ebx search --prop Name --value foo --limit 20 --out results.json
```

Options: `--prop` (required), `--value` (required), `--type` (filter by type first — use this for speed), `--regex`, `--limit N` (default 100), `--out <file>`

---

### Find what references an asset — `ebx refs`

Find all assets that contain a pointer to a given asset. Useful for understanding how assets are connected. The reference index builds automatically on first use (several minutes — it reads every EBX in the game) and is then cached, so subsequent runs are fast.

```
# What references Han Solo's blueprint?
FrostyCLI.exe --game-path "..." ebx refs s2/characters/heroes/hero_han

# Look up by raw GUID instead of path
FrostyCLI.exe --game-path "..." ebx refs --guid 82f196f5-ba34-4732-8b72-fb547f581497

# Traverse 2 hops out, save to file
FrostyCLI.exe --game-path "..." ebx refs s2/characters/heroes/hero_han --depth 2 --out refs.json
```

Options: `<path>` OR `--guid`, `--depth N` (default 1; -1 = unlimited), `--limit N` (default 1000), `--out <file>`

---

### Compare an object against shipped data — `ebx diff-shipped`

Takes one object and compares every field against the shipped population of its own type, then
reports where you disagree with the game.

```bash
FrostyCLI.exe --game-path "..." --project my-mod.fbproject   ebx diff-shipped Levels/MP/Kashyyyk_01/GameModes   --object e2da4a10-94cb-4bab-9bd9-d319c872c5fa
```

```
SubWorldReferenceObjectData  e2da4a10-94cb-4bab-9bd9-d319c872c5fa  in Levels/MP/Kashyyyk_01/GameModes
compared against 337 shipped instance(s) from 400 asset(s) scanned

  UNANIMOUS-DISAGREEMENT   BundleHeap.AllowGrow
      ours:    False
      shipped: 337/337 True

Result: 60 field(s) compared, 1 worth attention
```

This exists because none of these faults error. The data loads, the feature silently does nothing,
and the only way to find it has been to diff one object against shipped data by hand.

**The population is always shipped data**, read with `getUnmodifiedData` even for assets the project
has edited. Comparing against the project would let an object that was broken the same way as its
neighbours come back clean — which is exactly how these faults survive review.

#### Two kinds of finding

A **distribution** check catches a field the shipped population agrees on and you do not. Verdicts,
most serious first: `unanimous-disagreement`, `disagreement` (shipped is split and you match no
value), `rare` (you match under 10% of it). `matches` and `per-instance` are not shown in the table.

A **derivation** check catches the rest. A field whose shipped values are *all different* is not
therefore free: `Flags` on a placed reference object is per-instance unique and still constrained —
across the shipped population its low 25 bits equal the object's own exported GUID. A duplicated
object that keeps its source's `Flags` shares a sub-level identity with it, and a distribution check
cannot see that, because "unique" and "wrong" look identical.

```
  DERIVED-VIOLATION        Flags
      ours:    94264615
      shipped: 337 distinct values, one per instance
      note:    all 332 shipped instances satisfy (value & 0x01FFFFFF) == (instance GUID's first four
               bytes & 0x01FFFFFF); ours does not, so it names another object's sub-level identity
```

The derivation is established **from the population, never assumed**. Measured over the shipped
corpus it holds for 1318/1318 exported `SubWorldReferenceObjectData` and is cleanly all-or-nothing
per type — 100% on `SpatialPrefabReferenceObjectData` (14357/14426) and `StaticModelEntityData`
(12267/12267), 0% on `ObjectBlueprint`, `LayerData` and `LogicPrefabBlueprint`. Asserting it where it
does not hold would flag every correct object. Non-exported instances are skipped, because the rule
cannot apply to them (a `LayerReferenceObjectData` is not exported).

Note that only the **low 25 bits** are derived. The top 7 are real flags and they vary across shipped
data, so regenerating the whole word would clobber them.

#### Options

| Option | Meaning |
|---|---|
| `--object <selector>` | required — instance GUID, `TypeName`, or `TypeName[N]` |
| `--type <TypeName>` | compare against a different type than the object's own |
| `--scan <path-prefix>` | only look in assets under this path |
| `--all` | scan every EBX asset instead of same-root-type ones (slow, complete) |
| `--max-assets N` | assets to scan (default 400) |
| `--max-instances N` | shipped instances to collect (default 500) |
| `--max-fields N` / `--max-depth N` | table rows (default 40) / struct nesting (default 4) |
| `--format table\|json` | `--json` is the same as `--format json` |

By default the search is narrowed to assets whose **root type** matches the one you are asking about,
because that is where instances of a type actually live; scanning all 155k EBX assets costs minutes to
reach the same verdict. The report always states how many assets were scanned and how many instances
were found, so a bounded sample is never mistaken for the whole population.

---

### Set a property — `ebx set`

Change a scalar value on an EBX asset. Requires a loaded project.

```
# One invocation: load the project, edit, write it back
FrostyCLI.exe --game-path "..." --project my-mod.fbproject --save \
  ebx set s2/characters/heroes/hero_han --prop MaxHealth --value 200

# Or collect this run's edits into a new project
FrostyCLI.exe --game-path "..." --save-as my-edits.fbproject \
  ebx set s2/characters/heroes/hero_han --prop MaxHealth --value 200
```

**A project lives only as long as the process.** Every write command mutates the loaded project
in memory, and that is discarded at exit unless it is written back — so a `project load` in one
invocation followed by an edit in the next changes nothing. `--save` writes the loaded project
back to its own file after the command succeeds; `--save-as <file>` writes it somewhere new, and
starts a fresh project when none is loaded. Neither saves if the command failed, and both report
instead of writing under `--dry-run`.

The `--prop` argument uses dot-path syntax: `Components[0].MaxHealth` traverses into arrays.

**Note:** `ebx set` overwrites a scalar value (number, string, bool, enum) at an existing path — including an existing list index like `Components[0].MaxHealth`. To set a cross-asset reference use `ebx set-ref`; to add/remove list elements use `ebx array-add` / `ebx array-remove`.

---

### Add / remove list elements — `ebx array-add`, `ebx array-remove`

Append to or remove from a list property. `ebx set` can only overwrite an existing index; these change the list's length. Requires a loaded project.

```
# Append a string/number/enum element (coerced from --value)
FrostyCLI.exe --game-path "..." ebx array-add s2/foo/bar --prop SubtitleStringIds --value 0x12AB34CD

# Append a default-constructed struct element (then set its fields with ebx set)
FrostyCLI.exe --game-path "..." ebx array-add s2/foo/bar --prop Components

# Append a specific (polymorphic) object type
FrostyCLI.exe --game-path "..." ebx array-add s2/foo/bar --prop Components --type SomeComponentData

# Remove the element at index 3
FrostyCLI.exe --game-path "..." ebx array-remove s2/foo/bar --prop Components --index 3
```

Options for `array-add`: `<path>`, `--prop <list-path>` (required), `--value <v>` (for primitive/string/enum/CString elements) **or** `--type <TypeName>` (construct a specific object element); with neither, a struct/object element is default-constructed. Options for `array-remove`: `<path>`, `--prop <list-path>` (required), `--index <n>` (required).

**Note:** appending an empty `PointerRef` element then wiring it isn't supported in one step — add the element, then use `ebx set-ref` on `prop[newIndex]`.

`--create` builds the objects a `--prop` path needs in order to descend, and grows a list to reach an
index past its end — see [`--create`](#build-a-missing-chain-along-a-path----create).

---

### Clone an asset — `ebx clone`

Deep-copy an EBX asset to a new path with new GUIDs. Requires a loaded project.

```
FrostyCLI.exe --game-path "..." ebx clone s2/characters/heroes/hero_han --as s2/characters/heroes/hero_han_custom
```

---

### Set a cross-asset reference — `ebx set-ref`

Point a PointerRef property at a different asset. This is how you wire one asset to another — e.g. redirect a blueprint to use a different mesh asset. Requires a loaded project.

```
FrostyCLI.exe --game-path "..." ebx set-ref s2/objects/foo/bar \
  --prop Components[0].Mesh \
  --target s2/objects/architecture/bespin/wall_01
```

Options: `<path>`, `--prop <dot-path>` (required), `--target <ebx-path>` (required), `--class-guid <guid>` (optional — override the target's class GUID when it can't be inferred), `--create` (build missing hops along `--prop`; see below).

**Which object inside the target asset?** By default the target asset's **root**, which is right
almost always. It is wrong when the member declares what it points at and the root is not that
thing: `SubWorldInclusionSetting.Criterion` declares a `SubWorldInclusionCriterion`, while
`Gameplay/GameModes/GameModeInclusionSettings`' root is a `WorldPartInclusion` *container* holding
the `WorldPartInclusionCriterion` that shipped data actually points at. Writing the root there
serializes cleanly, loads, and addresses a container where the engine expects a criterion — so the
gate never applies and nothing says why.

So when the member records a target type and the root isn't it, `set-ref` looks for an exported
object in that asset which *is* (matched by assignability — shipped data carries subclasses). Exactly
one, it uses it and says so; several, it refuses and lists them, because any pick would be a coin
flip; none, it warns and keeps the root. `--class-guid` always overrides.

---

### Build a missing chain along a path — `--create`

`ebx set`, `ebx array-add` and `ebx set-ref` accept `--create`, which builds the objects a dotted
path needs in order to descend.

Gating a sub-world needs three partition objects joined by internal `PointerRef`s:

```
SubWorldReferenceObjectData.InclusionSettings → SubWorldInclusionSettings
   .Settings[0]                               → SubWorldInclusionSetting
      .Criterion                              → (external) GameModeInclusionSettings
      .EnabledOptions[0]                       = CString
```

Every hop is makeable one command at a time — but only if you already know each intermediate
object's GUID, and the command that creates one (`set-ref --target-object new:`) never reports the
GUID it made. So the chain could not be written from a script at all. With `--create` it is three
ordinary commands:

```bash
frostycli ebx array-add Levels/MP/Kashyyyk_01/GameModes --object <sub-world-guid>   --prop InclusionSettings.Settings --type SubWorldInclusionSetting --create

frostycli ebx set-ref Levels/MP/Kashyyyk_01/GameModes --object <sub-world-guid>   --prop InclusionSettings.Settings[0].Criterion   --target Gameplay/GameModes/GameModeInclusionSettings

frostycli ebx array-add Levels/MP/Kashyyyk_01/GameModes --object <sub-world-guid>   --prop InclusionSettings.Settings[0].EnabledOptions --value ModeBR
```

**The type is never guessed.** The generated SDK records it on each member as
`EbxFieldMeta.BaseType` — `Pointer` members name what they point at, `Array` members name their
element type. Where that is missing, or names an abstract type, `--create` refuses and tells you to
name a concrete one with `ebx set-ref --target-object new:<TypeName>`. An object of the wrong class
loads happily and resolves to nothing, so a guess here would be indistinguishable from success.

Created objects are given an instance GUID and added to the partition. Without both, an object is
unreachable from the root, and EBX serialization keeps only the reachable graph — so it is silently
dropped on save and the reference meant to reach it addresses a GUID that exists nowhere.

Three things it will not do:

| | |
|---|---|
| create anything without `--create` | a typo in a path stays an error rather than becoming a new object nobody asked for |
| create at the **leaf** | that `PointerRef` is what `set-ref` overwrites; replacing it would discard the reference you are about to write |
| grow a list that is already long enough | it would append elements you did not ask for |

---

### Create a new asset — `ebx create`

Create a brand-new EBX asset from scratch by type name. Requires a loaded project.

```
FrostyCLI.exe --game-path "..." ebx create s2/objects/custom/my_blueprint --type SoldierBlueprint
```

Options: `<path>`, `--type <TypeName>` (required — a Frosty/Frostbite type name).

---

### Delete a modified asset — `ebx delete`

Remove an asset that was added to the project. Requires a loaded project.

```
FrostyCLI.exe --game-path "..." ebx delete s2/objects/custom/my_blueprint
```

---

### Revert an asset — `ebx revert`

Revert a modified asset back to its base game state. Requires a loaded project.

```
FrostyCLI.exe --game-path "..." ebx revert s2/characters/heroes/hero_han
```

---

### Locate an asset — `where`

Resolve an asset reference to its canonical path/GUID/type — by exact path, by **file GUID** (e.g. a `__fileGuid` from an `ebx dump`), or by case-insensitive **substring** (lists matches):

```
FrostyCLI.exe --game-path "..." where hero_han                                  # substring -> matching paths
FrostyCLI.exe --game-path "..." where 82f196f5-ba34-4732-8b72-fb547f581497      # GUID -> the asset's path
FrostyCLI.exe --game-path "..." where s2/characters/heroes/hero_han             # exact -> confirm + show type/GUID
```

Options: `<name-or-guid>`, `--limit N` (max substring matches, default 50). Handy for turning a partial name or a GUID into the exact path the other commands expect.

---

### Export a mesh or texture — `export`

Export a single asset to a file.

```
# Export a mesh as FBX
FrostyCLI.exe --game-path "..." export s2/objects/soldier/common/common_soldier --out soldier.fbx

# Export as OBJ
FrostyCLI.exe --game-path "..." export s2/objects/soldier/common/common_soldier --out soldier.obj --format obj

# Export a texture as DDS
FrostyCLI.exe --game-path "..." export s2/textures/characters/heroes/hero_han_d --out han_diffuse.dds
```

---

### Bulk export — `export-batch`

Export many assets at once.

```
# Export all Cryo Chamber meshes to a folder
FrostyCLI.exe --game-path "..." export-batch --filter xp2/objects/cryo --out ./cryo-exports/

# Export only texture assets matching a prefix
FrostyCLI.exe --game-path "..." export-batch --filter s2/textures/characters --type TextureAsset --out ./textures/

# Keep going even if some fail
FrostyCLI.exe --game-path "..." export-batch --filter s2/levels --out ./levels/ --on-error continue
```

Options: `--filter <prefix>`, `--type <type>`, `--out <dir>` (required), `--format fbx|obj`, `--on-error fail|continue`, `--quiet`

---

### Import a texture — `import texture`

Replace an existing TextureAsset's pixels from an image file. DDS files are read directly; PNG/TGA/HDR are converted to DDS first (via the bundled `thirdparty/dxtex.dll`). Requires a loaded project.

```
# Must load a project first
FrostyCLI.exe --game-path "..." project load my-mod.fbproject

# Replace a texture from a DDS
FrostyCLI.exe --game-path "..." import texture wall_new.dds --as s2/textures/architecture/bespin/wall_01_c

# From a PNG (converted to DDS automatically)
FrostyCLI.exe --game-path "..." import texture wall_new.png --as s2/textures/architecture/bespin/wall_01_c

# Force the source format, and skip mipmap generation
FrostyCLI.exe --game-path "..." import texture wall_new.bin --as s2/.../wall_01_c --format dds --no-mipmaps
```

Options: `<file>`, `--as <ebx-path>` (required — an existing TextureAsset), `--format dds|png|tga|hdr` (override extension inference), `--no-mipmaps`.

**To create a *new* texture:** use `--template <existing-texture>` to make `--as` a brand-new TextureAsset in one step — it clones the template's EBX + Texture res + chunk (fully independent; the template is never modified) and imports your image into it:

```
FrostyCLI.exe --game-path "..." import texture my_image.dds \
  --as s2/textures/custom/my_tex --template s2/textures/.../wall_01_c
```

Use a template of the same texture type/format as your image. (The older two-step `ebx clone` then `import texture` also works but shares the template's resource.)

**Limitations:** supports `TT_2d`, `TT_Cube`, `TT_2dArray`, and `TT_3d` (cube/array DDS slices are re-laid-out for Frostbite on import; volume is written as-is). The imported texture's type must match the target's type, and SRGB textures require an SRGB-variant source format. PNG/TGA/HDR import needs `thirdparty/dxtex.dll` next to the exe (DDS input works without it); for cube/array/volume use a DDS source.

---

### Import a mesh — `import mesh`

Replace an existing MeshAsset's geometry from an FBX file. Requires a loaded project.

```
FrostyCLI.exe --game-path "..." project load my-mod.fbproject

# Replace a rigid mesh's geometry from FBX
FrostyCLI.exe --game-path "..." import mesh new_wall.fbx --as s2/objects/architecture/bespin/wall_01

# Apply a uniform scale on import
FrostyCLI.exe --game-path "..." import mesh new_wall.fbx --as s2/.../wall_01 --scale 2.0
```

Options: `<file.fbx>`, `--as <ebx-path>` (required — an existing MeshAsset), `--scale <f>` (uniform import scale, default 1.0).

**The FBX must structurally match the target** — same LOD count and section names. The reliable way to produce a valid FBX is to **export the target mesh first, edit it, and re-import**: the export uses the exact LOD/section naming the importer expects.

```
FrostyCLI.exe --game-path "..." export s2/.../wall_01 --out wall_01.fbx   # export
# ...edit wall_01.fbx in Blender/Maya, preserving the lodN node + section names...
FrostyCLI.exe --game-path "..." import mesh wall_01.fbx --as s2/.../wall_01
```

**To create a *new* mesh:** use `--template <existing-mesh>` to make `--as` a brand-new mesh asset in one step — it clones the template's EBX + geometry res + chunks (so the new mesh is fully independent; the template is never modified) and imports your FBX into it:

```
FrostyCLI.exe --game-path "..." import mesh my_mesh.fbx \
  --as s2/objects/custom/my_mesh --template s2/.../wall_01
```

The template must be **structurally similar** to your FBX (same LOD/section layout) — the new mesh reuses the template's vertex declarations and material assignment, so you don't author materials. (The older two-step `ebx clone` then `import mesh` also still works, but shares the template's geometry res.)

If your FBX has **more sections** than the template, add `--sections <N>` to grow each LOD to `N` sections before import (cloning the template's last section — same vertex declaration + material). On Battlefront II the template's shader block depot (`<mesh>_mesh/blocks`) grows with it: each new section gets its own mesh-parameter block and shares the template's material parameters, and the LOD's mesh-variation entry gets a matching RVM shader reference. Without those blocks the FBX importer silently skips the extra sections and the game faults the first time one of them is drawn (that is what "loads fine, crashes when you walk up to it" looks like):

```
FrostyCLI.exe --game-path "..." import mesh my_mesh.fbx \
  --as s2/objects/custom/my_mesh --template s2/.../wall_01 --sections 4
```

> ⚠️ **Section-growing is validated structurally (the mesh builds and re-parses) but NOT verified in-game.** Load the built mod and confirm the mesh renders correctly before relying on it. The grown sections reuse the template's material, so all sections share one material set.

**Skinned meshes:** pass `--skeleton <skeleton-ebx>` to import a skinned mesh (the bone names are mapped against that SkeletonAsset). Rigid/composite meshes don't need it. Geometry is fit into the target's existing vertex declaration, so vertex streams the target lacks (e.g. a second UV set) are dropped. A structurally invalid FBX (wrong LOD count / missing sections) fails the import; note the underlying plugin may clear the asset's linked-asset state on such a failure — `ebx revert` the mesh if a failed import leaves it in a bad state.

---

### Import a resource-backed asset — `import resource`

Replace the underlying res blob of a resource-backed asset (AtlasTexture, SvgImage, IesResource) with raw bytes from a file — the decompressed payload, e.g. what `res export` produces. Resolves `--as` as a res path **or** a res-backed EBX (via its `Resource` rid). Requires a loaded project.

```bash
# Replace an SVG image's res blob (round-trips with `res export`)
FrostyCLI.exe --game-path "..." import resource new.bin --as ui/art/hub/loadout/shockavailablebg

# Create a NEW resource-backed asset from a template (clones the template's EBX + res), then write into it
FrostyCLI.exe --game-path "..." import resource new.bin --as ui/art/custom/my_image --template ui/art/hub/loadout/shockavailablebg
```

Building a res from a *source* file (a real `.svg` / `.ies` / atlas image) is the editor's per-type importer and isn't done headlessly — this is the byte-level path. (Lua is absent from SWBF2, so there's no Lua res to target.)

---

### Inspect a level hierarchy — `level tree`, `level stats`

Show the level root and recursively followed sub-world references without modifying the project:

```
FrostyCLI.exe --game-path "..." level tree Levels/MP/StarKiller_01/StarKiller_01
FrostyCLI.exe --game-path "..." level tree Levels/MP/StarKiller_01/StarKiller_01 --format json --max-depth 2
```

Summarize the assets, object types, static-model groups, members, placements, and whether their
transforms are expected from EBX or Havok:

```
FrostyCLI.exe --game-path "..." level stats Levels/MP/StarKiller_01/StarKiller_01
FrostyCLI.exe --game-path "..." level stats Levels/MP/StarKiller_01/StarKiller_01 --format json
```

Both commands are read-only. `level stats` reports transform storage from the EBX structure and
physics-resource references; `level list` is the command that reads and returns the actual matrices.

---

### Inspect a level's baked lighting — `enlighten inspect`

Report the Enlighten entities and baked resources attached to a level. The report identifies the
dynamic/static Enlighten databases and the sky-visibility, irradiance and direction textures,
including texture dimensions, formats and backing chunk IDs. It is read-only.

```
FrostyCLI.exe --game swbf1 --game-path "..." enlighten inspect xp2/levels/clouds/clouds_04/clouds_04
FrostyCLI.exe --game swbf2 --game-path "..." enlighten inspect s2/levels/cloudcity_01/cloudcity_01 --format json
FrostyCLI.exe --game-path "..." enlighten inspect <level-or-enlighten-ebx> --hash
```

`--hash` also reads and hashes each backing RES/chunk payload. It is deliberately optional because
baked-lighting payloads can be very large.

---
### Check the Enlighten database model — `enlighten verify`

Parses and re-writes every shipped Enlighten system database (`EnlightenDataAsset`, resType 1892010814) and static
database (`StaticEnlightenData`, 3335336582) and compares payload and res meta byte for byte. All 407 in the
Battlefront II install round-trip (404 modelled; two 4-byte `0xDEADBEEF` placeholders and the default database with
dynamic probe data are kept raw). Run it after any change to `FrostyCLI.Core\Enlighten\EnlightenDatabase.cs`.

```
FrostyCLI.exe --game-path "..." enlighten verify [--only end|fls] [--limit n]
```

---

### Light a new level end to end — recipe

A level made with `level create --skirmish-from <host>` on its own terrain carries the host's baked lighting. This
gives it its own: GI on its terrain, lightmaps on its buildings, probes for soldiers and props, its own sun and lamps.
Every step writes to the project; nothing touches the host map.

```
# 1. the level's geometry for occlusion and bounce (its buildings; what the bake would have seen)
FrostyCLI.exe --game-path "..." --project my.fbproject enlighten geometry MyLevels/Lvl/Lvl --out lvl.obj --min-size 1

# 2. lamps: drop the host's lamp fixtures that came with the lighting variants (Tatooine's night: 1,319 Mos Eisley
#    wall lamps in Lighting_Night's static group, glowing over the empty ground; Sunny and Sunset: 13 ground lamps
#    each), place your own (optional), and keep their records for the bake
FrostyCLI.exe --game-path "..." --project my.fbproject --save level clear MyLevels/Lvl/Lighting_Night --delete-groups --only-type StaticModelGroupEntityData
FrostyCLI.exe --game-path "..." --project my.fbproject --save level import-lights MyLevels/Lvl/Night_Lighting --from lamps.json

# 3. relight every lighting variant into the level's own copies
FrostyCLI.exe --game-path "..." --project my.fbproject --save enlighten relight MyLevels/Lvl/Lvl `
    --terrain MyLevels/Lvl/Terrain/Terrain --host-terrain <host streaming tree .res> --calibration-radius 480 `
    --geometry lvl.obj --probes --charts MyLevels/Lvl/Content --clone-into MyLevels/Lvl/Lighting `
    --lights lamps.json@Night --sun-rotate 60 --sun-elevation 25 --sun-scale 1,0.9,0.8
```

What each part does, and where it is proven: the terrain tiles (T1, in game), new ground outside the host's tiles
(`--extend`, in game), charts for placed buildings and prefabs (in game), probes and terrain probe grids (in game), own
lighting assets (in game), sun direction, height and colour, a dusk sky (in game), lamps (in game; calibrated on
Tatooine Night's shipped bake with `--host-lights`). Pin a lighting variant for testing with the
`Lightning_Setup` picker's `RandomEventWeight` (Tatooine-based levels: index 0 Sunny, 2 Night).

---

### Re-light the terrain of a level's bake for a new terrain — `enlighten relight`

A level whose terrain was replaced (`terrain create --as`, a heightmap edit, ...) still carries the lightmap its old
ground was baked with: the host's buildings, bounce and hollows sit on the new ground as dark ghosts, and cliffs that
are new get the flat light of the sand that used to be there. `enlighten relight` recomputes the terrain tiles of every
bake the level loads (all of a level's lighting variants: Tatooine has three) for the new terrain, in the bake's own
layout and in the encoding the game's shader reads, and splices them into the lightmap atlases. Every other texel
(the mesh charts) stays byte for byte.

```
FrostyCLI.exe --game-path "..." --project my.fbproject --save enlighten relight Levels/MP/Tatooine_01/Tatooine_012 `
    --terrain levels/mp/tatooine_01/terrain/terrain `
    --host-terrain D:\corpus\levels_mp_tatooine_01_terrain_terrain.streamingtree_win32.res `
    --calibration-radius 480 --out D:\relight
```

| option | |
|---|---|
| `<level>` | the level or the sub-world that holds the lighting sub-worlds (searched three sub-world levels deep) |
| `--terrain` | the new terrain: res name, terrain EBX or a `.res` file |
| `--host-terrain` | the terrain the bake was made with (default: `--terrain`). The sky is fitted on it, so it must be the original |
| `--bakes FL_a,FL_b` | only these `StaticEnlightenData` |
| `--calibration-radius r` / `--calibration-exclude x,z,r` | leave the host's built-up area out of the fit |
| `--sun-gain g` | scale the sun that bounces off the terrain (default 1; the gain the probes measure is applied anyway) |
| `--geometry soup.obj` | placed meshes of the new level (`navmesh geometry --out`; its `terrain` group is skipped): they hide sky from the texels and probes near them and bounce light back, sunlit only where the sun reaches them |
| `--host-geometry soup.obj` | the host's meshes, for the fit (optional) |
| `--probes` | also re-light the static light probes (what soldiers, props and CLI-placed buildings are lit by) and move the terrain probe grids onto the new ground |
| `--terrain-color r,g,b` | the terrain albedo, when no visual environment of the level overrides `TerrainColor` |
| `--extend <metres>[,<texels>]` | new ground outside the host's tiles: a tile of that size (default 256 texels) for every grid cell of the new terrain's frame that no host tile covers, in new atlas rows (with `--probes`, one terrain probe grid per new tile) |
| `--only-new-tiles` | with `--extend`: leave the host's tiles as shipped (the old ground did not change) |
| `--charts <partition>` | lightmap charts for the object references in that partition placed with override Dynamic (`level add`): a rect in new atlas rows, a mesh-instance record under the placement's lightmap key, texels lit by the same model; the placement's override becomes None. Repeatable |
| `--chart-density t/m` | chart texels per metre of surface (default 1.5; shipped Tatooine charts are 16 x 16 texels at the median) |
| `--clone-into <asset prefix>` | give the level its own copies of every bake's lighting assets (5 atlases, static data, Enlighten data) under the prefix, point the level's lighting sub-worlds at them and bundle them: the host level keeps its shipped bake |
| `--sun-rotate <degrees>` | turn the lighting about the vertical axis: the fitted sky and the sun of the new bake, the level's own VE copy (`SunRotationX - degrees`) and its shadow-extrusion direction (needs `--clone-into`) |
| `--sun-elevation <degrees>` | raise or lower the sun: the level's own VE copy gets `SunRotationY`, the baked sun and the extrusion direction follow it through the VE's `SunShadowHeightScale` (needs `--clone-into`) |
| `--lights <lights.json\|ebx>[@bake]` | bake the bounce of these lights (`level export-lights` JSON, or a layer / sub-world / level / prefab walked the same way; `@Night` gives them to the bakes whose name contains `Night` only). Repeatable |
| `--sky-scale r,g,b`, `--sun-scale r,g,b` | tint or brighten the baked sky (and the sky-lit ground) and the bounced sun, per channel (one value for all three); with `--clone-into` the level's own VE sun colour is scaled the same way, so the runtime sun matches its bounce |
| `--host-lights <lights.json\|ebx>[@bake]` | the host level's own lights: they are traced on the host terrain and geometry, and the lamp gain its shipped bake implies near them (Tatooine Night: 1.35, after which the lamp-share bins centre on 1) scales the new level's lamps. Needs `--host-geometry` |
| `--light-gain g`, `--light-albedo r,g,b`, `--mesh-albedo r,g,b` | scale the lights' bounce (1); the albedo of the ground their light bounces off (default: the bake's `TerrainColor`, a sibling bake's where its VE does not set one) and of meshes (0.5) |
| `--light-photons n`, `--light-bounces n`, `--light-reach m` | photons per light (4096) and bounces (3); how far from a light probes look for lit surfaces (40 m) |
| `--no-import`, `--out` | write the DDS files, databases and one `.model.json` per bake without importing |

How it works (details: `projects\enlighten-authoring\SPEC.md` §4 and §7). For each tile texel: horizon angles of
the new terrain in 32 directions; sky radiance over the visible part of the hemisphere; for the hidden part, the
terrain's own bounce (`TerrainColor` x (sun + sky), from the visual environment). The sky itself is fitted per bake to
the bake's shipped terrain texels on the host terrain, as a non-negative combination of elevation and azimuth lobes, so
open flat ground comes out as it shipped (0.3-0.5% median on Tatooine) and new relief is lit by the same sky. Output:
L0 in the BC6H irradiance atlas, the radiance-weighted mean direction per colour in the three BC7 direction atlases
(`d * 0.5 + 0.5`), sky visibility / AO in the BC7 sky atlas.

**The bake's own sun and ground.** Each lighting sub-world records the light direction its shadows were baked with
(`ShadowExtrusionLightDirectionEntityData`, usually in a `..._Setup` layer); that is the sun the bake used, and on
Tatooine it differs from the visual environment's for Sunset (31.6 vs 15.5 degrees) and Night (54.2 vs 32.0): the
baker steepens the VE sun by the VE's `SunShadowHeightScale` (0.45 there), `normal(x, y / scale, z)`. The relight uses
the extrusion direction, or that rule where a variant has none, and prints both. `TerrainColor` counts only where a visual environment lists it in
`PropertyOverrides` (else a sibling bake's, else `--terrain-color`). With both, the sun that the shipped probes imply is
the same multiple of the VE sun for all three Tatooine bakes (1.27-1.34), and the relight scales the sun bounce by it.

**Probes** (`--probes`): the terrain probe grids move to 2 m above the new ground; every probe is re-lit over the full
sphere (fitted sky where it is open, terrain / mesh bounce where it is not), stored as SH L2 in the game's form. A
self-check against the shipped open-ground probes is printed per bake (Tatooine: 2.3-2.7% median). Probes more than
1 m under the ground or off the terrain keep their shipped lighting: Tatooine keeps the spawn screen's and the
end-of-round room's probe volumes at y = -3000.

**New ground** (`--extend`): the runtime lights a terrain patch from the first record whose box covers it, so ground
outside the host's tiles sampled atlas texel (0, 0). The new tiles sit on the new terrain's own quadtree grid; the atlas
grows downwards, every mesh chart and tile is rescaled onto the same texels (the uv transforms are normalised to the
atlas size) and the textures grow with their old blocks in place. Proven in game on a 4 km Tatooine terrain: the ring
reads the new tiles, Mos Eisley's 9,089 charts are untouched.

**Placed buildings** (`--charts`, `enlighten keys`): a mesh draws its lightmap only with override None and a record
under its lightmap key: FNV-1 over the mesh asset's GUID and its world transform quantised in float32 (translation
x100, rotation x10000, scale x100, mirrored flag). `enlighten keys <level>` computes that key for every group-member
instance and object reference a level loads and reports how many its bakes contain.

**Own lighting, own sun** (`--clone-into`, `--sun-rotate`): without `--clone-into` the relit bake is written into the
host's lighting assets, which every level that loads them (the host map too) shares while the mod is installed. With
it, the level's lighting sub-worlds point at their own copies and the host is untouched (checked: the host database
stays byte-identical to the shipped one). `--sun-rotate` then turns the level's sun and its baked lighting together.

**Lamps** (`--lights`): the game lights a map's lamps at runtime and the bake holds only their bounce
(`AffectRadiosity`). The relight takes each light in the engine's units (lumens / 4 pi for a point, / pi for a spot,
colour x `RadiosityColorScale`, IES profiles replacing the intensity; spots, rectangles and IES beams point along
-forward, the way Frostbite's shader aims them) and traces photons: the first surface they hit is the runtime's direct
light, what they bounce onto (up to 3 bounces) is stored, and every texel, chart and probe reads the bounced light
around it. Checked against Tatooine's own night bake with its 781 lights: the shipped texels near lamps rise with the
modelled lamp share across the board. The model's absolute level (the albedos DICE's materials gave the bake are not in
the game data) is calibrated on the host where it has lamps: `--host-lights` measures the gain its shipped bake implies
(Tatooine Night 1.35) and applies it. In game: TK24 at night with six lamps placed by `level import-lights` and baked
with `--lights`.
`export-lights` a lighting layer and pass it with `@Night` to light only the night bake.

**Other shipped maps**: the same self-relight on Naboo, Takodana, Hoth, Starkiller, Jakku, Yavin, Endor and Kashyyyk
reproduces their open-ground tiles within 1.2-2.8% median (Kashyyyk 7-14%, few open texels); forest floors need the
trees as geometry (`enlighten geometry`: Yavin's all-texel error 108% without, 25-28% with).

**Prefabs** (`--charts`, `enlighten keys`): a building placed as a prefab gets a chart for every mesh the prefab places
(through its own object and spatial-prefab references, recursively), keyed on child transform x placement. Checked
on shipped data: the composed keys of Tatooine's cantina (dome, walls, floor, bars) are the shipped bake's records;
the prefab meshes without one are small details the bake leaves probe-lit. In game: the cantina moved 5 cm draws black
walls without charts and is lit like its neighbours with them.

Limits: a new sun elevation keeps the fitted sky's shape (it belongs to the host bake's sun); without `--host-lights`
the lamps' bounce uses the physical defaults (ground TerrainColor, meshes 0.5, gain 1).

### A level's render geometry — `enlighten geometry`

```
FrostyCLI.exe --game-path "..." enlighten geometry Levels/MP/Tatooine_01/Tatooine_01 --out tatooine.obj [--min-size 1] [--region x0,z0,x1,z1]
    [--radiosity-only] [--max-size 400] [--skip substring,...] [--max-tris 2000]
```

Sets from the front end, cinematics and gameplay vehicles are always left out, and anything larger than `--max-size`
metres (400: sky domes, set shells - Yavin carries a front-end dome over the whole map that shaded every open texel).
Foliage stays in: the bake's forest floors are shaded by it (Yavin self-relight, all texels: 108% off without geometry,
25-28% with it; open sky 3-4%). `--radiosity-only` keeps only the meshes with lightmap uvs (Yavin: 50-60% on all texels)
for scenes whose foliage should not shade the ground.

Every mesh the level draws — static model group members (placed by EBX or by the group's Havok resource), object
references, prefabs down to their model entities — at the finest LOD within `--max-tris` (2000) per mesh, in world space, one OBJ group per placement.
It is what `enlighten relight --geometry / --host-geometry` reads: a navmesh export leaves out what nobody walks on
(most of Mos Eisley's walls).

---

### Inspect a terrain — `terrain info`, `terrain validate`

A level's terrain is three resources; the elevations live in the `TerrainStreamingTree`. Name it by
res path, by the terrain's EBX path (the `.streamingtree_win32` sibling is found for you), or by a
payload file on disk — which is how you inspect the *other* game's terrain, since one run loads one
game.

```
FrostyCLI.exe --game swbf2 --game-path "..." terrain info Levels/MP/Endor_01/Endor_01_Terrain/Endor_01_Terrain
FrostyCLI.exe --game swbf1 --game-path "..." terrain info levels/volcanic/volcanic_01/volcanic_01_terrain/volcanic_01_terrain.streamingtree_win32
FrostyCLI.exe --game swbf2 --game-path "..." terrain info <terrain> --against ..\volcanic_01.streamingtree
FrostyCLI.exe --game swbf2 --game-path "..." terrain validate <terrain>
```

`info` reports the header (including `skipLevels`, which is 1 in Battlefront 2015 and 2 in every
Battlefront II terrain), each raster-tree record and whether it parses, the tile geometry, world
extent, height band, node census by level, and the atlas capacity check. `--against` adds the
differences that matter when porting between two terrains.

`validate` checks the invariants that a rewrite depends on, and round-trips the heightfield through
the parser and serialiser to prove it comes back byte-identical before anything is written. All 12
shipped Battlefront II terrains pass.

Every record parses: heightfield, mask, colour, material (tag 3, 4-bit RLE with Battlefront II's extra
byte) and destruction depth, byte-exact on all 39 shipped terrains.

---

### Rewrite a terrain's elevations — `terrain retarget`

Give a terrain another terrain's landform. The donor's payload is kept byte for byte and only the
heightfield record is rewritten — mask, colour, material and layer trees, the streaming-node list
and every record length come through untouched.

```
# Battlefront (2015) Sullust's landform, on Battlefront II Endor's terrain, in Endor's playable band
FrostyCLI.exe --game swbf1 --game-path "E:\..." res export levels/volcanic/volcanic_01/volcanic_01_terrain/volcanic_01_terrain.streamingtree_win32 --out volcanic_01.streamingtree

FrostyCLI.exe --game swbf2 --game-path "F:\..." --save-as sullust.fbproject terrain retarget `
    levels/mp/endor_01/endor_01_terrain/endor_01_terrain.streamingtree_win32 `
    --source volcanic_01.streamingtree --band 140:320

# a flat plateau, to answer "is this terrain rendering at all"
FrostyCLI.exe --game swbf2 --game-path "..." terrain retarget <donor> --flat 210 --out flat.streamingtree
```

**`--band <lo>:<hi>` is the flag that decides whether you can see the result.** It remaps the
source's height band into those host metres. Without it the source's own heights are carried across
at scale, and a landform spanning 0–1024 m dropped into a level whose playable ground is a flat
~200 m renders perfectly, several hundred metres above the players — indistinguishable in game from
nothing rendering at all. The command warns when the rewritten band lands clear of the donor's own
ground; `--band auto` uses the donor's own band, and `--band none` opts out explicitly.

Without `--out` the resource is written into the loaded project, so `mod build` picks it up. With
`--out` it is written to a file and no project is needed.

What is rewritten, and why each part is needed:

| | |
|---|---|
| elevation samples | resampled from the source at each donor node's world XZ, then band-remapped |
| min/max pyramid | rebuilt; the layout is voted on from the donor's own shipped bytes, and left alone if the vote is not unanimous |
| occluder grid | same treatment. Battlefront II's layout matches none of the known variants, so in practice this is left as shipped |
| node bounding boxes | recomputed **bottom-up over subtrees** — a box spans its whole subtree, not its own samples, and a box that only fits its own node makes the cull job discard the subtree |

`--keep-derived` writes elevations only and leaves all three alone. It is a diagnostic: shipped
bounding boxes do not enclose rewritten samples, so the terrain will not draw.

Terrains are **not** interchangeable payloads. Splicing a foreign heightfield tree into a donor
never renders — `loadNodes` walks the streaming tree and the raster trees in lockstep asserting
that the node ids match, so a donor's streaming tree cannot index a heightfield with a different
topology. Resampling onto the donor's own tree is what works, and the cost is resolution: the port
lands at the donor's node count, so pick a donor with enough nodes (`terrain info` reports it).

---

### Edit a terrain as a heightmap — `terrain export-heights`, `terrain import-heights`

Take the elevations out as a plain 16-bit raw heightmap, edit it anywhere, and put it back.

```
FrostyCLI.exe --game swbf2 --game-path "..." terrain export-heights <terrain> --out endor.r16 [--size 2048]
# ...edit endor.r16 in World Machine / Photoshop / Blender...
FrostyCLI.exe --game swbf2 --game-path "..." --save-as t.fbproject terrain import-heights <terrain> --from endor.r16
```

The export writes a square, row-major, little-endian `u16` grid plus a `<file>.json` sidecar holding
the world rectangle and the height range, so the import needs no flags to put it back where it came
from. `--size` defaults to the terrain's own resolution capped at 2048; the result line reports the
native resolution so you can see what a smaller grid costs you.

---

### Build a new terrain — `terrain create`

A complete new `TerrainStreamingTree` from a heightmap: any size, any resolution, your own layer weight maps and
colour map, holes. Every raster tree is generated from scratch — heightfield (samples, borders, min/max pyramid,
occluder grids, tessellation density), layer mask, colour, material, destruction depth — together with the streaming
tree, by the rules the 39 shipped Battlefront II terrains follow. The result re-parses byte-exact before it is
written.

```
# a heightmap and a world size, rendered with Tatooine's terrain layers, in place of Tatooine's terrain
FrostyCLI.exe --game swbf2 --game-path "..." --save-as t.fbproject terrain create --heights h.r16 --size 3000 `
    --height-range 20:340 --as levels/mp/tatooine_01/terrain/terrain.streamingtree_win32

# to a file, for `level create --terrain-res` (the host is the level create terrain host's terrain)
FrostyCLI.exe ... terrain create --heights D:\t\heights.r32 --layers D:\t\layers.json `
    --host s9_3/coop_nt_fosd/testrange_gameplay_terrain/testrange_gameplay_terrain --out my.terrain
```

| input | |
|---|---|
| `--heights` | `.r16` (u16), `.r32` (float32 metres), `.png` (8/16-bit grey), `.exr` (scanline; ZIP, ZIPS, RLE or none). A `<file>.json` sidecar (written by `terrain export-heights` and by the Blender helper) supplies size, world rectangle, height range and row order |
| `--size`, `--center`/`--origin` | the world rectangle the heightmap covers, in metres (`<x>x<z>` for non-square). Samples are a vertex grid: the corners sit on the rectangle's corners |
| `--height-range lo:hi` | metres at 0 and at full scale for integer formats (float data is metres already; with this flag its min..max is remapped) |
| `--rows` | `min-z-first` (default — the top row of a top-down Blender view, since Frostbite is right-handed Y-up and Blender (x, y, z) is Frostbite (x, z, −y)) or `max-z-first` |
| `--host` | the terrain whose `TerrainData` will render this one: its layers, layer combinations and material declarations. Replacing a terrain with `--as` makes that terrain the host |
| `--layers layers.json` | layer weight maps (below); without it the whole terrain is the host's layer 0 |
| `--colormap rgb.png` / `--color r,g,b` | the colour tree; a host without one (the test range, four SP maps) gets none unless you ask |
| `--holes mask.png` / `--hole-below m` | holes: mask > 0.5, or heights below a level; NaN in float heightmaps is a hole too |
| `--spacing`, `--tolerance`, `--budget` | finest sample spacing (default: the heightmap's own), refinement tolerance in metres (0.05), data-node budget (1400) |
| `--materials idx.png` | physics material per cell (grey 0-15, indices into the host's material declarations) instead of each layer's `material`; `layers.json` can name it too (`"materials"`) |
| `--mask-level`, `--mask-tolerance`, `--mask-budget` | deepest mask level (4), how far (0-255 alpha) a layer may be off before a node gets finer tiles (2; 0 = every node to the deepest level), tiled-subtile budget (8,090, the most any shipped map uses) |
| `--base-host` | edit a region of the host instead of replacing it: outside the heightmap's rectangle the host's own samples; every node the edit does not reach keeps its bytes; the host's layers, colour and material are kept |
| `--hole-layer n`, `--hole-dilate m` | the layer painted opaque over holes (default: the host's, read from its VisualTerrain) and how far the paint reaches past them (1 m) |
| `--host-visual file` | the host's VisualTerrain resource when `--host` is a payload file without its sibling |
| `--clear-decals` | with `--as`: empty the terrain decals (footprints, tracks, stains) of every level using this terrain; they were placed for the old ground. Alone: `terrain clear-decals <terrain>` |

**The frame snaps to the host's grid.** A level's baked GI lightmaps are cut along its terrain's quadtree, and a
terrain patch that straddles two lightmap tiles gets no indirect light at all — pitch black wherever the sun does not
reach, in square blocks. So with a host the new terrain's square is the host's extent times a power of two, placed on
the host's grid, and every patch sits inside one tile (the result line says which square was chosen). The square is
never smaller than the host's: a smaller one renders pitch black in game, so a small map (say 512 m at 0.25 m in the
8 km test range) gets the host's square and a deep tree only where it has detail. Outside the heightmap the edge
blends into flat ground at the mean edge height over `--skirt-fade` metres (default: a quarter of the heightmap's
side). `--no-align` and `--allow-small-frame` opt out. Ground beyond the host's own
square has no lightmap tile: it is lit, but its bounced light is one constant value (no darker hollows), so keep
playable detail inside the host's square.

`layers.json` names the host layers to paint and their weight maps (same rectangle and row order as the heights;
paths relative to the json). Weights are splat weights — normalised per texel, one layer may omit its map and take the
remainder — and are converted to the renderer's ordered alpha stack. `material` picks the physics material
(an index into the host's material declarations) where that layer dominates.

```json
{ "weights": "splat",
  "layers": [ { "name": "sand", "hostLayer": 0 },
              { "name": "rock", "hostLayer": 5, "mask": "mask_rock.png", "material": 1 } ],
  "colormap": "colormap.png" }
```

Only host layers the host terrain itself paints are accepted: a `TerrainData` can list more layers than its layer
combinations cover (Tatooine lists 17, combines 0–10), and ground whose layer set no combination covers draws
nothing. `--any-layer` overrides.

**Holes and caves.** A hole is two things: heightfield samples of 0 (Havok's hole, also what stops the
triangles) and the host's *hole layer* painted opaque over it (the renderer clips the surface where that layer is
opaque). The hole layer is the VisualTerrain layer with physics disabled (`holesEnabled`); every shipped terrain that
has one has it last (Hoth 8, Endor 10, Takodana 18, Jakku 8, Naboo 22 …); Tatooine, Scarif, Felucia and the test
range have none, so holes on those hosts cut physics only. NaN (float heightmaps), `--hole-below` or a `--holes`
mask make the heightfield hole; `terrain create` paints the hole layer from the same mask. To cut a cave mouth into
a shipped map without touching the rest of it:

```
FrostyCLI.exe ... terrain export-heights levels/mp/hoth_01/hoth_01_terrain/hoth_01_terrain --region -140,-1442,-44,-1346 --out mouth.r16
# ...make the mouth NaN (Blender, Python, an image editor + --holes)...
FrostyCLI.exe ... --save-as d.fbproject terrain create --heights mouth.r32 --base-host `
    --as levels/mp/hoth_01/hoth_01_terrain/hoth_01_terrain.streamingtree_win32
```

**Your own layer look.** A layer's surface is a compiled terrain shader sampling ordinary texture assets; the terrain's
per-combination shader block depots (`<terrain>__<combo>__<pass>__0_graph/blocks`) name them by GUID. Give a layer
your own texture without touching the shared one — other maps and decals use it too:

```
FrostyCLI.exe ... import texture my_sand.png --as mymod/textures/t_my_sand_cs --template <the layer's texture>
FrostyCLI.exe ... terrain retexture <terrain> --from <the layer's texture> --to mymod/textures/t_my_sand_cs
FrostyCLI.exe ... bundle add mymod/textures/t_my_sand_cs --to <the level's bundle> --with-resources
```

`retexture` reports how many texture parameters it re-pointed and warns if the new texture is missing from a bundle
the terrain's depots load in. Do the `_NOH` (normal/occlusion/height) the same way. The change belongs to the terrain,
so every level that uses it sees it. Re-importing the shared texture (`import texture my_rock.png --as <texture>`)
also works but changes it everywhere. On Tatooine: layer 0 (base)
samples `T_MosEisley_DirtGravelGreyScale_01_CSNH` and `T_DesertBase_MudSmooth_01_CS` (Repack), layer 1 (town ground)
`T_MosEisley_SandPacked_01_CS`, layer 2 (paving) `T_MosEisley_DirtGravel_01_CS`, layer 3 (rocky overlay)
`T_DesertBase_Mudrocks_01_CS` (measured in game with labelled test textures). Leave `T_MosEisley_ColorTerrain_01`
alone — replacing it broke loading; the terrain's colour map is the colour tree (`--colormap`).

### Choose which texture goes where

A terrain draws with its host's layers, and a layer is only an index — so the workflow is: see the indices, pick,
paint, build.

1. **See them**: `terrain export-layers <host terrain> --out pal` then `python tools/terrain_palette.py pal` writes
   `pal/palette.png` — one tile per layer, the map's colour where the shipped map paints that layer, magenta marks and
   a zoomed inset for small layers, its index and coverage (Tatooine: 0 base dirt/mud, 1 packed town sand, 2 paving,
   3 rocky overlay — measured in game).
2. **Paint**: in Blender, one vertex group per layer you want, named `bfii:<index>` (weight 1 = that layer); or one
   grey PNG per layer listed in `layers.json` (`{"name": "rock", "hostLayer": 3, "mask": "rock.png"}`). Layers you do
   not paint are absent; the unpainted remainder is the first layer without a mask.
3. **Build**: `terrain create --heights ... --layers layers.json --host <host terrain>` (the Blender exporter writes
   `layers.json` for you).
4. **Your own look** for a layer: `import texture` + `terrain retexture` (below) swaps the texture a layer samples.

### See a terrain's layers — `terrain export-layers`

```
FrostyCLI.exe terrain export-layers levels/mp/tatooine_01/terrain/terrain --out tat_layers [--size 1024]
```

One grey PNG per painted layer (its alpha in the renderer's ordered stack, walked the way the renderer walks the mask),
`colour.png` from the colour tree, `material.png` (the physics material index, 0-15, per cell of the material tree) and
a `layers.json` that `terrain create` reads straight back (`"weights": "alpha"`, the square of the exported texel
centres, row order, and the mask/colour/material depths). Layers have no names in Battlefront II — a layer is its
index — so this is how you find out which index is sand and which is rock before painting your own.

Exported at the terrain's native size (`--size 4096` for a 2 km MP map) and fed back with its own heights, a shipped
terrain comes back as it was: Tatooine's layers and materials bit-identical, heights within 5 cm (`terrain
export-heights --size 4097`, then `terrain create --heights ... --host <that terrain> --layers layers.json --spacing 0.5`).

### A level with its own terrain — `level create --terrain-res`

```
FrostyCLI.exe --game swbf2 --game-path "..." --save-as b.fbproject level create `
    AddedContent/MyMod/Levels/MyLevel_01/MyLevel_01 --terrain-res my.terrain
```

The terrain host's `TerrainData` is copied to `<level dir>/Terrain/Terrain` with its three resources, the copy's
streaming tree is `my.terrain`, and the new level's terrain entities point at the copy — the host level keeps its own
terrain. The dependency closure ships the copy and its resources in the level's bundle. Build `my.terrain` with
`--host` set to the terrain host's terrain (default `S9_3/COOP_NT_FOSD`: the test range,
`s9_3/coop_nt_fosd/testrange_gameplay_terrain/testrange_gameplay_terrain`, 8 km, flat at 64 m, one layer); keep the
ground near y 64 where the arena's spawns stand (`--spawn-y`, `--arena-offset` move them).

### Blender terrain → playable Battlefront II terrain

1. Model or sculpt the terrain as a mesh in Blender (metres, Z up). Paint layers as vertex groups named
   `bfii:<hostLayer>[:<name>[:<material>]]` (Weight Paint), or give materials a custom property `bfii_layer`. Holes are
   simply places with no mesh under them.
2. Install `tools\blender\bfii_terrain_export.py` as an add-on and use **File > Export > BFII Terrain**, or headless:
   `blender -b scene.blend --python bfii_terrain_export.py -- --object Terrain --out D:\t --resolution 2049`.
   It writes `heights.r32` (+ sidecar), `layers.json`, one `mask_*.png` per layer, `colormap.png` (from a colour
   attribute or the base-colour image) and the layer materials' textures under `textures\`.
3. `terrain create --heights D:\t\heights.r32 --layers D:\t\layers.json --host <host terrain> --out my.terrain`
4. `level create <new level> --terrain-res my.terrain` (or `terrain create ... --as <terrain>.streamingtree_win32`
   to replace a shipped level's terrain), then `mod build`.

The format behind all of this, tree by tree: `F:\Frosty\projects\terrain-authoring\SPEC.md`.

### A navmesh for a level — `navmesh geometry`, `navmesh build`

AI soldiers walk on a NavPower navmesh the level streams in (`PathfindingSystemEntityData` in the level root →
`PathfindingStreamEntityData` → `PathfindingBlobAsset` → a chunk holding the navmesh image). A new level, or an old one
whose ground or props changed, needs its own.

```
FrostyCLI.exe --game swbf2 --game-path "..." --project b.fbproject --save navmesh build `
    AddedContent/MyMod/Levels/MyLevel_01/MyLevel_01 --region -230,-450,-60,-260 --terrain my.terrain `
    --donor levels/mp/tatooine_01/tatooine_01_pathfindingblobs_386940c0-c196-47d5-a178-655822c1cb13 `
    --cs 0.2 --climb 0.5
```

1. **Geometry** (`navmesh geometry`, also usable on its own with `--out soup.obj`): every placement the level makes
   (layers and sub-worlds included, static model group members and loose placements, prefabs followed through their
   transforms) is resolved to its mesh at the finest LOD within `--max-tris` (default 4000), put in world space, and
   the terrain inside `--region` is added as a grid (`--terrain-step`, holes left out). **Only meshes the game collides
   with are kept** — a static model whose components carry physics bodies (`StaticModelPhysicsComponentData` →
   `RigidBodyData` → `HavokAsset`). Light cones, Enlighten proxies, decals, hanging cloth, carpets, cables and ropes
   are render-only; kept, they close doorways (`--render-only` keeps them anyway, for diagnosis).
2. **Build** (`navgen`, a .NET 8 tool in `tools\navgen\Bfii.Navgen`, Recast via DotRecast; the solution build puts it
   next to FrostyCLI.exe as `navgen\navgen.dll`, where `navmesh build` finds it -- `--navgen <path>` or `FROSTYCLI_NAVGEN`
   override):
   voxelises the soup at `--cs`/`--ch`, with the agent (radius, height, step) of the **donor** navmesh — use the blob
   of a level whose AI you are reusing (Skirmish: Tatooine's Blast blob above) — then compiles the polygons into a
   NavPower image seeded from the donor and checks it round-trips byte-exact.
3. **Delivery**: a blob asset of the level's own (`<level>_pathfindingblobs`, cloned from the stream's current one)
   with a new chunk and its sizes, the level's pathfinding stream pointed at it with `Autoload` on, asset and chunk
   bundled with the stream's partition. With several streams, pick one with `--stream <instance guid>`.
4. **Spawn check**: every spawn point of the level (`AlternateSpawnEntityData`) inside the region is looked up on the new
   navmesh -- on the main piece (the connected piece most spawns stand on: the play space; on an interior the largest
   piece can be a roof), on an island (a ledge, a roof left without its stairs), or off it -- and the result is printed
   as `spawns`, with a warning naming any spawn that is not on the main piece. A bot that spawns on an island or off the
   navmesh stands still all round. A roof over a spawn is not mistaken for where it stands: the spawn belongs to the
   polygon under it nearest its own height. On a shipped level every mode's spawns in the region are counted.

The navmesh image code lives in `tools\navgen`: `Bfii.Navmesh` (read, write, compile and round-trip the
pathfinding blob), `navgen`, `navmesh` (`navmesh inspect <blob>`, `navmesh export-obj <blob> <out.obj>` -- e.g. a level's
own navmesh, exported with `chunk export`, to compare a generated one against) and the library's tests (`dotnet test
tools\navgen\Bfii.Navmesh.Tests`). They are vendored from BfiiMapTools; re-sync with `tools\sync-navgen.ps1`.

`--double-sided` turns near-horizontal faces that point down into floors. The geometry is the render meshes, and those
are often single-sided: Kamino's upper deck exists only as the hangar's ceiling seen from below, so without the switch it
gets no navmesh (DICE's covers it: 69% of DICE's Skirmish ground covered without the switch, 78% with it). Tops of
ceilings become walkable too, as separate pieces nobody spawns on. On a shipped outdoor map in place, pass the map's own
terrain (`--terrain Levels/MP/Tatooine_01/Terrain/Terrain`) or the ground is missing.

Keep the navmesh in the play space's height band: `--y-range y0,y1` leaves out geometry above and below it. Cloud City's
planet lies 900 m under the city, and a navmesh built with it (y -698..361) left every bot standing at its spawn, while
the same level with the navmesh kept to y 184-226 played (19 bots, median 544 m, 0 stuck; DICE's own: 680 m). `navmesh
build` warns when a navmesh spans more than 600 m of height (Death Star II's floors span 485 m and play).

Objects a prefab's own logic moves are left out of the geometry: a door prefab's leaves (PF_AutomaticDoor_Core writes
their Transform) would otherwise wall off every doorway. Doors placed as static group members stay; leave them out with
`--skip <mesh name>`.

Measured on Tatooine's Skirmish Blast (DICE's navmesh replaced by one generated from the level): 97% of DICE's
walkable ground covered, 91% of it in one connected piece, and 19 AI soldiers moving a median 707 m each in five
minutes (658 m on DICE's own navmesh).

#### A custom Skirmish map with bots, in place

The recipe proven in game (19 AI, 0 stuck, median 767 m per bot in four minutes on a custom terrain with placed
buildings): rebuild an existing Skirmish map, so its own game-mode flow starts the AI.

```
FrostyCLI.exe ... --save-as m.fbproject terrain create --heights my.r32 --host <tatooine terrain> --as levels/mp/tatooine_01/terrain/terrain.streamingtree_win32
FrostyCLI.exe ... --project m.fbproject --save level clear Levels/MP/Tatooine_01/Content --delete-groups --remove
FrostyCLI.exe ... --project m.fbproject --save level add Levels/MP/Tatooine_01/Content --entity-class ObjectReferenceObjectData `
    --blueprint Objects/Architecture/Tatooine/MosEisley/House_01A --pos 141,63.5,110 --rot 37,0,0
FrostyCLI.exe ... --project m.fbproject --save navmesh build Levels/MP/Tatooine_01/Tatooine_01 --region 40,0,210,190 `
    --terrain my.terrain --stream 386940c0-c196-47d5-a178-655822c1cb13 --cs 0.2 --climb 0.5
```

`level clear` empties the city sub-world (its layer references and its static model group) but keeps it loaded, so the
city's buildings stay available to place; Skirmish Blast streams the Blast blob (stream `386940c0`), which
`navmesh build` points at a navmesh generated from the new terrain and whatever is placed. Keep ground under the
mode's spawn points (Tatooine's Blast spawns stand at x 69..176, z 24..145).

#### A new level with Skirmish bots in one command — `level create --skirmish-from`

```
# 1. what to build on: the map's real ground round its Skirmish play space, its spawns, where they stand
FrostyCLI.exe ... level skirmish-template Levels/MP/Endor_01/Endor_01 --out D:\endor
# 2. Blender: File > Import > BFII Skirmish Template (D:\endor) -- ground mesh, one cone per spawn, the play space
#    outline; sculpt and paint, keeping ground under the spawns; File > Export > BFII Terrain (D:\endor\out)
# 3. the level, its terrain, its bots and their navmesh
FrostyCLI.exe ... --save-as n.fbproject level create AddedContent/MyMod/Levels/MyMap/MyMap `
    --skirmish-from Levels/MP/Endor_01/Endor_01 --heights D:\endor\out\heights.r32 --layers D:\endor\out\layers.json `
    --navgen
```

`--skirmish-from` works the recipe out from the map's own data and prints its reasoning: the shell (the map's root with
its logic, light, sound, effects and pathfinding layers, GameModes with its layers and automation sub-world, the map's
`Lobby` sub-world whole and its lighting sub-world whole -- Tatooine_012 and the like; the city, backdrops, cinematics,
other modes, art, collision-proxy and wreck layers left out, GameModes' own static geometry cleared), the mode sub-world
that nests the Skirmish child (TeamDeathmatch; Naboo_02's is called Blast) with its Online half left out, started by the
map's own game-mode flow; the navmesh stream gated on SkirmishBlast; spawns stood at the map's median spawn height; an
outdoor visual environment when the shell has none.

Why the Lobby and the lighting sub-world stay (bisected in game on Tatooine, 2026-09-24): the Lobby's art layer is the
spawn screen's dark room (`UI/InGame/Spawn/PF_Spawn_Darkroom_01`); without it the pre-deploy view is a blown-out white
and, as soon as any building is placed, every bot freezes in place -- their physics bodies never reach the Havok world.
The lighting sub-world carries the map's Enlighten bakes (rolled Sunny / Sunset / Night per launch); without it placed
static models draw pure black.

What was placed for the host's own city goes, since a new layout has none of it: occluder planes/volumes (in the open
they draw black and cull what is behind them), the lighting tree's city lights and effects (`*_Light`, `*_VFX`: street
lamps, smoke plumes, local probe / reflection / shadow-cache volumes), `Default_Lighting` (wall and ceiling lamps), the
Pathfinding link layer (jump links onto roofs), GameModes' collision pass (invisible clutter boxes at ground level the
navmesh cannot see), the root's own static group (the city's wall lamps, light cones, Enlighten proxies) and the shadow
extrusions of the root and the lighting variants (baked with the city standing). A left-out layer is also removed from
every kept partition that references it; otherwise the dependency closure bundles it again and it loads after all. `--heights` (+ `--layers`) builds the terrain against the map's own terrain on
the way (or pass a finished one with `--terrain-res`); `--navgen` builds the navmesh round the play space and installs it
through the Skirmish stream right after. Options given after `--skirmish-from` override its choices.

Built and validated clean on all 13 Skirmish maps with a terrain (Death Star II, Kamino and Cloud City have no terrain
entity), and played in game with a test terrain on each (2026-09-25; 19 AI, bots watched for 150 s after the player
deploys; metres moved per bot, median):

| host | bots | stuck | median | | host | bots | stuck | median |
|---|---|---|---|---|---|---|---|---|
| Tatooine | 19 | 0 | 206 m | | Starkiller | 19 | 0 | 454 m |
| Endor | 19 | 1 | 362 m | | Takodana | 19 | 0 | 569 m |
| Hoth | 18 | 0 | 351 m | | Crait | 19 | 0 | 512 m |
| Jakku | 19 | 0 | 564 m | | Kessel | 19 | 0 | 401 m |
| Naboo | 18 | 0 | 489 m | | Geonosis | 19 | 0 | 513 m |
| Naboo_02 | 19 | 0 | 544 m | | Yavin | 19 | 0 | 404 m |

(18 = two bots sharing a name, merged by the watcher.) **Jabba's Palace** builds and validates clean but never creates a
game world (HANG, twice), while the shipped level loads and plays through the same harness -- open.

**Buildings**: `--skirmish-content` keeps the map's main content sub-world (the one with its own static geometry and
network registry, Tatooine's `Content`) loaded but emptied, so its buildings and props can be placed on your ground from
their own bundle: `--prefab <ObjectBlueprint>@x,y,z,yaw`, placed the way `level add` places them, and the generated
navmesh walks round them. Played in game: Tatooine with six Mos Eisley houses (TK16/TK17: 19 AI, none stuck, median
464 m in 150 s; houses lit). Copying a building's assets into the level's bundle instead faulted the load
(game+0x15AC131).

`navmesh build ... --blob <navmesh.bin>` installs a navmesh image built elsewhere (an earlier build's `--keep` directory,
another tool) through the same stream, without geometry or navgen.

**A map of the game** (all optional, one command): `--combat-area <rect:x0,z0,x1,z1 | x,z;...>` fits the play-area
boundary to your layout ([`level combat-area`](#the-play-area-boundary--level-combat-area)); `--menu-name <n>
[--planet-name <p>] [--menu-description <d>]` puts the level in the Custom Arcade pickers of its modes (with its own
name, and the host's planet and factions under its own key); `--loading-screen <image|file.webm|host>` gives it its own
MP loading screen; `--kyber [name]` declares it to Kyber with its modes (the loading image, if any, as its tile). Proven
in game (2026-09-25): Custom Arcade lists it next to its host in Team Battle and Onslaught with its own name and
description, it loads and plays in both, and the pause menu names it. The picker field shows about 22 characters, and
shipped names are upper case.

**More modes**: `--modes Blast,HeroesVersusVillains,...` (with `--skirmish-from`) makes the level play those modes too.
Each shipped `GameModes` sub-world reference names the modes it is streamed for (`InclusionSettings` ->
`EnabledOptions`; Tatooine: TeamDeathmatch = Blast + the three Arcade modes, HeroArena = HeroesVersusVillains, Mode9 =
Mode9/ModeDefend/ModeE/ModeF, FantasyBattle = PlanetaryBattles, ...). Blast keeps TeamDeathmatch's Online half; any other
mode brings the sub-world that serves it, its art layers left out and its own static geometry cleared, grafted with the
Arcade mode. On a terrain of the level's own every moved spawn stands on the ground (ground + 0.5 m), and a mode with a
navmesh stream of its own (Mode9's, for the Instant Action missions ModeE/ModeF) gets it generated over every spawn of
the level + 30 m -- the missions play the host's mission layout on the new ground, beyond a fitted arena. The modes join
the level's description. Online PvP modes need players to leave pre-round: with `--bots-team1 N --bots-team2 M`, a shell
that has no auto-player manager (Tatooine) gets one in each online mode's logic layer (not in an always-loaded one, which
would add auto-players to Arcade as well; Heroes vs Villains 3 + 4 and Hero Showdown 1 + 2, their team sizes), with a
delay that starts itself when that mode's sub-world streams in and fires the manager's `ApplySettings` a second later --
a manager does nothing until then, and every shipped one is fired by its mode's stream-in. Proven in game: the teams
fill (Blast 8 v 8, HvV 4 v 4) and the round leaves pre-round. The bots do not spawn, though -- fighting bots need the
game's gameplay-bot logic (`Gameplay/GameModes/Mode9/Prefabs/PvE_BotLogic`), which `level create` does not graft; online,
players fill these modes. The offline modes a Tatooine_01-shell level plays need no bots of their own: Arcade Team
Battle, Onslaught and Duel and the Instant Action missions ModeE / ModeF are proven in game (2026-09-25; Team Battle,
Onslaught and Missions Attack picked from the game's own menus). Instant Action (Mode8) has no
sub-world of its own on any map -- Kamino_03 plays it in its Supremacy (Mode1) sub-world -- so it needs a Supremacy host.

#### A new level with Skirmish bots — `level create` on a Tatooine shell

Proven in game (19 AI, 0 stuck, median 580-825 m per bot): a level of its own name, Tatooine's shell without its city,
our terrain, Tatooine's Skirmish started by Tatooine's own game-mode flow, and a generated navmesh.

```
FrostyCLI.exe ... --save-as n.fbproject level create AddedContent/MyMod/Levels/MyMap/MyMap --terrain-res my.terrain `
    --terrain-from Levels/MP/Tatooine_01/Tatooine_01 `
    --terrain-keep Default,Setup,Lighting,Default_Lighting,Lighting_Sunny,Lighting_Sunny_Setup,Sound,Shared_Sound,Pathfinding,Pathfinding_Logic,GameModes,GameModes_Flow,GameModes_Automation,GameModes_Automation_logic,GameModes_CollisionPass `
    --keep-mode-lists --ve levels/lighting/tatooine/sunny/ve_pv_tatooine_sunny_01 `
    --mode-from Levels/MP/Tatooine_01/Tatooine_01 --mode SkirmishBlast --also-modes SkirmishOnslaught,SkirmishDuel `
    --mode-subworld TeamDeathmatch --mode-mirror GameModes --bots-team1 0 --bots-team2 0 `
    --mode-keep TeamDeathmatch,TeamDeathmatch_Logic,TeamDeathmatch_Setup,TeamDeathmatch_Shapes,TeamDeathmatch_Automation,TeamDeathmatch_Skirmish,TeamDeathmatch_Skirmish_Logic,TeamDeathmatch_Skirmish_Schematics,TeamDeathmatch_Skirmish_DefendAreas,TeamDeathmatch_Skirmish_AutoCover,TeamDeathmatch_Skirmish_ManualCover3,TeamDeathmatch_Skirmish_Pathfinding,TeamDeathmatch_Skirmish_Automation
FrostyCLI.exe ... --project n.fbproject --save navmesh build AddedContent/MyMod/Levels/MyMap/MyMap --region 40,0,210,190 `
    --terrain my.terrain --stream 386940c0-c196-47d5-a178-655822c1cb13 --cs 0.2 --climb 0.5
```

Build `my.terrain` against Tatooine's terrain (`terrain create --host <Tatooine streaming tree>`, a 2,048 m frame) with
ground at about 64 m under the Blast spawns (x 69..176, z 24..145). What each switch is for:
- `--keep-mode-lists`: Tatooine's own flow already names SkirmishBlast (its gate streams TeamDeathmatch, then fires
  its `StartGameModeSkirmish`, which streams the Skirmish child with the AI).
- `--mode-mirror GameModes`: the terrain host is also the mode host, so its root bundle is already mirrored; mirroring
  it a second time into the mode bundle faulted the game as the mode streamed in.
- `--ve ...sunny...`: Tatooine's outdoor VE comes with the city sub-world; without it the ground renders black.
- Sub-world references whose bundle the new level does not carry (the city, the lobby, cinematics' outros, the online
  half of TeamDeathmatch) are dropped by `level create` itself.

On the Test Range shell (the default `--terrain-from`), `--mode-start-event StartGameModeSkirmish` streams Skirmish and
spawns its AI, but that shell's flow leaves them inert. `--prefab <blueprint>@x,y,z[,yaw]` places a spatial prefab or an
ObjectBlueprint (as ObjectReferenceObjectData) in the shell's Default layer before the dependency closure.

---

### The play-area boundary — `level combat-area`

```
# what a level's modes use as their boundary, and where it sits
FrostyCLI.exe ... --project n.fbproject level combat-area AddedContent/MyMod/Levels/MyMap/MyMap
# fit it to your layout: a rectangle or any outline (x,z pairs), standing at --floor, --height 0 = no ceiling
FrostyCLI.exe ... --project n.fbproject --save level combat-area AddedContent/MyMod/Levels/MyMap/MyMap `
    --area rect:35,5,200,175 --floor 50 --height 0
# or in one go when the level is made (the navmesh region then follows the new area)
FrostyCLI.exe ... --save-as n.fbproject level create AddedContent/MyMod/Levels/MyMap/MyMap --skirmish-from Levels/MP/Tatooine_01/Tatooine_01 `
    --heights my.r32 --navgen --combat-area "rect:35,5,200,175" --combat-area-floor 50 --combat-area-height 0
```

Walking out of the combat area desaturates the picture, runs a countdown and kills the player when it ends (Skirmish's
prefab: 10 s, then 900000 damage; Blast measured ~7-8 s). Proven in game on a new level (2026-09-25): no warning at
nine points -- the centre, 4 m inside each fitted edge, 5-6 m inside two corners, a mesa top and a pit floor; 15 m
outside, the countdown and "RETURN TO OBJECTIVE", then "DEFEATED"; back inside within the countdown, it clears. The rule lives in each mode's logic prefab (`PF_Skirmish_Logic`: a CombatArea and
its manager); the level only supplies the outline, through an interface link named `CombatArea` (also `CombatAreaOnline`,
`CombatAreaSoldier`) from the prefab or sub-world reference to a `VolumeVectorShapeData` / `SpawnLocationFinderShapeData`
in one of the mode's layers. The command finds every such link in the level's partitions and rewrites each linked shape
(points stay local to the shape's transform). A level made with `level create --skirmish-from <map>` inherits the map's
own arena outline at the map's coordinates (Tatooine: 34 points round the Mos Eisley streets, x 69..176, z 24..145),
which fits the old city, not your layout.

Inside means inside the outline on x/z and, for a shape with a height, `floor < y <= floor + height` (Tatooine's arena:
y 57.8..97.8): keep the floor under your lowest ground and the ceiling over your highest, or use `--height 0`. The
HUD minimap (a 50 m radar, `UI/InGame/Hud/Radar/RadarWidget`) draws the same outline; it has no map image. The command
also counts the level's spawns the new outline leaves outside. `--only <name part>` limits it to the links of one mode's
partitions (HvV's arena is smaller than Blast's); `--field` takes other port names or raw port ids (`#-1609661850`: the
out-of-bounds stages of co-op's pf_mode9 and Strike's and Galactic Assault's boundaries arrive through unnamed ports).

### A level's loading screen — `level loading-screen`

```
# the host's screen (Tatooine's planet video) instead of the default Yavin placeholder
FrostyCLI.exe ... --project n.fbproject --save level loading-screen AddedContent/MyMod/Levels/MyMap/MyMap --like Levels/MP/Tatooine_01/Tatooine_01
# a screen of its own: a still with a slow push-in (VP8 WebM through ffmpeg), or a .webm you made
FrostyCLI.exe ... --project n.fbproject --save level loading-screen AddedContent/MyMod/Levels/MyMap/MyMap --like Levels/MP/Tatooine_01/Tatooine_01 --image shot.png --seconds 12
```

The multiplayer loading screen is picked by level path: `UI/Configuration/WSUILoadingScreenSettings
.MPLoadLevelScreenOverrides[]` = { LevelName, LoadingScreenAssetName, IntroMovieName, MovieName, DisplayTimeout } (names,
not references, looked up in the UI superbundle's `loadingscreens_bundle`). A level with no entry gets the default screen
and its Yavin placeholder videos. The screen (`WSUILoadingScreenAsset`) plays an intro and a loop `MovieTexture2Asset`,
each one chunk holding a .webm (`Planet_Tatooine_01.webm`, 2.15 MB). With `--image`/`--movie` the like-level's movie and
screen assets are cloned under `UI/Video/Loading/<Leaf>` and `UI/LoadingScreens/MPLoadLevel/MPLoadLevel<Leaf>Asset` into
the same bundles, the movie pointing at a new chunk; `--image` needs ffmpeg (FFMPEG, PATH or `pip install imageio-ffmpeg`).
`level create ... --loading-screen <image|file.webm|host>` does the same when the level is made. Proven in game
(2026-09-25): picked from Custom Arcade, the level loads behind its own screen, under the game's overlay "<planet> /
<menu name>" and a tip.

### Check a level for structural damage — `level validate`

Walk a level and every sub-world it references, reporting broken references and inconsistent
placement data without modifying anything (`level roundtrip <level>` additionally serialises the
level with the EBX writer and reads it back, reporting any `Objects[]` entry that comes back null;
the bundled SDK writer used to drop one object per save when two hash codes collided):

```
FrostyCLI.exe --game-path "..." level validate Levels/MP/StarKiller_01/StarKiller_01
FrostyCLI.exe --game-path "..." level validate Levels/MP/StarKiller_01/StarKiller_01 --format json
```

What it checks:

| Code | Severity | Meaning |
|---|---|---|
| `level-missing`, `level-unreadable` | error | the named level itself does not resolve or fails to load |
| `subworld-missing`, `subworld-unreadable` | error | a `SubWorldReferenceObjectData` points at an asset that is absent or fails to load |
| `subworld-unnamed` | error | a sub-world reference carries an empty `BundleName` |
| `blueprint-unresolved`, `blueprint-missing` | error | a `StaticModelGroupMemberData.MemberType` names no blueprint, or one absent from the catalog |
| `physics-res-missing` | error | the group's physics component names a resource that does not exist |
| `havok-unreadable` | error | the physics resource exists but cannot be decoded |
| `havok-tree-mismatch` | error | a compound shape has no sound AABB tree of the `2N-1` nodes its `N` instances require. The tree is walked the way the runtime walks it: every node must be reachable at most once and the leaves must carry each instance index exactly once. The traversal consults that tree before it touches any geometry, so a bad one means collision with holes in it -- invisible outside the game |
| `transform-count-mismatch` | error | no compound instance array in the resource holds the number of transforms the members need |
| `placement-array-short` | error | `InstanceTransforms` is shorter than `InstanceCount` |
| `placement-unresolved` | error | instances with neither inline transforms nor a physics resource |
| `instance-count-negative` | error | a negative `InstanceCount` |
| `range-not-contiguous`, `range-width-mismatch`, `range-sentinel-mismatch` | error | `PhysicsPartRange` / `NetworkIdRange` do not tile the group as inclusive spans of `InstanceCount x <per-instance count>`, with `0xFFFFFFFF` where a member owns none |
| `partbbox-count-mismatch`, `networkid-count-mismatch` | error | `PartBoundingBoxes` length or `NetworkIdCount` disagrees with the totals its members declare |
| `mesh-not-in-mvdb` | error | a placed mesh is registered in no `MeshVariationDb_Win32` in the level tree, so the runtime will not draw it. Reported once per mesh, not per placement |
| `mesh-not-bundled` | error | a placed mesh is in no bundle the level loads |
| `mvdb-missing` | error | the level tree has mesh placements but no `MeshVariationDb_Win32` at all |
| `traversal-failed` | error | an asset threw while being walked |
| `parallel-array-long` | warning | a per-instance side array is longer than `InstanceCount`, so its tail can never be read |
| `member-type-not-entity`, `member-type-object-missing` | error | `MemberType` does not reference the entity object inside its blueprint (`ObjectBlueprint.Object`); the client casts it to an entity, so a root reference crashes the level on load |
| `healthstate-id-mismatch` | error | `HealthStateEntityManagerId` is not `(group index << 16) \| member index`, the value the engine's group builder assigns |
| `group-multiple-physics` | warning | a group names more than one physics resource; only the first is read |
| `subworld-depth-truncated` | warning | `--max-depth` stopped the walk before a sub-world |
| `object-unresolved`, `member-null`, `group-no-members` | warning | structurally empty entries |

Sweep every level in the game with `--all` (which takes no path) -- the regression check to run
after editing a mod:

```
FrostyCLI.exe --game-path "..." level validate --all
FrostyCLI.exe --game-path "..." level validate --all --format json
```

```
Result: 44 level(s), 653940 placements, 0 with findings: 0 error(s), 0 warning(s)
```

Exit codes: **0** clean, **2** findings were reported, **1** usage or load failure. Warnings alone
exit 0 unless `--strict` is passed, which promotes them. `--max-issues N` caps the table listing
(default 100); JSON always carries every issue. Under `--all` a level whose root will not load is
reported as a finding rather than aborting the sweep.

Short per-instance side arrays (`InstanceObjectVariation`, `InstanceRenderingOverrides`) are *not*
reported: shipped levels routinely carry them and every consumer index-guards its reads.

The three mesh rules run **only when the target is a root `LevelData`**. Registration is a
whole-level property: a sub-world's props are routinely registered in its parent's database (as
`Levels/MP/StarKiller_01/Art_Exterior`'s are), and nothing in a sub-world names its parent, so
validating one on its own would report every such placement as unregistered. Under `--all`, which
enumerates root levels, sub-worlds are still covered through their parents.

All 44 shipped SWBF2 levels validate clean, so a finding means something genuinely differs from
retail data.

---

### Level entity list — `level list`

List all entities placed in a level.

```
FrostyCLI.exe --game-path "..." level list s2/levels/cloudcity_01/cloudcity_01

# Filter by blueprint path prefix
FrostyCLI.exe --game-path "..." level list s2/levels/cloudcity_01/cloudcity_01 --filter s2/objects/architecture

# Output as JSON
FrostyCLI.exe --game-path "..." level list s2/levels/cloudcity_01/cloudcity_01 --format json --out entities.json

# Find what is near a point, nearest first -- the way to locate something worth editing
FrostyCLI.exe --game-path "..." level list Levels/MP/StarKiller_01/Art_Exterior --near "143.5,1266.1,840.9" --radius 12
```

`--near "x,y,z"` keeps only placements within `--radius` (default 10) and sorts them by distance.
The table prints the address each row needs for `level move` / `add` / `remove`:

```
Blueprint                                       Pos (x, y, z)          Distance  Address
.../Crate_01/Crate_01                           140.8, 1263.0, 839.0       4.59  --group 0 --member 72 --instance 466
```

Ranking happens over the whole level before `--limit` applies, so the nearest placement is found
even when it sits thousands of rows into the listing. JSON rows gain a `distance` field.

When a listing comes back empty, `level list` now says what the asset actually is — the root type
and a histogram of its objects — so an empty sub-world, an asset that is not a level, and a layout
this build does not recognise are told apart instead of all reading `0 entities total`. The same
applies when a Havok resource carries no `hknp` compound shape: the error names the classes it did
find, which is how an older Havok layout announces itself.

`level list` follows a level's **layer** references as well as its sub-world references. Layers are
the dominant container -- `StarKiller_01` holds 98 layer references against 29 sub-world references
-- so stopping at them left most of a level unvisited: traversing both takes that level from 30
assets and 163 objects to 128 assets and 2,442 objects across 116 types.

Everything that names a blueprint at a transform is reported as a placement, not just static-model-
group instances: props (`ObjectReferenceObjectData`), spatial prefabs, lights, effects, decals and
sound emitters. Those rows carry `source: "entity"` and, since they are not part of a static model
group, their `group` / `member` / `instance` fields are **null** — only static-group rows are
addressable by `level move` / `add` / `remove`.

For Battlefront II, `level list` also follows a level's sub-world references and expands
`StaticModelGroupEntityData.MemberDatas` into individual placements. The transform source is chosen
**per member**: a member holding any inline `InstanceTransforms` reads all of its instances from EBX,
and only members without them draw from the group's 64-bit Havok physics resource. JSON output
identifies these rows with `source: "ebx"` or `source: "havok"` and includes the group, member and
instance indexes — the address `level move` and `level add` take.

A physics resource holds several `hknpStaticCompoundShape` objects: the group's placement array, a
duplicate second collision slot, and one per prop whose own collision is built from several pieces.
Only one of them is the placement array, and it is identified by holding exactly as many instances as
the group's members need -- `level validate` reports `transform-count-mismatch` when none does.
EBX-backed grouped placements can be moved, added and removed as described below. Havok-backed
placements can be added, moved and removed too: those commands rewrite the group's physics resource
(every collision slot's record and rebuilt AABB tree) alongside the EBX bookkeeping.

---

### Add an entity to a level — `level add`

Place a blueprint instance into a level at a given position. Requires a loaded project.

```
FrostyCLI.exe --game-path "..." level add s2/levels/cloudcity_01/cloudcity_01 \
  --blueprint s2/objects/architecture/bespin/wall_01 \
  --pos "0,5,10" \
  --rot "0,90,0" \
  --scale 1.0
```

Position uses Frostbite world space (meters). Rotation is Euler angles (degrees, Y-X-Z order).

`level add` places the prop and nothing else. The mesh still has to be registered and bundled
before it will render — use `level place` to do all three at once.

---

### Place a prop end to end — `level place`

A prop only renders when three things are true: the placement exists, the mesh is registered in a
`MeshVariationDb_Win32`, and the mesh is in a bundle the level loads. `level place` does all three
in one command, skipping whichever are already satisfied so it is safe to re-run.

```
FrostyCLI.exe --game-path "..." level place s2/levels/cloudcity_01/cloudcity_01   --group 0 --blueprint s2/objects/props/crate_01 --pos "0,5,10"
```

| Flag | Meaning |
| --- | --- |
| `--group` | static model group index (required) |
| `--blueprint`, `--pos` | as `level add`; `--rot` / `--scale` also accepted |
| `--mesh` | override the mesh, when the blueprint's cannot be resolved automatically |
| `--mvdb` | database to register in (defaults to `<level>/MeshVariationDb_Win32`) |
| `--bundle` | bundle to add the mesh to (defaults to the level's own bundle) |
| `--like` | inherit `MaterialId`s from an already-registered mesh (see `mvdb add`) |

If the placement succeeds but a later step fails, the command says so and exits non-zero — the
prop is placed but will not render until the reported step is repaired.

---

### Add a raw entity to a layer — `level add-entity`

Create an `EntityData` sub-object directly inside a layer's EBX partition and add an internal
reference to its `Objects` list. This is for entities that are not external blueprints, such as
spawn points, gameplay volumes and other layer-owned world objects. Requires a loaded project.

```powershell
FrostyCLI.exe --game-path "..." --project my-map.fbproject --save `
  level add-entity Levels/MP/StarKiller_01/SomeLogicLayer `
  --type AlternateSpawnEntityData --pos "132.33,148.91,-291.68" --rot "0,-125,0" `
  --set Team=Team1 --set Priority=1 --set Enabled=true
```

Options: `<layer-ebx-path>`, `--type <EntityData type>` (required), `--pos "x,y,z"`,
`--rot "x,y,z"`, `--scale <f>`, repeated `--set Field=Value`, and
`--transform-field <path>` when the entity's transform is not named `Transform`.

The command assigns a fresh exported GUID and unused internal object ID, registers the new object in
the partition, and writes a `PointerRef` into `Objects`. It refuses non-`EntityData` types and fails
if a requested transform or field cannot be written. It does not create connections, bundles,
network-registry entries or subordinate resources; those still need their normal asset-specific
steps. Use `--save` or `--save-as` so the edit is not discarded when the process exits.

---

### Copy lights between levels or games — `level export-lights`, `level import-lights`

Export every `Pbr*LightEntityData` a layer, sub-world, level or blueprint reaches, as world-space
records, then recreate them in another layer. The pair is a JSON hand-off so the two halves can run
against different games: read the Battlefront (2015) source with `--game swbf1`, write into a
Battlefront II layer with the default game.

```powershell
FrostyCLI.exe --game swbf1 --game-path "..." `
  level export-lights XP2/Levels/Clouds/Clouds_04/StreamedLevel --out clouds04-lights.json

FrostyCLI.exe --game-path "..." --save-as ported.fbproject `
  level import-lights S2/Levels/CloudCity_01/StreamedLevel --from clouds04-lights.json `
  --intensity-scale 1 --skip-disabled
```

`export-lights` follows container references (layers, sub-worlds) for lights placed directly, and
expands every placement the level readers know about (EBX- or Havok-backed static groups, prefab
instances) through the placed blueprint's own lights, composing transforms through each level of
instancing. Blueprint contents are read once and reused per placement. `--max-depth <n>` bounds the
blueprint nesting (default 4); anything it could not resolve is listed under `skipped`.

`import-lights` creates one entity per record with a fresh GUID and internal id, writes the
transform, and copies each exported field **by name onto the target type**. Fields the target does
not have (2015's `Temperature`, `LightColorMode`) are counted under `fields_skipped`; fields the
target adds (Battlefront II's shadow-cache and cull settings) keep their defaults. Engine-managed
members (`Flags`) are never copied.
Options: `--from <file>` (required), `--intensity-scale <k>` multiplies `Intensity`,
`--offset "x,y,z"` shifts every light, `--skip-disabled` drops records whose `Enabled` is false.
Requires a loaded project; use `--save` / `--save-as`. Exposure differs between the two games, so
expect to tune `--intensity-scale`; nothing here has been checked in-game.

**Pointer-valued members — IES profiles and a rectangular light's texture — are carried too.** They
are recorded by **file GUID**, because the two games ship much of the Bespin content unchanged under
identical GUIDs: 2015's `T_IES_Hotel_01/04/05` all resolve in Battlefront II under its own names. On
import each target is resolved by GUID first (which finds the asset whatever the destination calls
it), then by the recorded path as a fallback for an asset that was kept but renamed. A target the
destination genuinely lacks is **reported, not written** — a pointer at an absent asset is worse than
a light with none — and counted under `refs_unresolved` / `refs_skipped`.

Exporting `XP2/Levels/Clouds/Clouds_04/StreamedLevel` and importing into Cloud City:

```
records: 784   added: 781
refs_written: 150   refs_unresolved: 2
refs_skipped: Texture -> XP2/Objects/_Lighting/T_CeilingMask_01_M (not in this game):2
```

152 of those lights carried a target that earlier builds discarded: 12 IES profiles and 140 light
textures. 150 resolved straight onto Battlefront II's own copies; the 2 that did not are a texture
the game genuinely does not ship. Recreating an absent target from a packaged payload (`import
resource` handles `IesResource`) is not done — measured against real data it is the ~1% tail, so the
reuse path carries the workflow.

---

### Remove an entity from a level — `level remove`

Delete a placed entity by its instance GUID (get GUIDs from `level list`). Requires a loaded project.

```
FrostyCLI.exe --game-path "..." level remove s2/levels/cloudcity_01/cloudcity_01 \
  --guid 82f196f5-ba34-4732-8b72-fb547f581497
```

---

### Reposition an entity — `level move`

Move/rotate/rescale an existing entity by its instance GUID. Requires a loaded project.

```
FrostyCLI.exe --game-path "..." level move s2/levels/cloudcity_01/cloudcity_01 \
  --guid 82f196f5-ba34-4732-8b72-fb547f581497 \
  --pos "0,5,10" --rot "0,90,0" --scale 1.0
```

Options: `<level>`, `--guid <instance-guid>` (required), `--pos "x,y,z"` (required), `--rot "x,y,z"`, `--scale <f>`.

---

### Edit a Battlefront II static placement — `level move`, `level add`, `level remove`

BFII static props are not entities in a flat list: they are instances of a member inside a
`StaticModelGroupEntityData`, addressed by the `group`, `member` and `instance` indexes that
`level list --format json` reports. Pass `--member` (with `--instance` for `move` and `remove`)
instead of `--guid` to edit one. Requires a loaded project.

```
# Move one instance. Omit --rot to keep the existing rotation.
FrostyCLI.exe --game-path "..." --project my.fbproject \
  level move Levels/Frontend/Backdrop_01 --group 0 --member 0 --instance 0 --pos "11.5,22.25,-33.75"

# Place another instance of a blueprint the member already carries.
FrostyCLI.exe --game-path "..." --project my.fbproject \
  level add Levels/Frontend/Backdrop_01 --group 0 --member 0 --pos "100,200,300"

# Delete one instance.
FrostyCLI.exe --game-path "..." --project my.fbproject \
  level remove Levels/Frontend/Backdrop_01 --group 0 --member 0 --instance 1
```

Remember `--save` (or `--save-as`): without it the edit is discarded when the process exits.

`--group` defaults to 0. `level add --from-instance K` (default 0) chooses which existing instance
the new one copies its per-instance settings from; the new instance takes the next index on that
member.

**EBX-backed members are the simple case.** A member holding inline `InstanceTransforms` owns its
placements outright — across every shipped SWBF2 level such members always carry
`PhysicsPartCountPerInstance == 0` and `NetworkIdCountPerInstance == 0`, so they have no physics
parts and no network ids, and an edit touches nothing else.

**Havok-backed members are handled through the physics resource.** `level move` rewrites the
placement's record in every collision slot (rotation, translation, scale and the packed flag word)
and rebuilds each slot's AABB tree; without `--rot` the current rotation and scale are kept. `level
remove` drops the record from every slot, renumbers the part tags after it, shrinks the resource's
part tables, and on the EBX side lowers `InstanceCount` and `PhysicsPartRange`, shifts every later
member's ranges, and removes the part's `PartBoundingBoxes` and physics `Parts` entries. Removing a
member's last instance is refused, since dropping the member itself would renumber every later one.

**Adding to a Havok-backed member is supported.** `level add <level> --group g --member m --pos ...`
detects that the member has no inline transforms and grows the group's `HavokPhysicsData` resource
instead: a copy of `--from-instance` (default 0) with the new transform is inserted after the member's
last record in every level compound that holds the member (collision, raycast and, where present,
character collision), the part tags after it are renumbered, each compound's AABB tree is rebuilt from
the shipped leaf boxes plus the new one, and the resource's reloc header and 16-byte res meta are
regenerated. On the EBX side the member's `InstanceCount` and `PhysicsPartRange` grow, every later
member's ranges shift, and `PartBoundingBoxes` and the physics component's `Parts` gain the new part
entries. Compound, convex-hull, sphere and capsule collision roots are supported, multi-part
(destructible) props contribute every part with its own bounding box, a prop without a separate
raycast shape reuses its collision shape in that slot as shipped groups do, and a shape class the
level's resource has never named is added to its class table. The unedited resource writes back
byte-identical, and `level validate` passes on the whole level afterwards. Mirrored
(negative-determinant) transforms are refused because the pipeline bakes a mirrored copy of the
shape for those. Not yet verified in-game. Use
`level validate` to see the EBX/Havok split before planning an edit, and `level stats` for the
totals.

`level add` and `level remove` touch a per-instance side array (`InstanceObjectVariation`,
`InstanceRenderingOverrides`, ...) only when it is exactly as long as the instance count. Shipped
levels carry deliberately short ones and every consumer index-guards its reads, so appending to a
short array would hand its value to the instance at the old tail, and trimming one would re-point
every entry after the hole.

Removing a member's last instance leaves the member in place with no instances -- it renders
nothing, and dropping the member itself would renumber every later member, which these commands
never do.

#### Placing a blueprint the group has never held

`level add --group G --blueprint <bp> --pos "x,y,z"` creates a **new member** rather than another
instance of an existing one. This is the case a map port is made of, since every prop in an
imported map is new to its group.

```
FrostyCLI.exe --game-path "..." --save-as my-edits.fbproject \
  level add Levels/Frontend/Backdrop_01 --group 0 \
  --blueprint Objects/Props/ObjectSets/Starkiller/Crate_01/Crate_01 --pos "5,6,7"
```

`MeshAsset` is read from the blueprint's `StaticModelEntityData.Mesh`; pass `--mesh <ebx>` when the
blueprint does not name one. Passing `--group` with `--blueprint` selects this path (or
`--as-group-member` explicitly); without it, `--blueprint` still takes the flat entity-list path.

The member is built the way the engine's `StaticModelGroupBuilder` builds one. `MemberType`
references the **entity inside the blueprint** (`ObjectBlueprint.Object`), not the blueprint's root:
the client casts that reference to an entity and reads its components, and pointing it at the root
is what made earlier builds of this command crash the level on load. `HealthStateEntityManagerId`
is `(group index << 16) | member index`, and `PartComponentCount` counts the entity's
`PartComponentData` components.

If the prop ships a physics resource (`<blueprint>_physics_win32`), the member is created
**Havok-backed with real collision**: the prop's shape graph is transplanted verbatim into the
group's physics resource, one instance of it is placed in each collision slot the prop provides a
root for, the group's part count grows by the prop's part count, and `PhysicsPartRange`,
`PartBoundingBoxes` and the physics component's `Parts` are extended to match. Since the new parts
go after every existing one, no other member's ranges move. A prop without a physics resource is
created EBX-backed (one inline transform, no parts) and renders without collision, like the
EBX-backed members shipped levels carry. Neither variant has been verified in-game yet.

The command also **bundles** what the member needs: the blueprint, the mesh with its MeshSet res,
geometry chunks and shader-block res, every texture its materials reference, and the physics asset
with its res. Shipped members' blueprints sit in the sub-world's own bundle (Endor's lights are in
`win32/Levels/MP/Endor_01/Endor_01`, not in the sibling `Shared_Art`), so that is the default
target; `--bundle <name>` overrides it and `--no-bundle` skips the step. Everything already present is left alone, so re-adding a prop the level ships is a no-op.
Registering the mesh in the sub-world's `MeshVariationDb_Win32` (`mvdb add`) is still a separate
step.

Two things this does not do, and warns about: the mesh must also be registered in the sub-world's
`MeshVariationDb_Win32` (`mvdb add`) and bundled with the level (`bundle add --with-resources`), or
it will not render.

---

### Scaffold a SubWorld — `level scaffold`

Clone an entire SubWorld structure (e.g. `supremacy` from CloudCity Beta 3) to a new prefix, deep-copying every asset, assigning fresh file GUIDs, and remapping internal references. This is how you stamp out a new SubWorld container. Requires a loaded project.

```
FrostyCLI.exe --game-path "..." level scaffold s2/levels/cloudcity_01/supremacy \
  --as s2/levels/cloudcity_01/cryochamber
```

Options: `<template-prefix>`, `--as <dest-prefix>` (required), `--types A,B,...` (clone only these EBX types;
everything else under the prefix stays shared), `--keep-instance-guids` (keep each cloned root's instance GUID —
without it the root is re-rolled while references into it are remapped by file GUID only).

> **Fixed 2026-09-21:** reference remapping used to be a silent no-op — `PointerRef.External` has no setter, so
> writing it back through reflection did nothing and every clone kept pointing at its template. The planner now
> builds a new `PointerRef`, remaps every object of the partition (not only what the root reaches) and returns the
> whole cloned partition (`Plan_Result.NewAsset`), so exported objects that only other partitions reference survive.

---

### Duplicate a whole level under a new name — `level dupe`

Make a separately loadable copy of a shipped level under a name that did not ship. **Nothing shipped is replaced:**
the copy has its own LevelData, sub-world tree, bundles, network registries, MVDBs, per-level physics RES and
registration, and the original level keeps loading untouched with the mod installed. Verified in game on
`Levels/MP/Tatooine_01` and `S2/Levels/CloudCity_01` (see `I:\New Levels\FINDINGS.md`).

```
FrostyCLI.exe --game swbf2 --game-path "..." --save-as mymap.fbproject \
  level dupe Levels/MP/Tatooine_01/Tatooine_01 --as Levels/MP/MyMap_01/MyMap_01
FrostyCLI.exe ... --project mymap.fbproject mod build --out mymap.fbmod
# load it (test harness): hookspawn Levels/MP/MyMap_01/MyMap_01 SkirmishBlast
```

About a minute for Tatooine (187 structure assets, 28 bundles, ~80k memberships). What it does, in order:

1. Clones the level **structure** under the new name — `LevelData, SubWorldData, LayerData, WorldPartData,
   LevelDescriptionAsset, MeshVariationDatabase, NetworkRegistryAsset` (override with `--types`). The level's own
   leaf is renamed where it leads a name (`Tatooine_01` → `MyMap_01`, `Tatooine_012` → `MyMap_012`). Whole
   partitions, instance GUIDs kept; cross-partition references remapped; `BundleName` / `LevelName` / bundle-list
   strings retargeted. Fails if any reference still points at a cloned host partition.
2. Copies the per-level RES the structure references by RID (static-model-group physics = placement transforms and
   collision, terrain decals) under the new path and repoints them, so `level move/add/remove` on the copy never
   touches the host.
3. Registers the level: the description's `LevelName`, `LevelGuid` (the LevelData's instance GUID) and bundle list
   point at the copy, it joins `win32/gameplay/wrgameconfiguration`, and its **file** GUID is appended to
   `LevelListReport.BuiltLevels`.
4. Creates one bundle per shipped bundle of the host and mirrors every shipped membership into it (minus the cloned
   originals and the copied RES), linking meshes/textures and taking chunk H32/FirstMip from the BundleManager cache
   (`F:\Frosty\Frosty Editor\Caches`, needed once; `--frosty-plugins <dir>` to point elsewhere).

Content — meshes, textures, blueprints, terrain, lighting, schematics — stays **shared by reference** and ships as
membership only; replace or clone it on the copy like any other asset. `SchematicsAsset` is deliberately not cloned:
its `InstanceType` names a class compiled into the executable, and a renamed copy resolves to nothing.

**Several new levels go in one project.** Every copy appends to `LevelListReport`, and EBX edits are last-mod-wins in
Frosty, so two separately built dupe mods unregister each other. Run `level dupe` repeatedly on the same project.

Options: `<shipped LevelData path>`, `--as <new level path>` (required), `--types`, `--frosty-plugins <dir>`,
`--bundle-manager` (also run the BundleManager pass — not needed, and it clears bundle edits it did not compute),
`--no-mirror`.

### Bundle helpers — `bundle contents`, `bundle mirror`, `bundle complete`

```
bundle contents <bundle> [--out file.tsv]          # every ebx / res / chunk member (chunks with h32 / firstMip)
bundle mirror <src-bundle> --to <dst-bundle> [--skip-cloned <from-prefix>=<to-prefix>] [--skip-prefix <p>]
bundle complete [--level <bundle>,...] [--frosty-plugins <dir>]   # headless BundleManager pass
```

`mirror` adds membership only, links mirrored meshes/textures to their res (fills chunk H32/FirstMip) and reports
`chunksWithH32Zero` — ship none: a texture chunk with H32 0 in a new bundle fails at D3D creation. Run `bundle
complete` **before** `mirror`, never after: the pass clears every bundle edit it did not compute itself.

### Retarget strings across assets — `ebx replace-string`

```
ebx replace-string <asset-prefix> --from <text> --to <text> [--type <EbxType>]
```

Rewrites every `string`/`CString` field (list elements and nested structs included) in every object of every asset
under the prefix — the way to reach `SubWorldReferenceObjectData.BundleName` on internal objects.

---

### Edit mesh variations — `mvdb`

Read and edit MeshVariationDatabase entries, which control which materials/textures a mesh uses. `mvdb list` is read-only; `add`/`remove` require a loaded project.

```
# List entries in a mesh variation database
FrostyCLI.exe --game-path "..." mvdb list s2/levels/cloudcity_01/cloudcity_01 --filter wall

# Add a variation entry
FrostyCLI.exe --game-path "..." mvdb add s2/levels/cloudcity_01/cloudcity_01 \
  --mesh s2/objects/architecture/bespin/wall_01 \
  --variation-hash 0x12345

# Remove all entries referencing a mesh
FrostyCLI.exe --game-path "..." mvdb remove s2/levels/cloudcity_01/cloudcity_01 \
  --mesh s2/objects/architecture/bespin/wall_01
```

`mvdb add` derives every material record from the mesh's internal materials, including its
surface-shader hash and texture references. `mvdb copy <db> --mesh <mesh> --from <db>` clones a
mesh's entry from another database instead, keeping the shipped ids; it is the route for meshes
whose materials bind an internal shader instance, which `mvdb add` cannot derive. The variation hash defaults to `0` and accepts either
unsigned decimal or `0x`-prefixed hexadecimal values.

**`MaterialId` and `--like`.** `MaterialId` is per material *instance* and its derivation is
unknown, so a fresh entry writes `0`. Shipped entries carry real non-zero ids. `--like <mesh>`
copies the donor's complete material records (ids, shader identity and texture parameters),
matching materials by position and replacing their Material pointers with the new mesh's. This is
correct when the new mesh is a clone of the donor and keeps its compiled material permutation.

```
FrostyCLI.exe --game-path "..." mvdb add levels/mp/kamino_01/kamino_01/MeshVariationDb_Win32   --mesh objects/props/crate_01_copy_mesh --like objects/props/crate_01_mesh
```

### Duplicate a mesh variation — `mesh variation-dupe`

Copies an ObjectVariation as a new variation with its own look. It can change the copy's textures or vector
parameters and point placements or character skins at it. The source variation is left unchanged.

```
FrostyCLI.exe --project mod.fbproject --save mesh variation-dupe \
  --source S2/Objects/Props/ObjectSets/Bespin/BespinContainer_01_A/BespinContainer_01_A_AdministratorPalace \
  --as     S2/Objects/Props/ObjectSets/Bespin/BespinContainer_01_A/BespinContainer_01_A_Magenta \
  --texture _CS=S2/Objects/Props/ObjectSets/Bespin/BespinContainer_01_A/T_MyContainer_CS \
  --repoint S2/Levels/CloudCity_01/StreamedLevel
```

| Option | Does |
|---|---|
| `--mesh <mesh>` (repeatable) | only these meshes (default: every mesh the source has a depot for) |
| `--texture <param>=<texture>` | bind a texture in the depot and the database row, e.g. `_CS=...` or `BaseColor=...` |
| `--vector <param>=x,y,z,w` | set a vector parameter |
| `--mvdb <database>` | databases to register in (default: every MeshVariationDatabase that depends on the source) |
| `--repoint <partition>` | switch that partition's placements of the source to the copy: static-group members via `InstanceObjectVariation`, `ReferenceObjectData` via `ObjectVariation` |
| `--repoint-all` | also switch instances that use the default look (hash 0) |
| `--skin <..._dccb>` | point a character skin's parts at the copy (clone skins) |
| `--report <file>` | write a JSON report |

The copy is written the way BFII ships a variation:
- `NameHash` is Fnv1 of the new name.
- Instance GUIDs are fresh.
- Every mesh gets a new shader block depot at `<variation>/<meshfile>_<meshNameHash>/shaderblocks_variation/blocks`.
  Its per-LOD entries are re-keyed to `CityHash64(meshNameHash, variationHash, lod)`.
- Database rows point at the copy's own `MeshMaterialVariation` objects.
- Every texture it binds is bundled together with its res and chunks, linked.

The problems it avoids (each one caused "no change" or a crash before):
- **The depot is written as a new res, not edited in place.** An in-place edit is stored as a delta keyed by block
  hash, so every re-keyed block is dropped.
- **Parameter blocks are salted.** The game caches them by hash, so an unsalted copy shows the old values.
- **Depot texture slots take the texture's root instance guid.** A file guid there crashes at `game+0x1522B97`.
- **Texture chunks are linked** (H32/FirstMip). Unlinked chunks in a new bundle fail with E_INVALIDARG at
  `game+0x161C7BD`.

For **clone skins**, pass `--skin` for every `_dccb` that should wear the copy and `--mvdb` for those skins'
databases (`..._bpb/MeshVariationDb_Win32`).
- A part that pairs a mesh with a variation (`MeshAndVariationPair`) is repointed.
- A part that uses a plain mesh blueprint gets a new pair.
- The player's default skin depends on planet, era and profile. For example, the Kamino skirmish player is a Phase 1
  clone.

Tested in game (2026-09-27):
- The Cloud City containers were switched to a magenta copy.
- The 104th/501st clone assault body, helmets and 1P arms were switched to a magenta copy and pointed at by the
  planet-default skins.

`mesh variation-plugin-run --plugin <MeshVariationStudioSwbf2.dll> --mesh <mesh> --template <variation> --name <n>
[--set-texture p=tex] [--repoint <partition>]` loads the Mesh Variation Studio editor plugin. It then runs that
plugin's own create and sync code headlessly, so the plugin can be tested in game without clicking through the editor.

---

### Port a prop between games — `port export`, `port import`

Moves a static prop blueprint from one game to another through a package directory. The asset
manager is bound to one game per process, so the port is two commands:

```
# 2015 side: FBX, DDS textures, physics bytes and a manifest
FrostyCLI.exe --game swbf1 --game-path "E:\STAR WARS Battlefront" \
  port export XP2/Objects/Props/PipeReflective_01/PipeReflective_01 --out C:\port\pipe \
  --mvdb XP2/Levels/Clouds/Clouds_04/StreamedLevel/MeshVariationDb_Win32

# Battlefront II side: textures, mesh, materials, physics and blueprint in the project
FrostyCLI.exe --project mod.fbproject --save \
  port import C:\port\pipe --as Objects/Props/ObjectSets/Bespin/Ported/PipeReflective_01/PipeReflective_01 \
  --mvdb levels/mp/endor_01/endor_01/MeshVariationDb_Win32 --bundle win32/Levels/MP/Endor_01/Endor_01

# then place it like any shipped prop
FrostyCLI.exe --project mod.fbproject --save level add levels/mp/endor_01/endor_01 \
  --group 0 --blueprint Objects/Props/ObjectSets/Bespin/Ported/PipeReflective_01/PipeReflective_01 --pos 10,60,10
```

`port export` writes `manifest.json`, `mesh.fbx`, `textures/*.dds`, `physics.bin` and a deep dump of
the blueprint. Texture parameters come from a MeshVariationDatabase (2015 meshes keep none on the
mesh itself); pass `--mvdb` to name the level's database, else every database is scanned.

`port import` reuses anything the target game already ships under the same file GUID (textures and
shaders of the Bespin object set are shared between the two games unchanged) and creates the rest:

- **Textures** not present are imported as new assets cloned from `--texture-template` (default: a
  texture of the same prop that was found, else `T_MetalBase_01_CS`).
- **Mesh**: a target mesh is cloned as the template (`--mesh-template`, else picked automatically: a
  mesh using the same shader with the same LOD count, or more LODs which are trimmed, else any mesh
  with the same LOD and material count). Its sections are renamed to the source's so the FBX maps
  onto them, materials are added when the source has more, the template's occluder mesh is dropped,
  and its shader block depot is duplicated so the texture parameters can be written into it. When the
  source has more sections per LOD than the template, the depot is grown too (one shader block per
  section, reported as `N shader block section(s) added to the depot`); a section without a block
  imports silently and then crashes the game the first time it is drawn.
- **Materials** keep the source shader when the target has it, else `--shader-fallback`
  (default `Shaders/Presets/SS_PropsMetallicPreset`). Texture parameters go on the mesh EBX, into
  the depot when the template's blocks know the name, and into the MVDB entry via `--mvdb`.
  `--keep-template-materials` instead leaves the template shader and parameters intact and copies
  its complete MVDB material record onto the clone. Use this when the destination level has no
  compiled permutation for the source shader; appearance follows the template until deliberately
  remapped.
- **Composite meshes** are imported as rigid meshes (their sections already sit in part pose in the
  FBX; the importer's composite path needs part bones a foreign FBX lacks). `--keep-composite`
  forces a composite template.
- **Physics**: a HavokAsset cloned from `--physics-template` (default: the blueprint template's)
  with the source bytes; 2015 resources are byte-compatible.
- **Blueprint**: cloned from `--blueprint-template` (default: the Bespin `CryoChamber_MetalPipe_S64_01`)
  with mesh, rigid-body assets and part boxes rewired. `--bundle` adds everything created with
  resources; `--no-physics` skips the collision.

Shaders that the target game lacks cannot be ported (they are compiled into the shader database);
the fallback keeps the mesh rendering but the look is generic. Every substitution is reported.

**Shader permutations are per level.** Every level bundle carries four `rvmdatabase_*` resources
(dx12pc, dx12nv, dx11, dx11nv) of compiled shader permutations, and each mesh's shader block depot
references permutations in them by GUID. A prop that BFII ships can still crash a level on load
when that level's databases lack its permutations: the Bespin `CryoChamber` props only exist in the
single-player root databases. Until permutations can be merged, swap the target level's databases
for a level's that carries them:

```
FrostyCLI.exe --project mod.fbproject --save res copy   levels/sp/rootlevel/rootlevel/rvmdatabase_dx12pcrvmdatabase   --to levels/mp/endor_01/endor_01/rvmdatabase_dx12pcrvmdatabase --fast   # and the three other variants
```

`--fast` keeps LZ4 compression on the copied data (hundreds of megabytes; the shipping zstd pass
would take minutes). The SP root databases are a superset of Endor's own permutations for Endor's
meshes, so the swap loses nothing there; check other pairs before relying on it. None of the 346
community mods in the local corpus touches an RVM database, so treat the swap as a last resort:
pick a target level that already ships the props' shaders (Bespin props into Cloud City) first.

**Do all four, and check that you did — `rvm check`.** The swap is per resource and the merge tool
handles DX11 only, so both routes fail the same way: three of the four variants get left behind, the
level loads on the GPU path you edited, and it can crash on someone else's hardware. Nothing in the
project says so, which is what makes it worth a command:

```
FrostyCLI.exe --project mod.fbproject rvm ls s2/levels/cloudcity_01/cloudcity_01
FrostyCLI.exe --project mod.fbproject rvm check s2/levels/cloudcity_01/cloudcity_01
FrostyCLI.exe --project mod.fbproject rvm check --all --format json
```

| Code | Severity | Meaning |
|---|---|---|
| `rvm-variants-partial` | error | the project modifies some but not all of a level's permutation databases — apply the same change to every variant |
| `rvm-variant-missing` | warning | the level carries fewer than the four expected variants |

`rvm ls` prints the four databases with their present/modified state. Exit codes match
`level validate`: **0** clean, **2** findings, **1** usage or load failure, `--strict` promoting
warnings. Neither subcommand parses database *contents* — the record format is not known here, so
this reports what is decidable from the resources rather than guessing at permutation coverage.

Better than swapping: **merge** the permutations a mesh needs into the level's own databases. The
database is content-addressed (fixed-size records referencing each other by hash), so the missing
permutation set and its dependency closure can be appended without touching anything else. The
merge tool lives in the Glacier workspace (`examples/sdk/rvm_merge.rs`, DX11 databases):

```
mount_game --dump-res s2/levels/cloudcity_01/cloudcity_01/rvmdatabase_dx11rvmdatabase --out cc.bin
mount_game --dump-res levels/sp/rootlevel/rootlevel/rvmdatabase_dx11rvmdatabase --out sp.bin
rvm_merge --target cc.bin --source sp.bin --guids refs.txt --out merged.bin --verify
FrostyCLI.exe --project mod.fbproject --save res import merged.bin --as s2/levels/cloudcity_01/cloudcity_01/rvmdatabase_dx11rvmdatabase --framed
```

`refs.txt` holds the `RvmShaderRef` GUIDs from the meshes' `<mesh>_mesh/blocks` depots (type-4
entries). `--framed` keeps the 16-byte res meta the dump carries, which the merged database needs
since its header moves.

`res revert <res>` drops the project's modification of a resource (or removes one it added).

**Resident LODs.** Shipped Battlefront II meshes flag only the LODs whose chunks sit in the level
bundle as `IsBaseLod`; the higher LODs are streamed on demand from outside the bundle. A clone keeps
every LOD chunk in the bundle, so `port import` and `import mesh --template` now set `IsBaseLod` on
every clone LOD (reported as `N LOD(s) marked bundle-resident`). Without it the game asks the
streaming system for a chunk it does not have and draws with a null vertex buffer as soon as the
higher LOD is selected: the map loads, then crashes when you get close.

**Composite meshes.** A CompositeMeshAsset exports with a `PART_n` skeleton and a skin per section,
which Frosty's importer only accepts into a composite target (into a rigid template it throws inside
`ProcessSection`). Pass `--as-rigid` to `port export` to write the parts as plain geometry in their
rest pose (no skeleton, no skins); `port import` then treats the package like any rigid mesh.

**Inspecting a mesh — `port mesh-info <mesh>...`** prints the MeshSet's resource meta, and per LOD
the type, the geometry chunk (with the chunk entry's size, logical range, H32 and bundle count),
buffer sizes and every section's geometry window. Use it to compare a clone against the shipped mesh
it was cloned from: shipped Battlefront II meshes keep their highest LODs in chunks outside the level
bundle (streamed on demand) and the lower LODs in the bundle.

#### Whole layouts — `port export-layer`, `port import-layer`

```
# 2015 side: every static placement of the sub-world with its blueprint and transform
FrostyCLI.exe --game swbf1 --game-path "E:\STAR WARS Battlefront" \
  port export-layer XP2/Levels/Clouds/Clouds_04/StreamedLevel --out C:\port\clouds04 [--group 0]

# Battlefront II side: rebuild it as entities of a target sub-level
FrostyCLI.exe --project mod.fbproject --save \
  port import-layer C:\port\clouds04 --into s2/levels/cloudcity_01/streamedlevel \
  --mvdb s2/levels/cloudcity_01/streamedlevel/MeshVariationDb_Win32 --offset 40,14,-82 \
  --ported-prefix Objects/Props/ObjectSets/Bespin/Ported [--prefer-ported] [--limit 500] [--dry-run]
```

Each source blueprint is resolved once: the same file GUID in the target game, else a ported copy
under `--ported-prefix` (`<prefix>/<name>/<name>`, what `port import --as` produces), else an
`ObjectBlueprint` with the same basename. `--prefer-ported` checks the ported copy before the GUID,
which is useful when the target game ships the same asset under a shader database the destination
level does not load. `--only-guid` disables the name match; `--only-ported` keeps only the ported
copies. Every placement becomes an `ObjectReferenceObjectData` entity of the
target level, the way community map mods add props, written in one pass (the level is loaded once
and written once; 8,117 placements take about ten seconds). For each distinct blueprint the whole
EBX dependency closure (blueprint, class schematics, physics asset, mesh, LOD group, shader graphs,
textures, with their res and chunks) is added to the level's own bundle, and the mesh is registered
in `--mvdb` by **copying** its entry from a database that already has it (a shipped prop's textures
live in that entry, not in the mesh), deriving one only when none does. Target the sub-level that
holds the map's geometry (Cloud City's `streamedlevel`), whose bundle and database are what the
game loads with it. `--limit` counts placements made, `--source-group` restricts to one source
group, `--new-members-only` skips blueprints the level already places, `--continue` keeps going
past a failure. Unresolved blueprints are skipped and listed with their placement counts. Run
`level validate` afterwards: it names any `Objects[]` entry that lost its target.

`--as-members --group <i>` keeps the older route that appends members to a `StaticModelGroup`;
Battlefront II crashes on load with a new member in every form tried, so it is research only.

---

### Bundle membership — `bundle`

List bundles, see which bundles an asset is in, or add an asset to a bundle so the built mod ships it there. `bundle ls` is read-only; `bundle add` requires a loaded project.

```
# List all bundles (name + type)
FrostyCLI.exe --game-path "..." bundle ls --limit 50

# Which bundles is an asset already in?
FrostyCLI.exe --game-path "..." bundle ls s2/characters/heroes/hero_han

# Add an asset (ebx, res, or chunk-GUID) to a bundle
FrostyCLI.exe --game-path "..." bundle add s2/objects/custom/my_blueprint \
  --to win32/s2/levels/cloudcity_01/cloudcity_01

# Add a mesh/texture AND its linked res + chunks to the bundle (so a new asset ships complete)
FrostyCLI.exe --game-path "..." bundle add s2/objects/custom/my_mesh \
  --to win32/s2/levels/cloudcity_01/cloudcity_01 --with-resources
```

```
# Remove a project-added bundle membership (inverse of add)
FrostyCLI.exe --game-path "..." bundle remove s2/objects/custom/my_blueprint \
  --from win32/s2/levels/cloudcity_01/cloudcity_01
```

Options for `add`: `<asset>` (ebx path, res path, or chunk GUID), `--to <bundle-name>` (required — must be an existing bundle), `--with-resources` (also pull the asset's linked sub-resources into the bundle: mesh → MeshSet res + LOD geometry chunks; texture → Texture res + pixel chunk; other single-`Resource` assets → their res). The asset is marked dirty so a subsequent `mod build` includes the membership. `remove` takes `--from <bundle-name>` and undoes a **project-added** membership (it doesn't strip base-game bundle membership).

---

### Inspect blueprint connections — `connection ls`

List a blueprint's Property / Link / Event connections — the logic wiring the Frosty GUI's Connection Editor shows. Read-only; useful for auditing spawn managers, state machines, and SubWorld logic.

```bash
# All connections on a blueprint, as source.field -> target.field
FrostyCLI.exe --game-path "..." connection ls s2/objects/custom/my_blueprint

# Only property connections
FrostyCLI.exe --game-path "..." connection ls s2/.../my_blueprint --kind property
```

Each endpoint is shown as the referenced object (internal sub-object as `Type(instance-guid)`, external as `ext:<ebx-path>#<class-guid>`) and its field/event.

---

### Follow and check logic offline — `logic trace`, `logic lint`

```
frostycli logic trace <blueprint> <Type[tag]>.<Port> [--back] [--depth 6] [--max 300] [--kinds event,property,link]
frostycli logic lint  <blueprint>... [--scan <path-prefix>] [--max-assets 2000] [--summary] [--duplicates]
```

`logic trace` answers "what does this reach" (or, with `--back`, "what feeds this") from the files, across
blueprints: into a prefab reference through its Interface port of the same name, back out to the reference
that entered it, and into the file an external endpoint lives in (GameModes wiring GameModes_Flow). An entity
decides for itself what an input does, so the trace carries on from all of its outputs; read the chain.
Lines are in the live feed's form, so an instance can go straight to `game fire` / `game inspect`:

```
frostycli logic trace UI/InGame/Spawn/Screens/SpawnScreen "UIElementWidgetReference[22].OnOkAction"
  E UIElementWidgetReference[22].OnOkAction -> EventGate[82].In  @UI/InGame/Spawn/Screens/SpawnScreen
    E EventGate[82].Out -> CompareBool[50].In  @UI/InGame/Spawn/Screens/SpawnScreen
      E CompareBool[50].OnTrue -> CompareBool[58].In  @...
        E CompareBool[58].OnTrue -> KitLimitationLock[90].0x217ae527  @...
```

`logic lint` reads wiring mistakes before a launch: `dangling` (an endpoint that is null, not in its file,
or in a missing file/export), `no-port` (a wire into a logic prefab reference on a port its blueprint's
Interface doesn't have), `subworld-pc` (a SubWorldReferenceObject with IsWin32SubLevel false never streams
on PC) and, with `--duplicates`, the same wire twice. Every check was calibrated on the shipped game: 0
issues across ~12,300 shipped blueprints and levels, so what it reports on a mod is worth reading. Exit 3
when it finds something. With `--project <file>` it reads the project's version of each asset.

### Author blueprint connections — `connection add`, `connection remove`

Wire one object's field to another's inside a blueprint — the logic half of modding, as opposed to
the geometry half. Requires a loaded project.

```bash
FrostyCLI.exe --game-path "..." --project my-mod.fbproject --save \
  connection add Addons/Mode1/Mode1/UI/InGame/Hud/Shared/PF_IndexObjectiveIconSelect \
  --source 00000000-0000-0000-0000-000000000001 --source-field Value \
  --target 00000000-0000-0000-0000-00000000001c --target-field In0

# Drop the connection at index 3 of the property array
FrostyCLI.exe --game-path "..." --project my-mod.fbproject --save \
  connection remove <blueprint> --index 3
```

Endpoints are the instance GUIDs `connection ls` prints. By default both objects must live in the
blueprint's own EBX partition — these are internal refs, which is exactly why `ebx set-ref`
(external file references) cannot author them.

#### Endpoints in another asset

Most real logic is cross-asset. Kashyyyk's mode graph is 63 event connections stored on the
`SubWorldData` of `Levels/MP/Kashyyyk_01/GameModes`, but its endpoints live in a different partition,
`Levels/MP/Kashyyyk_01/GameModes_Flow`. `--source-ebx` / `--target-ebx` name that other asset; the
selector is then resolved *inside* it and the endpoint written as an **external** ref (the other
asset's file GUID plus the object's exported instance GUID). Either side, or both, may be external.

```bash
FrostyCLI.exe --game-path "..." --project my-mod.fbproject --save   connection add Levels/MP/Kashyyyk_01/GameModes --kind event   --source-ebx Levels/MP/Kashyyyk_01/GameModes_Flow   --source 34be33c7-b369-4bd6-bb09-be587d537252 --source-field OnTrue   --target f99dbaa2-03bc-4eb8-b101-845285427988 --target-field StreamIn   --realm Server
```

The other asset is registered as a dependency of the one being edited, because bundling walks
`EbxAsset.Dependencies` — a cross-partition endpoint missing from that list can be left out of the
bundle and the reference then resolves to nothing at load, silently.

Three things are refused rather than written, because each produces a connection that authors
cleanly and does nothing in game:

| Refused | Why |
|---|---|
| an endpoint object with no **exported** instance GUID | an external ref addresses its target by that GUID; without one it points at nothing (`LayerReferenceObjectData`, for one, is not exported) |
| `new:<Type>` together with `--source-ebx`/`--target-ebx` | `new:` creates the object in the asset being edited; across assets there is nothing here to save it into, so it would be dropped |
| `--source-ebx` naming the asset being edited | an external ref to a partition's own file GUID is not an internal ref by another name; the runtime reads it as a broken import |

An unrecognised `--option` is now an error too. It used to be skipped while its *value* — which does
not start with `--` — became the asset path, so a flag an older build did not know silently
retargeted the whole command at that value and wrote the connection into the wrong file.

The field on each side may be a **readable name**, which is hashed, or an explicit `0x` id when the
name is unknown. Shipped data stores the field twice — `SourceFieldId` as a signed int and
`SourceField` as the `"0x…"` string beside it — and both are written. The hashing was verified
against `PF_IndexObjectiveIconSelect`, where `Value`, `StringId`, `In0` and `In1` hash to the
`0x0d6ef36e`, `0xd2c4765d`, `0x0b87d372` and `0x0b87d373` the shipped connections carry.

`--kind property|link|event` picks the array (default `property`); `--index` on `remove` is the
position within that array, as `connection ls` lists it.

> ⚠️ **Realm decides whether a connection does anything.** Prefer `--realm` over a raw `--flags`
> value. Across 285,632 shipped `PropertyConnection` instances the realm bits are `Client` 51.8%,
> `Server` 27.0%, `ClientAndServer` 21.2%; the default is `ClientAndServer`, which is not the most
> common but is the only one that behaves on both sides. A connection on the wrong realm authors
> cleanly and is silently inert — if one behaves oddly in game, compare it against a shipped
> connection of the same shape via `connection ls`.

**Not verified in-game.** The written data round-trips and matches the shape of shipped connections,
but no authored connection has been confirmed to drive behaviour in a running match — use
`mod test` and check the result yourself.

---

### Material / shader parameters — `material set`

Set a shader parameter on a mesh's material, updating its **ShaderBlockDepot** (what the game reads when rendering). Requires a loaded project.

```
FrostyCLI.exe --game-path "..." material set s2/objects/.../foo_mesh \
  --material 0 --param Color --value "0.25,0.5,0.75,1.0"
```

Options: `<mesh-ebx>`, `--material <index>` (the material slot), `--param <name>` (a shader parameter name), `--value <v>`. The value is coerced to the parameter's existing type — `true`/`false` for bools, a single number for scalars, `x,y,z,w` for vectors, a GUID for texture params.

Only parameters that **already exist** on the material's shader block are set (it reads the parameter's type to coerce correctly; it won't add a new, mis-typed parameter). Sets the value across every LOD section that uses that material.

> ⚠️ This updates the ShaderBlockDepot the **game** reads, not the material EBX's source value (so `ebx dump` of the material still shows the old value). And it's validated structurally — **confirm the new value renders in-game** before relying on it.

---

### Localized strings — `loc`

Read, write, and search localized game strings by key. `get`/`search` are read-only; `set` requires a loaded project.

```
# Read a string (key as decimal or 0xHEX)
FrostyCLI.exe --game-path "..." loc get 0x12AB34CD

# Set a string
FrostyCLI.exe --game-path "..." loc set 0x12AB34CD --value "Cryo Chamber"

# Search string values
FrostyCLI.exe --game-path "..." loc search "Bespin" --limit 50 --out hits.json
```

Options for `search`: `<query>`, `--regex`, `--limit N` (default 200), `--out <file>`.

---

### Raw data — `res ls`, `res export`, `chunk export`

Access Res (resource) and Chunk (binary blob) assets directly. Mainly useful for debugging or when you need the raw bytes behind a texture or mesh.

```
# List res assets by prefix
FrostyCLI.exe --game-path "..." res ls s2/textures/characters --limit 50

# Export raw res data
FrostyCLI.exe --game-path "..." res export s2/textures/characters/heroes/hero_han_d --out han.res

# ...with its 16-byte res meta first (what `res import --framed` reads back)
FrostyCLI.exe --game-path "..." res export levels/mp/tatooine_01/tatooine_01/rvmdatabase_dx11rvmdatabase --out tat.full --framed

# Show a res meta
FrostyCLI.exe --game-path "..." res meta objects/architecture/tatooine/moseisley/wall_01/wall_01_512_combined_01_mesh_mesh/blocks

# Export a chunk by its GUID (GUIDs come from ebx dump output)
FrostyCLI.exe --game-path "..." chunk export 82f196f5-ba34-4732-8b72-fb547f581497 --out chunk.bin
```

The res meta is the header the game reads before the data. A ShaderBlockDepot's is `{u16 version;
u16; u32 dataLength; u32 relocTableBytes; u32 resourceCount}`, an RVM database's `{version; payload
length; header length; hash}`. **A resource whose size changes must ship with a matching meta**:
imported raw, it keeps the old one and the game faults while loading the level (SWBF2: a write
access violation at `game+0x5498867`). Export `--framed`, fix the lengths, import `--framed`. In a
pipeline: `{"op": "res-export", ..., "framed": true}`.

---

### What mods actually do — `corpus`

```
FrostyCLI.exe corpus stats                               # what is indexed
FrostyCLI.exe corpus field RateOfFire                    # values shipped mods use for a field
FrostyCLI.exe corpus asset characters/hero/anakin        # which mods modify or duplicate it
FrostyCLI.exe corpus mod "Anakin Clone Armored"          # every asset one mod touches
FrostyCLI.exe corpus conflicts "Anakin Clone Armored"    # mods that set the same fields differently
FrostyCLI.exe corpus bundles "Anakin Clone Armored"      # bundles it adds assets to
FrostyCLI.exe corpus wires --type SphereQuery            # common blueprint wiring across mods
FrostyCLI.exe corpus vanilla <asset>                     # the game's own values for one asset
FrostyCLI.exe corpus diff "Anakin Clone Armored"         # exactly what it changes vs the game
FrostyCLI.exe corpus donor <added asset>                 # what a duplicated asset was copied from
FrostyCLI.exe corpus dump-vanilla                        # index the game once; makes vanilla/diff instant
```

A corpus is thousands of shipped `.fbmod` mods parsed into one searchable index. It answers what the
editor cannot: the real range of a tunable before you change it, what a mod changes rather than what it
carries, which mods silently overwrite each other, and how other modders wired a mechanism.

The corpus is a folder (`--dir`, else `FROSTYCLI_CORPUS`, else `corpus\` beside the exe):

| File | Needed for | Made by |
|---|---|---|
| `corpus.db` | everything: field values, conflicts, wiring, bundles, per-mod asset lists | the corpus build (SQLite) |
| `asset-index.jsonl` | `diff`, `donor` — the flattened values each mod ships | the corpus build |
| `vanilla-index.jsonl` | optional: instant `vanilla` / `diff` | `corpus dump-vanilla`, from your own install |
| `strings.txt` | optional: readable hashed names | Frosty Editor's `strings.txt` |

Aggregate queries need no game and return in about a second. `vanilla` and `diff` read the game's values
straight from the install when there is no `vanilla-index.jsonl`, so they need `--game-path` until
`corpus dump-vanilla` has written one (about 2-3 minutes after the game loads; it reads shipped data only,
never a loaded project's edits, and repoints `corpus.db` at the new file). SQLite is Windows' own
`winsqlite3.dll`: nothing extra ships.

Values are flattened to `Type.Field -> value`, with references as `ext:<file guid>` and a key seen on
several objects collapsed into a list. `diff` compares floats to a relative tolerance, res ids in either
spelling and hashed names by hash, and skips internal references (object indexes differ between readers).
A change flagged `(multi)` comes from a collapsed key: a lead, not proof; `--multi` shows them.
Validated against the editor-built baseline: 99.87% of 60,873 values identical before hashed names were
resolved, the rest hashed names.

### Build and cache the asset index — `index build`

Pre-build the search catalog so `ebx search` and `ebx refs` don't need to on first use.

```
# Build catalog only (fast — a few seconds)
FrostyCLI.exe --game-path "..." index build

# Build catalog + full reference index (slow — several minutes)
FrostyCLI.exe --game-path "..." index build --refs
```

The index is cached in `%APPDATA%\FrostyCLI\` and reused until the game updates.

---

### Project management — `project`

Load, create, or save a Frosty project file (`.fbproject`).

```
FrostyCLI.exe --game-path "..." project load my-mod.fbproject
FrostyCLI.exe --game-path "..." project new
FrostyCLI.exe --game-path "..." project save my-mod.fbproject
```

---

### Build a mod — `mod build`

Package the current project as a `.fbmod` file.

```
FrostyCLI.exe --game-path "..." mod build --out cryo-chamber.fbmod --mod-name "Cryo Chamber" --mod-author "Alexia"
```

`--embed <name>=<file>` adds an embedded resource beside the icon and screenshots (repeatable).

### Offer a level on Kyber — `kyber declare`

```
FrostyCLI.exe ... --project n.fbproject --save kyber declare --level AddedContent/MyMod/Levels/MyMap/MyMap `
    --name "My Map" --modes SkirmishBlast,Blast,HeroesVersusVillains --image tile.png
FrostyCLI.exe ... --project n.fbproject kyber show
```

Kyber (the community server) lists the maps and modes a mod declares when you host. The declaration is Kyber Level
Declarator's JSON (`{"version":1,"maps":[{name, id, image, supportedModes}], "modes", "modeMappings",
"modeNameOverrides"}`, `id` = the level path Kyber launches, `image` a base64 JPEG of at most 1024 px). As in the plugin,
it is kept in the project as chunk `BEBEBEBE-BEBE-BEBE-BEBE-BEBEBEBEBEBE`, and `mod build` ships it as the embedded
resource `KyberLevelDeclarationData` and leaves the chunk out, so projects move between the CLI and Frosty with the plugin.

---

### Pre-flight a mod — `mod lint`

```
FrostyCLI.exe --game-path "..." --project my-mod.fbproject mod lint
FrostyCLI.exe --game-path "..." --project my-mod.fbproject mod lint --format json --strict
```

Checks the loaded project for the faults that produce a mod which **builds and applies cleanly but
is broken in game**. Those faults share a shape — an asset the project adds or edits is not
reachable by the runtime, so nothing renders and nothing errors — and they are all decidable without
launching the game, which is what makes them worth a command rather than a `mod test` run.

| Code | Severity | Meaning |
|---|---|---|
| `added-ebx-unbundled` | error | an EBX the project **adds** is in no bundle, so the game never loads it. `ebx clone` and `ebx create` both produce assets in this state — bundle them with `bundle add <asset> --to <bundle>` |
| `added-res-unbundled` | error | an added res is in no bundle, so its owning asset resolves to nothing at runtime |
| `added-chunk-unbundled` | warning | an added chunk is in no bundle; correct only when a res references it by id |
| `level-*` | error/warning | every level the project modifies is handed to the same validator `level validate` runs, so mesh registration, bundling, Havok tables and placement ranges are all covered — see [level validate](#check-a-level-for-structural-damage--level-validate) for the full code list |

A **modified** asset inherits the bundles it already shipped in, so only *added* assets can be
orphaned this way — which is why the unbundled rules fire on added assets only.

Exit codes: **0** clean, **2** findings, **1** usage or load failure. Warnings alone exit 0 unless
`--strict` promotes them. `--max-issues N` caps the table listing (default 100); JSON always carries
every issue. `--no-levels` skips level validation when you only want the bundling checks (much
faster on a mod that touches a large level).

`mod lint` is the static half of verification and `mod test` is the dynamic half: lint catches what
is decidable from the project, test catches what only a running game can tell you.

---

### `mod test` — does this mod load in the game?

```
frostycli mod test <file.fbmod>|--vanilla [--level S2/Levels/CloudCity_01/CloudCity_01] [--mode SkirmishBlast]
                   [--timeout 240] [--task FrostyAutoTest] [--script <autotest.ps1>] [--module <FrostyGameLink.dll>]
                   [--keep] [--feeds | --feeds-early] [--record <file> [--record-filter <f>] [--record-props]]
```

Unattended in-game load test: the mod goes alone into a Frosty Mod Manager pack (`AutoTest`), the mod
manager applies and launches the game, the FrostyGameLink module is injected into the game (and into
the process the game re-execs itself into), the engine's own first spawn is hooked to load
`--level`/`--mode` instead of the frontend, and the verdict is **LOADED** (a world came up),
**CRASH** (the process went away) or **HANG** (nothing within `--timeout`). Exit code 0 / 1 / 2 for
LOADED / CRASH-or-HANG / error. Everything happens in `tools/gametest/autotest.ps1`; the command only
writes its argument file, fires the scheduled task and waits for `autotest-result.json`.

One-time setup, from an **elevated** PowerShell (injecting into the re-exec'd game process needs
admin, so the script runs as a scheduled task with highest privileges in your interactive session):

```
powershell -ExecutionPolicy Bypass -File tools\gametest\register-autotest.ps1
```

Requirements: Frosty Mod Manager 1.0.6.3 with the game profile set up (the script edits
`%LOCALAPPDATA%\Frosty\manager_config.json`), and the FrostyGameLink module: build it once with
`tools\gamelink\build.bat` (any Visual Studio 2019+ with the C++ workload; the harness picks up
`tools\gamelink\bin\FrostyGameLink.dll`, or pass `--module`). The game's own
window comes up during the test and is closed afterwards; the runner's console stays hidden — a stray
click in it would freeze it (console quick-edit). `--mode` must be one of the level's shipped
`GameMode` inclusion values (Cloud City: Blast, HeroesVersusVillains, Mode2, Mode6, ModeC,
SkirmishBlast, SkirmishDuel, SkirmishOnslaught); a wrong value is an empty world, not a crash.
Typical timings: 40 s to LOADED when the pack was already applied, ~80 s more for a fresh apply.
`--keep` leaves the game running after a LOADED verdict so you can look at the result yourself.
`--vanilla` (or `mod test vanilla`) launches the plain game with no mod - a baseline, still with the
module injected and the level hook armed. `--feeds` turns on the live logic and property feeds right
after LOADED (see `game` below); if the game then crashes, the module log
(`C:\ProgramData\FrostyGameLink\gamelink.log`) ends with the last logic events and property writes
before it as `feed:` lines.

On a CRASH or EXIT verdict `mod test` prints a crash report: the fault, the known cause if the fault
address or one of the first stack entries is in `tools/gametest/known-crashes.json` (the 2,048-mesh
cap, sounds outside their bundle, missing imports, map-spawned _MP vehicles, ...), the stack, and the
`feed:` lines. `frostycli game crash` prints the same for the newest crash in the log (e.g. one during
your own play). Add entries to `known-crashes.json` as crashes get diagnosed.

`--record <file>` records the level's logic from the moment it starts loading until LOADED, in
`game record` format: it implies `--feeds-early` (the harness asks for the feeds right after injection;
gamelink arms them as soon as a level is loading - never at the main menu) and drains them into the file
while it waits. This is what catches game-mode start-up, which happens before LOADED. Filter it, e.g.
`--record-filter Levels/` (a level's own blueprints) or `--record-filter GameModes,TeamDeathmatch`;
`--record-props` adds property values. Then `game diff` against a recording of the shipped map.

### `game` — watch the logic of a running game

```
frostycli game status
frostycli game trace on [filter] | off | [n] | status | filter <f> | clear | pause | resume | dump
frostycli game prop  on [filter] | off | [n] | status | filter <f> | clear
frostycli game overlay on [filter] | off | filter <f> | feed events|props|both
frostycli game follow [--prop|--both] [--seconds 30] [--keep-on] [filter]
frostycli game record <file> [--prop|--both] [--seconds 30] [filter]
frostycli game diff <a.txt> <b.txt> [--loose] [--max 40]
frostycli game wires <blueprint filter> [--events|--props] [--against <recording>]... [--never]
frostycli game budget
frostycli game timeline <recording> [--all] [--match <a,b,...>]
frostycli game fire    <Type[tag]>.<Event> @<blueprint> [S|C] [all]
frostycli game deliver <Type[tag]>.<Event> @<blueprint> [S|C] [all]
frostycli game inspect <Type[tag]> @<blueprint> [S|C]
frostycli game deploy [--timeout 30]
frostycli game set     <Type[tag]>.<Field> = <value> @<blueprint> [S|C] [all]
frostycli game crash [--log <gamelink.log>] [--known <known-crashes.json>]
frostycli game log [n]  |  game send <raw gamelink command>  [--port 31500]
```

Talks to the FrostyGameLink module in a game started by `mod test` (127.0.0.1:31500); no game data
is loaded, so it answers immediately. Two feeds:

```
S EventGate[33].Out -> LogicPrefabReferenceObject#32914a11.TriggerUpdate  @AI/BattleAI/Prefabs/Skirmish/PF_Skirmish_AI_Ability_Button_Logic
S InterfaceDescriptor[4].DistanceToTarget -> CompareFloat[3].A = 153.515  @AI/BattleAI/.../PF_Skirmish_AI_Ability_Hunter_Logic
```

- `trace` - every logic event travelling along an event connection; `prop` - every property value
  arriving at an entity, with the wire it came over and the value.
- `S`/`C` server/client; `[33]` the instance's index in its EBX file (Frosty's placeholder GUID
  `...000000000033`), `#32914a11` an exported instance's real GUID; `@` the blueprint that owns the wire.
- Filters are comma-separated, `!` excludes: `game follow GameModes_Flow`, `game prop on MosEspa,!Phantom`.
- `game overlay on` draws the newest lines over the game (Dear ImGui, DX11); F9 hides it, F10 pauses.

Typical use: a mode that never starts (`game follow GameModes_Flow`), bots that join but never spawn,
wiring added with `connection add` that never fires, or copying how a shipped map does something.

**Compare two runs.** `game record` saves a feed with timestamps; `game diff` lists the wires that
fired in one recording and never in the other, in the order they first fired, and names the first
divergence - where the second run went another way:

```
frostycli mod test --vanilla --level <host level> --record vanilla.txt --record-filter Levels/
frostycli mod test MyPort.fbmod --level <port level> --record port.txt --record-filter Levels/
frostycli game diff vanilla.txt port.txt --loose
```

A game mode starts during loading, so record with `mod test --record` (from the first logic of the
level) rather than `game record` (from now on). Live example, vanilla Cloud City against a Mos Espa port:
the diff showed Cloud City starting its mode via `OnStreamedIn -> SyncedBool.SetTrue` and
`StreamInOffline -> SubWorldReferenceObject.StreamIn`, the port via `StartGameModeSkirmish -> EventGate.Open`
and `-> SubWorldReferenceObject.StreamIn` - on a port that never starts, the missing step shows up there.

`--loose` compares `Type.Event -> Type.Event` only (no instance indices, no blueprint paths), which is
what you want when the port's blueprints have different paths and instance orders from the shipped one.

**Start-up at a glance.** `game timeline <recording>` lists only the stream-in/out, `OnStreamedIn` and
game-mode-start wires of a recording, in order, repeats folded - the shape of a level's start-up on one
screen (Mos Espa: GameModes StreamIn -> OnStreamedIn -> StartGameModeSkirmish -> TeamDeathmatch StreamIn ->
... -> TeamDeathmatch_Skirmish StartGameMode). Times from `mod test --record` are its 3-second drain
batches; order within a batch is exact. `--match` picks other event/field names, `--all` keeps repeats.

**Engine limits.** `game budget` reads the live count of unique MeshAssets against the mesh streaming
manager's fixed 2,048 (going over crashes at `game+0x15AC36B` / `0x7C652xx`; placements of one mesh
share a slot, soldiers and vehicles streamed at runtime take slots too), plus loaded sub-levels and
entities per realm. Check it on the port while playing, not just at load. `mod test` prints it after a
LOADED verdict while the game is still up (`--keep`).

**Fire an event by hand.** Test wiring without playing up to it: `game fire` makes an instance emit
an event (every wire from it runs, as if it had fired); `game deliver` sends an event into an instance
(as if a wire delivered it). Take the instance from a feed or `wires` line:

```
frostycli game fire    EventGate[13].Out @PF_Skirmish_AI_Ability_Logic
frostycli game deliver SubWorldReferenceObject#55a4757e.StreamIn @CloudCity_01/GameModes
```

The tag is the feed's (`[13]` or `#55a4757e`), the event a name or 8 hex digits, the blueprint any
unique part of its path; `S`/`C` picks the realm (default server), `all` every placement of the
blueprint. Requests run inside the event hook on the realm's own logic thread, so the trace must be on.

**Deploy without touching the PC.** `game deploy` presses the spawn screen's START from inside the game: it
fires the event the button sends (`UIElementWidgetReference[22].OnOkAction` in
`UI/InGame/Spawn/Screens/SpawnScreen`), so the screen's own checks and the class/hero limit lock run, then
waits until the local player controls a soldier. No keyboard or mouse input reaches the PC, unlike the
harness's old deploy. `mod test --deploy` does it after LOADED (it implies `--keep`).

**Look inside an instance, change a value live.** `game inspect` shows every field of a live instance
as the entity reads it now (`live` where its bus holds a wired or overridden value, else the EBX value),
its wire-only inputs and outputs with their current values and peers, and its recent wires. `game set`
writes a wired field through the engine's own property writer, so everything listening reacts - flip a
Bool, nudge a float, move a position - without rebuilding:

```
frostycli game inspect CompareBool[d] @LoadingSpinner C
frostycli game set     CompareBool[d].Bool = false @LoadingSpinner C
frostycli game set     Or[16].Out = toggle @3DRotationInput3AxisWidget C
```

Booleans take `true`/`false`/`toggle`; numbers, enum names, `x, y, z` vectors and positions work too.
An unwired field is refused (the entity reads its EBX value directly); the wire's source overwrites a
written value the next time it writes. A feed must be on, as for `fire`.

**Wires that never fired.** `game wires <blueprint>` lists every event and property connection of the
matching loaded blueprints; with `--against` one or more recordings each is marked `fired` or `NEVER`
(`--never` shows only those). A wire that never fires while the thing it should start doesn't happen
is the one to look at - wrong field id, a layer whose connections aren't read, a server-only gate.

```
frostycli game wires MyPort/GameModes_Flow --against port.txt --never
```

Limits: a property field fed by several wires is credited to the first one in the property feed, so
the others can read `NEVER`; property wires into a prefab's interface reach the feed as `relay` lines
and can't be matched. Event wires are exact.
Both feeds only arm with a level loaded - patching the game's code earlier stalls or kills it.
Full reference: `tools/gamelink/TRACE.md`.

### `level clear` — empty a shipped layer or sub-world

```
frostycli level clear <level-ebx> [--delete-groups] [--remove] [--keep-type <TypeName>]... [--only-type <TypeName>]...
                                                                    (pipeline op: level-clear)
```

Marks every object that carries an `Excluded` flag as excluded (placements, sub-world references,
lights, effects…) and, with `--delete-groups`, drops the `StaticModelGroupEntityData` objects — the
Havok-backed static geometry that holds thousands of instances each. Use it to replace a map
rather than add to it: clear the geometry layers and the streamed level first, then place your own
content. `--remove` takes the objects out of the Objects list instead of flagging them (the runtime
ignores `Excluded` on shipped data). `--keep-type LocalLightEntityData` (repeatable) leaves a type
alone; `--only-type <TypeName>` (repeatable) touches objects of the listed types only, e.g. a
lighting variant's fixture group without its Enlighten entity. Run it *before* adding your own
objects to the same layer. Pipeline fields: `delete_groups`, `remove`, `keep_types`, `only_types`.

## Pipeline files — `run`

Instead of running commands one by one, you can write a JSON pipeline that executes a sequence of operations automatically. This is the AI-friendly path — an AI writes the pipeline, the CLI runs it.

```
FrostyCLI.exe run my-pipeline.json
```

### Pipeline structure

```json
{
  "game": "swbf2",
  "game-path": "C:\\Program Files\\EA Games\\STAR WARS Battlefront II",
  "steps": [
    { "op": "project-load", "file": "my-mod.fbproject" },
    { "op": "ebx-set",   "ebx": "s2/foo/bar", "prop": "Name", "value": "new_name" },
    { "op": "ebx-clone", "source": "s2/foo/bar", "as": "s2/foo/bar_copy" },
    { "op": "project-save", "file": "my-mod.fbproject" },
    { "op": "mod-build", "out": "my-mod.fbmod", "mod-name": "My Mod", "mod-author": "me" }
  ]
}
```

### Available pipeline ops

| Op | Required fields | Optional fields |
|---|---|---|
| `project-load` | `file` | |
| `project-new` | | |
| `project-save` | | `file` |
| `mod-build` | `out`, `mod-name` | `mod-author`, `mod-version`, `mod-description` |
| `export` | `ebx`, `out` | `format` |
| `ebx-set` | `ebx`, `prop`, `value` | |
| `ebx-clone` | `source`, `as` | |
| `ebx-delete` | `ebx` | |
| `ebx-revert` | `ebx` | |
| `ebx-set-ref` | `ebx`, `prop`, `target` | `class_guid` |
| `ebx-create` | `ebx`, `type` | |
| `ebx-array-add` | `ebx`, `prop` | `value`, `type` |
| `ebx-array-remove` | `ebx`, `prop`, `index` | |
| `level-add` | `ebx`, `blueprint` | `pos`, `rot`, `scale` |
| `level-add-entity` | `ebx`, `type` | `pos`, `rot`, `scale`, `transform_field`, `set` |
| `level-import-lights` | `ebx`, `from` | `intensity_scale`, `offset`, `skip_disabled` |
| `level-remove` | `ebx`, `instance_guid` | |
| `level-move` | `ebx`, `instance_guid`, `pos` | `rot`, `scale` |
| `level-scaffold` | `template`, `as` | |
| `level-dupe` | `template` (shipped LevelData), `as` (new level) | |
| `level-place` | `ebx`, `blueprint`, `pos`, `group` | `rot`, `scale`, `mesh`, `mvdb`, `bundle`, `like`, `variation_hash` |
| `mvdb-add` | `ebx`, `mesh` | `like`, `variation_hash` |
| `mvdb-remove` | `ebx`, `mesh` | |
| `connection-add` | `ebx`, `source`, `source_field`, `target`, `target_field` | `kind`, `flags` |
| `connection-remove` | `ebx`, `index` | `kind` |
| `loc-set` | `key`, `value` | |
| `import-texture` | `file`, `as` | `format`, `no_mipmaps` |
| `import-mesh` | `file`, `as` | `scale` |
| `import-sound` | `file`, `as` | `variation`, `encoding` (`auto`/`pcm`/`ealayer3`), `flatten` |
| `audio-inspect`, `audio-dependencies` | `ebx` | `out` |
| `audio-inventory` | — | `like`, `out` |
| `audio-export` | `ebx`, `out` | — |
| `audio-import` | `file`, `as`, `template`, `bundle` | — |
| `audio-place` | `ebx`, `into`, `pos` | `play_on_creation` |
| `audio-graph-inspect`, `audio-graph-validate` | `ebx` | `out` |
| `audio-graph-export` | `ebx`, `out` | — |
| `audio-graph-plan` | `file`, `as` | `graph_map`, `out` |
| `audio-graph-import` | `file`, `as`, `bundle` | `graph_map`, `out` |
| `bundle-add` | `ebx` (or `res`/`chunk_id`), `bundle` | |
| `material-set` | `ebx` (mesh), `index` (material), `prop` (param), `value` | |
| `bundle-remove` | `ebx` (or `res`/`chunk_id`), `bundle` | |
| `ebx-dump` | `ebx` | `out`, `no_resolve`, `deep`, `object` |
| `ebx-search` | `prop`, `value` | `type`, `regex`, `limit`, `out` |
| `ebx-refs` | `ebx` OR `guid` | `depth`, `limit`, `out` |
| `res-export` | `res`, `out` | |
| `chunk-export` | `chunk_id`, `out` | |
| `res-import` | `file`, `res` (or `as`) | `type` (create-new) |
| `terrain-create` | `heights`, `out` and/or `as` | `host`, `layers`, `colormap`, `color`, `holes`, `hole_below`, `world_size`, `center`, `origin`, `height_range`, `rows`, `spacing`, `world_y`, `tolerance`, `budget`, `no_align`, `clear_decals`, `report`, `args` (any other `terrain create` options) |
| `terrain-export-heights` | `terrain` (or `res`), `out` | `size` |
| `chunk-import` | `file`, `chunk_id` | `new` (create-new) |
| `mvdb-copy` | `ebx`, `mesh`, `from` | `variation_hash` |
| `mod-apply` | `file` | |
| `mod-diff` | | `base`, `format`, `out` |
| `mod-lint` | | `format`, `strict` |
| `mod-test` | `file` | `level`, `mode`, `timeout` |
| `level-validate` | `ebx` | `format`, `strict` |
| `bundle-complete` | | `level`, `cosmetic` — never saves; follow it with `project-save` |

Each step can include `"on-error": "fail"` (default — abort pipeline) or `"on-error": "continue"` (log and keep going).
An op the runner does not recognise is a failed step, not a skipped one. When steps failed under `continue`,
the run ends with a line naming each of them (`2 of 5 steps failed: step 1 (mod-lint), step 3 (ebx-set)`)
and exits 1. `mod-lint` and `level-validate` fail their step when they report findings, so they work as
gates in front of `mod-build`.

### Example: inspect a level and export all its architecture meshes

```json
{
  "game": "swbf2",
  "game-path": "C:\\Program Files\\EA Games\\STAR WARS Battlefront II",
  "steps": [
    {
      "op": "ebx-dump",
      "ebx": "s2/levels/cloudcity_01/cloudcity_01",
      "out": "cloudcity_dump.json"
    },
    {
      "op": "ebx-search",
      "prop": "Name",
      "value": "wall",
      "type": "RigidMeshAsset",
      "limit": 50,
      "out": "wall_assets.json"
    }
  ]
}
```

---

## What's not done yet

The core mod pipeline is done (and validated on SWBF2 + SWBF1). What remains are the gaps versus
the Frosty Editor GUI:

- **Handler-based mod merge — done.** `mod build` and `mod apply` both run per-type custom action
  handlers headlessly: the build-time PluginManager populates its handler tables from the loaded plugins'
  `[RegisterCustomHandler]` attributes, and `mod apply` resolves + runs the handler for handler-based
  res/ebx (only resources whose handler plugin isn't loaded are skipped, with a warning). Out of the box
  the loaded plugin set carries BespinMeshSetPlugin's **ShaderBlockDepot** handler (validated end-to-end).
  The broader GUI handlers — **localization** (LocalizedStringResource / UITextDatabase), the SWBF2
  **merger** (NetworkRegistry, MeshVariationDatabase, WSTeamData, UIMetaData, ProfileOptions) — load and
  register headlessly via `--handler-plugins <FE Plugins dir>` (they instantiate with no WPF/UI).
- **Per-type bundle auto-linking — done.** `bundle add --with-resources` pulls a mesh's MeshSet res + LOD
  chunks, a texture's Texture res + pixel chunk, or another resource-backed asset's res into the bundle
  alongside the EBX.
- **From-scratch mesh import** — mesh import is replace-existing (or new-from-template via `--template`,
  which reuses a structurally-similar mesh's layout); authoring brand-new geometry/material setups from a
  bare FBX is still a GUI capability not ported.
- **Connection authoring — done, unverified in-game.** `connection add` / `connection remove` author a
  blueprint's Property/Link/Event wiring, resolving endpoints as internal refs by instance GUID (the
  resolver `ebx set-ref` lacks) or, with `--source-ebx`/`--target-ebx`, as external refs into another
  partition — which is the shape essentially all real logic uses. Readable field names are hashed to
  the ids shipped data carries. Two gaps remain: realm is chosen, not derived (`--realm`, default
  `ClientAndServer`), and no authored connection has been confirmed to drive behaviour in a running
  match.
- **Battlefront II grouped-placement writes — partly done.** `level move` / `add` / `remove` edit a
  placement addressed by group/member/instance, on EBX-backed and Havok-backed members alike; the
  Havok paths rewrite the group's physics resource. `level add --group G --blueprint ...` creates a
  new member with collision and bundles its assets. Mesh-variation registration remains a separate
  step, and none of the physics writes have been verified in-game.
- **Source-format resource import** — `import resource` replaces a res blob byte-for-byte; building a res
  from a real `.svg`/`.ies`/atlas-image source (the editor's per-type importer) is out of scope.
- **Visual/interactive features** (asset tree, property grid, 3D viewport, previews) — out of scope
  for a CLI by design.
- **Performance** — pre-built indexes, parallel pipeline execution, faster startup. *(planned)*

For comparison, these were **done** during the build-out: cross-asset refs (`ebx set-ref`), list
add/remove (`ebx array-add`/`array-remove`), `ebx create`, sound import (PCM), skinned mesh import
(`--skeleton`), cube/2D-array textures, res/chunk import, `mod build`/`diff`/`apply` (EBX+Res+Chunk),
`mod diff --base` against a `.fbmod` or a `.fbproject`, and full SWBF1 parity for the write/import ops.

---

## Known limitations

- **Texture import is replace-existing.** You can replace an existing TextureAsset's pixels from DDS/PNG/TGA/HDR (and make new textures by cloning then importing). 2D, cube, 2D-array, and volume (`TT_3d`) textures are supported; create-texture-from-scratch is not.
- **Mesh import is replace-existing.** You can replace an existing mesh's geometry from FBX (rigid/composite directly; skinned via `--skeleton <skeleton-ebx>`), and the FBX must match the target's LOD/section structure.
- **Sound import supports native packages and WAV replacement.** `audio export/import` creates independent waves from destination templates while retaining original channels, variations, loops and seek data. `import sound` replaces an existing variation from channel-matched PCM16 WAV; multi-segment replacement requires explicit `--flatten`. Compressed encoding preserves mono/stereo and requires ffmpeg plus the bundled encoder; automatic mode falls back to PCM. Whole sound graphs and mixer/reverb systems still require destination mapping. See [audio porting](docs/audio-porting.md).
- **`mod apply` covers EBX + Res + Chunk — both modifications and added/new assets.** Modifications go through ModifyEbx/ModifyRes/ModifyChunk; added assets through AddEbx / AddRes (original ResRid preserved) / AddChunk (id preserved). Only **handler-based** resources (e.g. a ShaderBlockDepot carried by a mesh mod) are still skipped with a warning — applying those needs the handler executor (the build side *does* run handlers). So the CLI can now ingest content mods that add new EBX/Res/Chunk; editor mesh/material mods that ship handler resources still need the Frosty editor/mod manager.
- **`mod diff --base` accepts a `.fbmod` or a `.fbproject`.** A `.fbmod` is diffed mod-vs-project; a second `.fbproject` is parsed read-only (no second AssetManager needed) and diffed project-vs-project. Each asset is tagged both / mod-only / project-only.
- **`loc get`/`loc set` need a profile with a localized string database.** Games/profiles without one report "no localized string database loaded."
- **The reference index (`ebx refs`) takes several minutes to build on first use** for a full SWBF2 install. It then builds automatically and is cached next to the asset cache (invalidated when the game changes), so later runs are fast — `index build --refs` is only an optional way to pre-warm it offline.
- **Output is `stdout` by default.** Redirect with `--out <file>` on any inspect command if you want to save results.

---

## Tips for working with Claude

If you're using Claude to write pipelines or interpret asset dumps, give it the output of `ebx dump` on the asset you want to work with. The JSON output is designed to be readable by an LLM — every object has a `__type` field and every cross-asset reference has a `__path` field showing what it points to.

Example prompt pattern:
> Here is the dump of `s2/levels/cloudcity_01/cloudcity_01`. I want to find all entities that use blueprints under `s2/objects/architecture/bespin`. Write a pipeline that exports each of those blueprint meshes to an `exports/` folder.

Claude can then write the `ebx-search` + `export` pipeline steps targeting exactly the right asset paths.
