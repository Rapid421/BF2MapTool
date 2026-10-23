#!/bin/bash
# build_noai.sh <LABEL> <SRC LABEL> <version>: a test variant of an existing level project with no Skirmish AI -- the
# level's AIPlayerProviderEntityData (TeamDeathmatch_Skirmish_Schematics) loses both teams, so no bot joins and nothing
# shoots the player while boundary_test.py walks him round the edges. Same level path as <SRC LABEL>; own mod file.
set -e
L="$1"; SRC="$2"; VER="$3"
cd F:/Frosty/projects/map-playable
CLI=F:/Frosty/projects/map-playable/frostycli-wt/FrostyCLI/bin/x64/Release/FrostyCLI.exe
GP="F:/STAR WARS Battlefront II"
if tasklist | grep -qi starwars; then echo "GAME RUNNING - abort"; exit 1; fi
SCH="AddedContent/MapPlayable/Levels/$SRC/TeamDeathmatch_Skirmish_Schematics"
cp work/$SRC.fbproject work/$L.fbproject
for i in 1 0; do
  $CLI --game swbf2 --game-path "$GP" --project work/$L.fbproject --save ebx array-remove $SCH --object AIPlayerProviderEntityData \
      --prop Teams --index $i 2>&1 | grep -a -E "OK|ERR|rror|not found" | head -3
done
$CLI --game swbf2 --game-path "$GP" --project work/$L.fbproject mod build --out mods/$L.fbmod \
    --mod-name "$L map-playable test (no Skirmish AI)" --mod-version $VER 2>&1 | grep -a -E "OK|ERR"
ls -la mods/$L.fbmod | awk '{print $5}'
