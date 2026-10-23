"""Check scene for the four collision settings. Every soldier spawns on a placed spawn (auto spawns off), so where
soldiers STAND shows what is solid:

  exact    podium A (x -27, y -9): a 2 m box. Soldiers stand on its top.
  custom   podium B (x -27, y +9): the visible box is 1 m high, its collision mesh 2 m. Soldiers stand 1 m ABOVE it.
  hull     podium C (x +30, y 0): two pillars 2 m apart, one object. Its convex hull closes the gap: soldiers stand in
           the air between the pillars.
  none     the west wall of team 2's yard (x +20): drawn, not solid. Team 2 can only leave its yard THROUGH it.
    blender -b --factory-startup --python make_coltest.py -- <out.blend> [level name]
"""
import math
import os
import sys

import bmesh
import bpy
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..', 'addon', 'bf2_map_tool')))
import props

argv = sys.argv[sys.argv.index('--') + 1:]
OUT = argv[0]
NAME = argv[1] if len(argv) > 1 else 'ColTest'
# 'all' = every spawn group; 'a' / 'b' = ONLY the custom podium and the hull gap carry spawns (a: team 1 custom, team 2
# hull; b: the other way round), so the height the player comes to rest at measures that collision shape in game
VARIANT = argv[2] if len(argv) > 2 else 'all'
bpy.ops.wm.read_factory_settings(use_empty=True)
props.register()
sc = bpy.context.scene
sc.bf2.name = NAME
sc.bf2.display_name = 'Collision test'
sc.bf2.planet = 'Test'
sc.bf2.sky = 'clear'
sc.bf2.auto_spawns = False
rng = np.random.default_rng(6)


def image(name, rgba):
    h, w = rgba.shape[:2]
    img = bpy.data.images.new(name, w, h, alpha=True)
    img.pixels.foreach_set(np.ascontiguousarray(rgba[::-1], np.float32).ravel())
    img.pack()
    return img


