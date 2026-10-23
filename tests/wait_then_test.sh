#!/bin/sh
# Wait for a build's marker file (it must hold "rc 0"), then game-test that level through the queue.
# sh wait_then_test.sh <marker> <LevelName> <shots script.py> <tag> [mode]      marker: logs/<tag>.done
cd "$(dirname "$0")/.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
MARK=$1; NAME=$2; SHOTS=$3; TAG=$4; MODE=${5:-SkirmishBlast}
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
rm -f "$ROOT/logs/$TAG.done"
n=0
until [ -f "$MARK" ]; do
  sleep 5; n=$((n + 1))
  if [ $n -gt 720 ]; then echo "waited 60 min for $MARK - gave up $(date +%T)" > "$ROOT/logs/$TAG.done"; exit 1; fi
done
if ! grep -q "rc 0" "$MARK"; then echo "not started: $(cat "$MARK") $(date +%T)" > "$ROOT/logs/$TAG.done"; exit 1; fi
W=$ROOT/work/$NAME
python "$SHOTS" "$W/export/map.json" "$ROOT/work/settings.json" "$W/review_shots_$TAG.json" > "$ROOT/logs/${TAG}_shots.log" 2>&1
python "$ROOT/tools/game_test.py" "$W/mods/$NAME.fbmod" "AddedContent/$NAME/Levels/${NAME}_01/${NAME}_01" "$MODE" --out "$W/review/$TAG" \
  --shots "$W/review_shots_$TAG.json" --hold 420 --wait 7200 > "$ROOT/logs/$TAG.log" 2>&1
echo "game rc $? $(date +%T)" > "$ROOT/logs/$TAG.done"
