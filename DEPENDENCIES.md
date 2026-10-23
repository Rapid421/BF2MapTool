# Dependencies

FrostyCLI is a **headless console app that loads the Frosty Editor SDK DLLs** to operate on
Battlefront game data without the GUI. This file lists what you must supply, what ships bundled, and
the compatibility constraints.

## You must supply (NOT bundled)

| Dependency | Why | Notes |
|---|---|---|
| **Windows 10/11 x64** | Native deps (DirectX/SharpDX, FBX SDK) are Windows x64 | — |
| **.NET Framework 4.8 runtime** | Target framework of every project | Preinstalled on current Windows; otherwise [download](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) |
| **Star Wars Battlefront II** (and/or **Battlefront 2015**) install | The game data FrostyCLI reads/writes | Point at it with `--game-path` / `FROSTYCLI_GAME_PATH_SWBF2` / `frostycli.json` |
| **ffmpeg.exe** (with `libmp3lame`) | *Optional* — only for compressed `import sound --ealayer3` | On PATH, at `C:\ffmpeg\bin\ffmpeg.exe`, or in `thirdparty/`. Without it, sound import falls back to uncompressed PCM |
| **An FE editor `Plugins/` folder** | *Optional* — only for `--handler-plugins` (the broader merge handlers) | From a matching FE‑1063 editor build. See [Handler plugins](#handler-plugins-for---handler-plugins) for the exact DLLs |

## Bundled in the release / `lib/` (committed so it builds + runs standalone)

**Frosty SDK + core (from the FE‑1063 “sersavagefork” build):**
- `FrostySdk.dll`, `FrostyCore.dll`, `FrostyHash.dll`, `FrostyControls.dll`
- `Profiles/` — Frosty game profiles (incl. `starwarsbattlefrontii`)

**Frosty plugins bundled + loaded headlessly** (these ship in the release and `lib/`):
- `MeshSetPlugin.dll` — the Frosty MeshSet plugin: MeshSet/mesh types (mesh export/import, `material set`, `bundle --with-resources`) **and** the `ShaderBlockDepot` Res custom action handler that `mod build` / `mod apply` run out of the box. *Note:* this build carries a small FBXExporter fix for **SWBF1** mesh export (stock FE MeshSetPlugin NREs on those meshes), so don't swap in a vanilla MeshSetPlugin if you need SWBF1 mesh export.
- `TexturePlugin.dll` — the Frosty Texture plugin: Texture asset/resource types used by texture export/import.

> The SoundWave and bundle-membership features are **not** plugins here — that logic was **ported** into FrostyCLI's own code (`HeadlessSoundImporter` / `EALayer3Encoder` from the Sound Editor plugin; `BundleCommand` from the Bundle Editor plugin), so no sound/bundle plugin DLL is referenced or shipped.

**Third-party libraries:**
- `Newtonsoft.Json.dll`
- `SharpDX.dll`, `SharpDX.DXGI.dll`, `SharpDX.Direct3D11.dll`, `SharpDX.Mathematics.dll` (texture/DDS)
- `thirdparty/libfbxsdk.dll` (mesh FBX I/O)
- `thirdparty/libzstd.1.1.5.dll`, `libzstd.dll`, `zlibwapi.dll`, `liblz4.so.1.8.0.dll` (cas (de)compression)
- `thirdparty/dxtex.dll` (PNG/TGA/HDR → DDS conversion for `import texture`)
- `thirdparty/ealayer3_encode.exe`, `ealayer3_encode.dll`, `libmpg123-0.dll` (EALayer3 sound encode; still needs ffmpeg)

**First-party (built from this repo):**
- `FrostyCLI.exe`, `FrostyCLI.Core/Commands/Pipeline.dll`, and `Adapters/FrostyCLI.Adapters.{Mesh,Texture}.dll`

## Vendored source: the terrain format (`FrostyCLI.Core/Terrain/`)

The terrain streaming-tree parser/serialiser and the derived-array builders are **vendored source**
from **BfiiMapTools** (`src/Bfii.TerrainFormat`, `src/Bfii.TerrainDerive`), not a DLL. That repo owns
the terrain byte format; these are copies, and edits belong upstream.

Re-sync after an upstream change:

```
.\tools\sync-terrain-format.ps1              # BfiiMapTools beside frostycli
.\tools\sync-terrain-format.ps1 -Upstream D:\path\to\BfiiMapTools\src
```

The script stamps each file with its upstream SHA-256 and applies exactly two mechanical adaptations,
failing loudly if the code it rewrites has moved:

1. `#nullable enable` per file — upstream enables it project-wide, which FrostyCLI.Core cannot do
   without lighting up every other file in the assembly.
2. `System.Buffers.Binary` → direct little-endian byte arithmetic, and the span-based `Guid`
   members → `Terrain/Net48Compat.cs`. Upstream targets net8.0; FrostyCLI targets .NET Framework 4.8,
   where those need the `System.Memory` package — and pulling `System.Memory` +
   `System.Runtime.CompilerServices.Unsafe` into a process that hosts the Frosty SDK risks
   assembly-binding conflicts for no gain at five call sites.

`Net48Compat.cs` is hand-written and is never overwritten by the sync.

Not vendored: upstream's `Verification.cs` and `X1Runner.cs` (diagnostics needing `System.Text.Json`),
and the mask-stride/sizing work not used by elevation editing.

## Handler plugins (for `--handler-plugins`)

These are **NOT bundled** — they're loaded on demand from an FE editor `Plugins/` folder you point at
with `--handler-plugins <dir>`, to enable the broader merge handlers during `mod build` / `mod apply`.
FrostyCLI loads only this curated, known-safe set (they instantiate headlessly, no WPF/UI):

| Plugin DLL | Provides (custom action handler → asset type) |
|---|---|
| `BiowareLocalizationPlugin.dll` | `LocalizedStringResource` (localized text merge) |
| `FsLocalizationPlugin.dll` | `UITextDatabase` |
| `Swbf2MergerPlugin.dll` | `NetworkRegistryAsset`, `MeshVariationDatabase`, `WSTeamData`, `UIMetaDataAsset`, `ProfileOptionsAsset` |
| `DifficultyWeaponTableDataPlugin.dll` | `DifficultyWeaponTableData` (loads, but not an SWBF2 type → registers only where the game defines it) |

Without `--handler-plugins`, only the bundled `ShaderBlockDepot` handler runs; these others are simply
unavailable (no error).

## Compatibility — IMPORTANT

The bundled Frosty SDK DLLs are from a **specific FE‑1063 (sersavagefork)** build. They must match the
Frosty version that understands your game's data. If you use a **different Frosty build**, the SDK DLLs
may be incompatible (load errors, wrong profile, bad reads).

To re-point at a different Frosty build, replace `lib/` and rebuild:
```
# setup.ps1 copies the SDK DLLs + profiles from a sibling FrostyBatchExporter\lib build:
.\setup.ps1
dotnet build FrostyCLI.sln -c Release -p:Platform=x64
```

## Licensing note
The bundled Frosty SDK / plugin DLLs originate from FrostyToolsuite (and a community fork). Redistribute
them only in accordance with their licenses. This repo is private; if you make it public, review those
licenses before publishing the binaries.
