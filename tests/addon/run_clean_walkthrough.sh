#!/bin/sh
# Clean-machine walk-through of docs/setup.md + quickstart.md with nothing of the development state:
#   a NEW Blender profile (no add-on installed), a NEW empty work folder, the packaged zip only.
#   1 package the add-on            2 install it from the zip, enable, fill the preferences, Check setup
#   3 open the test arena, press Port map in a windowed Blender, wait for the .fbmod
# sh run_clean_walkthrough.sh <tag>   -> tmp/clean_<tag>/ (profile, work), tests/addon/<tag>.json, logs/<tag>_*.log, marker logs/<tag>.done
cd "$(dirname "$0")/../.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
TAG=${1:-clean}
BL="C:/Program Files/Blender Foundation/Blender 5.2/blender.exe"
C=$ROOT/tmp/clean_$TAG
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
rm -rf "$C"; mkdir -p "$C/profile" "$C/work"
export BLENDER_USER_SCRIPTS=$C/profile
rm -f "$ROOT/logs/$TAG.done"
{
  python "$ROOT/tests/addon/clean_settings.py" "$ROOT/work/settings.json" "$C/work" "$C/settings.json" || { echo "settings failed"; exit 1; }
  "$BL" -b --factory-startup --python "$ROOT/tests/scenes/make_arena.py" -- "$C/arena.blend" CleanArena > "$ROOT/logs/${TAG}_scene.log" 2>&1 || { echo "scene failed"; exit 1; }
  python "$ROOT/tools/make_zip.py" > "$ROOT/logs/${TAG}_zip.log" 2>&1 || { echo "zip failed"; exit 1; }
  ZIP=$(ls -t "$ROOT"/dist/bf2_map_tool-*.zip | head -1)
  echo "zip $ZIP"
  "$BL" -b --factory-startup --python "$ROOT/tests/addon/test_install.py" -- "$ZIP" "$C/settings.json" > "$ROOT/logs/${TAG}_install.log" 2>&1 || { echo "install failed"; exit 1; }
  grep -o 'ADDON_TEST .*' "$ROOT/logs/${TAG}_install.log" | cut -c1-1500
  "$BL" --factory-startup "$C/arena.blend" --python "$ROOT/tests/addon/test_port_button.py" -- "$C/settings.json" "$ROOT/tests/addon/$TAG.json" --limit 2400 > "$ROOT/logs/${TAG}_button.log" 2>&1
  echo "button rc $?"
} > "$ROOT/logs/$TAG.run.log" 2>&1
echo "$(tail -1 "$ROOT/logs/$TAG.run.log") $(date +%T)" > "$ROOT/logs/$TAG.done"
