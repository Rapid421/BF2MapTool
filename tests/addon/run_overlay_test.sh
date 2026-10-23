#!/bin/sh
# Package the add-on, install it in a throw-away Blender profile, switch the collision overlay on and photograph it.
# sh run_overlay_test.sh <scene.blend> <tag>   -> tests/addon/<tag>.png / .json, marker logs/<tag>.done
cd "$(dirname "$0")/../.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
SCENE=$1; TAG=${2:-overlay}
BL="C:/Program Files/Blender Foundation/Blender 5.2/blender.exe"
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
export BLENDER_USER_SCRIPTS=$ROOT/tmp/blender_scripts
rm -f "$ROOT/logs/$TAG.done" "$ROOT/tests/addon/$TAG.json"
{
  python "$ROOT/tools/make_zip.py" > "$ROOT/logs/${TAG}_zip.log" 2>&1 || { echo "zip failed"; exit 1; }
  ZIP=$(ls -t "$ROOT"/dist/bf2_map_tool-*.zip | head -1)
  "$BL" -b --factory-startup --python "$ROOT/tests/addon/test_install.py" -- "$ZIP" "$ROOT/work/settings.json" > "$ROOT/logs/${TAG}_install.log" 2>&1 || { echo "install failed"; exit 1; }
  timeout 180 "$BL" --factory-startup "$SCENE" --python "$ROOT/tests/addon/test_overlay_ui.py" -- "$ROOT/tests/addon/$TAG.png" "$ROOT/tests/addon/$TAG.json" > "$ROOT/logs/${TAG}_ui.log" 2>&1
  echo "overlay rc $?"
} > "$ROOT/logs/$TAG.run.log" 2>&1
echo "$(tail -1 "$ROOT/logs/$TAG.run.log") $(date +%T)" > "$ROOT/logs/$TAG.done"
