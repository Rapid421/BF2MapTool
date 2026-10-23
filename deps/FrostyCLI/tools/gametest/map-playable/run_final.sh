#!/bin/bash
# run_final.sh <MP4 version>: one more harness turn, taken fairly (let another session run first, then take its gap):
#  1. MP3 (0925.08, built) in Blast, alone (its bots join but do not spawn): out 15 m for 2 s and back -- does the
#     warning clear without a death? -- then the fatal walk-out;
#  2. MP3 in ModeF (the Instant Action defend mission);
#  3. MP4 = MP3 + MP2 in one `level create`, then menu mode for the Instant Action pickers (drive with menu.py).
# Release with `python scripts/harness.py --release` after the menu test.
cd F:/Frosty/projects/map-playable
V4="$1"
for i in $(seq 1 36); do
    c=$(cat F:/Frosty/gamelink/harness.claim 2>/dev/null)
    if [ -n "$c" ] && ! echo "$c" | grep -q "map-playable"; then echo "$(date +%H:%M:%S) other session's turn: $c"; break; fi
    sleep 5
done
python scripts/fastclaim.py "MP3 out-and-back + ModeF + MP4 menu test" 7200 || exit 1
python scripts/run_level_test.py MP3o mods/MP3.fbmod AddedContent/MapPlayable/Levels/MP3/MP3 --mode Blast --hold 200 \
    --after "boundary_test.py MP3o work/mp3_outback_plan.json" > work/run_MP3o.log 2>&1
python scripts/run_level_test.py MP3f mods/MP3.fbmod AddedContent/MapPlayable/Levels/MP3/MP3 --mode ModeF --hold 240 \
    --after "mode_probe.py MP3c_modef 120" > work/run_MP3f.log 2>&1
bash scripts/build_mp4.sh MP4 "$V4" work/mp2_loading.png > work/build_MP4.log 2>&1 || { echo "MP4 build failed"; python scripts/harness.py --release; exit 1; }
python scripts/run_menu.py MP4 "$V4" - --hold 720 --no-build
echo "MENU UP $(date +%H:%M:%S)"
