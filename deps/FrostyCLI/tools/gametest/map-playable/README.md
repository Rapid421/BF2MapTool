# map-playable in-game test scripts

The scripts that proved, in game, what `docs/map-playable.md` claims: a `level create` level fitted with a play-area
boundary, a menu entry, a loading screen, a Kyber declaration and more modes (run by run in `docs/map-playable-log.md`).

They were written for one workstation and assume its layout. Adjust these before reusing them:

- the workspace `F:\Frosty\projects\map-playable` holding `work/` (logs, JSON results, the heightmap), `shots/`, `mods/`
  and this CLI as `frostycli-wt/` (`ROOT` is the scripts' parent directory; the `.sh` files `cd` into the workspace);
- the shared harness in `F:\Frosty\gamelink` (`tools/gametest/autotest.ps1` run as the elevated scheduled task
  `FrostyAutoTest`, with `autotest-args.json`, `harness.claim`, `menu.request` / `menu.done`);
- the probe game-link DLL and its Python client (`I:\SullustArchive\gamelink_probe`, `I:\SullustArchive\collisionprobe`)
  for `svplayers`, `svpos`, `teleport`, `sublevels`, `hud`, `timescale`;
- Frosty Mod Manager at `I:\FrostyModManager`, and a navgen build for `level create --navgen` (the CLI now ships its
  own under `navgen/`).

## The harness, shared with other sessions

- `harness.py` -- `claim(tag, wait)` / `release()` / `launch(...)` / `wait_verdict` / `wait_closed`. A claim is created
  exclusively and verified. A launch is refused while another session's claim exists or a game runs. Holds are capped
  at 720 s, because the task stops after 20 min and would leave the game running. `python harness.py --status | --release`.
- `fastclaim.py <tag> [s]` -- take the gap another session leaves between its runs (0.05 s claim poll, game state
  sampled in a thread, the same exclusive create + verify).

## Runners

- `run_mp.py <LABEL> <ver> <plan|-> [--build-cli] [--mode M] [--hold S] [--after "<script> args"] [--release] -- <create args>`
  -- claim, optionally build the CLI and run the unit tests, build the level (`build_level.sh`), launch with the probe,
  run the plan or the `--after` script, wait for the game to close.
- `run_level_test.py <label> <mod> <level> [--mode M] [--hold S] [--after ...] [--release]` -- one run of a built mod.
- `run_menu.py <LABEL> <ver> <img|-> [--hold S] [--no-build]` -- build (`build_mp2.sh`) and start the game in menu mode
  (launch only); drive it with `menu.py`.
- `build_level.sh`, `build_mp2.sh`, `build_mp4.sh` -- `level create` on the Tatooine_01 shell with the test terrain and
  navgen, plus the menu entry, loading screen, Kyber and modes; `mod build` with a bumped version.

## Probes

- `boundary_test.py <label> <plan.json>` -- teleports the local soldier to the plan's inside points (ground + 1.5 m from
  the heightmap), an out-and-back, and a fatal walk-out, recording alive/position and shooting each step
  (`plans/mp1_plan.json`, `plans/mp1_out_plan.json`, `plans/mp3_outback_plan.json`).
- `mode_probe.py <label> [watch s]` -- streamed sub-levels, every server player twice, AI that moved, three shots.
- `menu.py send '<steps>' [shot] | shot <name> | burst <name> <s> <every>` -- clicks and keys through the harness's menu
  mode, game-window shots.
- `shot.py` (game window only, refused if the game is not running), `views.py` (HUD off, time frozen, camera views),
  `radar_crop.py` (the minimap), `fbmod_embedded.py` (a mod's embedded resources: the Kyber declaration's position),
  `map_art.py` (loading-screen / Kyber art from the heightmap: relief, the boundary, the name).

The `go_*.sh`, `run_mp3*.sh`, `run_next.sh`, `run_final.sh`, `run_duel.sh` and `build_fewai.sh` / `build_noai.sh`
files are the exact batches behind the log's entries. `build_noai.sh` builds MP1b, which crashes the game (game+0x1B1483F).
