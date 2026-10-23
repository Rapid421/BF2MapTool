"""Put a source game's own collision (lswtss_collision.py output) into a prepared scene for the BF2 Map Tool:
  * collision triangles -> invisible "Collision mesh" objects (role collision, no owner = standalone), merged per tile;
  * the game's fall-kill planes -> kill volumes (a box from the plane down `kill_depth` metres);
  * trigger volumes (water) -> left out;
  * every render mesh's own collision -> None (the game's collision replaces it), except objects whose name matches
    `keep_render` (e.g. LEGO buildables that have no collider in the level data).
    blender -b --factory-startup <in.blend> --python import_game_collision.py -- <addon dir> <collision.npz> <out.blend>
        [tile=64] [kill_depth=60] [keep_render=GSC] [drop=WaterVolume] [kill=PlaneFallKill]
"""
import json
import os
import sys

import bpy
import numpy as np

argv = sys.argv[sys.argv.index('--') + 1:]
ADDON, NPZ, OUT = argv[:3]
opt = dict(a.split('=', 1) for a in argv[3:] if '=' in a)
TILE = float(opt.get('tile', 64))
KILL_DEPTH = float(opt.get('kill_depth', 60))
keep_render = [s.lower() for s in opt.get('keep_render', 'GSC').split(',') if s]
drop = [s.lower() for s in opt.get('drop', 'WaterVolume').split(',') if s]
kill = [s.lower() for s in opt.get('kill', 'PlaneFallKill').split(',') if s]
sys.path.insert(0, os.path.dirname(ADDON))
props = __import__(os.path.basename(ADDON) + '.props', fromlist=['props'])
if not hasattr(bpy.types.Scene, 'bf2'):
    props.register()
sc = bpy.context.scene
z = np.load(NPZ)
pos, src, names = z['pos'].astype(np.float64), z['src'], [str(n) for n in z['names']]
ent = [n.split('|')[1].lower() for n in names]
is_kill = np.array([any(k in e for k in kill) for e in ent])
is_drop = np.array([any(d in e for d in drop) for e in ent])
rep = {'kill_volumes': [], 'dropped_shapes': [names[i] for i in np.where(is_drop)[0]], 'render_collision_off': 0, 'render_kept': 0}

old = bpy.data.collections.get('Game collision')
if old is not None:
    for o in list(old.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    bpy.data.collections.remove(old)
coll = bpy.data.collections.new('Game collision')
sc.collection.children.link(coll)

# ---- solid triangles, merged per tile
solid = ~(is_kill | is_drop)[src]
P = pos[solid]
cen = P.mean(1)
key = np.floor(cen[:, :2] / TILE).astype(np.int64)
order = np.lexsort((key[:, 1], key[:, 0]))
P, key = P[order], key[order]
cut = np.nonzero(np.any(np.diff(key, axis=0) != 0, axis=1))[0] + 1
n_obj = 0
for part in np.split(np.arange(len(P)), cut):
    if not len(part):
        continue
    tris = P[part]
    v = tris.reshape(-1, 3)
    me = bpy.data.meshes.new('GameCol_%d_%d' % tuple(key[part[0]]))
    me.from_pydata([tuple(x) for x in v], [], [(3 * i, 3 * i + 1, 3 * i + 2) for i in range(len(tris))])
    me.update()
    ob = bpy.data.objects.new(me.name, me)
    coll.objects.link(ob)
    ob.bf2.role = 'collision'
    ob.hide_render = True
    ob.display_type = 'WIRE'
    n_obj += 1
rep['collision_objects'] = n_obj
rep['collision_triangles'] = int(len(P))

# ---- fall-kill planes -> kill volumes
for s in np.unique(src[is_kill[src]]):
    t = pos[src == s].reshape(-1, 3)
    lo, hi = t.min(0), t.max(0)
    top = float(hi[2])
    me = bpy.data.meshes.new('KillVolume_' + names[s].split('|')[1])
    bx = [(lo[0], lo[1], top - KILL_DEPTH), (hi[0], lo[1], top - KILL_DEPTH), (hi[0], hi[1], top - KILL_DEPTH), (lo[0], hi[1], top - KILL_DEPTH),
          (lo[0], lo[1], top), (hi[0], lo[1], top), (hi[0], hi[1], top), (lo[0], hi[1], top)]
    me.from_pydata(bx, [], [(0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)])
    ob = bpy.data.objects.new(me.name, me)
    coll.objects.link(ob)
    ob.bf2.role = 'kill_volume'
    ob.hide_render = True
    ob.display_type = 'WIRE'
    rep['kill_volumes'].append({'name': names[s], 'x': [float(lo[0]), float(hi[0])], 'y': [float(lo[1]), float(hi[1])], 'top_z': top})

# ---- render meshes stop being solid (the game's collision replaces them)
for o in bpy.data.objects:
    if o.type == 'MESH' and o.bf2.role == 'static' and o.name not in coll.objects:
        cols = ' '.join(c.name for c in o.users_collection).lower()
        if any(k.lower() in (o.name.lower() + ' ' + cols) for k in keep_render):
            rep['render_kept'] += 1
            continue
        o.bf2.collision = 'none'
        rep['render_collision_off'] += 1
json.dump(rep, open(os.path.splitext(OUT)[0] + '.collision.json', 'w'), indent=1)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=True)
print('GAME COLLISION', json.dumps({k: (v if not isinstance(v, list) else len(v)) for k, v in rep.items()}), '->', OUT)
for k in rep['kill_volumes']:
    print('  kill volume', k)
