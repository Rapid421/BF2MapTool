#!/bin/bash
# run_duel.sh: the last offline mode check -- MP4 (0925.09, built) in SkirmishDuel (Arcade Duel), probed with
# mode_probe.py; taken in the gap another session leaves (fastclaim.py), released at the end.
cd F:/Frosty/projects/map-playable
python scripts/fastclaim.py "MP4 Arcade Duel check" 7200 || exit 1
python scripts/run_level_test.py MP4d mods/MP4.fbmod AddedContent/MapPlayable/Levels/MP4/MP4 --mode SkirmishDuel --hold 240 \
    --after "mode_probe.py MP4_duel 90" --release > work/run_MP4d.log 2>&1
echo "done $(date +%H:%M:%S)"
