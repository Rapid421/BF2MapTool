#!/bin/bash
# build_fewai.sh <LABEL> <SRC LABEL> <version> [max AI]: a test variant of a level project with the Skirmish AI capped
# (PF_TeamDeathmatch_Skirmish's AIPlayerProviderManagerEntityData.MaxAIPlayerCount 20 -> <max AI>, default 2), so
# boundary_test.py can walk the player round the edges without being shot. (Emptying the level's AIPlayerProvider teams
# instead crashed the game 2.5 s after world-up at game+0x1B1483F, MP1b.) Same level path as <SRC LABEL>; own mod file.
set -e
L="$1"; SRC="$2"; VER="$3"; N="${4:-2}"
cd F:/Frosty/projects/map-playable
CLI=F:/Frosty/projects/map-playable/frostycli-wt/FrostyCLI/bin/x64/Release/FrostyCLI.exe
GP="F:/STAR WARS Battlefront II"
if tasklist | grep -qi starwars; then echo "GAME RUNNING - abort"; exit 1; fi
cp work/$SRC.fbproject work/$L.fbproject
$CLI --game swbf2 --game-path "$GP" --project work/$L.fbproject --save ebx set Gameplay/GameModes/Skirmish/PF_TeamDeathmatch_Skirmish \
    --object AIPlayerProviderManagerEntityData --prop MaxAIPlayerCount --value $N 2>&1 | grep -a -E "OK|ERR|rror|not found" | head -3
$CLI --game swbf2 --game-path "$GP" --project work/$L.fbproject mod build --out mods/$L.fbmod \
    --mod-name "$L map-playable test (Skirmish AI capped at $N)" --mod-version $VER 2>&1 | grep -a -E "OK|ERR"
ls -la mods/$L.fbmod | awk '{print $5}'
