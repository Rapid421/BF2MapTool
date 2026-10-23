#!/bin/sh
# Headless: build a scene with the tool (as the add-on's button does, from the sources), then game-test it.
# sh run_scene_test.sh <scene.blend> <LevelName> <shots script.py or -> <tag> [marker to wait for before the GAME test] [extra game_test args]
# markers: logs/<tag>_build.done, logs/<tag>.done      pictures: work/<LevelName>/review/<tag>/
cd "$(dirname "$0")/.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
SCENE=$1; NAME=$2; SHOTS=$3; TAG=$4; WAIT=$5; EXTRA=$6
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
if [ -n "$WAIT" ] && [ "$WAIT" != "-" ]; then
  n=0
  until [ -f "$WAIT" ] || [ $n -gt 480 ]; do sleep 5; n=$((n + 1)); done
fi
ARGS=""
if [ "$SHOTS" != "-" ]; then
  python "$SHOTS" "$W/export/map.json" "$ROOT/work/settings.json" "$W/review_shots_$TAG.json" > "$ROOT/logs/${TAG}_shots.log" 2>&1
  ARGS="--shots $W/review_shots_$TAG.json"
fi
python "$ROOT/tools/game_test.py" "$MOD" "AddedContent/$NAME/Levels/${NAME}_01/${NAME}_01" SkirmishBlast --out "$W/review/$TAG" $ARGS --hold 420 $EXTRA > "$ROOT/logs/$TAG.log" 2>&1
echo "game rc $? $(date +%T)" > "$ROOT/logs/$TAG.done"
