#!/bin/sh
# Load one tool-built level in every game mode it declares, one after the other (each test waits in the game-test queue).
# sh run_modes.sh <LevelName> <tag> [modes...]      results: work/<LevelName>/review/<tag>_<mode>/result.json, marker logs/<tag>_modes.done
cd "$(dirname "$0")/.." || exit 1
ROOT=$(pwd -W 2>/dev/null) || ROOT=$(pwd)
NAME=$1; TAG=$2; shift 2
MODES=${*:-"SkirmishBlast HeroesVersusVillains SkirmishOnslaught Blast"}
export TMP=$ROOT/tmp TEMP=$ROOT/tmp TMPDIR=$ROOT/tmp
W=$ROOT/work/$NAME
MOD="$W/mods/$NAME.fbmod"
rm -f "$ROOT/logs/${TAG}_modes.done"
python "$ROOT/tests/auto_shots.py" "$W/export/map.json" "$ROOT/work/settings.json" "$W/review_shots_$TAG.json" > "$ROOT/logs/${TAG}_shots.log" 2>&1
OUT=""
for M in $MODES; do
  python "$ROOT/tools/game_test.py" "$MOD" "AddedContent/$NAME/Levels/${NAME}_01/${NAME}_01" "$M" --out "$W/review/${TAG}_$M" \
    --shots "$W/review_shots_$TAG.json" --hold 300 --wait 7200 > "$ROOT/logs/${TAG}_$M.log" 2>&1
  OUT="$OUT $M=rc$?"
done
echo "$OUT $(date +%T)" > "$ROOT/logs/${TAG}_modes.done"
