"""LEGO Star Wars: The Skywalker Saga level collision -> world-space triangles (Blender axes), for the BF2 Map Tool.

Source (the game's own data, read from the level's .scene_baked layers and the .dno shapes they name):
  * MeshColliderComponent  -> a .dno collision mesh (big-endian): ... 'SERIALISED' ... u32 6, u32 nv, u32 a, u32 nt, u32 c,
                              u32 1, nv x 3 f32 vertices, nt x (4 x u16 indices [i0 i1 i2 i2], u8 flags, u8 flags), then a
                              bounding-volume tree and the shape name (decoded 2026-10-16 on plane_1unit_pcv: 4 v / 2 t,
                              door_1unit: 8 v / 10 t, fed_street_light: 125 v / 184 t)
  * BoxColliderComponent   -> u16 0, f32 1.0, 3 x f32 half extents (unit box 0.5)
  * CylinderColliderComponent -> u16 0, f32 1.0, f32 radius, f32 half height
  each placed by its entity's transform chain (scale, quaternion xyzw, position; parent -> child, game axes), then mapped
  to Blender as the level exporter does: game (x, y, z) -> Blender (-x, -z, y).
Entities that are disabled (or under a disabled ancestor) are left out; capsule / sphere colliders (characters, pickups)
and the spline walls are reported but not built.

    python lswtss_collision.py <extract_cache/files> <level folder fragment> <out.npz> [--layers art,props,...]
Output npz: pos (T, 3, 3) float32 Blender world, src (T) int index into names, names; plus <out>.json with counts.
"""
import collections
import json
import math
import re
import struct
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, r'C:\Users\rubes\Documents\Codex\2026-10-14\i-x20\outputs\LSWTSS_Level_Exporter')
import prepare_level as PL  # noqa: E402  (the level exporter's own scene frame reader)
sys.path.insert(0, str(Path(__file__).resolve().parent))

root, frag, out = Path(sys.argv[1]), sys.argv[2].lower(), Path(sys.argv[3])
layers = None
if '--layers' in sys.argv:
    layers = sys.argv[sys.argv.index('--layers') + 1].lower().split(',')

dno_index = {}
for p in root.rglob('*.dno'):
    dno_index[str(p.relative_to(root)).replace('\\', '/').lower()] = p
    dno_index.setdefault(p.name.lower(), p)
from lswtss_dno import read_dno, _dno_cache  # noqa: E402


def box(hx, hy, hz):
    c = np.array([[sx * hx, sy * hy, sz * hz] for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)])
    f = [(0, 1, 3), (0, 3, 2), (4, 6, 7), (4, 7, 5), (0, 4, 5), (0, 5, 1), (2, 3, 7), (2, 7, 6), (0, 2, 6), (0, 6, 4), (1, 5, 7), (1, 7, 3)]
    return c[np.array(f)]


def cylinder(r, h, n=12):
    """axis = local Y (the game's up)"""
    a = np.linspace(0, 2 * math.pi, n, endpoint=False)
    top = np.stack([r * np.cos(a), np.full(n, h), r * np.sin(a)], 1)
    bot = top.copy(); bot[:, 1] = -h
    T = []
    for i in range(n):
        j = (i + 1) % n
        T += [(bot[i], top[i], top[j]), (bot[i], top[j], bot[j]), ((0, h, 0), top[j], top[i]), ((0, -h, 0), bot[i], bot[j])]
    return np.array(T, np.float64)


