#!/bin/sh
# A real scene, Blender against game: build it with baked lighting (sky = the scene's World), photograph the game from
# the views in <views.json>, render the same views in Blender, and paste them side by side.
# sh compare_scene.sh <scene.blend> <LevelName> <views.json> <tag> [bake quality, default draft] [vertical fov, default 55]
# markers: logs/<tag>_build.done, logs/<tag>.done, logs/<tag>_ref.done   out: work/<LevelName>/review/<tag>/ (game), .../ref (Blender), .../side
cd "$(dirname "$0")/../.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
SCENE=$1; NAME=$2; VIEWS=$3; TAG=$4; BAKE=${5:-draft}; FOV=${6:-55}
BL="C:/Program Files/Blender Foundation/Blender 5.2/blender.exe"
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
rm -f "$ROOT/logs/${TAG}_build.done" "$ROOT/logs/$TAG.done" "$ROOT/logs/${TAG}_ref.done"
W=$ROOT/work/$NAME
OUT=$W/review/$TAG
mkdir -p "$W/export" "$OUT/ref"
# Blender's own pictures, alongside the build
( "$BL" -b --factory-startup "$SCENE" --python "$ROOT/tests/lightmatch/ref_pictures.py" -- "$VIEWS" "$OUT/ref" "$FOV" > "$ROOT/logs/${TAG}_ref.log" 2>&1
  echo "rc $? $(date +%T)" > "$ROOT/logs/${TAG}_ref.done" ) &
cd "$ROOT/runtime" || exit 1
if [ "$BAKE" = "-" ]; then      # no light bake: the same build without lightmaps (tells a bake fault from a material fault)
  python -m bf2maptool build "$SCENE" --settings "$ROOT/work/settings.json" --blender "$BL" --export-script "$ROOT/addon/bf2_map_tool/export_scene.py" \
    --export-dir "$W/export" > "$ROOT/logs/${TAG}_build.log" 2>&1
else
  python -m bf2maptool bake "$SCENE" --settings "$ROOT/work/settings.json" --blender "$BL" --export-script "$ROOT/addon/bf2_map_tool/export_scene.py" \
    --export-dir "$W/export" --quality "$BAKE" > "$ROOT/logs/${TAG}_build.log" 2>&1
fi
RC=$?
echo "rc $RC $(date +%T)" > "$ROOT/logs/${TAG}_build.done"
MOD="$W/mods/$NAME.fbmod"
if [ $RC -ne 0 ] || [ ! -f "$MOD" ]; then echo "no game test: build rc $RC $(date +%T)" > "$ROOT/logs/$TAG.done"; exit 1; fi
LIGHTMATCH_VIEWS="$VIEWS" python "$ROOT/tests/lightmatch/shots.py" "$W/export/map.json" "$ROOT/work/settings.json" "$W/review_shots_$TAG.json" > "$ROOT/logs/${TAG}_shots.log" 2>&1
python "$ROOT/tools/game_test.py" "$MOD" "AddedContent/$NAME/Levels/${NAME}_01/${NAME}_01" SkirmishBlast --out "$OUT" --shots "$W/review_shots_$TAG.json" \
  --fov "$FOV" --hold 420 --wait 7200 > "$ROOT/logs/$TAG.log" 2>&1
echo "game rc $? $(date +%T)" > "$ROOT/logs/$TAG.done"
wait
python "$ROOT/tests/lightmatch/side_by_side.py" "$OUT/ref" "$OUT" "$OUT/side" > "$ROOT/logs/${TAG}_side.log" 2>&1
