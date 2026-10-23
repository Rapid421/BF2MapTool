#!/bin/sh
# Time the bake presets on one scene: the bake stage only (--until bake), one preset after the other.
# sh time_presets.sh <scene.blend> <LevelName> <tag> [presets, default "normal high"]   -> logs/<tag>_<preset>.log, work/<Name>/bake_<preset>.json, marker logs/<tag>.done
cd "$(dirname "$0")/.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
SCENE=$1; NAME=$2; TAG=$3; shift 3
PRESETS=${*:-"normal high"}
BL="C:/Program Files/Blender Foundation/Blender 5.2/blender.exe"
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
rm -f "$ROOT/logs/$TAG.done"
W=$ROOT/work/$NAME
cd "$ROOT/runtime" || exit 1
OUT=""
for Q in $PRESETS; do
  python -m bf2maptool bake "$SCENE" --settings "$ROOT/work/settings.json" --blender "$BL" --export-script "$ROOT/addon/bf2_map_tool/export_scene.py" \
    --export-dir "$W/export" --quality "$Q" --until bake > "$ROOT/logs/${TAG}_$Q.log" 2>&1
  OUT="$OUT $Q=rc$?"
  cp "$W/bake.json" "$W/bake_$Q.json" 2>/dev/null
done
echo "$OUT $(date +%T)" > "$ROOT/logs/$TAG.done"
