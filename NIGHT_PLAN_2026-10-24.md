# Overnight plan, 2026-10-23 night -> 2026-10-24 17:00 (the user is asleep; full permissions)

Goal: a tool a stranger can download, point at ANY Blender scene and get a good-looking, playable map with baked
lighting. Every change lands as a PR on Rapid421/BF2MapTool (public), merged by the session that made it after its
test passed for real. Nobody waits for the user: decide, test, write the evidence in the PR and in the Log below.

Rules: `PLAN_2026-10-24.md` "Shared rules" (own worktree per session, one game at a time via
`tools/game_test.py --wait 3600`, one bake at a time, no pop-up windows, time limits, never `python -`, versioned
outputs). Do not commit game-derived files. The clean-install test bed is `E:/cleantest_v1` (release zip unzipped,
fresh Blender profile, scripts in `E:/cleantest_v1/scripts`; `stranger_dojo.py` drives Prepare -> Check -> Port map).

## S0 - this session (owns merging, the release, the core fixes)
1. **Exposure vs sky** (dojo + Utapau washed out): Prepare picks Interior exposure but keeps a sunny sky; the sun
   floods in. Fix: interior maps get an interior sky setup (sun off or dimmed, interior EV) and a Check warning when
   interior exposure meets a sunny sky. Test: dojo game picture not washed out.
2. **Dojo front wall missing in game** (`Wall Plaster 008-012`, `Wall Brick Plaster 001-004` are in the export):
   trace split / import / place, fix. Test: dojo picture shows the wall.
3. **Blast spawns**: spawns on ONE connected navmesh part; spawn-finder areas limited to that floor's height
   (Height 0 reached a floor 60 m below on Utapau). Test: dojo + arena + Hunters game tests.
4. **Test harness**: the first `game shot` timed out and the game closed; the bot probe sees 0 soldiers (wrong
   entity filter). Fix both; the probe must report bot movement. Test on the dojo.
5. **Light probes median 0** on the dojo bake (dynamic objects black indoors?) - check, fix.
6. **Bake fail-safe** test (fake bakers in `E:/BF2MapTool_night/failsafe`).
7. Morning: merge everything, `tests/run_all.py`, rebuild the bundle with the Menu Maker exe, new GitHub release.

## N1 - robustness on random scenes (new session)
Run the stranger pipeline on every other downloaded scene (`C:/Users/rubes/Downloads/*.blend`: tatooine hunters,
snoke room, jedi survivor thing, t.blend; skip character-only files like kratos) and two maps from `F:/Maps`.
Open downloads with `--disable-autoexec`. For each: Prepare, Check, Port map with bake, one game test with pictures.
Fix every GENERIC problem found (single-sided / zero-thickness walls, procedural materials, lamps in surfaces,
unapplied scale, huge or tiny scenes, missing images, glass, emissive screens ...) in the tool, each as its own PR.

## Existing sessions, continuing on their own
- **bf2-world (F2)**: answer its own open questions from the pictures it took, finish metallic cut-out + decals,
  PR + merge.
- **bf2-editor (F3)**: prove the per-object flags in game, PR + merge.
- **bf2-hvv**: continue to a Heroes vs Villains round that starts and plays; PR + merge.

## Log
