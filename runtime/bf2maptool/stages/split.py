"""Stage `split`: the exported scene triangles -> single-material pieces the game can hold, written as FBX.

Rules (each one measured on a past port):
* one material per piece; at most CAP triangles (3,000);
* positions are stored as 16-bit floats relative to the piece centre, so a piece may not be large: its extent is capped
  (EXT, 48 m) and long triangles are cut by exact midpoint subdivision (EDGE, 48 m); both caps relax with the distance
  from the play area (d / 2 and d / 4), where the error is too small to see;
* objects are kept whole inside a piece where the caps allow (units = one object's triangles of one material);
* uv coordinates are 16-bit floats too: every uv island (cut further into 2 x 2 tile blocks) is moved by whole numbers
  to sit near 0..1 - the texture wraps, so nothing changes on screen;
* a double-sided material gets the reversed copy of each triangle in the same piece.

Output: pieces/<name>.fbx (game axes, metres, relative to the piece centre), pieces/<name>.npz (the same triangles with
their source object, for the collision stage) and pieces/pieces.json.
"""
import json
import os

import numpy as np

from .. import hosts, config, fbx
from ..runner import StageError

CAP = 3000          # triangles per piece
EXT = 48.0          # metres: piece extent (unit centroids) near the play area
EDGE = 48.0         # metres: longest triangle edge near the play area
PARAMS = {'cap': CAP, 'ext': EXT, 'edge': EDGE, 'v': 13}     # v5: piece files with unshared uvs; v9: T-junctions repaired, corners + centres on GRID;
                                                             # v10: per-corner T-junction tolerance, fine detail not snapped, extents grow over budget; v11: faces collapsing in Half3 dropped; v12: budget 1,200; v13: exact zone shrinks over budget
# A map may take bigger pieces (a dense ripped map at 3,000 triangles a piece runs into thousands of pieces, past the
# game's budget of about 2,048 unique meshes a level). The importer writes 16-bit indices: 21,845 triangles with no
# shared corner is the ceiling, so 20,000 is the most a piece may hold. Far from the play area the extent cap may also
# grow with the whole distance instead of half of it (Half3 step at 500 m extent = 0.25 m, seen from 400 m or more).
CAP_MAX = 20000
BAND_REACH = 150.0   # metres beyond the play radius where walls are still cut along their texture rows
PIECE_BUDGET = 1200  # pieces a level may have on the Kamino host: CR90 interior at 1,189 and 1,145 loads, at 1,441 it crashed
                     # 2 s after the world came up (game+0x7C6529A, every time; LEGO Utapau at 1,455 too) - same geometry


def caps(job):
    m = job.map.get('map') or {}
    cap = int(min(max(int(m.get('piece_tris') or CAP), 1000), CAP_MAX))
    far = float(m.get('piece_far') or 0.5)
    return cap, min(max(far, 0.5), 2.0)   # 2 = extent twice the distance: Half3 step d/1024 at d away, under a pixel


def ext_near(job):
    """piece extent near the play area. Half3 keeps a 1/64 m step for every |x| < 32 m, so any extent under 64 m is as
    exact as 48 m (positions are relative to the piece centre); a map may take up to 63 m"""
    v = float((job.map.get('map') or {}).get('piece_ext') or EXT)
    return min(max(v, 24.0), 63.0)


def inputs(job):
    m = job.map
    objs = [(o['name'], o['role'], config.file_hash(job.src(o['geo']))) for o in m['objects'] if o['role'] == 'static']
    mats = {k: (v['class'], v.get('double_sided'), v.get('skip')) for k, v in m['materials'].items()}
    d = {'objects': objs, 'materials': mats, 'play': job.play(), 'keep': (job.map.get('play') or {}).get('keep_distance'), 'params': PARAMS, 'prefix': job.piece_prefix}
    if caps(job) != (CAP, 0.5) or ext_near(job) != EXT:
        d['caps'] = list(caps(job)) + [ext_near(job)]
    # the host's far limit joins the inputs only where it cuts something (so builds it does not touch stay up to date)
    lim = float(getattr(hosts.get(job.host), 'FAR_LIMIT', 0.0) or 0.0)
    radius, keep_d = job.play()[1], float(d['keep'] or 0.0)
    if lim > 0 and ((radius + keep_d) if keep_d > 0 else float('inf')) > max(lim, radius + 60.0):
        d['far_limit'] = lim
    return d


def outputs(job):
    return [job.path('pieces', 'pieces.json')]


def to_game(v):
    """scene (x, y, z) -> game axes (x, z, -y); a rotation, so winding is kept"""
    return np.stack([v[..., 0], v[..., 2], -v[..., 1]], -1)


