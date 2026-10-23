#!/bin/sh
# Headless: build a scene that has Supremacy ticked, then game-test its SECOND level (<Name>_02) in the offline Supremacy mode.
# sh run_sup.sh <scene.blend> <LevelName> <shots script.py or -> <tag> [bake: - | draft | normal | high] [mode, default Mode8]
# markers: logs/<tag>_build.done, logs/<tag>.done      pictures: work/<LevelName>/review/<tag>/
cd "$(dirname "$0")/.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
SCENE=$1; NAME=$2; SHOTS=$3; TAG=$4; BAKE=${5:--}; MODE=${6:-Mode8}
BL="C:/Program Files/Blender Foundation/Blender 5.2/blender.exe"
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
rm -f "$ROOT/logs/${TAG}_build.done" "$ROOT/logs/$TAG.done"
W=$ROOT/work/$NAME
mkdir -p "$W/export"
cd "$ROOT/runtime" || exit 1
if [ "$BAKE" = "-" ]; then
  python -m bf2maptool build "$SCENE" --settings "$ROOT/work/settings.json" --blender "$BL" --export-script "$ROOT/addon/bf2_map_tool/export_scene.py" \
    --export-dir "$W/export" > "$ROOT/logs/${TAG}_build.log" 2>&1
else
  python -m bf2maptool bake "$SCENE" --settings "$ROOT/work/settings.json" --blender "$BL" --export-script "$ROOT/addon/bf2_map_tool/export_scene.py" \
    --export-dir "$W/export" --quality "$BAKE" > "$ROOT/logs/${TAG}_build.log" 2>&1
fi
RC=$?
echo "rc $RC $(date +%T)" > "$ROOT/logs/${TAG}_build.done"
MOD="$W/mods/$NAME.fbmod"
if [ $RC -ne 0 ] || [ ! -f "$MOD" ] || [ ! -f "$W/supremacy.json" ]; then echo "no game test: build rc $RC $(date +%T)" > "$ROOT/logs/$TAG.done"; exit 1; fi
ARGS=""
if [ "$SHOTS" != "-" ]; then
  python "$SHOTS" "$W/export/map.json" "$ROOT/work/settings.json" "$W/review_shots_$TAG.json" > "$ROOT/logs/${TAG}_shots.log" 2>&1
  ARGS="--shots $W/review_shots_$TAG.json"
fi
python "$ROOT/tools/game_test.py" "$MOD" "AddedContent/$NAME/Levels/${NAME}_02/${NAME}_02" "$MODE" --out "$W/review/$TAG" $ARGS --hold 480 --wait 7200 > "$ROOT/logs/$TAG.log" 2>&1
echo "game rc $? $(date +%T)" > "$ROOT/logs/$TAG.done"
