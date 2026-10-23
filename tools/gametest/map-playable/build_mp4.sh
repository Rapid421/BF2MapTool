#!/bin/bash
# build_mp4.sh <LABEL> <version> <loading image>: everything at once -- the MP1 level (Tatooine shell, test terrain,
# fitted combat area) with the extra modes and bots of MP3 and the menu entry, loading screen and Kyber declaration of
# MP2, all from one `level create`. Never run while the game runs.
set -e
L="$1"; VER="$2"; IMG="$3"
cd F:/Frosty/projects/map-playable
bash scripts/build_level.sh "$L" "$VER" --combat-area "rect:35,5,200,175" --combat-area-floor 50 --combat-area-height 0 \
    --modes Blast,HeroesVersusVillains,ModeE,ModeF --bots-team1 7 --bots-team2 8 \
    --menu-name "Dune Sea Arena" --planet-name "Tatooine" \
    --menu-description "A new battlefield in the Dune Sea: open sand, a mesa and a sinkhole. Built with frostycli level create." \
    --loading-screen "$IMG" --kyber "Dune Sea Arena"
grep -a -E "menu entry|picker|lookup|loading screen|Kyber|auto-player|embedded|category" work/${L}_create.log | head -20
python scripts/fbmod_embedded.py mods/$L.fbmod KyberLevelDeclarationData | head -12
