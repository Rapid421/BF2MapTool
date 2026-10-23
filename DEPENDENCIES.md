# What the BF2 Map Tool needs

Everything a build touches, where it lives on the development PC, and where its source is kept.

| Piece | Used for | Where it comes from | Path on the dev PC (settings.json key) |
|---|---|---|---|
| **Star Wars Battlefront II (2017)** | game data read by every build; testing | EA app / Steam | `F:/STAR WARS Battlefront II` (`game_path`) |
| **Blender 5.2** (blender.org build, not the Microsoft Store one) | the add-on, and the headless scene export | blender.org | `C:/Program Files/Blender Foundation/Blender 5.2/blender.exe` (`blender`) |
| **Python 3.13** | runs the runtime (`runtime/bf2maptool`) outside Blender | python.org | `.../Python313/python.EXE` (`python`) |
| Python packages: numpy, pillow, scipy, xatlas, imageio-ffmpeg | meshes, textures, lightmap charts, the loading picture's video | pip — the add-on's *Install Python packages* button puts them in its private folder (`pylib`) | `addon/bf2_map_tool/prefs.py` `PACKAGES` |
| **FrostyCLI (fork, branch `feat/bf2maptool`)** | every Frosty operation: projects, EBX, bundles, meshes, mod-build, `mod merge --menu-only` | source in this repo: `deps/FrostyCLI` (build `FrostyCLI.sln`, configuration ReleaseMenu, .NET) | `cli-wt/FrostyCLI/bin/x64/ReleaseMenu/FrostyCLI.exe` (`frostycli`) — the ReleaseMenu build is the one with `--menu-only` |
| **fluxbake** | the light bake (lightmaps + probes) | source in this repo: `deps/FrostyCLI/tools/fluxbake/fluxbake.cpp`, built by `build_src.cmd` (MSVC) | `cli-wt/tools/fluxbake/bin/fluxbake_src.exe` (`fluxbake`) |
| **Frosty Editor plugins** | the BFII asset handlers FrostyCLI loads | Frosty Editor 1.0.6.x release | `F:/Frosty/Frosty Editor/Plugins` (`plugins_path`) |
| **HavokCollisionTool** | collision (.res) for the map pieces | source in this repo: `deps/HavokCollisionTool` (vendors FrostyHavokPlugin, its license and pinned commit are in `vendor/`). Build: `dotnet build -c Release` (.NET 8) | `I:/UtapauPort/havoktool/HavokCollisionTool/bin/Release/net8.0/HavokCollisionTool.dll` (`havok_tool`) |
| Havok template | the empty collision resource the tool fills | this repo: `deps/havok_template.res` | `I:/UtapauPort/col/template.res` (`havok_template`) |
| .NET 8 runtime | FrostyCLI and the Havok tool | Microsoft | — |
| Frosty Mod Manager (or Kyber) | playing the made `.fbmod` files | Frosty release / Kyber | — |
| PyInstaller (dev only) | builds `BF2 Map Menu Maker.exe` from `tools/menu_maker/menu_maker.py` | pip | — |

`settings.example.json` lists every key with the dev PC's values; copy it to `work/settings.json` and change the paths.

## What is NOT in the repo

- `work/`, `tmp/`, `logs/`, `dist/` — build caches and outputs (many GB).
- `cli-wt/` — the dev PC's working checkout of the FrostyCLI fork (the same code is in `deps/FrostyCLI`).
- Test scenes, ripped-map exports, game pictures (`tests/maps`, `tests/survey`, `*.blend`, `*.png`, ...). The small test
  scenes are made again by the `tests/**/make_*.py` scripts.
- The released bundle zip (add-on + runtime + FrostyCLI + plugins + Menu Maker exe) is attached to the GitHub release.
