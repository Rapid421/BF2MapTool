"""Check scene for the glass class: a glass wall across a hall with a red pillar and a blue pillar behind it.
Seen from the south the pillars must show THROUGH the pane; a pillar beside the pane (not behind it) is the reference.
The pane is solid (collision Exact): team 2 behind it reaches team 1 only around its ends.
    blender -b --factory-startup --python make_glasstest.py -- <out.blend> [level name]
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
NAME = argv[1] if len(argv) > 1 else 'GlassTest'
bpy.ops.wm.read_factory_settings(use_empty=True)
props.register()
sc = bpy.context.scene
sc.bf2.name = NAME
sc.bf2.display_name = 'Glass test'
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


M_FLOOR = material('gt_floor', image('gt_floor_c', checker(512, (0.34, 0.35, 0.36), (0.26, 0.27, 0.28), 8)))
M_RED = material('gt_red', image('gt_red_c', checker(256, (0.70, 0.12, 0.10), (0.55, 0.10, 0.08), 4)))
M_BLUE = material('gt_blue', image('gt_blue_c', checker(256, (0.10, 0.20, 0.70), (0.08, 0.16, 0.55), 4)))
M_GLASS = material('gt_glass', image('gt_glass_c', checker(64, (0.75, 0.85, 0.90), (0.72, 0.82, 0.88), 2)), rough=0.05)
M_GLASS.bf2.cls = 'glass'
box('floor', (70.0, 40.0, 1.0), (0.0, 0.0, -0.5), M_FLOOR)
box('glass_wall', (16.0, 0.12, 3.5), (0.0, 0.0, 1.75), M_GLASS)
box('pillar_red_behind', (1.5, 1.5, 3.0), (-3.0, 5.0, 1.5), M_RED)
box('pillar_blue_behind', (1.5, 1.5, 3.0), (3.0, 5.0, 1.5), M_BLUE)
box('pillar_red_beside', (1.5, 1.5, 3.0), (12.0, 5.0, 1.5), M_RED)
for i, y in enumerate((-1.5, 0.0, 1.5)):
    for team, x, yaw in (('team1', -26.0, -90.0), ('team2', 26.0, 90.0)):
        o = bpy.data.objects.new('spawn_%s_%d' % (team, i), None)
        o.empty_display_type = 'SINGLE_ARROW'
        o.location = (x, y - 8.0 if team == 'team1' else y + 8.0, 0.0)
        o.rotation_euler = (math.radians(90.0), 0.0, math.radians(yaw))
        sc.collection.objects.link(o)
        o.bf2.role, o.bf2.team = 'spawn', team
sun = bpy.data.lights.new('sun', 'SUN'); sun.energy = 3.0
so = bpy.data.objects.new('sun', sun); so.rotation_euler = (math.radians(40.0), 0.0, math.radians(200.0))
sc.collection.objects.link(so)
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=OUT)
print('GLASSTEST DONE', OUT, 'objects', len(sc.objects))
