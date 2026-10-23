#!/bin/bash
# run_mp3b.sh <version>: MP3 rebuilt with the CLI whose added auto-player managers are started (a self-starting delay
# -> ApplySettings, as the shipped managers are by their mode's stream-in), then Blast and Heroes vs Villains (do the bots
# fill the teams and the round start?), then the MP1 walk-out (the MP1c mod: out-and-back, then the fatal walk-out), all
# under one claim, released at the end. Logs: work/run_MP3.log (copied per mode), work/run_MP1d.log.
cd F:/Frosty/projects/map-playable
V="$1"
python scripts/run_mp.py MP3 "$V" - --build-cli --hold 300 --mode Blast --after "mode_probe.py MP3b_blast 150" -- \
    --combat-area "rect:35,5,200,175" --combat-area-floor 50 --combat-area-height 0 \
    --modes Blast,HeroesVersusVillains,ModeE,ModeF --bots-team1 7 --bots-team2 8 \
    || { python scripts/harness.py --release; exit 1; }
cp work/run_MP3.log work/run_MP3b_blast.log
grep -q "no mod built\|CLI build rc [1-9]" work/run_MP3b_blast.log && { python scripts/harness.py --release; exit 1; }
python scripts/run_mp.py MP3 "$V" - --no-build --hold 300 --mode HeroesVersusVillains --after "mode_probe.py MP3b_hvv 150"
cp work/run_MP3.log work/run_MP3b_hvv.log
python scripts/run_level_test.py MP1d mods/MP1c.fbmod AddedContent/MapPlayable/Levels/MP1/MP1 --hold 240 \
    --after "boundary_test.py MP1d work/mp1_out_plan.json" --release > work/run_MP1d.log 2>&1
echo "done $(date +%H:%M:%S)"
