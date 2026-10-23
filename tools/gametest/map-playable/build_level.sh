#!/bin/bash
# build_level.sh <LABEL> <version> [extra level-create args...]
# A new Skirmish level on the Tatooine shell (level create --skirmish-from, the navmesh session's test terrain for
# Tatooine: floor 63.5 m over the Blast play box, a mesa and a pit, hills outside), then level validate, the combat-area
# listing, and mod build. Writes work/<LABEL>.fbproject, work/<LABEL>_create.log, mods/<LABEL>.fbmod.
# Level: AddedContent/MapPlayable/Levels/<LABEL>/<LABEL>. Never run while the game runs.
set -e
L="$1"; VER="$2"; shift 2
cd F:/Frosty/projects/map-playable
CLI=F:/Frosty/projects/map-playable/frostycli-wt/FrostyCLI/bin/x64/Release/FrostyCLI.exe
NAVGEN=F:/Frosty/BfiiMapTools/artifacts/bin/Bfii.Navgen/Release/net8.0/navgen.dll
GP="F:/STAR WARS Battlefront II"
LEVEL="AddedContent/MapPlayable/Levels/$L/$L"
if tasklist | grep -qi starwars; then echo "GAME RUNNING - abort"; exit 1; fi
rm -f work/$L.fbproject
$CLI --game swbf2 --game-path "$GP" --save-as work/$L.fbproject level create $LEVEL \
    --skirmish-from Levels/MP/Tatooine_01/Tatooine_01 --terrain-res work/tat/terrain.res --navgen $NAVGEN "$@" \
    > work/${L}_create.log 2>&1 || { echo "level create FAILED"; tail -20 work/${L}_create.log; exit 1; }
grep -a -E "combat area|OK +level create|OK +navmesh|ERR|FAIL|xception" work/${L}_create.log | head -30
$CLI --game swbf2 --game-path "$GP" --project work/$L.fbproject level combat-area $LEVEL 2>&1 | grep -a "combat-area" | head -20
$CLI --game swbf2 --game-path "$GP" --project work/$L.fbproject level validate $LEVEL 2>&1 | grep -a "Result:" | tail -1
$CLI --game swbf2 --game-path "$GP" --project work/$L.fbproject mod build --out mods/$L.fbmod \
    --mod-name "$L map-playable test" --mod-version $VER 2>&1 | grep -a -E "OK|ERR"
ls -la mods/$L.fbmod | awk '{print $5}'