def load_scene(job):
    """all static triangles: dict of arrays pos, nrm, uv (N, 3, .), mat (N) index into names, obj (N) index into objects"""
    P, Nn, U, Mi, Oi = [], [], [], [], []
    mat_names, mat_index, obj_names = [], {}, []
    skipped = {}
    for o in job.map['objects']:
        if o['role'] != 'static':
            continue
        z = np.load(job.src(o['geo']), allow_pickle=False)
        slots = [str(s) for s in z['slots']]
        local = np.empty(len(slots), np.int64)
        keep_slot = np.ones(len(slots), bool)
        for i, s in enumerate(slots):
            name = s or '(no material)'
            rec = job.map['materials'].get(s)
            if rec is not None and rec.get('skip'):
                keep_slot[i] = False
            if name not in mat_index:
                mat_index[name] = len(mat_names); mat_names.append(name)
            local[i] = mat_index[name]
        mi = z['mat'].astype(np.int64)
        keep = keep_slot[mi]
        if (~keep).any():
            skipped[o['name']] = int((~keep).sum())
        if not keep.any():
            continue
        obj_names.append(o['name'])
        P.append(z['pos'][keep]); Nn.append(z['nrm'][keep].astype(np.float64)); U.append(z['uv'][keep])
        Mi.append(local[mi[keep]]); Oi.append(np.full(int(keep.sum()), len(obj_names) - 1, np.int64))
    if not P:
        raise StageError('The scene has no map geometry to export. Add mesh objects (role "Map geometry").')
    return {'pos': np.concatenate(P), 'nrm': np.concatenate(Nn), 'uv': np.concatenate(U), 'mat': np.concatenate(Mi),
            'obj': np.concatenate(Oi), 'mat_names': mat_names, 'obj_names': obj_names, 'skipped': skipped}


def clean(S):
    """non-finite uvs -> 0.5 (and counted); zero normals -> the face normal"""
    uv = S['uv']
    bad = ~np.isfinite(uv).all(axis=(1, 2)) | (np.abs(np.nan_to_num(uv)) > 1000).any(axis=(1, 2))
    uv[bad] = 0.5
    n = S['nrm']
    fn = np.cross(S['pos'][:, 1] - S['pos'][:, 0], S['pos'][:, 2] - S['pos'][:, 0])
    fl = np.linalg.norm(fn, axis=1, keepdims=True)
    fn = np.where(fl > 1e-20, fn / np.maximum(fl, 1e-20), [0.0, 0.0, 1.0])
    ln = np.linalg.norm(n, axis=2)
    weak = (ln < 1e-8) | ~np.isfinite(ln)
    n[weak] = np.repeat(fn[:, None, :], 3, 1)[weak]
    S['nrm'] = n / np.maximum(np.linalg.norm(n, axis=2, keepdims=True), 1e-20)
    return int(bad.sum())


def subdivide(S, centre, radius, edge=EDGE):
    """cut every triangle with an edge longer than max(edge, d / 4) into four at its edge midpoints, until none is left
    (d = distance of its nearest corner from the play area). uvs / normals are interpolated: the surface is unchanged."""
    c = np.asarray(centre, np.float64)
    total = 0
    for _ in range(12):
        p = S['pos']
        e = np.stack([np.linalg.norm(p[:, 1] - p[:, 0], axis=1), np.linalg.norm(p[:, 2] - p[:, 1], axis=1),
                      np.linalg.norm(p[:, 0] - p[:, 2], axis=1)], 1).max(1)
        d = np.maximum(0.0, np.linalg.norm(p - c, axis=2) - radius).min(1)
        cut = e > np.maximum(edge, d / 4.0)
        if not cut.any():
            break
        total += int(cut.sum())
        new = {}
        for k in ('pos', 'nrm', 'uv'):
            a = S[k][cut]
            m01, m12, m20 = (a[:, 0] + a[:, 1]) / 2, (a[:, 1] + a[:, 2]) / 2, (a[:, 2] + a[:, 0]) / 2
            if k == 'nrm':
                for m, (i, j) in ((m01, (0, 1)), (m12, (1, 2)), (m20, (2, 0))):
                    l = np.linalg.norm(m, axis=1, keepdims=True)
                    m[:] = np.where(l > 1e-8, m / np.maximum(l, 1e-20), a[:, i])
            four = np.stack([np.stack([a[:, 0], m01, m20], 1), np.stack([m01, a[:, 1], m12], 1),
                             np.stack([m20, m12, a[:, 2]], 1), np.stack([m01, m12, m20], 1)], 1)
            new[k] = four.reshape(-1, 3, a.shape[-1])
        for k in ('pos', 'nrm', 'uv'):
            S[k] = np.concatenate([S[k][~cut], new[k]])
        for k in ('mat', 'obj', 'dup'):
            S[k] = np.concatenate([S[k][~cut], np.repeat(S[k][cut], 4)])
    return total


GRID = 1.0 / 64.0    # metres. Every corner and every piece centre sits on this grid, so a corner's offset from its piece
                     # centre is k/64 with |offset| < 32 m - exactly representable as a 16-bit float. Two pieces that
                     # share a corner then store it at the SAME place (measured on the OG Coruscant port before this:
                     # shared corners up to 27 mm apart, median 4 mm - the bright hairline cracks seen in game).
TJ_TOL = 0.01        # metres: a corner this close to the inside of another triangle's edge is a T-junction. Not only
                     # exact ones: OG Coruscant has 1,710 within 0.2 mm and 3,203 within 8 mm (corners modelled just off a
                     # neighbour's edge - a hairline gap); the edge is bent onto the corner, at most 1 cm


