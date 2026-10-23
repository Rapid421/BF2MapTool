#!/bin/sh
# Headless: build a scene with the tool (as the add-on's button does, from the sources), then game-test it.
# The game test waits in the game-test queue (tools/game_test.py --wait), so several of these can run side by side.
# sh run_scene_test3.sh <scene.blend> <LevelName> <shots script.py or -> <tag> [probe script.py run after the pictures or -] [mode]
# markers: logs/<tag>_build.done, logs/<tag>.done      pictures: work/<LevelName>/review/<tag>/
cd "$(dirname "$0")/.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
SCENE=$1; NAME=$2; SHOTS=$3; TAG=$4; PROBE=$5; MODE=${6:-SkirmishBlast}
BL="C:/Program Files/Blender Foundation/Blender 5.2/blender.exe"
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
rm -f "$ROOT/logs/${TAG}_build.done" "$ROOT/logs/$TAG.done"
W=$ROOT/work/$NAME
mkdir -p "$W/export"
cd "$ROOT/runtime" || exit 1
python -m bf2maptool build "$SCENE" --settings "$ROOT/work/settings.json" --blender "$BL" --export-script "$ROOT/addon/bf2_map_tool/export_scene.py" \
  --export-dir "$W/export" > "$ROOT/logs/${TAG}_build.log" 2>&1
RC=$?
echo "rc $RC $(date +%T)" > "$ROOT/logs/${TAG}_build.done"
MOD="$W/mods/$NAME.fbmod"
if [ $RC -ne 0 ] || [ ! -f "$MOD" ]; then echo "no game test: build rc $RC $(date +%T)" > "$ROOT/logs/$TAG.done"; exit 1; fi
ARGS=""
if [ "$SHOTS" != "-" ]; then
  python "$SHOTS" "$W/export/map.json" "$ROOT/work/settings.json" "$W/review_shots_$TAG.json" > "$ROOT/logs/${TAG}_shots.log" 2>&1
  ARGS="--shots $W/review_shots_$TAG.json"
fi
LEVEL="AddedContent/$NAME/Levels/${NAME}_01/${NAME}_01"
if [ -n "$PROBE" ] && [ "$PROBE" != "-" ]; then
  python "$ROOT/tools/game_test.py" "$MOD" "$LEVEL" "$MODE" --out "$W/review/$TAG" $ARGS --hold 480 --wait 7200 \
    --then "python \"$PROBE\" \"$W/export/map.json\" \"$ROOT/work/settings.json\"" > "$ROOT/logs/$TAG.log" 2>&1
else
  python "$ROOT/tools/game_test.py" "$MOD" "$LEVEL" "$MODE" --out "$W/review/$TAG" $ARGS --hold 420 --wait 7200 > "$ROOT/logs/$TAG.log" 2>&1
fi
echo "game rc $? $(date +%T)" > "$ROOT/logs/$TAG.done"
