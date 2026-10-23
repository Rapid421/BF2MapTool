#!/bin/sh
# P0-2: validate + install the extension zip into a throw-away profile, then test it in a second Blender start.
# sh run_extension.sh <blender version> <tag> -> E:/BF2MapTool_night/ext_<tag>/
cd "$(dirname "$0")/../.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
V=$1; TAG=${2:-$1}
BL="C:/Program Files/Blender Foundation/Blender $V/blender.exe"
N=${NIGHT_ROOT:-E:/BF2MapTool_night}
C=$N/ext_$TAG
rm -rf "$C"; mkdir -p "$C/scripts" "$C/config" "$C/ext" "$C/work" "$N/tmp"
export TMP=$N/tmp TEMP=$N/tmp TMPDIR=$N/tmp
export BLENDER_USER_SCRIPTS=$C/scripts BLENDER_USER_CONFIG=$C/config BLENDER_USER_EXTENSIONS=$C/ext
ZIP=$(ls -t "$ROOT"/tmp/night/dist/bf2_map_tool-*-extension.zip | head -1)
timeout 300 "$BL" --command extension validate "$ZIP" > "$C/validate.log" 2>&1; echo "validate rc=$?"
timeout 300 "$BL" --command extension install-file -r user_default -e "$ZIP" > "$C/install.log" 2>&1; echo "install rc=$?"
timeout 600 "$BL" -b --python "$ROOT/tests/addon/test_extension.py" -- "$ROOT/tmp/night/bundle/BF2MapTool_WIP_2026-10-12" "$C/work" "$C/result.json" > "$C/blender.log" 2>&1
echo "test rc=$? $V $(date +%T)"