def snap(a):
    return np.round(np.asarray(a, np.float64) / GRID) * GRID


SNAP_MIN_EDGE = 4 * GRID     # metres: a corner whose shortest edge is at least this is snapped wherever it is


def snap_where_needed(S):
    """snap the corners that can make a crack - those shared by two objects or two materials (two pieces meet there) -
    and every corner of normal-sized triangles (moving it under 8 mm changes nothing visible). The corners of fine
    detail that belong to one surface keep their exact place: the grid moved LEGO Utapau's stud corners 8 mm, 64 % of
    their shortest edge (median), crumpling them; inside one piece Half3 stores them finer than the grid. -> kept"""
    pos = S['pos']
    n = len(pos)
    if not n:
        return 0
    flat = pos.reshape(-1, 3)
    verts, inv = np.unique(np.round(flat, 6), axis=0, return_inverse=True)
    inv = inv.reshape(-1)
    key = np.repeat(S['obj'].astype(np.int64) * (int(S['mat'].max()) + 1) + S['mat'].astype(np.int64), 3)
    kmin = np.full(len(verts), np.iinfo(np.int64).max); kmax = np.full(len(verts), np.iinfo(np.int64).min)
    np.minimum.at(kmin, inv, key); np.maximum.at(kmax, inv, key)
    elen = np.linalg.norm(pos[:, [1, 2, 0]] - pos, axis=2)          # edge k joins corner k and k + 1
    vmin = np.full(len(verts), np.inf)
    inv3 = inv.reshape(n, 3)
    for k in range(3):
        np.minimum.at(vmin, inv3[:, k], elen[:, k]); np.minimum.at(vmin, inv3[:, (k + 1) % 3], elen[:, k])
    do = ((kmin != kmax) | (vmin >= SNAP_MIN_EDGE))[inv]
    flat = flat.astype(np.float64)
    flat[do] = snap(flat[do])
    S['pos'] = flat.reshape(n, 3, 3).astype(pos.dtype)
    return int((~((kmin != kmax) | (vmin >= SNAP_MIN_EDGE))).sum())


