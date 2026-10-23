#!/bin/sh
# P0-1: the new-user flow on one Blender version, in a new profile and a new work folder.
# sh run_newuser.sh <blender version, e.g. 5.2> <tag>  -> tmp/night/nu_<tag>/{profile,work,result.json,blender.log}
cd "$(dirname "$0")/../.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
V=$1; TAG=${2:-$1}
BL="C:/Program Files/Blender Foundation/Blender $V/blender.exe"
C=${NIGHT_ROOT:-E:/BF2MapTool_night}/nu_$TAG
mkdir -p "${NIGHT_ROOT:-E:/BF2MapTool_night}/tmp"; export TMP=${NIGHT_ROOT:-E:/BF2MapTool_night}/tmp TEMP=${NIGHT_ROOT:-E:/BF2MapTool_night}/tmp TMPDIR=${NIGHT_ROOT:-E:/BF2MapTool_night}/tmp
rm -rf "$C"; mkdir -p "$C/profile" "$C/work"
export BLENDER_USER_SCRIPTS=$C/profile
ZIP=$(ls -t "$ROOT"/tmp/night/dist/bf2_map_tool-*.zip | grep -v extension | head -1)
timeout 1800 "$BL" -b --factory-startup --python "$ROOT/tests/addon/test_newuser.py" -- "$ZIP" \
  "$ROOT/tmp/night/bundle/BF2MapTool_WIP_2026-10-12" "$C/work" "$C/result.json" > "$C/blender.log" 2>&1
echo "rc=$? $V $(date +%T)"
