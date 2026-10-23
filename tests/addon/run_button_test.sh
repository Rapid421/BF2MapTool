#!/bin/sh
# Regenerate the test arena, package the add-on, install it in a throw-away Blender profile and press Port map.
# sh run_button_test.sh <tag>   -> tests/addon/<tag>.json, logs/<tag>_*.log, marker logs/<tag>.done
cd "$(dirname "$0")/../.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
TAG=${1:-button}
BL="C:/Program Files/Blender Foundation/Blender 5.2/blender.exe"
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
export BLENDER_USER_SCRIPTS=$ROOT/tmp/blender_scripts
rm -f "$ROOT/logs/$TAG.done"
{
  "$BL" -b --factory-startup --python "$ROOT/tests/scenes/make_arena.py" -- "$ROOT/tests/scenes/arena.blend" > "$ROOT/logs/${TAG}_scene.log" 2>&1 || { echo "scene failed"; exit 1; }
  python "$ROOT/tools/make_zip.py" > "$ROOT/logs/${TAG}_zip.log" 2>&1 || { echo "zip failed"; exit 1; }
  ZIP=$(ls -t "$ROOT"/dist/bf2_map_tool-*.zip | head -1)
  "$BL" -b --factory-startup --python "$ROOT/tests/addon/test_install.py" -- "$ZIP" "$ROOT/work/settings.json" > "$ROOT/logs/${TAG}_install.log" 2>&1 || { echo "install failed"; exit 1; }
  "$BL" --factory-startup "$ROOT/tests/scenes/arena.blend" --python "$ROOT/tests/addon/test_port_button.py" -- "$ROOT/work/settings.json" "$ROOT/tests/addon/$TAG.json" --limit 2400 > "$ROOT/logs/${TAG}_button.log" 2>&1
  echo "button rc $?"
} > "$ROOT/logs/$TAG.run.log" 2>&1
echo "$(tail -1 "$ROOT/logs/$TAG.run.log") $(date +%T)" > "$ROOT/logs/$TAG.done"