def repair_tjunctions(S, tol=TJ_TOL, cell=2.0, two_grid=True):
    """split every triangle at the corners of OTHER triangles that lie inside its edges (T-junctions: the 2005 maps
    abut separate objects that way, and midpoint subdivision makes more). Afterwards every place where surfaces meet is
    a corner both sides share, which the grid snap then puts at one stored position. -> triangles split"""
    pos = S['pos']
    n = len(pos)
    verts, inv = np.unique(np.round(pos.reshape(-1, 3), 6), axis=0, return_inverse=True)
    inv = inv.reshape(n, 3)
    # edges into the cells of their bounding boxes. Two grids: the short edges on a FINE one sized from the map's own edges
    # (LEGO Utapau, 2026-10-23: median edge 1.8 cm - in 2 m cells every vertex met thousands of stud edges, 224 s per
    # 100 k triangles, hours for the map), the long ones on the 2 m grid as before. Any cell size finds the same hits;
    # only the "more than 20,000 cells: skipped" rule depends on it, and the long edges keep it on the 2 m grid. The
    # candidates of a vertex are taken in edge order as before, so the result is the same, triangle for triangle.
    ea = np.concatenate([pos[:, 0], pos[:, 1], pos[:, 2]]); eb = np.concatenate([pos[:, 1], pos[:, 2], pos[:, 0]])
    elen = np.linalg.norm(eb - ea, axis=1)
    # each corner's own tolerance: at most a quarter of its shortest edge. A corner of a 2 mm stud 5 mm from a brick's
    # edge is not a T-junction (LEGO Utapau with the flat 1 cm: 1.39 M triangles fanned, 3.4 M -> 8.6 M, 21 min); a
    # corner modelled 8 mm off a neighbour's edge on a normal-sized mesh (OG Coruscant) still is
    vtol = np.full(len(verts), float(tol))
    for k in range(3):
        for c in (k, (k + 1) % 3):
            np.minimum.at(vtol, inv[:, c], 0.25 * elen[k * n:(k + 1) * n])
    fine = float(np.clip(4.0 * np.median(elen), 0.1, cell)) if (len(elen) and two_grid) else cell
    grids = []
    for c, sel in ((fine, elen <= 8.0 * fine), (cell, elen > 8.0 * fine)) if fine < cell else ((cell, np.ones(len(ea), bool)),):
        idx = np.nonzero(sel)[0]
        lo = np.floor((np.minimum(ea[idx], eb[idx]) - tol) / c).astype(np.int64); hi = np.floor((np.maximum(ea[idx], eb[idx]) + tol) / c).astype(np.int64)
        grid = {}
        for k, e in enumerate(idx.tolist()):
            l, h = lo[k], hi[k]
            if (h - l + 1).prod() > 20000:
                continue
            for x in range(l[0], h[0] + 1):
                for y in range(l[1], h[1] + 1):
                    for z in range(l[2], h[2] + 1):
                        grid.setdefault((x, y, z), []).append(e)
        grids.append((grid, np.floor(verts / c).astype(np.int64)))
    inserts = {}                                           # edge id -> [(t, vertex id)]
    for vi in range(len(verts)):
        parts = [g.get(tuple(vc[vi])) for g, vc in grids]
        parts = [p for p in parts if p]
        if not parts:
            continue
        cand = np.sort(np.concatenate([np.asarray(p, np.int64) for p in parts])) if len(parts) > 1 else np.asarray(parts[0])
        a, b = ea[cand], eb[cand]
        ab = b - a
        L2 = (ab * ab).sum(1)
        ok = L2 > 1e-12
        t = np.where(ok, ((verts[vi] - a) * ab).sum(1) / np.maximum(L2, 1e-30), -1.0)
        d = np.linalg.norm(a + ab * t[:, None] - verts[vi], axis=1)
        L = np.sqrt(L2)
        tv = vtol[vi]
        hit = ok & (t * L > tv) & ((1 - t) * L > tv) & (d < tv)
        for e, te in zip(cand[hit], t[hit]):
            inserts.setdefault(int(e), []).append((float(te), vi))
    if not inserts:
        return 0
    by_tri = {}
    for e, lst in inserts.items():
        by_tri.setdefault(e % n, {})[e // n] = sorted(set(lst))
    P, Nn, U, keep = [], [], [], np.ones(n, bool)
    src = []
    for ti, edges in by_tri.items():
        # the triangle's outline with the inserted corners in order; corner = (pos, nrm, uv)
        poly = []
        for k in range(3):
            i, j = k, (k + 1) % 3
            poly.append((pos[ti, i], S['nrm'][ti, i], S['uv'][ti, i]))
            for te, vi in edges.get(k, ()):
                nrm = S['nrm'][ti, i] * (1 - te) + S['nrm'][ti, j] * te
                ln = np.linalg.norm(nrm)
                poly.append((verts[vi], nrm / ln if ln > 1e-8 else S['nrm'][ti, i], S['uv'][ti, i] * (1 - te) + S['uv'][ti, j] * te))
        # a fan from the triangle's centre: every outline corner (inserted ones too) becomes a real corner. (A fan from
        # one of its corners left the points on that corner's own two edges out - T-junctions again.)
        cp = pos[ti].mean(0); cn = S['nrm'][ti].mean(0); cu = S['uv'][ti].mean(0)
        ln = np.linalg.norm(cn)
        cn = cn / ln if ln > 1e-8 else S['nrm'][ti, 0]
        made = 0
        for k in range(len(poly)):
            a, b = poly[k], poly[(k + 1) % len(poly)]
            p = np.array([cp, a[0], b[0]])
            if np.linalg.norm(np.cross(p[1] - p[0], p[2] - p[0])) < 1e-10:
                continue
            P.append(p); Nn.append(np.array([cn, a[1], b[1]])); U.append(np.array([cu, a[2], b[2]]))
            src.append(ti); made += 1
        if made:
            keep[ti] = False
        else:
            del P[len(P) - made:], Nn[len(Nn) - made:], U[len(U) - made:], src[len(src) - made:]
    src = np.array(src, np.int64)
    S['pos'] = np.concatenate([pos[keep], np.array(P, pos.dtype).reshape(-1, 3, 3)])
    S['nrm'] = np.concatenate([S['nrm'][keep], np.array(Nn, S['nrm'].dtype).reshape(-1, 3, 3)])
    S['uv'] = np.concatenate([S['uv'][keep], np.array(U, S['uv'].dtype).reshape(-1, 3, 2)])
    for k in ('mat', 'obj', 'dup'):
        S[k] = np.concatenate([S[k][keep], S[k][src]])
    return int((~keep).sum())


EXACT_EXT = 63.0     # metres: corners of a piece no wider than this sit under 32 m from its (grid) centre - exact in Half3


def _fit_exact(parts, sel, pos, cen, pd, far=300.0):
    """pieces near the play area whose CORNERS span EXACT_EXT or more (a whole object kept together, measured 64 - 79 m
    on OG Coruscant) are halved along their widest axis until they fit, so every corner stays exact in Half3"""
    for part in parts:
        stack = [np.asarray(part)]
        while stack:
            p = stack.pop()
            v = pos[sel[p]].reshape(-1, 3)
            ext = v.max(0) - v.min(0)
            if len(p) < 2 or ext.max() < EXACT_EXT or pd[sel[p]].min() > far:
                yield p
                continue
            ax = int(np.argmax(ext))
            c = cen[sel[p], ax]
            m = np.median(c)
            lo, hi = p[c <= m], p[c > m]
            if len(lo) == 0 or len(hi) == 0:
                yield p
                continue
            stack += [hi, lo]


def band_materials(job):
    """Materials whose triangles are kept inside ONE vertical repeat of their texture (v in 0..1): opaque materials
    with a normal-map image. Why: the host's lightmap-capable opaque surface samples the normal map with "wrap U,
    clamp V" (seen in game: relief only where 0 <= v <= 1), so a wall that repeats its texture upwards would be flat
    above the first repeat. Cut along the whole-number v lines, every part can be moved into 0..1 and keeps its relief
    AND its lightmap."""
    from .. import hosts
    if not getattr(hosts.get(job.host), 'OPAQUE_V_CLAMPED', False):
        return set()
    out = set()
    for name, rec in job.map['materials'].items():
        n = (rec.get('inputs') or {}).get('normal')
        if rec.get('class', 'opaque') == 'opaque' and not rec.get('template') and isinstance(n, dict) and 'image' in n and not n['image'].get('missing'):
            out.add(name)
    return out


def _clip(poly, k, keep_above):
    """Sutherland-Hodgman against the line v = k; poly = list of (pos, nrm, uv) corner tuples"""
    out = []
    for i in range(len(poly)):
        a, b = poly[i], poly[(i + 1) % len(poly)]
        da, db = a[2][1] - k, b[2][1] - k
        ina, inb = (da >= 0, db >= 0) if keep_above else (da <= 0, db <= 0)
        if ina:
            out.append(a)
        if ina != inb and abs(da - db) > 1e-12:
            t = da / (da - db)
            uv = a[2] + (b[2] - a[2]) * t
            uv[1] = k                                    # exactly on the line: both sides of the cut get the same corner
            out.append((a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, uv))
    return out


def cut_v_bands(S, flagged, centre=None, reach=float('inf')):
    """cut the triangles of the flagged materials along whole-number v lines and move every part into v 0..1.
    -> (triangles that were cut, triangles they became)"""
    m = np.array([n in flagged for n in S['mat_names']])[S['mat']]
    if not m.any():
        return 0, 0
    uv = S['uv']
    lo = np.floor(uv[:, :, 1].min(1) + 1e-6); hi = np.ceil(uv[:, :, 1].max(1) - 1e-6)
    one = m & (hi - lo <= 1.0)                           # inside one repeat already: only moved
    uv[one, :, 1] -= lo[one][:, None]
    far = np.zeros(len(m), bool)
    if centre is not None and reach < float('inf'):
        # far from the play area the relief above the first repeat cannot be seen: no cut there (on a ripped city
        # backdrop the cut made five triangles of every one, thousands of extra pieces)
        far = np.linalg.norm(S['pos'].mean(1)[:, :2] - np.asarray(centre, np.float64)[:2], axis=1) > reach
    cut = np.nonzero(m & (hi - lo > 1.0) & ~far)[0]
    if not len(cut):
        return 0, 0
    P, Nn, U, M, O, D = [], [], [], [], [], []
    for t in cut:
        tri = [(S['pos'][t, c].astype(np.float64), S['nrm'][t, c].astype(np.float64), uv[t, c].astype(np.float64)) for c in range(3)]
        for k in range(int(lo[t]), int(hi[t])):
            poly = _clip(_clip(tri, float(k), True), float(k + 1), False)
            for j in range(1, len(poly) - 1):
                tr = (poly[0], poly[j], poly[j + 1])
                p = np.array([c[0] for c in tr])
                if np.linalg.norm(np.cross(p[1] - p[0], p[2] - p[0])) < 1e-10:
                    continue
                n = np.array([c[1] for c in tr]); ln = np.linalg.norm(n, axis=1, keepdims=True)
                u = np.array([c[2] for c in tr]); u[:, 1] -= k
                P.append(p); Nn.append(np.where(ln > 1e-8, n / np.maximum(ln, 1e-20), n)); U.append(u)
                M.append(S['mat'][t]); O.append(S['obj'][t]); D.append(S['dup'][t])
    keep = np.ones(len(S['pos']), bool); keep[cut] = False
    S['pos'] = np.concatenate([S['pos'][keep], np.array(P, S['pos'].dtype).reshape(-1, 3, 3)])
    S['nrm'] = np.concatenate([S['nrm'][keep], np.array(Nn, S['nrm'].dtype).reshape(-1, 3, 3)])
    S['uv'] = np.concatenate([S['uv'][keep], np.array(U, S['uv'].dtype).reshape(-1, 3, 2)])
    S['mat'] = np.concatenate([S['mat'][keep], np.array(M, S['mat'].dtype)])
    S['obj'] = np.concatenate([S['obj'][keep], np.array(O, S['obj'].dtype)])
    S['dup'] = np.concatenate([S['dup'][keep], np.array(D, bool)])
    return len(cut), len(P)


def _pieces_of_material(cen, obj, pd, cap, ext_cap, far=0.5):
    """index arrays (into this material's triangles), one per piece. cen = triangle centroids, obj = unit id per
    triangle, pd = distance of each centroid from the play area."""
    out = []

    def split_faces(idx):
        if len(idx) == 0:
            return
        c = cen[idx]
        ext = c.max(0) - c.min(0)
        cap_here = max(ext_cap, float(pd[idx].min()) * far)
        if (len(idx) <= cap and ext.max() <= cap_here) or len(idx) < 2:
            out.append(idx); return
        ax = int(np.argmax(ext))
        order = idx[np.argsort(c[:, ax], kind='stable')]
        h = len(order) // 2
        split_faces(order[:h]); split_faces(order[h:])

    units = [np.nonzero(obj == u)[0] for u in np.unique(obj)]

    def uext(idx):
        c = cen[idx]
        return float((c.max(0) - c.min(0)).max()) - max(0.0, float(pd[idx].min()) * far - ext_cap)
    small = []
    for u in units:
        if len(u) > cap or uext(u) > ext_cap:
            split_faces(u)                       # one object's surface is itself too big: cut it by its faces
        else:
            small.append(u)

    def rec(us):
        if not us:
            return
        cs = np.array([cen[u].mean(0) for u in us])
        ext = cs.max(0) - cs.min(0)
        cap_here = max(ext_cap, min(float(pd[u].min()) for u in us) * far)
        if (sum(len(u) for u in us) <= cap and ext.max() <= cap_here) or len(us) < 2:
            out.append(np.concatenate(us)); return
        ax = int(np.argmax(ext))
        order = np.argsort(cs[:, ax], kind='stable')
        h = len(order) // 2
        rec([us[k] for k in order[:h]]); rec([us[k] for k in order[h:]])
    rec(small)
    return out


def shift_uv_islands(pos, uv, keep_v=None):
    """move every uv island (cut into 2 x 2 tile blocks) by whole numbers towards 0..1, in place.
    Triangles belong to one island when they share an edge with the same uvs at both ends. -> (groups, moved)"""
    T = len(pos)
    vkey = np.round(pos.reshape(-1, 3), 5)
    _, vid = np.unique(vkey, axis=0, return_inverse=True)
    vid = vid.reshape(T, 3)
    ukey = np.round(uv.reshape(-1, 2), 5)
    _, uid = np.unique(ukey, axis=0, return_inverse=True)
    uid = uid.reshape(T, 3)
    parent = np.arange(T)

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a
    seen = {}
    for t in range(T):
        for i in range(3):
            j = (i + 1) % 3
            a, b = (vid[t, i], uid[t, i]), (vid[t, j], uid[t, j])
            k = (a, b) if a <= b else (b, a)
            o = seen.get(k)
            if o is None:
                seen[k] = t
            else:
                ra, rb = find(t), find(o)
                if ra != rb:
                    parent[ra] = rb
    roots = np.array([find(t) for t in range(T)])
    cu = np.floor(uv[:, :, 0].mean(1) / 2.0).astype(np.int64)
    cv = np.floor(uv[:, :, 1].mean(1) / 2.0).astype(np.int64)
    key = np.stack([roots, cu, cv], 1)
    _, g = np.unique(key, axis=0, return_inverse=True)
    g = g.reshape(-1)
    ng = int(g.max()) + 1
    su = np.zeros(ng); sv = np.zeros(ng); cnt = np.zeros(ng)
    np.add.at(su, g, uv[:, :, 0].sum(1)); np.add.at(sv, g, uv[:, :, 1].sum(1)); np.add.at(cnt, g, 3.0)
    du = np.floor(su / cnt); dv = np.floor(sv / cnt)
    uv[:, :, 0] -= du[g][:, None]
    if keep_v is None:
        uv[:, :, 1] -= dv[g][:, None]
    else:                      # banded triangles already sit in v 0..1 and must stay there
        uv[:, :, 1] -= np.where(keep_v, 0.0, dv[g])[:, None]
    return ng, int(((du != 0) | (dv != 0)).sum())


def run(job, log):
    cap_n, far_k = caps(job)
    S = load_scene(job)
    bad_uv = clean(S)
    centre, radius = job.play()
    # geometry far outside the play area is left out (a 2 km terrain under a 200 m map): a triangle stays when one of
    # its corners lies within play radius + keep distance of the play centre. 0 = keep everything.
    keep_d = float((job.map.get('play') or {}).get('keep_distance') or 0.0)
    cropped = 0
    # the host level cannot take geometry beyond its far limit (see the host module): whatever "Keep geometry within"
    # says, nothing farther than that from the play centre is built - except the play area itself, never cut
    far_limit = float(getattr(hosts.get(job.host), 'FAR_LIMIT', 0.0) or 0.0)
    reach = (radius + keep_d) if keep_d > 0 else float('inf')
    capped = False
    if far_limit > 0 and reach > max(far_limit, radius + 60.0):
        reach, capped = max(far_limit, radius + 60.0), True
    # and no triangle may reach past the room the host level's world box leaves (MAX_EXTENT, horizontal, from the play
    # centre): a 9 km backdrop triangle with one corner inside "Keep geometry within" would otherwise stay whole
    max_ext = float(getattr(hosts.get(job.host), 'MAX_EXTENT', 0.0) or 0.0)
    if reach < float('inf') or max_ext > 0:
        keep_d = reach - radius
        d = np.linalg.norm(S['pos'] - np.asarray(centre, np.float64), axis=2)
        near = d.min(1) <= reach
        if max_ext > 0:
            dh = np.abs(S['pos'][:, :, :2] - np.asarray(centre, np.float64)[:2]).max(axis=(1, 2))
            near &= dh <= max_ext
        cropped = int((~near).sum())
        if cropped == len(near):
            raise StageError('All map geometry lies more than %.0f m outside the play area (centre %s, radius %.0f m). Check the play '
                             'area, or set "Keep geometry within" to 0.' % (keep_d, [round(float(x)) for x in centre], radius))
        if cropped:
            for k in ('pos', 'nrm', 'uv', 'mat', 'obj'):
                S[k] = S[k][near]
            log('split: %d triangle(s) farther than %.0f m outside the play area left out%s' % (cropped, keep_d, ' (the host level takes nothing farther out)' if capped else ''))
    S['dup'] = np.zeros(len(S['pos']), bool)
    # double-sided materials: the reversed copy of every triangle, in the same piece
    ds = np.array([bool((job.map['materials'].get(n) or {}).get('double_sided')) for n in S['mat_names']])
    m = ds[S['mat']]
    if m.any():
        for k in ('pos', 'uv'):
            S[k] = np.concatenate([S[k], S[k][m][:, ::-1]])
        S['nrm'] = np.concatenate([S['nrm'], -S['nrm'][m][:, ::-1]])
        for k in ('mat', 'obj'):
            S[k] = np.concatenate([S[k], S[k][m]])
        S['dup'] = np.concatenate([S['dup'], np.ones(int(m.sum()), bool)])
    n_src = len(S['pos'])
    n_cut = subdivide(S, centre, radius)
    banded = band_materials(job)
    band_cut, band_made = cut_v_bands(S, banded, centre, radius + BAND_REACH)
    if banded:
        log('split: %d material(s) with a normal map kept inside one vertical texture repeat: %d triangle(s) cut into %d' % (
            len(banded), band_cut, band_made))
    n_tj = repair_tjunctions(S)
    n_fine = snap_where_needed(S)
    if n_fine:
        log('split: %d corner(s) of fine detail inside one surface keep their exact place (not on the grid)' % n_fine)
    # the snap lines up corners that sat 1 - 2 cm off a neighbour's edge (measured: 1,490 on OG Coruscant): a second
    # pass with the exact tolerance makes them shared too. Their corners are on the grid already; only the fan centres
    # are not, and those are inside one triangle, shared by nobody
    n_tj += repair_tjunctions(S, tol=2e-4)
    log('split: %d triangle(s) split at T-junctions; corners on the %.4f m grid' % (n_tj, GRID))
    cen = S['pos'].mean(1)
    pd = np.maximum(0.0, np.linalg.norm(cen - np.asarray(centre), axis=1) - radius)
    out_dir = job.dirs['pieces']
    os.makedirs(out_dir, exist_ok=True)
    keep = set()
    pieces = []
    idx = 0
    inexact = 0                                           # pieces with a corner 32 m or more from their centre
    order = sorted(range(len(S['mat_names'])), key=lambda i: S['mat_names'][i])

    ext_n = ext_near(job)
    exact_far = 300.0                                     # metres from the play area within which pieces stay exact

    def count(cap):
        n = 0
        for mi in order:
            sel = np.nonzero(S['mat'] == mi)[0]
            if len(sel):
                n += sum(1 for _ in _fit_exact(_pieces_of_material(cen[sel], S['obj'][sel], pd[sel], cap, ext_n, far_k), sel, S['pos'], cen, pd,
                                               far=exact_far))
        return n
    # a level holds about PIECE_BUDGET pieces (see there). Over it the triangles-per-piece cap is raised until it fits;
    # when a big map is still over at the most a piece may hold, the extent caps grow too: far pieces first (extent up
    # to twice their distance - still under a pixel of Half3 step, see caps()), then the near extent to 63 m (as exact),
    # then the zone where pieces must stay exact shrinks (300 -> 150 -> 60 m outside the play area)
    n_pieces = count(cap_n)
    while n_pieces > PIECE_BUDGET:
        if cap_n < CAP_MAX:
            cap_n = min(CAP_MAX, int(cap_n * 1.5))
            what = '%d triangles per piece' % cap_n
        elif far_k < 2.0:
            far_k = min(2.0, far_k * 2.0)
            what = 'far pieces up to %.1f x their distance across' % far_k
        elif ext_n < 63.0:
            ext_n = 63.0
            what = 'near pieces up to 63 m across'
        elif exact_far > 60.0:
            # the exact-corner rule halves every piece within exact_far back to 63 m, undoing the far growth (LEGO
            # Utapau: one ring wall 200 - 300 m out made 811 pieces). Past 60 m outside the play area a Half3 step of
            # 3 cm (offsets 32 - 64 m) is under a pixel
            exact_far = 150.0 if exact_far > 150.0 else 60.0
            what = 'pieces kept exact only within %.0f m of the play area' % exact_far
        else:
            break
        n_pieces = count(cap_n)
        log('split: over %d pieces - %s now gives %d' % (PIECE_BUDGET, what, n_pieces))
    if n_pieces > PIECE_BUDGET:
        # over the budget the level crashes while it comes up (seen every time at 1,441 and 1,455): stop here instead
        raise StageError('The map makes %d pieces even at %d triangles each and the largest piece sizes; a level holds about %d '
                         '(more crashes the game as the level comes up). Leave far-away or hidden objects out (role "Leave out"), '
                         'lower "Keep geometry within", or merge many small materials into fewer.' % (n_pieces, cap_n, PIECE_BUDGET))
    for mi in order:
        sel = np.nonzero(S['mat'] == mi)[0]
        if len(sel) == 0:
            continue
        mname = S['mat_names'][mi]
        for part in _fit_exact(_pieces_of_material(cen[sel], S['obj'][sel], pd[sel], cap_n, ext_n, far_k), sel, S['pos'], cen, pd,
                               far=exact_far):
            t = sel[part]
            pos = S['pos'][t]; nrm = S['nrm'][t]; uv = S['uv'][t].copy()
            lo = pos.reshape(-1, 3).min(0); hi = pos.reshape(-1, 3).max(0)
            ctr = snap((lo + hi) / 2)                 # on the grid too: every offset is then exact in Half3 (|offset| < 32 m)
            if np.abs(pos - ctr).max() >= 32.0:
                inexact += 1
            # drop triangles that collapse when corners are welded (a piece cannot hold a face without area) - welded as
            # the game STORES them: Half3 offsets from the centre. Corners of fine detail are off the grid, and two of
            # them 2 mm apart 20 m from the centre are one Half3 value (CR90 interior: 5,202 such faces -> the level
            # crashed 2 s after it came up, game+0x7C6529A, every time)
            vk = np.round(pos, 5)
            hk = (pos - ctr).astype(np.float16)
            ok = ~((vk[:, 0] == vk[:, 1]).all(1) | (vk[:, 1] == vk[:, 2]).all(1) | (vk[:, 0] == vk[:, 2]).all(1) |
                   (hk[:, 0] == hk[:, 1]).all(1) | (hk[:, 1] == hk[:, 2]).all(1) | (hk[:, 0] == hk[:, 2]).all(1))
            collapsed = int((~ok).sum())
            if not ok.any():
                continue
            pos, nrm, uv, t = pos[ok], nrm[ok], uv[ok], t[ok]
            groups, moved = shift_uv_islands(pos, uv, np.ones(len(pos), bool) if mname in banded else None)
            name = '%s_%04d_%s' % (job.piece_prefix, idx, config.safe(mname))[:96]
            idx += 1
            gp = to_game(pos - ctr); gn = to_game(nrm)
            fbx.write(os.path.join(out_dir, name + '.fbx'), gp, gn, uv, material=config.safe(mname))
            np.savez_compressed(os.path.join(out_dir, name + '.npz'), pos=gp.astype(np.float64), obj=S['obj'][t].astype(np.int32),
                                dup=S['dup'][t])
            keep.update((name + '.fbx', name + '.npz'))
            e1 = pos[:, 1] - pos[:, 0]; e2 = pos[:, 2] - pos[:, 0]
            area = np.linalg.norm(np.cross(e1, e2), axis=1) / 2
            d1 = uv[:, 1] - uv[:, 0]; d2 = uv[:, 2] - uv[:, 0]
            uva = np.abs(d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]) / 2
            objs = sorted({S['obj_names'][i] for i in np.unique(S['obj'][t])})
            pieces.append({'name': name, 'file': name + '.fbx', 'material': mname, 'objects': objs, 'tris': int(len(pos)),
                           'centre': [round(float(x), 6) for x in ctr], 'size': [round(float(x), 3) for x in (hi - lo)],
                           'uv_groups': groups, 'uv_groups_shifted': moved,
                           'uv_range': [round(float(x), 3) for x in (uv[..., 0].min(), uv[..., 0].max(), uv[..., 1].min(), uv[..., 1].max())],
                           'area_m2': round(float(area.sum()), 3), 'uv_area': round(float(uva.sum()), 5),
                           'zero_area_tris': int((area < 1e-10).sum()), 'zero_uv_tris': int(((uva < 1e-12) & (area >= 1e-10)).sum()),
                           'collapsed_dropped': collapsed, 'play_distance': round(float(pd[t].min()), 1)})
    for f in os.listdir(out_dir):                       # pieces of an earlier split that no longer exist
        if f.endswith(('.fbx', '.npz')) and f not in keep:
            os.remove(os.path.join(out_dir, f))
    with open(os.path.join(out_dir, 'pieces.json'), 'w', encoding='utf-8') as f:
        json.dump({'pieces': pieces, 'objects': S['obj_names'], 'v_banded': sorted(banded), 'params': PARAMS, 'play': {'centre': list(centre), 'radius': radius},
                   'skipped_faces': S['skipped'], 'bad_uv_faces': bad_uv}, f, indent=1)
    summary = {'pieces': len(pieces), 'triangles': sum(p['tris'] for p in pieces), 'materials': len({p['material'] for p in pieces}),
               'max_tris': max(p['tris'] for p in pieces), 'source_triangles': n_src, 'left_out_far': cropped, 'v_band_materials': len(banded), 'v_band_cut': [band_cut, band_made], 'subdivided': n_cut, 'bad_uv_faces': bad_uv,
               'uv_groups_shifted': sum(p['uv_groups_shifted'] for p in pieces), 'area_m2': round(sum(p['area_m2'] for p in pieces), 1),
               'tjunction_split': n_tj, 'pieces_not_exact': inexact}
    log('split: %(pieces)d pieces, %(triangles)d triangles, %(materials)d materials, largest piece %(max_tris)d triangles; '
        '%(subdivided)d long triangles cut; %(uv_groups_shifted)d uv groups moved; %(area_m2).0f m2' % summary)
    return summary
