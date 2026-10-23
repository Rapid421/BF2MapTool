# Sound graph packages and migration

The `audio graph-*` commands handle the **SoundPatchAsset** graph above the wave
packages described in [audio-porting.md](audio-porting.md). They target the bundled
FE-1063 SDK and the `swbf1`/`swbf2` profiles.

A package stores the patch's reachable object graph as a checksummed object table.
Internal pointers use table indices, preserving shared objects and cycles. Ordered
node lists, public inputs/events, ports, constants, output nodes and compiled
`SoundGraphInfo` tables are retained. Game recordings are separate wave packages.

## Inspect and export

```text
frostycli --game swbf1 audio graph-inspect <patch> --out graph-report.json
frostycli --game swbf1 audio graph-validate <patch>
frostycli --game swbf1 audio graph-export <patch> --out graph-package.json
```

Inspection reports node execution order, object-table indices, all node fields,
value counts and external reference identities. Validation checks internal reference
ranges, duplicate nodes, output membership, connected port/event bounds, plugin
counts, plugin references and constructor/connection ranges. Index zero remains valid
for connected output ports. It does not infer new node order or recompute counters.
Export requires a new file. Do not commit exported game graph data to this repository.

## Plan against the destination SDK

```text
frostycli --game swbf2 audio graph-plan graph-package.json --as Mods/Audio/MyPatch --out plan.json
frostycli --game swbf2 audio graph-plan graph-package.json --as Mods/Audio/MyPatch --map migration.json --out plan.json
```

Planning does not require a project and never registers an asset. It constructs the
graph with the currently loaded destination types and reports:

- `issues`: missing types/fields, incompatible values, unmapped references and failed validation;
- `source_references`: source file/class GUID pairs, paths, object types and wave channel counts;
- `changes`: explicitly requested field/type migrations;
- `destination_defaults`: fields absent from the source that retain SDK constructor defaults;
- `ready`: whether the migrated graph passed validation and a destination EBX write/read round trip.

The exit code is nonzero when the plan is not ready. Review destination defaults as
well as changes: newly introduced playback controls can affect behavior.

Cross-profile planning requires **every external reference to be mapped**, including
system buses and mixer objects whose GUIDs happen to match across games. References
are keyed by `source-file-guid/source-class-guid`, not a path alone: one mixer asset
can contain many distinct exported objects. A destination reference selects an asset's
root unless `class_guid` selects a particular exported object. Wave replacements must
retain the source channel count. Missing dependencies are never copied automatically.

## Explicit migration file

The following illustrates the format; replace the placeholders and include only
rules needed by the graph being migrated:

```json
{
  "references": {
    "<source-file-guid>/<source-class-guid>": {
      "asset": "Mods/Audio/MyImportedWave"
    }
  },
  "fields": {
    "SamplerNodeData.OffsetType": {
      "rename": "OffsetMode",
      "value": "SamplerOffsetMode_Absolute"
    },
    "NumberGeneratorNodeData.Min": { "constant_port": true },
    "SoundPatchAsset.AILoudness": { "drop": true }
  }
}
```

Field rules use `SourceType.Field` for all instances or `objects[3].Field` for one
exported table row. A full nested path, such as
`objects[2].Info.Connections[0].Context`, can target a nested record field. Specific
paths take precedence over type-wide rules. Supported rules are:

- `rename`: select a destination field name;
- `value`: supply an explicit replacement, including initialization of a new destination field;
- `constant_port: true`: wrap a numeric source value in a disconnected AudioGraphNodePort;
- `drop: true`: explicitly discard a legacy field; cannot be combined with other rules.

An optional `types` object maps source type names to destination type names. Type
substitution must still satisfy the destination field type. Unknown sections/options,
unused rules, duplicate writes to one destination field and unknown enum names fail.
The exporter’s object table is the path authority; expanded `ebx dump --deep` paths
are a different representation.

Replacement values use the package's typed format:

```json
{
  "fields": {
    "ObjectOutputNodeData.AttenuationCurve": {
      "value": {
        "target": {
          "asset": "<destination-curve-owner>",
          "class_guid": "<exported-FloatCurve-guid>",
          "type": "FloatCurve"
        }
      }
    },
    "ObjectOutputNodeData.ReverbAttenuationCurve": {
      "value": { "null": true }
    }
  }
}
```

