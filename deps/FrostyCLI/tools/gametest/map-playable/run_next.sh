#!/bin/bash
# run_next.sh <MP3 version> <MP2 version>: the next harness turn, taken fairly -- first let the pathfinding session run a
# map (wait up to 3 min for another session to claim after our release), then take the gap it leaves (fastclaim.py).
# In the turn: MP3 rebuilt with gameplay bots (AutoPlayerEntity + AutoplayerIsGameplay next to the auto-player manager),
# Blast and HvV probed again; then MP2 built (menu entry, loading screen, Kyber) and the game started in menu mode for
# the Custom Arcade test, driven by hand with scripts/menu.py. Release with `python scripts/harness.py --release`.
cd F:/Frosty/projects/map-playable
V3="$1"; V2="$2"
for i in $(seq 1 36); do
    c=$(cat F:/Frosty/gamelink/harness.claim 2>/dev/null)
    if [ -n "$c" ] && ! echo "$c" | grep -q "map-playable"; then echo "$(date +%H:%M:%S) other session's turn: $c"; break; fi
    sleep 5
done
python scripts/fastclaim.py "MP3 walk-out + MP3c gameplay bots + MP2 menu test" 7200 || exit 1
# the walk-out first, on the MP3 build already made (0925.06): in its Blast round the bots join but never spawn, so the
# player is alone in a live round whose boundary is the fitted arena (the Arcade AI killed him at once in MP1/MP1d)
python scripts/run_level_test.py MP3w mods/MP3.fbmod AddedContent/MapPlayable/Levels/MP3/MP3 --mode Blast --hold 240     --after "boundary_test.py MP3w work/mp1_out_plan.json" > work/run_MP3w.log 2>&1
python scripts/run_mp.py MP3 "$V3" - --build-cli --hold 300 --mode Blast --after "mode_probe.py MP3c_blast 150" -- \
    --combat-area "rect:35,5,200,175" --combat-area-floor 50 --combat-area-height 0 \
    --modes Blast,HeroesVersusVillains,ModeE,ModeF --bots-team1 7 --bots-team2 8 \
    || { python scripts/harness.py --release; exit 1; }
cp work/run_MP3.log work/run_MP3c_blast.log
grep -q "no mod built\|CLI build rc [1-9]" work/run_MP3c_blast.log && { python scripts/harness.py --release; exit 1; }
python scripts/run_mp.py MP3 "$V3" - --no-build --hold 300 --mode HeroesVersusVillains --after "mode_probe.py MP3c_hvv 150"
cp work/run_MP3.log work/run_MP3c_hvv.log
python scripts/run_menu.py MP2 "$V2" work/mp2_loading.png --hold 1500
echo "MENU UP $(date +%H:%M:%S)"
