#!/bin/bash
# go_mp2.sh <version>: after the MP3b batch has finished (work/go_mp3b.log says "done"), claim the harness, build MP2
# (the MP1 level + Custom Arcade entry + its own loading screen + Kyber declaration) and start the game in menu mode
# for 30 min; the menus are then driven with scripts/menu.py. Release with `python scripts/harness.py --release`.
cd F:/Frosty/projects/map-playable
until grep -q "^done " work/go_mp3b.log 2>/dev/null; do sleep 10; done
python scripts/run_menu.py MP2 "$1" work/mp2_loading.png --hold 1800
