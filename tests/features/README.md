# Feature auto-test (one arena, one build, one game run)

Every new feature adds ONE module here, `tests/features/<feature>.py`, with two functions:

```python
def add(scene, origin):
    """put one example of the feature into the Blender scene (bpy), near `origin` (x, y, z in Blender metres);
    use only add-on operators / properties a user would use, so the UI path is tested too"""

def check(work_dir, level_dir):
    """read the BUILT level back (dumps under <work_dir>/dumps, or FrostyCLI ebx-dump on <work_dir>/projects/audit.fbproject)
    and return a list of problem strings - empty = the feature is in the level as intended"""
```

Optional: `SHOTS = [{'name': ..., 'at': [x, y, z], 'look': [x, y, z]}]` in GAME coordinates for the game picture.

`run_features.py` (owned by S0) makes the arena scene (`tests/scenes/make_arena.py`), gives each module its own
20 x 20 m plot (`origin`), calls every `add`, builds the map, calls every `check`, then runs ONE game test with every
module's SHOTS. Feature sessions run `run_features.py --only <feature>` for their own build and do not launch the game
themselves except for what only a game test can show (then via `tools/game_test.py --wait 3600`).
