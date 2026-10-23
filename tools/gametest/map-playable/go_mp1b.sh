#!/bin/bash
# go_mp1b.sh: claim the harness, build MP1b (MP1 without Skirmish AI), run the boundary test, release.
cd F:/Frosty/projects/map-playable
python -c "import sys; sys.path.insert(0,'scripts'); import harness; harness.claim('MP1b no-AI boundary test', wait=7200)" || exit 1
echo "claimed $(date +%H:%M:%S)"
bash scripts/build_noai.sh MP1b MP1 0925.02 || { python scripts/harness.py --release; exit 1; }
python scripts/run_level_test.py MP1b mods/MP1b.fbmod AddedContent/MapPlayable/Levels/MP1/MP1 --hold 300 \
    --after "boundary_test.py MP1b work/mp1_plan.json" --release
echo "done $(date +%H:%M:%S)"
