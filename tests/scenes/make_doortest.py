"""Check scene for doors: a hall with two cross walls.
  door_a      slides UP; team 1's hand-placed spawns stand 3 m from it, inside its trigger reach, so it must be OPEN
              whenever a soldier has just spawned there - the player included, who is deployed and left standing.
  door_b      slides to the LEFT; team 2's spawns stand 3 m from it (the same proof for the other team / axis).
  door_a_far  slides up, 11 m from every spawn: the control, shut unless a bot walks up to it.
    blender -b --factory-startup --python make_doortest.py -- <out.blend> [level name]
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
NAME = argv[1] if len(argv) > 1 else 'DoorTest'
bpy.ops.wm.read_factory_settings(use_empty=True)
props.register()
sc = bpy.context.scene
sc.bf2.name = NAME
sc.bf2.display_name = 'Door test'
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


M_FLOOR = material('dt_floor', image('dt_floor_c', checker(512, (0.34, 0.35, 0.36), (0.26, 0.27, 0.28), 8)))
M_WALL = material('dt_wall', image('dt_wall_c', checker(512, (0.45, 0.40, 0.32), (0.38, 0.34, 0.27), 4)), rough=0.8)
M_DOOR = material('dt_door', image('dt_door_c', checker(256, (0.55, 0.20, 0.15), (0.45, 0.16, 0.12), 2)), rough=0.6)
box('floor', (84.0, 34.0, 1.0), (0.0, 0.0, -0.5), M_FLOOR)
for nm, size, loc in (('wall_n', (84.0, 1.0, 5.0), (0.0, 17.5, 2.5)), ('wall_s', (84.0, 1.0, 5.0), (0.0, -17.5, 2.5)),
                      ('wall_w', (1.0, 36.0, 5.0), (-42.5, 0.0, 2.5)), ('wall_e', (1.0, 36.0, 5.0), (42.5, 0.0, 2.5))):
    box(nm, size, loc, M_WALL)
# two cross walls (x = -25 and x = +25), each with doorways 4 m wide at y -2 .. 2 (and wall A a second one at y 9 .. 13)
for wx, tag in ((-25.0, 'a'), (25.0, 'b')):
    spans = ((-17.0, -2.0), (2.0, 9.0), (13.0, 17.0)) if tag == 'a' else ((-17.0, -2.0), (2.0, 17.0))
    for k, (y0, y1) in enumerate(spans):
        box('mid_%s_%d' % (tag, k), (0.6, y1 - y0, 5.0), (wx, (y0 + y1) / 2, 2.5), M_WALL)
    box('lintel_%s' % tag, (0.6, 4.0, 1.5), (wx, 0.0, 4.25), M_WALL)
box('lintel_a_far', (0.6, 4.0, 1.5), (-25.0, 11.0, 4.25), M_WALL)
d = box('door_a', (0.4, 3.9, 3.4), (-25.0, 0.0, 1.7), M_DOOR, role='door')
d.bf2.door_axis = 'up'                      # team 1 spawns 3 m west of it
d = box('door_b', (0.4, 3.9, 3.4), (25.0, 0.0, 1.7), M_DOOR, role='door')
d.bf2.door_axis = 'left'                    # team 2 spawns 3 m east of it
d = box('door_a_far', (0.4, 3.9, 3.4), (-25.0, 11.0, 1.7), M_DOOR, role='door')
d.bf2.door_axis = 'up'                      # the control: 11 m from every spawn
for i, y in enumerate((-1.2, 0.0, 1.2)):
    for team, x, yaw in (('team1', -28.0, -90.0), ('team2', 28.0, 90.0)):
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
print('DOORTEST DONE', OUT, 'objects', len(sc.objects))
