#!/bin/bash
# run_mp3.sh <version>: the modes test. MP3 = the MP1 level (Tatooine shell, test terrain, fitted combat area) that also
# plays online Blast, Heroes vs Villains and the Instant Action missions (ModeE / ModeF, the Mode9 sub-world and its own
# navmesh stream), with auto-player bots in the online modes' layers. One build, then one harness run per mode with
# mode_probe.py, under one claim, released at the end. Logs: work/run_MP3*.log.
cd F:/Frosty/projects/map-playable
V="$1"
python scripts/run_mp.py MP3 "$V" - --no-build --hold 240 --mode Blast --after "mode_probe.py MP3_blast 90" -- \
    --combat-area "rect:35,5,200,175" --combat-area-floor 50 --combat-area-height 0 \
    --modes Blast,HeroesVersusVillains,ModeE,ModeF --bots-team1 7 --bots-team2 8 || exit 1
cp work/run_MP3.log work/run_MP3_blast.log
python scripts/run_mp.py MP3 "$V" - --no-build --hold 240 --mode HeroesVersusVillains --after "mode_probe.py MP3_hvv 90"
cp work/run_MP3.log work/run_MP3_hvv.log
python scripts/run_mp.py MP3 "$V" - --no-build --hold 300 --mode ModeE --after "mode_probe.py MP3_modee 120" --release
cp work/run_MP3.log work/run_MP3_modee.log
