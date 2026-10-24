"""Spawns sit on ONE connected navmesh part (LEGO Utapau, 2026-10-23: they were spread over 7 unconnected parts and
the Blast bots stood still). Read-only on a finished work folder:
    python test_spawn_part.py <work>/<Map>/export/map.json <settings.json>      exit 0 = every spawn spot on one part"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime'))
from bf2maptool import config                      # noqa: E402
from bf2maptool.stages import spawns as S          # noqa: E402

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
nav = job.dirs['nav'] if hasattr(job, 'dirs') else os.path.join(job.work, 'nav')
lvl = job.level.split('/')[-1]
floor = S.Floor(os.path.join(nav, lvl + '_navmesh.polys.obj'), os.path.join(nav, lvl + '_geometry.obj'), S.anchors(job))
P = S.Planner(job, floor, np.random.default_rng(11))
roots = np.array(floor.cand_root)
print('parts joined into the floor:', len(floor.part_area), ' safe spots all parts:', len(P.C_all), ' spawn spots:', len(P.C))
print('spawn part:', P.spawn_part)
# every spawn spot must come from one part: look each spot up among the candidates of its part
cand = {tuple(np.round(c, 4)): r for c, r in zip(floor.candidates(np.random.default_rng(11), per_m2=float(np.clip(600.0 / max(floor.area, 1.0), 1 / 6.0, 2.0))), floor.cand_root)}
parts = {cand.get(tuple(np.round(c, 4))) for c in P.C}
ok = len(parts) == 1 and None not in parts and len(P.C) >= 8 and len(P.C_all) >= len(P.C)
print('PASS' if ok else 'FAIL', 'parts under the spawn spots:', len(parts))
sys.exit(0 if ok else 1)
