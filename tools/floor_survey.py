"""Walkable-floor survey of a prepared scene, for placing spawns and command posts by design.
    blender -b <prepared.blend> --python floor_survey.py -- <out.json> cx cy radius step zlo zhi
Per grid column, every up-facing surface (normal z > 0.85) between zlo and zhi with 2 m of headroom and 0.6 m of side
room. For each floor point: cover = how many of 16 chest-height rays (1.3 m) hit something within 12 m, and sky = whether
it is open above. Connectivity: neighbouring points (one grid step) whose heights differ by < 0.7 m are joined.
"""
import json
import math
import sys

import bpy
from mathutils import Vector

a = sys.argv[sys.argv.index('--') + 1:]
out = a[0]
cx, cy, R, step, zlo, zhi = (float(x) for x in a[1:7])
sc = bpy.context.scene
dg = bpy.context.evaluated_depsgraph_get()
statics = {o.name for o in sc.objects if getattr(o, 'bf2', None) is None or o.bf2.role == 'static'}
skipped_mats = {m.name for m in bpy.data.materials if getattr(m, 'bf2', None) is not None and (m.bf2.skip or m.bf2.cls == 'holo')}


def solid(ob, idx):
    if ob.name not in statics:
        return False
    if getattr(ob, 'bf2', None) is not None and ob.bf2.collision == 'none':
        return False
    try:
        mi = ob.data.polygons[idx].material_index
        m = ob.material_slots[mi].material
        return not (m and m.name in skipped_mats)
    except (IndexError, AttributeError):
        return True


def cast(o, d, dist):
    """first SOLID hit along a ray (non-solid surfaces are stepped through)"""
    o = Vector(o)
    for _ in range(12):
        ok, loc, nrm, idx, ob, _m = sc.ray_cast(dg, o, Vector(d), distance=dist)
        if not ok:
            return None
        if solid(ob, idx):
            return loc, nrm, ob
        dist -= (loc - o).length + 0.02
        o = loc + Vector(d) * 0.02
        if dist <= 0:
            return None
    return None


DIRS = [(math.cos(i * math.pi / 8), math.sin(i * math.pi / 8), 0.0) for i in range(16)]
pts = []
n = int(R / step)
for gx in range(-n, n + 1):
    for gy in range(-n, n + 1):
        x, y = cx + gx * step, cy + gy * step
        if (x - cx) ** 2 + (y - cy) ** 2 > R * R:
            continue
        z0 = zhi + 3.0
        for _ in range(10):
            h = cast((x, y, z0), (0, 0, -1), z0 - zlo + 0.5)
            if h is None:
                break
            loc, nrm, ob = h
            if loc.z < zlo:
                break
            if nrm.z > 0.85:
                head = cast(loc + Vector((0, 0, 0.05)), (0, 0, 1), 2.0)
                side = any(cast(loc + Vector((0, 0, 1.0)), d, 0.6) for d in ((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0)))
                if head is None and not side:
                    cov = sum(1 for d in DIRS if cast(loc + Vector((0, 0, 1.3)), d, 12.0) is not None)
                    sky = cast(loc + Vector((0, 0, 2.1)), (0, 0, 1), 200.0) is None
                    pts.append([round(x, 2), round(y, 2), round(loc.z, 2), cov, int(sky), ob.name[:40]])
            z0 = loc.z - 0.1
json.dump({'centre': [cx, cy], 'radius': R, 'step': step, 'z': [zlo, zhi], 'points': pts}, open(out, 'w'))
print('SURVEY DONE', len(pts))