def mat(xf):
    s = xf['scale'][:3]; x, y, z, w = xf['quaternion_xyzw']; p = xf['position'][:3]
    R = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                  [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                  [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
    M = np.eye(4); M[:3, :3] = R * np.array(s)[None, :]; M[:3, 3] = p
    return M


B = np.array([[-1, 0, 0], [0, 0, -1], [0, 1, 0]], np.float64)   # game -> Blender
pos_all, src_all, names = [], [], []
stats = collections.Counter()
skipped = collections.Counter()
files = [p for p in sorted(root.rglob('*.scene_baked')) if frag in str(p).lower().replace('\\', '/')]
for p in files:
    if layers and not any(p.name.lower().endswith(l + '.scene_baked') or p.name.lower() == l + '.scene_baked' for l in layers):
        continue
    data = p.read_bytes()
    rows = PL.frames(data)['records']
    kids = collections.defaultdict(list)
    for i, r in enumerate(rows):
        kids[r['parent']].append(i)
    ent_parent, local, enabled = {}, {}, {}
    for i, r in enumerate(rows):
        if r['class'] != 'apiEntity':
            continue
        par = r['parent']
        while par is not None and rows[par]['class'] != 'apiEntity':
            par = rows[par]['parent']
        ent_parent[i] = par
        enabled[i] = bool(r['enabled_byte'])
        for j in kids[i]:
            c = rows[j]
            raw = data[c['payload_start']:c['end']]
            if c['class'] == 'apiTransformComponent' and len(raw) == 108:
                v = struct.unpack_from('<12f', raw)
                local[i] = {'scale': v[:4], 'quaternion_xyzw': v[4:8], 'position': v[8:12]}

    def world(i):
        M = np.eye(4)
        chain = []
        while i is not None:
            chain.append(i); i = ent_parent.get(i)
        for k in reversed(chain):
            if k in local:
                M = M @ mat(local[k])
        return M

    def live(i):
        while i is not None:
            if not enabled.get(i, True):
                return False
            i = ent_parent.get(i)
        return True

    for i, r in enumerate(rows):
        if r['class'] != 'apiEntity':
            continue
        for j in kids[i]:
            c = rows[j]
            cls = c['class']
            if cls not in ('MeshColliderComponent', 'BoxColliderComponent', 'CylinderColliderComponent', 'CapsuleColliderComponent',
                           'SphereColliderComponent', 'SplineCollisionWall::Component'):
                continue
            if not c['enabled_byte'] or not live(i):
                skipped[cls + ' disabled'] += 1; continue
            raw = data[c['payload_start']:c['end']]
            local_tris = None
            if cls == 'MeshColliderComponent':
                m = re.search(rb'[A-Za-z0-9_./\\ #()\-]+\.dno\x00', raw, re.I)
                if not m:
                    skipped['mesh collider without .dno'] += 1; continue
                key = m.group()[:-1].decode('ascii').replace('\\', '/').lower()
                dp = dno_index.get(key) or dno_index.get(key.split('/')[-1])
                if dp is None:
                    skipped['dno not extracted: ' + key.split('/')[-1]] += 1; continue
                local_tris = read_dno(dp)
                label = key.split('/')[-1]
            elif cls == 'BoxColliderComponent' and len(raw) >= 18:
                hx, hy, hz = struct.unpack_from('<3f', raw, 6)
                local_tris = box(hx, hy, hz); label = 'box'
            elif cls == 'CylinderColliderComponent' and len(raw) >= 14:
                rr, hh = struct.unpack_from('<2f', raw, 6)
                local_tris = cylinder(rr, hh); label = 'cylinder'
            else:
                skipped[cls + ' (not built)'] += 1; continue
            if local_tris is None or not len(local_tris):
                skipped[cls + ' empty'] += 1; continue
            M = world(i)
            w = local_tris.reshape(-1, 3) @ M[:3, :3].T + M[:3, 3]
            w = (w @ B.T).reshape(-1, 3, 3)[:, ::-1]               # Blender axes; winding kept facing out after the mirror
            pos_all.append(w.astype(np.float32))
            src_all.append(np.full(len(w), len(names), np.int32))
            names.append('%s|%s|%s' % (p.stem.replace('.scene_baked', ''), r.get('name') or 'entity', label))
            stats[cls] += 1
            stats['triangles'] += len(w)
pos = np.concatenate(pos_all) if pos_all else np.zeros((0, 3, 3), np.float32)
src = np.concatenate(src_all) if src_all else np.zeros(0, np.int32)
np.savez_compressed(out, pos=pos, src=src, names=np.array(names))
lo, hi = (pos.reshape(-1, 3).min(0).tolist(), pos.reshape(-1, 3).max(0).tolist()) if len(pos) else (None, None)
rep = {'built': dict(stats), 'skipped': dict(skipped.most_common()), 'shapes': len(names), 'bbox': [lo, hi], 'dno_files': len(_dno_cache)}
json.dump(rep, open(out.with_suffix('.json'), 'w'), indent=1)
print(json.dumps({k: rep[k] for k in ('built', 'shapes', 'bbox', 'dno_files')}))
print('skipped', list(skipped.most_common(15)))
