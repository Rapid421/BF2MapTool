# BF2 Map Tool - handover (2026-10-23)

Read this first if you are picking the project up (a person or another AI). It says what works, what does not, how
to build and test, and the traps that cost days to find. `DEPENDENCIES.md` lists every external piece;
`docs/quickstart.md` is the user guide; `NIGHT_BACKLOG.md` is the open-item list; `STATUS.md` is the long run log.

## What it is

A Blender add-on (`addon/bf2_map_tool`, sidebar tab "BF2 Map") that turns a Blender scene into a new Star Wars
Battlefront II (2017) level and writes a `.fbmod`. The add-on only collects settings and exports the scene; all the
work is done by the runtime (`runtime/bf2maptool`, plain Python 3.13), which drives a FrostyCLI fork
(repo `Rapid421/FrostyCLI-BF2MapTool`). Each build stage caches its result: a re-build redoes only what changed.

Pipeline (`runtime/bf2maptool/chain.py`): shell -> shaderdb -> import -> place -> doors -> lights -> look -> navmesh ->
spawns -> kill -> [bake] -> [supremacy] -> audit -> finish (the mod).

## State

**Works, seen in game:** test arena (doors, kill pits, cut-outs, emissives, draft bake, Supremacy), Mustafar,
Rhen Var, Hunters Bastion, LEGO Utapau, Tantive IV, LEGO Naboo (game sky + bake + sun shadows look right).
Game skies: any of 23 shipped skies as the lighting template (`stages/look.py` `GAME_SKIES`). Menu Maker
(`tools/menu_maker`): several maps in one menu, offline and on Kyber.

**Half done / known broken**
- LEGO Utapau light bake: fluxbake aborts (0xC0000409, no message) on its scene. 0.65 M triangles without
  light-blocking pieces bakes; with the cheapest-per-area blockers (even 37 of them, 0.31 M tris) it aborts after
  ~38 min. Not found which mesh. Since 2026-10-23 the bake retries without those blockers, then builds the mod
  UNBAKED with a warning (`stages/bake.py` `BakerDied` / `unbaked`), so a build no longer dead-ends.
- Heroes vs Villains: loads, but the test harness cannot start a round (no spawn screen with one player).
- Navmesh on very detailed rips splits into hundreds of parts (P2-11).
- Everything was only ever run on the author's PC.

## How to build and test

- Settings: copy `settings.example.json` to `work/settings.json`, fix the paths.
- Build a map from an exported scene:
  `python -m bf2maptool port <export>/map.json --settings work/settings.json [--bake draft|normal|high]`
  (run from `runtime/`, with the add-on's package folder on PYTHONPATH). `plan` instead of `port` says what would be redone.
- Tests: `python tests/run_all.py` (the suite; about an hour), `tests/unit/*.py` (seconds).
- In-game test (this PC only): `tools/game_test.py <a.fbmod;b.fbmod> <level> <mode> --out <dir> --shots shots.json`.
  It drives Frosty Mod Manager + the FrostyGameLink harness (`F:\Frosty\gamelink`), has watchdogs, takes pictures.
- Menu mod check without the game: `tools/check_merged_menu.py "a.fbmod;b.fbmod;menu.fbmod" <out> MapA MapB`.
- Bundle for another PC: `python tools/make_bundle.py work/settings.json <out>` (put `BF2 Map Menu Maker.exe` in
  `<out>` first; build it with PyInstaller `--onefile --windowed tools/menu_maker/menu_maker.py`).

## Traps (each cost hours; the fixes are in the code)

- **Piece budget:** more than ~1,400 map pieces on the Kamino host crashes after load (game+0x7C6529A). Prepare
  joins pieces to about 1,200.
- **Black screen with a game sky:** a level's grading LUT is a 3D texture whose data lives inline in its own level
  bundle (chunkLinked 0); cloned without it the screen is black. The look stage retargets it to
  `Levels/Lighting/Global/T_Walrus_BaseGrade_01`.
- **No sunlight with a game sky:** cloud shadows. The look stage turns CloudShadowEnable off.
- **Menu:** every map mod carries its own copy of the shared menu assets; the last loaded wins. Never unpack and
  rebuild whole mods to combine them (lost bundles, black loading screen) - use `mod merge --menu-only`.
- **Light bake memory:** fluxbake dies without a message when RAM runs out; the stage refuses under 6 GB free.
  It also dies on heavy scenes (see Utapau) - the stage caps the scene at 2 M triangles and drops far pieces.
- **Scale:** LSWTSS (LEGO) rips are about 1.33x too big for a soldier; Prepare scales them by 0.75.
- **Accented paths** (e.g. "wörk") break some tools: game pictures go through an ASCII temp folder.
- **Windows:** every subprocess runs with CREATE_NO_WINDOW and a time limit (the user games on this PC).
