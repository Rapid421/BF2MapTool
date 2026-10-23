"""split.repair_tjunctions: the two-grid search (fine cells for short edges, 2 m cells for long ones) gives exactly the
output of the original single 2 m grid, on geometry that has both: 2 cm 'studs' cut into 1/2/3 strips (T-junctions)
and long 12 m triangles whose edges carry corners of the studs.   python test_tjunction_grid.py"""
import copy
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime'))
from bf2maptool.stages import split  # noqa: E402

s = 0.02
tris = []
for row in range(60):
    k = 1 + row % 3
    for col in range(60):
        x0, y0 = col * s, row * s
        xs = np.linspace(x0, x0 + s, k + 1)
        for i in range(k):
            a, b, c, d = (xs[i], y0, 0), (xs[i + 1], y0, 0), (xs[i + 1], y0 + s, 0), (xs[i], y0 + s, 0)
            tris += [(a, b, c), (a, c, d)]
# long triangles: one edge along y = 0 from x = -6 to +6 m (the stud corners on y = 0 lie inside it), one far away
tris += [((-6.0, 0.0, 0.0), (6.0, 0.0, 0.0), (0.0, -5.0, 0.0)), ((-6.0, 30.0, 1.0), (6.0, 30.0, 1.0), (0.0, 36.0, 1.0))]
pos = np.array(tris, np.float64)
n = len(pos)
S = {'pos': pos, 'nrm': np.tile([0, 0, 1.0], (n, 3, 1)), 'uv': pos[:, :, :2].copy(), 'mat': np.zeros(n, np.int64),
     'obj': np.zeros(n, np.int64), 'dup': np.zeros(n, bool)}
a, b = copy.deepcopy(S), copy.deepcopy(S)
t = time.time(); na = split.repair_tjunctions(a, two_grid=False); ta = time.time() - t
t = time.time(); nb = split.repair_tjunctions(b); tb = time.time() - t
same = na == nb and all(np.array_equal(a[k], b[k]) for k in ('pos', 'nrm', 'uv', 'mat', 'obj', 'dup'))
long_split = na > 0 and len(a['pos']) > n
print('%d triangles, split %d / %d, identical: %s, single grid %.2f s, two grids %.2f s' % (n, na, nb, same, ta, tb))
print('PASS' if same and long_split else 'FAIL')
sys.exit(0 if same and long_split else 1)
