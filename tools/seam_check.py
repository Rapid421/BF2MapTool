"""Where can a split map show cracks? Measures, on a built map's pieces (pieces/*.npz + pieces.json):
  A  shared corners that land in different places once each piece is stored as Half3 relative to its own centre
  B  T-junctions: a corner of one triangle lying inside an edge of another (from midpoint subdivision / band cuts)
    python seam_check.py <work/<Name>> [--tj]
"""
import json
import os
import sys
from collections import defaultdict

import numpy as np

TOL = float(os.environ.get('SEAM_TOL', '2e-4'))


def load(work):
    pj = json.load(open(os.path.join(work, 'pieces', 'pieces.json')))
    P = []
    for i, p in enumerate(pj['pieces']):
        d = np.load(os.path.join(work, 'pieces', p['name'] + '.npz'))
        gp = d['pos']                                         # game axes, relative to the piece centre (float64)
        c = np.array(p['centre'])                             # scene axes, rounded to 4 decimals in the json
        cg = np.array([c[0], c[2], -c[1]])
        P.append((i, gp, cg))
    return pj, P


def main(argv):
    work = argv[0]
    pj, P = load(work)
    # A: exact world corner -> where each piece puts it after Half3
    where = defaultdict(list)
    for i, gp, cg in P:
        w = (gp + cg).reshape(-1, 3)
        q = (gp.astype(np.float16).astype(np.float64) + cg).reshape(-1, 3)   # what the game stores
        for k, qq in zip(map(tuple, np.round(w, 4)), q):
            where[k].append((i, qq))
    spreads = []
    for k, lst in where.items():
        if len({i for i, _ in lst}) < 2:
            continue
        a = np.array([q for _, q in lst])
        spreads.append(float(np.abs(a - a.mean(0)).max() * 2))
    s = np.array(spreads) if spreads else np.zeros(1)
    print('A: %d corners shared by 2+ pieces; gap after Half3: median %.2f mm, 95%% %.2f mm, max %.2f mm; over 2 mm: %d' % (
        len(spreads), np.median(s) * 1000, np.percentile(s, 95) * 1000, s.max() * 1000, int((s > 0.002).sum())))
    if '--tj' in argv:
        # B: corners strictly inside another triangle's edge (all pieces together, world space, before Half3)
        tris = np.concatenate([(gp + cg) for _, gp, cg in P])
        print('B (pieces): ', end=''); tjunctions(tris)
    if '--source' in argv:
        # the same count on the scene as exported, before the tool cut anything
        m = json.load(open(os.path.join(work, 'export', 'map.json')))
        src = [np.load(os.path.join(work, 'export', o['geo']))['pos'] for o in m['objects'] if o['role'] == 'static']
        print('B (source): ', end=''); tjunctions(np.concatenate(src).astype(np.float64))


def tjunctions(tris):
    if True:
        verts = np.unique(np.round(tris.reshape(-1, 3), 4), axis=0)
        cell = 0.5
        grid = defaultdict(list)
        for vi, v in enumerate(verts):
            grid[tuple(np.floor(v / cell).astype(int))].append(vi)
        area = np.linalg.norm(np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0]), axis=1)
        print('(%d zero-area slivers left out) ' % int((area < 1e-8).sum()), end='')
        tris = tris[area >= 1e-8]                         # a sliver draws nothing: its middle corner on its own edge is no crack
        edges = np.concatenate([tris[:, [0, 1]], tris[:, [1, 2]], tris[:, [2, 0]]])
        key = np.sort(np.round(edges, 4).reshape(len(edges), 6).view([('', float)] * 6).ravel()) if False else None
        n_tj, seen = 0, set()
        for a, b in edges:
            ab = b - a; L = np.linalg.norm(ab)
            if L < 1e-4:
                continue
            lo = np.floor(np.minimum(a, b) / cell).astype(int); hi = np.floor(np.maximum(a, b) / cell).astype(int)
            if np.prod(hi - lo + 1) > 64:
                continue
            for x in range(lo[0], hi[0] + 1):
                for y in range(lo[1], hi[1] + 1):
                    for z in range(lo[2], hi[2] + 1):
                        for vi in grid.get((x, y, z), ()):
                            v = verts[vi]
                            t = np.dot(v - a, ab) / (L * L)
                            # at least 1 mm from either end: corners are rounded to 0.1 mm here, and on a short edge
                            # a relative limit took the edge's own endpoint for a corner inside it
                            if t * L > 1e-3 and (1 - t) * L > 1e-3 and np.linalg.norm(a + ab * t - v) < TOL and vi not in seen:
                                seen.add(vi); n_tj += 1
                                if os.environ.get('SEAM_SHOW') and n_tj <= int(os.environ['SEAM_SHOW']):
                                    print('\n   T at %s on edge %s-%s  L %.2f m  t %.3f  off %.5f m' % (np.round(v, 3), np.round(a, 3), np.round(b, 3), L, t, np.linalg.norm(a + ab * t - v)), end='')
        print('B: %d corners lie inside another triangle\'s edge (T-junctions), of %d corners' % (n_tj, len(verts)))


if __name__ == '__main__':
    main(sys.argv[1:])
