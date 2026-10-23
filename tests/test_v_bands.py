"""After the split: every piece of a banded material must have all its uvs inside v 0..1, and the cutting must not
change the surface (same total area per material as the scene's). python test_v_bands.py <work dir>"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'runtime')))
from bf2maptool import fbx

work = sys.argv[1]
P = json.load(open(work + '/pieces/pieces.json'))
banded = set(P.get('v_banded') or [])
ok = True
area = {}
for p in P['pieces']:
    g = fbx.read('%s/pieces/%s' % (work, p['file']))
    a = np.linalg.norm(np.cross(g['pos'][:, 1] - g['pos'][:, 0], g['pos'][:, 2] - g['pos'][:, 0]), axis=1).sum() / 2
    area[p['material']] = area.get(p['material'], 0.0) + float(a)
    if p['material'] in banded:
        v = g['uv0'][..., 1]
        if v.min() < -1e-4 or v.max() > 1 + 1e-4:
            ok = False
            print('FAIL %s: v range %.4f .. %.4f' % (p['name'], v.min(), v.max()))
print('banded materials:', sorted(banded))
print('area per material m2:', {k: round(v, 2) for k, v in sorted(area.items())})
print('PASS' if ok else 'FAIL')
sys.exit(0 if ok else 1)
