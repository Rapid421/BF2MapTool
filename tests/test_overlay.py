"""Does the add-on's collision overlay show exactly what the build made solid?
    python test_overlay.py <scene.blend> <work/Name folder of a finished build> <settings.json> <out.json>

Left side: `overlay.solid_triangles()` read in Blender from the scene (what the viewport draws).
Right side: the collision the `collision` stage wrote (col/<piece>.obj, piece frame, game axes) put back into scene
coordinates. Doors are left out on both sides (their shape is made by the doors stage).
Pass: every overlay triangle's centre lies on the built collision (within 2 cm) and every built collision triangle's
centre lies on the overlay, and the two areas agree within 1 %.
"""
import json
import os
import subprocess
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', 'runtime')))
from bf2maptool import config
from bf2maptool.checks.space import Tris

SCENE, WORK, SETTINGS, OUT = sys.argv[1:5]
S = config.load_settings(SETTINGS)
TOL = 0.02
dump = os.path.join(WORK, 'tmp', 'overlay_dump.npz')
os.makedirs(os.path.dirname(dump), exist_ok=True)
r = subprocess.run([S['blender'], '-b', SCENE, '--factory-startup', '--python', os.path.join(HERE, 'overlay_dump.py'), '--',
                    os.path.normpath(os.path.join(HERE, '..', 'addon', 'bf2_map_tool')), dump], capture_output=True, text=True, timeout=600,
                   stdin=subprocess.DEVNULL)
if 'OVERLAY DUMP' not in r.stdout:
    print((r.stdout + r.stderr)[-1500:]); sys.exit(2)
z = np.load(dump)
kinds, counts = [str(k) for k in z['kinds']], z['counts']
ends = np.cumsum(counts)
ov = [(kinds[i], str(z['names'][i]), z['tris'][ends[i] - counts[i]:ends[i]]) for i in range(len(kinds))]
A = Tris([('%s:%s' % (k, n), t) for k, n, t in ov if k != 'door'])

with open(os.path.join(WORK, 'pieces', 'pieces.json'), encoding='utf-8') as f:
    PJ = json.load(f)
parts = []
for p in PJ['pieces']:
    op = os.path.join(WORK, 'col', p['name'] + '.obj')
    if not os.path.exists(op):
        continue
    v, f = [], []
    for line in open(op):
        if line.startswith('v '):
            v.append([float(x) for x in line.split()[1:4]])
        elif line.startswith('f '):
            f.append([int(x) - 1 for x in line.split()[1:4]])
    v = np.array(v); g = v[np.array(f)]                                  # game axes, piece frame
    scene = np.stack([g[..., 0], -g[..., 2], g[..., 1]], -1) + np.asarray(p['centre'])     # back from (x, z, -y)
    parts.append((p['name'], scene))
B = Tris(parts)


def off(X, Y):
    """triangles of X whose centre is further than TOL from Y's surface"""
    bad = []
    for i, c in enumerate(X.t.mean(1)):
        d, _ = Y.distance(c, TOL)
        if d is None:
            bad.append(i)
    return bad


a_off, b_off = off(A, B), off(B, A)
res = {'scene': SCENE, 'work': WORK,
       'overlay': {'triangles': int(len(A.t)), 'area_m2': round(float(A.area.sum()), 3),
                   'by_kind': {k: int(sum(len(t) for kk, _, t in ov if kk == k)) for k in sorted(set(kinds))}},
       'built': {'triangles': int(len(B.t)), 'area_m2': round(float(B.area.sum()), 3), 'pieces_with_collision': len(parts)},
       'overlay_not_in_build': {'triangles': len(a_off), 'area_m2': round(float(A.area[a_off].sum()), 3),
                                'objects': sorted({A.names[int(A.owner[i])] for i in a_off})[:20]},
       'build_not_in_overlay': {'triangles': len(b_off), 'area_m2': round(float(B.area[b_off].sum()), 3),
                                'pieces': sorted({B.names[int(B.owner[i])] for i in b_off})[:20]}}
area_ok = abs(res['overlay']['area_m2'] - res['built']['area_m2']) <= 0.01 * max(res['built']['area_m2'], 1e-9)
res['pass'] = not a_off and not b_off and area_ok and len(A.t) > 0
json.dump(res, open(OUT, 'w'), indent=1)
print(json.dumps(res, indent=1))
print('PASS' if res['pass'] else 'FAIL')
sys.exit(0 if res['pass'] else 1)
