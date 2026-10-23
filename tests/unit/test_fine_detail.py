"""split: fine detail survives. (1) the T-junction tolerance is per corner (a quarter of its shortest edge, at most
1 cm): a 2 mm stud's corner 5 mm inside a brick's edge is NOT a T-junction, a stud corner exactly on the edge is,
and a corner of a normal-sized mesh 8 mm off its neighbour's edge (OG Coruscant) still is. (2) snap_where_needed:
corners shared by two objects and corners of normal-sized triangles go on the 1/64 m grid; a stud's own corners keep
their place.   python test_fine_detail.py"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime'))
from bf2maptool.stages import split  # noqa: E402

ok = True


def expect(what, got, want):
    global ok
    good = got == want
    ok &= good
    print('%-4s %-62s got %-8r want %r' % ('ok' if good else 'FAIL', what, got, want))


def scene(tris, objs):
    pos = np.array(tris, np.float64)
    n = len(pos)
    return {'pos': pos, 'nrm': np.tile([0, 0, 1.0], (n, 3, 1)), 'uv': pos[:, :, :2].copy(), 'mat': np.zeros(n, np.int64),
            'obj': np.array(objs, np.int64), 'dup': np.zeros(n, bool)}


brick = [((0, 0, 0), (0.10, 0, 0), (0.10, 0.10, 0))]                    # its edge y = 0 runs x 0 .. 0.10
# a 2 mm stud triangle whose corner (0.05, 0.005) is 5 mm inside the brick's edge: no T-junction
near = [((0.05, 0.005, 0), (0.052, 0.006, 0), (0.051, 0.007, 0))]
expect('stud corner 5 mm inside a brick edge: brick not split', split.repair_tjunctions(scene(brick + near, [0, 1])), 0)
# the same stud with its corner ON the edge: a real T-junction
on = [((0.05, 0.0, 0), (0.052, 0.001, 0), (0.051, 0.002, 0))]
expect('stud corner exactly on the brick edge: brick split', split.repair_tjunctions(scene(brick + on, [0, 1])), 1)
# normal-sized meshes: a 1 m triangle's corner 8 mm off a 2 m neighbour's edge (modelled just off it) - still repaired
wall = [((0, 0, 0), (2, 0, 0), (2, 2, 0))]
off = [((1.0, 0.008, 0), (2.0, -1.0, 0), (1.0, -1.0, 0))]
expect('1 m mesh corner 8 mm off a 2 m edge: still a T-junction', split.repair_tjunctions(scene(wall + off, [0, 1])), 1)

# snap_where_needed
stud = [((0.3003, 0.3007, 0.1001), (0.3021, 0.3011, 0.1001), (0.3012, 0.3029, 0.1001))]   # one object's own 2 mm detail
big = [((1.003, 1.004, 0), (2.003, 1.004, 0), (2.003, 2.004, 0))]                         # a 1 m triangle
a = [((5.0013, 5.0, 0), (5.0033, 5.0, 0), (5.0023, 5.002, 0))]                            # two objects' 2 mm triangles
b = [((5.0013, 5.0, 0), (5.0023, 4.998, 0), (5.0033, 5.0, 0))]                            # sharing an edge
S = scene(stud + big + a + b, [0, 1, 2, 3])
before = S['pos'].copy()
kept = split.snap_where_needed(S)
on_grid = lambda p: bool(np.allclose(np.round(p / split.GRID) * split.GRID, p, atol=1e-9))     # noqa: E731
expect('stud corners keep their exact place', bool(np.array_equal(S['pos'][0], before[0])), True)
expect('1 m triangle corners on the grid', on_grid(S['pos'][1]), True)
expect('corners shared by two objects on the grid', on_grid(S['pos'][2][:2]) and on_grid(S['pos'][3][[0, 2]]), True)
expect('the shared corners still coincide', bool(np.array_equal(S['pos'][2][0], S['pos'][3][0]) and np.array_equal(S['pos'][2][1], S['pos'][3][2])), True)
expect('corners kept off the grid (3 stud + 1 + 1 unshared)', kept, 5)
print('PASS' if ok else 'FAIL')
sys.exit(0 if ok else 1)