Pointers use `ref` for an existing package object, `null` for a null pointer, or
`target` for an explicit destination reference. Struct values use
`{"type":"TypeName","fields":{...}}`; lists use JSON arrays. `references` remaps
every occurrence of an external identity, including wave-player and ReferencedData
entries. A per-field `target` override instead affects just the selected field.

The examples are **migration choices**, not universally equivalent conversions.
In particular, choosing an absolute sampler offset or borrowing a BFII attenuation
curve requires checking the original sound's behavior. No automatic AudioCurve-to-
FloatCurve interpolation conversion is claimed.

## Import, save and place

```text
frostycli --game swbf2 --save-as work/graph-test.fbproject audio graph-import graph-package.json --as Mods/Audio/MyPatch --map migration.json --bundle <destination-bundle> --out import-report.json
frostycli --project work/graph-test.fbproject audio graph-validate Mods/Audio/MyPatch
```

Import requires a complete project or `--save-as` to start a new one. It repeats
planning, assigns fresh file/object identities, adds the new patch to the requested
bundle, links its dependencies and verifies read-back. Existing asset names are
refused; an unsuccessful import reverts its added patch. `--dry-run` performs the
preflight without adding or saving assets.

Dependencies must already exist in the destination project. Import waves into their
destination bundle first, map shared audio-system objects to appropriate BFII objects,
and bundle additional patch dependencies using the existing bundle tools. Graph
import does not modify or recursively bundle those dependencies. Use `audio place`
and `connection add` to place the resulting patch and wire gameplay triggers.

JSON pipelines expose `audio-graph-inspect`, `audio-graph-validate`,
`audio-graph-export`, `audio-graph-plan` and `audio-graph-import`. Inspection/export
use `ebx`; planning/import use `file` and `as`; the migration file is `graph_map`.
Import additionally requires `bundle`. All accept optional report `out` (required
for export). See [test-audio-graph.ps1](../tools/test-audio-graph.ps1) for a complete
package/import/save/reopen pipeline.

## Scope and evidence

This is graph serialization and explicit schema/reference migration, **not a sound
graph compiler**. It retains supplied compiled plugin tables; it does not generate
new compiled topology, prove plugin ABI compatibility, or translate arbitrary mixer,
reverb, SoundPatchConfigurationAsset or gameplay systems. `ready` proves structural
and storage compatibility. Every report retains `runtime_validation_required: true`;
playback and audible equivalence still require an in-game test.

Exact local profile observations: the SWBF2015 shipped
`XP2/Sound/Planets/Bespin/Clouds_04/SFX/SW01XP2_Planets_Bespin_Clouds_04_SFX_CarboniteChamber_LiftAndFreeze_01`
uses embedded AudioCurve records, SamplerNodeData.OffsetType and scalar generator
bounds. The bundled BFII SDK uses FloatCurve pointers, SamplerOffsetMode and
AudioGraphNodePort bounds. Planning reports these differences without silently
dropping them. These observations establish schema differences, not runtime equivalence.

API evidence is the repository's pinned FE-1063 dependencies (see
[DEPENDENCIES.md](../DEPENDENCIES.md)), particularly `FrostySdk.IO.EbxAsset`,
`EbxWriter.WriteAsset`, `PointerRef` and the per-game SDK types. The standard EBX
writer traversal is used for graph preflight: the audio-wave helper's transient/raw-
order flags did not produce a readable multi-object graph in the local test.

Verification:

```text
dotnet build FrostyCLI.sln -c Release -p:Platform=x64
FrostyCLI.Tests/bin/x64/Release/FrostyCLI.Tests.exe --unit
FrostyCLI.Tests/bin/x64/Release/FrostyCLI.Tests.exe --filter SoundGraphIntegrationTests
powershell -ExecutionPolicy Bypass -File tools/test-audio-graph.ps1 -Game2017 "F:/STAR WARS Battlefront II" -WorkDir work/graph-roundtrip
```

Set `FROSTYCLI_GAME_PATH_SWBF2` for the integration suite. The script uses a new
disposable BFII project and a fresh process for reopening. It does not port a map,
build a mod or launch the game.
