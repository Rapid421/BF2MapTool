#!/bin/bash
# build_mp2.sh <LABEL> <version> <loading image|-> : the MP1 level (Tatooine shell, test terrain, fitted combat area) plus
# what makes it a map of the game, all from `level create`: its Custom Arcade entry (Team Battle / Onslaught / Duel), its
# own loading screen (from <loading image>; "-" = Tatooine's) and its Kyber declaration. Never run while the game runs.
set -e
L="$1"; VER="$2"; IMG="$3"
cd F:/Frosty/projects/map-playable
LS="$IMG"; if [ "$IMG" = "-" ]; then LS=host; fi
bash scripts/build_level.sh "$L" "$VER" --combat-area "rect:35,5,200,175" --combat-area-floor 50 --combat-area-height 0 \
    --menu-name "Dune Sea Arena" --planet-name "Tatooine" \
    --menu-description "A new battlefield in the Dune Sea: open sand, a mesa and a sinkhole. Built with frostycli level create." \
    --loading-screen "$LS" --kyber "Dune Sea Arena"
grep -a -E "menu entry|picker|lookup|loading screen|Kyber|string database|embedded|category" work/${L}_create.log | head -12
python scripts/fbmod_embedded.py mods/$L.fbmod KyberLevelDeclarationData | head -30
