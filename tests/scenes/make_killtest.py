"""Check scene for kill volumes (and a door that lies flat): team 2 spawns ON a hatch over a pit.
  hatch     a door lying flat in a hole of the floor; it slides open as soon as a soldier stands on it
  pit       2.8 m deep, solid walls and floor
  Kill pit  a kill volume filling the pit from its floor to 0.8 m under the hatch
A soldier who spawns on the hatch drops into the pit when the hatch opens and must DIE there. The player is put on
team 2 by the game in these tests; tools/kill_probe.py deploys him and follows his camera height and whether he still
has a soldier. Team 1 spawns on plain floor 42 m away.
    blender -b --factory-startup --python make_killtest.py -- <out.blend> [level name]
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
NAME = argv[1] if len(argv) > 1 else 'KillTest'
bpy.ops.wm.read_factory_settings(use_empty=True)
props.register()
sc = bpy.context.scene
sc.bf2.name = NAME
sc.bf2.display_name = 'Kill volume test'
sc.bf2.planet = 'Test'
sc.bf2.sky = 'clear'
sc.bf2.auto_spawns = False            # everyone spawns at the six placed spawns, inside the two door triggers
rng = np.random.default_rng(5)


def image(name, rgba, colorspace='sRGB'):
    h, w = rgba.shape[:2]
    img = bpy.data.images.new(name, w, h, alpha=True)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(np.ascontiguousarray(rgba[::-1], np.float32).ravel())
    img.pack()
    return img


def checker(n, a, b, cells):
    y, x = np.mgrid[0:n, 0:n]
    k = ((x * cells // n) + (y * cells // n)) % 2
    img = np.where(k[..., None] == 0, np.array(a), np.array(b)).astype(np.float32)
    img += rng.normal(0, 0.01, img.shape).astype(np.float32)
    return np.dstack([np.clip(img, 0, 1), np.ones((n, n), np.float32)])


def material(name, base, rough=0.6, metal=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    p = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
    p.inputs['Roughness'].default_value = rough
    p.inputs['Metallic'].default_value = metal
    t = nt.nodes.new('ShaderNodeTexImage'); t.image = base
    nt.links.new(t.outputs['Color'], p.inputs['Base Color'])
    return m


def box(name, size, loc, mat, role='static'):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0, calc_uvs=True)
    for v in bm.verts:
        v.co.x *= size[0]; v.co.y *= size[1]; v.co.z *= size[2]
    uv = bm.loops.layers.uv.verify()
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        u_ax, v_ax = [(1, 2), (0, 2), (0, 1)][ax]
        for l in f.loops:
            l[uv].uv = (l.vert.co[u_ax] / 4.0, l.vert.co[v_ax] / 4.0)
    bm.to_mesh(me); bm.free()
    me.materials.append(mat)
    o = bpy.data.objects.new(name, me)
    o.location = loc
    sc.collection.objects.link(o)
    o.bf2.role = role
    return o


M_FLOOR = material('kt_floor', image('kt_floor_c', checker(512, (0.34, 0.35, 0.36), (0.26, 0.27, 0.28), 8)))
M_WALL = material('kt_wall', image('kt_wall_c', checker(512, (0.45, 0.40, 0.32), (0.38, 0.34, 0.27), 4)), rough=0.8)
M_DOOR = material('kt_hatch', image('kt_hatch_c', checker(256, (0.55, 0.20, 0.15), (0.45, 0.16, 0.12), 2)), rough=0.6)
HX = 20.0                                   # the hole: 4 x 4 m around (HX, 0)
# the floor in four parts around the hole (64 x 30 m in all)
box('floor_w', (50.0, 30.0, 1.0), (HX - 2.0 - 25.0, 0.0, -0.5), M_FLOOR)
box('floor_e', (10.0, 30.0, 1.0), (HX + 2.0 + 5.0, 0.0, -0.5), M_FLOOR)
box('floor_n', (4.0, 13.0, 1.0), (HX, 2.0 + 6.5, -0.5), M_FLOOR)
box('floor_s', (4.0, 13.0, 1.0), (HX, -2.0 - 6.5, -0.5), M_FLOOR)
# the pit under the hole: floor top at -2.8, walls from -3.3 up to the floor slab
box('pit_floor', (5.0, 5.0, 0.5), (HX, 0.0, -3.05), M_WALL)
for nm, size, loc in (('pit_w', (0.5, 5.0, 2.3), (HX - 2.25, 0.0, -2.15)), ('pit_e', (0.5, 5.0, 2.3), (HX + 2.25, 0.0, -2.15)),
                      ('pit_n', (4.0, 0.5, 2.3), (HX, 2.25, -2.15)), ('pit_s', (4.0, 0.5, 2.3), (HX, -2.25, -2.15))):
    box(nm, size, loc, M_WALL)
d = box('hatch', (3.9, 3.9, 0.3), (HX, 0.0, -0.15), M_DOOR, role='door')
d.bf2.door_axis = 'up'                      # a door lying flat: "up" is the scene's +y - it slides north under the floor edge
box('Kill pit', (3.6, 3.6, 2.0), (HX, 0.0, -1.8), M_WALL, role='kill_volume')
for i, y in enumerate((-0.9, 0.0, 0.9)):
    for team, x, yaw in (('team1', HX - 42.0, -90.0), ('team2', HX, 90.0)):
        o = bpy.data.objects.new('spawn_%s_%d' % (team, i), None)
        o.empty_display_type = 'SINGLE_ARROW'
        o.location = (x, y, 0.0)
        o.rotation_euler = (math.radians(90.0), 0.0, math.radians(yaw))
        sc.collection.objects.link(o)
        o.bf2.role, o.bf2.team = 'spawn', team
sun = bpy.data.lights.new('sun', 'SUN'); sun.energy = 3.0
so = bpy.data.objects.new('sun', sun); so.rotation_euler = (math.radians(40.0), 0.0, math.radians(20.0))
sc.collection.objects.link(so)
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=OUT)
print('KILLTEST DONE', OUT, 'objects', len(sc.objects))
