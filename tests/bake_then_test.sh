#!/bin/sh
# Bake an already exported / built level (work/<LevelName>/export/map.json) and game-test the baked mod.
# sh bake_then_test.sh <LevelName> <draft|normal|high> <tag> [shots script.py]      markers: logs/<tag>_build.done, logs/<tag>.done
cd "$(dirname "$0")/.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
NAME=$1; Q=$2; TAG=$3; SHOTS=${4:-$ROOT/tests/auto_shots.py}
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
rm -f "$ROOT/logs/${TAG}_build.done" "$ROOT/logs/$TAG.done"
W=$ROOT/work/$NAME
cd "$ROOT/runtime" || exit 1
python -m bf2maptool port "$W/export/map.json" --settings "$ROOT/work/settings.json" --bake "$Q" > "$ROOT/logs/${TAG}_build.log" 2>&1
RC=$?
echo "rc $RC $(date +%T)" > "$ROOT/logs/${TAG}_build.done"
if [ $RC -ne 0 ]; then echo "no game test: build rc $RC $(date +%T)" > "$ROOT/logs/$TAG.done"; exit 1; fi
python "$SHOTS" "$W/export/map.json" "$ROOT/work/settings.json" "$W/review_shots_$TAG.json" > "$ROOT/logs/${TAG}_shots.log" 2>&1
python "$ROOT/tools/game_test.py" "$W/mods/$NAME.fbmod" "AddedContent/$NAME/Levels/${NAME}_01/${NAME}_01" SkirmishBlast --out "$W/review/$TAG" \
  --shots "$W/review_shots_$TAG.json" --hold 420 --wait 7200 > "$ROOT/logs/$TAG.log" 2>&1
echo "game rc $? $(date +%T)" > "$ROOT/logs/$TAG.done"
