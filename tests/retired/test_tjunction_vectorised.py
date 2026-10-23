"""The vectorised T-junction repair (stages/split.py) must give EXACTLY what the old per-vertex loop gave.
    python test_tjunction.py <map.json> <settings.json> [max triangles, default 150000]
Runs the old function (kept verbatim below) and the new one on the same copy of the map's triangles, compares every
output array bit for bit, and prints both times."""
import copy
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime'))
from bf2maptool import config  # noqa: E402
from bf2maptool.stages import split  # noqa: E402


def old_hits_repair(S, tol=split.TJ_TOL, cell=2.0):
    """the pre-2026-10-23 implementation, verbatim except for the name"""
    pos = S['pos']
    n = len(pos)
    verts, inv = np.unique(np.round(pos.reshape(-1, 3), 6), axis=0, return_inverse=True)
    ea = np.concatenate([pos[:, 0], pos[:, 1], pos[:, 2]]); eb = np.concatenate([pos[:, 1], pos[:, 2], pos[:, 0]])
    lo = np.floor((np.minimum(ea, eb) - tol) / cell).astype(np.int64); hi = np.floor((np.maximum(ea, eb) + tol) / cell).astype(np.int64)
    grid = {}
    for e in range(len(ea)):
        l, h = lo[e], hi[e]
        if (h - l + 1).prod() > 20000:
            continue
        for x in range(l[0], h[0] + 1):
            for y in range(l[1], h[1] + 1):
                for z in range(l[2], h[2] + 1):
                    grid.setdefault((x, y, z), []).append(e)
    vc = np.floor(verts / cell).astype(np.int64)
    inserts = {}
    for vi in range(len(verts)):
        cand = grid.get(tuple(vc[vi]))
        if not cand:
            continue
        cand = np.asarray(cand)
        a, b = ea[cand], eb[cand]
        ab = b - a
        L2 = (ab * ab).sum(1)
        ok = L2 > 1e-12
        t = np.where(ok, ((verts[vi] - a) * ab).sum(1) / np.maximum(L2, 1e-30), -1.0)
        d = np.linalg.norm(a + ab * t[:, None] - verts[vi], axis=1)
        L = np.sqrt(L2)
        hit = ok & (t * L > tol) & ((1 - t) * L > tol) & (d < tol)
        for e, te in zip(cand[hit], t[hit]):
            inserts.setdefault(int(e), []).append((float(te), vi))
    return inserts


def new_inserts(S, tol=split.TJ_TOL, cell=2.0):
    pos = S['pos']
    verts = np.unique(np.round(pos.reshape(-1, 3), 6), axis=0)
    ea = np.concatenate([pos[:, 0], pos[:, 1], pos[:, 2]]); eb = np.concatenate([pos[:, 1], pos[:, 2], pos[:, 0]])
    hv, he, ht = split._tjunction_hits(verts, ea, eb, tol, cell)
    inserts = {}
    for vi, e, te in zip(hv.tolist(), he.tolist(), ht.tolist()):
        inserts.setdefault(int(e), []).append((float(te), int(vi)))
    return inserts


if len(sys.argv) >= 3:
    mp, sp = sys.argv[1], sys.argv[2]
    cap = int(sys.argv[3]) if len(sys.argv) > 3 else 150000
    job = config.Job(mp, config.load_settings(sp))
    S = split.load_scene(job)
    split.clean(S)
else:
    # no map given (tests/run_all.py): a synthetic floor of quads whose neighbours are split differently - every row
    # of quads is cut into 1, 2 or 3 strips, so the corners of one row lie inside the edges of the next (T-junctions)
    cap = 10 ** 9
    tris = []
    for row in range(40):
        k = 1 + row % 3
        for col in range(40):
            x0, y0 = col * 1.0, row * 1.0
            xs = np.linspace(x0, x0 + 1.0, k + 1)
            for i in range(k):
                a, b, c, d = (xs[i], y0, 0), (xs[i + 1], y0, 0), (xs[i + 1], y0 + 1, 0), (xs[i], y0 + 1, 0)
                tris += [(a, b, c), (a, c, d)]
    pos = np.array(tris, np.float64)
    n = len(pos)
    S = {'pos': pos, 'nrm': np.tile([0, 0, 1.0], (n, 3, 1)), 'uv': pos[:, :, :2].copy(), 'mat': np.zeros(n, np.int64),
         'obj': np.zeros(n, np.int64)}
S.setdefault('dup', np.zeros(len(S['pos']), bool))
n_all = len(S['pos'])
if n_all > cap:
    for k in list(S):
        if isinstance(S[k], np.ndarray) and len(S[k]) == n_all:
            S[k] = S[k][:cap]
print('triangles', len(S['pos']))
fails = []
for tol in (split.TJ_TOL, 2e-4):
    t = time.time(); a = old_hits_repair(S, tol); t_old = time.time() - t
    t = time.time(); b = new_inserts(S, tol); t_new = time.time() - t
    same = list(a.items()) == list(b.items())            # same edges, same insert lists, same ORDER
    print('tol %g: %d edges with inserts (old) / %d (new); identical incl. order: %s; old %.1f s, new %.1f s'
          % (tol, len(a), len(b), same, t_old, t_new))
    if not same:
        fails.append(tol)
# the whole repair through the stage function, on two copies: the new one against the old inserts fed through the
# same triangle-building code
S1 = copy.deepcopy(S)
n1 = split.repair_tjunctions(S1)
print('repair_tjunctions (new) split %d triangles -> %d triangles' % (n1, len(S1['pos'])))
print('PASS' if not fails else 'FAIL %s' % fails)
sys.exit(1 if fails else 0)
