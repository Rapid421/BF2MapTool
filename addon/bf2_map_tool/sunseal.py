"""Faces the sun reaches from BEHIND get a second face looking at the sun.

Blender (Cycles) lets a one-sided roof or wall block the sun whichever way its faces look; the game does not - its sun
shadows are cast only by faces that look towards the sun, so a ceiling modelled with its faces pointing down lets the sun
into the room (seen in game 2026-10-12 on a ripped map: a control room black in Blender, sunlit in game).
`seal` finds every exported triangle that looks away from the sun and whose back the sun reaches, and appends its
mirror image (reversed winding, reversed normals, same UVs and material) to the object's geometry file. Seen from the
sun's side that is exactly what Blender shows there (unless the material has Backface Culling on - those are left alone).

    seal(scene, M, out_dir, sun_travel_dir, log) -> {'triangles': n, 'objects': k}
"""
import os

import bpy
import numpy as np
from mathutils import Vector

FACING = -0.05          # looks away from the sun by more than this (cosine)
START = 0.002           # the ray starts this far off the face, on the sun's side
MAX_SKIPS = 6           # hits on things that are not map geometry (helpers) are stepped over
SHADOW_REACH = 250.0    # metres beyond the play radius that are still sealed
BUDGET_S = 1500.0       # seconds; objects are done nearest the play area first


def _dist(rec, out_dir, pc):
    if not rec.get('geo'):
        return float('inf')
    z = np.load(os.path.join(out_dir, rec['geo']), allow_pickle=False)
    c = z['pos'].reshape(-1, 3).mean(0)
    return float(np.hypot(c[0] - pc[0], c[1] - pc[1]))


def seal(scene, M, out_dir, travel, log=print):
    s = -Vector(travel).normalized()                       # towards the sun
    sn = np.array(s, np.float64)
    dg = bpy.context.evaluated_depsgraph_get()
    solid = {o['name'] for o in M['objects'] if o.get('geo') and o.get('role', 'static') == 'static'}
    culled = {n for n, m in M['materials'].items() if m.get('backface_culling')}
    total, touched = 0, 0
    import time as _time
    _t0 = _tlast = _time.time()
    # only near the play area: the game draws sun shadows a few hundred metres out, farther faces change nothing
    play = M.get('play') or {}
    pc = np.array(play.get('centre') or [0.0, 0.0, 0.0], np.float64)
    reach = float(play.get('radius') or 0.0) + SHADOW_REACH if play.get('centre') else float('inf')
    # one BVH tree of every solid static triangle (world space, as exported): a hit on it = the sun is blocked
    from mathutils.bvhtree import BVHTree
    V, T = [], []
    for rec in M['objects']:
        if rec.get('geo') and rec.get('role', 'static') == 'static':
            zz = np.load(os.path.join(out_dir, rec['geo']), allow_pickle=False)
            pp = zz['pos'].reshape(-1, 3).astype(np.float64)
            base = len(V)
            V.extend(map(tuple, pp))
            T.extend((base + 3 * i, base + 3 * i + 1, base + 3 * i + 2) for i in range(len(pp) // 3))
    tree = BVHTree.FromPolygons(V, T, all_triangles=True, epsilon=0.0)
    del V, T
    log('sun seal: occluder tree of %d triangles built (%.0f s)' % (sum(r.get('tris', 0) for r in M['objects'] if r.get('geo') and r.get('role', 'static') == 'static'), _time.time() - _t0))
    order = sorted(range(len(M['objects'])), key=lambda k: _dist(M['objects'][k], out_dir, pc))
    budget = BUDGET_S
    stopped = 0
    for _k, ri in enumerate(order):
        rec = M['objects'][ri]
        if _time.time() - _t0 > budget:
            stopped = len(order) - _k
            break
        if _time.time() - _tlast > 30.0:
            _tlast = _time.time()
            log('sun seal: %d of %d objects (%.0f s)' % (_k, len(M['objects']), _tlast - _t0))
        if not rec.get('geo') or rec.get('role', 'static') != 'static':
            continue
        path = os.path.join(out_dir, rec['geo'])
        z = dict(np.load(path, allow_pickle=False))
        pos = z['pos'].astype(np.float64)
        ng = np.cross(pos[:, 1] - pos[:, 0], pos[:, 2] - pos[:, 0])
        ln = np.linalg.norm(ng, axis=1)
        ok = ln > 1e-10
        cosv = np.zeros(len(pos)); cosv[ok] = (ng[ok] @ sn) / ln[ok]
        slots = [str(x) for x in z['slots']]
        keep = np.array([slots[i] not in culled for i in z['mat']], bool) if len(slots) else np.ones(len(pos), bool)
        near = np.linalg.norm(pos.mean(1)[:, :2] - pc[:2], axis=1) <= reach
        cand = np.where(ok & (cosv < FACING) & keep & near)[0]
        if not len(cand):
            continue
        cen = pos[cand].mean(1)
        hit_sun = np.zeros(len(cand), bool)
        for k in range(len(cand)):
            loc, _n, _i, _d = tree.ray_cast(Vector(cen[k]) + s * START, s, 100000.0)
            hit_sun[k] = loc is None
        idx = cand[hit_sun]
        if not len(idx):
            continue
        add = {'pos': z['pos'][idx][:, ::-1], 'nrm': -z['nrm'][idx][:, ::-1], 'uv': z['uv'][idx][:, ::-1], 'mat': z['mat'][idx]}
        if 'col' in z:
            add['col'] = z['col'][idx][:, ::-1]
        for key, a in add.items():
            z[key] = np.concatenate([z[key], a.astype(z[key].dtype)])
        np.savez_compressed(path, **z)
        rec['tris'] = int(len(z['pos']))
        rec['sun_sealed'] = int(len(idx))
        total += int(len(idx)); touched += 1
    log('sun seal: %d face(s) of %d object(s) doubled so they block the sun as in Blender%s' % (total, touched,
        '; time budget reached, the %d farthest objects not checked' % stopped if stopped else ''))
    return {'triangles': total, 'objects': touched, 'not_checked_far_objects': stopped}
