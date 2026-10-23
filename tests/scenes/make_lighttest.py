"""Check scene for the three light modes: four roofed bays side by side, open to the south, under a night sky, no sun.
  bay_baked    one 1000 W point lamp, mode Baked    - lit only through the light bake (lightmap + probes)
  bay_dynamic  the same lamp, mode Dynamic          - lit only by a real-time light
  bay_both     the same lamp, mode Both             - in the bake AND a real-time light
  bay_none     no lamp                              - the control: what the sky alone gives
The same lamp in the same room: the floor brightness of the four bays, measured from the pictures
(lighttest_measure.py), is the conversion check - Baked and Dynamic should come out alike, Both about their sum.
    blender -b --factory-startup --python make_lighttest.py -- <out.blend> [level name] [watts] [bays] [lamp reach m]
bays = mode:watts,... (e.g. baked:1000,dynamic:60,dynamic:125,none:0) for a calibration row; the bay list is also
written to <out>.bays.json for the shots and the measurement.
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
NAME = argv[1] if len(argv) > 1 else 'LightTest'
bpy.ops.wm.read_factory_settings(use_empty=True)
props.register()
sc = bpy.context.scene
sc.bf2.name = NAME
sc.bf2.display_name = 'Light test'
sc.bf2.planet = 'Test'
sc.bf2.sky = 'night'                   # as little sky light as possible: the bays are lit by their lamps
sc.bf2.sun_on = False
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


WATTS = float(argv[2]) if len(argv) > 2 else 1000.0
M_FLOOR = material('lt_floor', image('lt_floor_c', checker(512, (0.42, 0.42, 0.42), (0.36, 0.36, 0.36), 8)))
M_WALL = material('lt_wall', image('lt_wall_c', checker(512, (0.50, 0.50, 0.50), (0.44, 0.44, 0.44), 4)), rough=0.8)
box('floor', (96.0, 48.0, 1.0), (0.0, 0.0, -0.5), M_FLOOR)
SPEC = argv[3] if len(argv) > 3 else 'baked:%g,dynamic:%g,both:%g,none:0' % (WATTS, WATTS, WATTS)
spec = [(x.split(':')[0], float(x.split(':')[1])) for x in SPEC.split(',')]
BAYS = []
for k, (mode, watts) in enumerate(spec):
    BAYS.append({'tag': '%s_%d' % (mode, int(watts)) if len(argv) > 3 else mode, 'mode': mode, 'watts': watts, 'cx': 20.0 * (k - (len(spec) - 1) / 2)})
bpy.data.objects.remove(bpy.data.objects['floor'])
box('floor', (20.0 * len(spec) + 40.0, 48.0, 1.0), (0.0, 0.0, -0.5), M_FLOOR)
for b in BAYS:
    tag, cx = b['tag'], b['cx']
    box('bay_%s_n' % tag, (13.0, 0.5, 5.0), (cx, 14.25, 2.5), M_WALL)
    box('bay_%s_w' % tag, (0.5, 12.0, 5.0), (cx - 6.25, 8.0, 2.5), M_WALL)
    box('bay_%s_e' % tag, (0.5, 12.0, 5.0), (cx + 6.25, 8.0, 2.5), M_WALL)
    box('bay_%s_roof' % tag, (13.0, 13.0, 0.5), (cx, 8.0, 5.25), M_WALL)
    if b['mode'] != 'none':
        d = bpy.data.lights.new('lamp_' + tag, 'POINT')
        d.energy = b['watts']; d.color = (1.0, 1.0, 1.0)
        if len(argv) > 4:                      # the same reach for every lamp, so only the strength differs
            d.use_custom_distance = True; d.cutoff_distance = float(argv[4])
        d.bf2.mode = b['mode']
        o = bpy.data.objects.new('lamp_' + tag, d)
        o.location = (cx, 8.0, 3.5)
        sc.collection.objects.link(o)
import json
with open(OUT + '.bays.json', 'w') as f:
    json.dump(BAYS, f, indent=1)
SX = 10.0 * len(spec)
for i, y in enumerate((-16.0, -14.0, -12.0)):
    for team, x, yaw in (('team1', -SX, -90.0), ('team2', SX, 90.0)):
        o = bpy.data.objects.new('spawn_%s_%d' % (team, i), None)
        o.empty_display_type = 'SINGLE_ARROW'
        o.location = (x, y, 0.0)
        o.rotation_euler = (math.radians(90.0), 0.0, math.radians(yaw))
        sc.collection.objects.link(o)
        o.bf2.role, o.bf2.team = 'spawn', team
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=OUT)
print('LIGHTTEST DONE', OUT, 'objects', len(sc.objects), 'watts', WATTS)