def checker(n, a, b, cells):
    y, x = np.mgrid[0:n, 0:n]
    k = ((x * cells // n) + (y * cells // n)) % 2
    img = np.where(k[..., None] == 0, np.array(a), np.array(b)).astype(np.float32)
    img += rng.normal(0, 0.01, img.shape).astype(np.float32)
    return np.dstack([np.clip(img, 0, 1), np.ones((n, n), np.float32)])


def material(name, a, b, cells=4):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    p = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
    p.inputs['Roughness'].default_value = 0.7
    t = nt.nodes.new('ShaderNodeTexImage'); t.image = image(name + '_c', checker(256, a, b, cells))
    nt.links.new(t.outputs['Color'], p.inputs['Base Color'])
    return m


def mesh_obj(name, bm, loc, mat, role='static', collision='exact'):
    uv = bm.loops.layers.uv.verify()
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        u_ax, v_ax = [(1, 2), (0, 2), (0, 1)][ax]
        for l in f.loops:
            l[uv].uv = (l.vert.co[u_ax] / 4.0, l.vert.co[v_ax] / 4.0)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me); bm.free()
    me.materials.append(mat)
    o = bpy.data.objects.new(name, me)
    o.location = loc
    sc.collection.objects.link(o)
    o.bf2.role, o.bf2.collision = role, collision
    return o


def box(name, size, loc, mat, role='static', collision='exact', parts=((0.0, 0.0, 0.0),)):
    """one object made of one or more boxes of `size`, each moved by an offset in `parts`"""
    bm = bmesh.new()
    for off in parts:
        r = bmesh.ops.create_cube(bm, size=1.0)
        for v in r['verts']:
            v.co.x = v.co.x * size[0] + off[0]; v.co.y = v.co.y * size[1] + off[1]; v.co.z = v.co.z * size[2] + off[2]
    bm.normal_update()
    return mesh_obj(name, bm, loc, mat, role, collision)


def ramp(name, x0, x1, y, width, top, mat):
    """a wedge from the floor at x0 up to height `top` at x1"""
    bm = bmesh.new()
    v = [bm.verts.new(c) for c in ((x0, y - width / 2, 0), (x1, y - width / 2, 0), (x1, y - width / 2, top),
                                   (x0, y + width / 2, 0), (x1, y + width / 2, 0), (x1, y + width / 2, top))]
    for f in ((0, 1, 2), (5, 4, 3), (0, 2, 5, 3), (1, 4, 5, 2), (0, 3, 4, 1)):
        bm.faces.new([v[i] for i in f])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return mesh_obj(name, bm, (0.0, 0.0, 0.0), mat)


def spawn(name, loc, team, yaw):
    o = bpy.data.objects.new(name, None)
    o.empty_display_type = 'SINGLE_ARROW'
    o.location = loc
    o.rotation_euler = (math.radians(90.0), 0.0, math.radians(yaw))
    sc.collection.objects.link(o)
    o.bf2.role, o.bf2.team = 'spawn', team


M_FLOOR = material('ct_floor', (0.34, 0.35, 0.36), (0.26, 0.27, 0.28), 8)
M_WALL = material('ct_wall', (0.45, 0.40, 0.32), (0.38, 0.34, 0.27))
M_EXACT = material('ct_exact', (0.20, 0.50, 0.20), (0.16, 0.42, 0.16))        # green
M_CUSTOM = material('ct_custom', (0.20, 0.30, 0.60), (0.16, 0.25, 0.50))      # blue
M_HULL = material('ct_hull', (0.60, 0.45, 0.15), (0.50, 0.38, 0.12))          # orange
M_NONE = material('ct_none', (0.60, 0.15, 0.15), (0.50, 0.12, 0.12))          # red
box('floor', (84.0, 44.0, 1.0), (0.0, 0.0, -0.5), M_FLOOR)
for nm, size, loc in (('wall_n', (84.0, 1.0, 4.0), (0.0, 22.5, 2.0)), ('wall_s', (84.0, 1.0, 4.0), (0.0, -22.5, 2.0)),
                      ('wall_w', (1.0, 46.0, 4.0), (-42.5, 0.0, 2.0)), ('wall_e', (1.0, 46.0, 4.0), (42.5, 0.0, 2.0))):
    box(nm, size, loc, M_WALL)
# A exact: 6 x 6 x 2 box, ramp from the east
box('podium_exact', (6.0, 6.0, 2.0), (-27.0, -9.0, 1.0), M_EXACT)
ramp('ramp_exact', -18.0, -24.0, -9.0, 4.0, 2.0, M_FLOOR)
# B custom: the visible box is 1 m high; its collision mesh (role Collision mesh, not drawn) is 2 m high
vis = box('podium_custom', (6.0, 6.0, 1.0), (-27.0, 9.0, 0.5), M_CUSTOM, collision='custom')
col = box('podium_custom_col', (6.0, 6.0, 2.0), (-27.0, 9.0, 1.0), M_CUSTOM, role='collision')
vis.bf2.collision_object = col
ramp('ramp_custom', -18.0, -24.0, 9.0, 4.0, 2.0, M_FLOOR)
# C hull: two pillars 2 x 6 x 2, 2 m apart, ONE object; the hull closes the gap between them
box('podium_hull', (2.0, 6.0, 2.0), (30.0, 0.0, 1.0), M_HULL, collision='hull', parts=((-2.0, 0.0, 0.0), (2.0, 0.0, 0.0)))
ramp('ramp_hull', 21.0, 27.0, 0.0, 4.0, 2.0, M_FLOOR)
# team 2's yard: x 20 .. 42, y -12 .. 12; north / south walls solid, the WEST wall drawn but not solid
box('yard_n', (22.0, 1.0, 4.0), (31.0, 12.5, 2.0), M_WALL)
box('yard_s', (22.0, 1.0, 4.0), (31.0, -12.5, 2.0), M_WALL)
box('yard_w_none', (0.6, 4.0, 4.0), (20.0, 8.0, 2.0), M_NONE, collision='none')        # the only gap in the west side ...
box('yard_w_a', (0.6, 6.0, 4.0), (20.0, -9.0, 2.0), M_WALL)                            # ... the rest of that side is solid,
box('yard_w_b', (0.6, 4.0, 4.0), (20.0, 4.0, 2.0), M_WALL)                             # except where the ramp comes in (y -2 .. 2)
box('yard_w_c', (0.6, 2.0, 4.0), (20.0, 11.0, 2.0), M_WALL)
box('yard_w_d', (0.6, 4.0, 4.0), (20.0, -4.0, 2.0), M_WALL)
box('yard_gate_none', (0.6, 4.0, 4.0), (20.0, 0.0, 2.0), M_NONE, collision='none')     # across the ramp's foot: drawn, not solid
TC, TH = ('team2', 'team1') if VARIANT == 'b' else ('team1', 'team2')
if VARIANT == 'all':
    for i, y in enumerate((-10.0, -9.0, -8.0)):
        spawn('spawn_t1_exact_%d' % i, (-27.0, y, 2.0), 'team1', -90.0)
for i, y in enumerate((8.0, 9.0, 10.0)):
    spawn('spawn_custom_%d' % i, (-27.0, y, 2.0), TC, -90.0)                           # 1 m above the visible blue box
for i, y in enumerate((-1.5, 0.0, 1.5)):
    spawn('spawn_hull_%d' % i, (30.0, y, 2.0), TH, 90.0)                               # in the gap between the pillars
if VARIANT == 'all':
    for i, y in enumerate((-8.0, -6.0, 6.0)):
        spawn('spawn_t2_yard_%d' % i, (36.0, y, 0.0), 'team2', 90.0)
sun = bpy.data.lights.new('sun', 'SUN'); sun.energy = 3.0
so = bpy.data.objects.new('sun', sun); so.rotation_euler = (math.radians(40.0), 0.0, math.radians(20.0))
sc.collection.objects.link(so)
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=OUT)
print('COLTEST DONE', OUT, 'objects', len(sc.objects))
