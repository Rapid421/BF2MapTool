#!/bin/sh
# One full cycle on the test arena: button build (run_button_test.sh), then the game test with the door probe.
# sh run_arena_cycle.sh <button tag> <review folder name>      marker: logs/<tag>_cycle.done
cd "$(dirname "$0")/../.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
TAG=$1; REV=$2
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
rm -f "$ROOT/logs/${TAG}_cycle.done"
sh "$ROOT/tests/addon/run_button_test.sh" "$TAG"
MOD="$ROOT/work/ToolArena/mods/ToolArena.fbmod"
if python "$ROOT/tools/button_ok.py" "$ROOT/tests/addon/$TAG.json" > "$ROOT/logs/${TAG}_ok.log" 2>&1 && [ -f "$MOD" ]; then
  python "$ROOT/tests/scenes/arena_shots.py" "$ROOT/work/ToolArena/export/map.json" "$ROOT/work/settings.json" "$ROOT/work/ToolArena/review/shots.json" > "$ROOT/logs/${TAG}_shots.log" 2>&1
  python "$ROOT/tools/game_test.py" "$MOD" AddedContent/ToolArena/Levels/ToolArena_01/ToolArena_01 SkirmishBlast --out "$ROOT/work/ToolArena/review/$REV" \
    --shots "$ROOT/work/ToolArena/review/shots.json" --hold 480 \
    --then "python \"$ROOT/tools/door_probe.py\" \"$ROOT/work/ToolArena/export/map.json\" \"$ROOT/work/settings.json\"" > "$ROOT/logs/${TAG}_game.log" 2>&1
  echo "game rc $? $(date +%T)" > "$ROOT/logs/${TAG}_cycle.done"
else
  echo "no game test: $(cat "$ROOT/logs/$TAG.done" 2>/dev/null) $(date +%T)" > "$ROOT/logs/${TAG}_cycle.done"
fi
