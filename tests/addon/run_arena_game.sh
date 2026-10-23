#!/bin/sh
# Wait for a marker file, then game-test the arena mod with the door probe.
# sh run_arena_game.sh <marker to wait for (must hold "rc 0")> <review folder name> <tag>     marker: logs/<tag>.done
cd "$(dirname "$0")/../.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
WAIT=$1; REV=$2; TAG=$3
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
rm -f "$ROOT/logs/$TAG.done"
n=0
until [ -f "$WAIT" ]; do
  sleep 5; n=$((n + 1))
  if [ $n -gt 360 ]; then echo "waited 30 min for $WAIT - gave up $(date +%T)" > "$ROOT/logs/$TAG.done"; exit 1; fi
done
if ! grep -q "rc 0" "$WAIT"; then echo "not started: $(cat "$WAIT") $(date +%T)" > "$ROOT/logs/$TAG.done"; exit 1; fi
python "$ROOT/tests/scenes/arena_shots.py" "$ROOT/work/ToolArena/export/map.json" "$ROOT/work/settings.json" "$ROOT/work/ToolArena/review/shots.json" > "$ROOT/logs/${TAG}_shots.log" 2>&1
python "$ROOT/tools/game_test.py" "$ROOT/work/ToolArena/mods/ToolArena.fbmod" AddedContent/ToolArena/Levels/ToolArena_01/ToolArena_01 SkirmishBlast \
  --out "$ROOT/work/ToolArena/review/$REV" --shots "$ROOT/work/ToolArena/review/shots.json" --hold 480 \
  --then "python \"$ROOT/tools/door_probe.py\" \"$ROOT/work/ToolArena/export/map.json\" \"$ROOT/work/settings.json\"" > "$ROOT/logs/$TAG.log" 2>&1
echo "game rc $? $(date +%T)" > "$ROOT/logs/$TAG.done"
