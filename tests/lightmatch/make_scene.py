"""The light-match scene: everything that gives light in a Blender scene, each on grey patches of known reflectance, so
the game picture can be compared with Blender's own render number by number.

  ramp_sun    8 patches (reflectance 0.02 .. 0.8) in the open sun
  ramp_shade  the same 8 in the shadow of a wall: sky + bounce only
  bay_point / bay_spot / bay_area   closed rooms (no opening - the cameras are inside), one lamp each, mode Both
  bay_baked   the same point lamp, mode Baked
  bay_glow    a closed room lit by a glowing panel (emission strength 5)
  World       an HDRI written by this script: a sky gradient with four coloured markers at 25 degrees up
              (+X red, +Y green, -X blue, -Y yellow), so the panorama's direction can be read off a screenshot
  Sun         strength 3 W/m2, slightly warm, 50 degrees up
Spawns stand 30 m south of the patches so the fight stays out of the pictures.
Also writes <out>.views.json: the camera views (scene coordinates) both Blender and the game are photographed from.

    blender -b --factory-startup --python make_scene.py -- <out.blend> <addon dir> <runtime dir> [level name] [view transform]
"""
import json
import math
import os
import sys

import bmesh
import bpy
import numpy as np

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, ADDON, RUNTIME = argv[0], argv[1], argv[2]
NAME = argv[3] if len(argv) > 3 else 'LightMatch'
VIEW = argv[4] if len(argv) > 4 else 'Standard'
sys.path.insert(0, ADDON)
sys.path.insert(0, RUNTIME)
import props
from bf2maptool import hdr

bpy.ops.wm.read_factory_settings(use_empty=True)
props.register()
sc = bpy.context.scene
sc.bf2.name = NAME
sc.bf2.display_name = 'Light match'
sc.bf2.planet = 'Test'
sc.bf2.sky = 'world'
sc.bf2.fog_density = 0.0
sc.bf2.auto_spawns = False
sc.view_settings.view_transform = VIEW
sc.view_settings.look = 'None'
sc.view_settings.exposure = 0.0
sc.view_settings.gamma = 1.0
HERE = os.path.dirname(os.path.abspath(OUT))
os.makedirs(HERE, exist_ok=True)
REFL = (0.02, 0.05, 0.10, 0.18, 0.30, 0.45, 0.60, 0.80)
VIEWS = []


def flat(name, rgb, rough=1.0, emission=None, strength=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    p = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    p.inputs['Base Color'].default_value = (rgb[0], rgb[1], rgb[2], 1.0)
    p.inputs['Roughness'].default_value = rough
    p.inputs['Metallic'].default_value = 0.0
    for k in ('Specular IOR Level', 'Specular'):
        if k in p.inputs:
            p.inputs[k].default_value = 0.0
    if emission is not None:
        (p.inputs.get('Emission Color') or p.inputs['Emission']).default_value = (emission[0], emission[1], emission[2], 1.0)
        p.inputs['Emission Strength'].default_value = strength
        m.bf2.cls = 'emissive'
    m.bf2.cls_auto = False
    return m


def box(name, size, loc, mat):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bm.loops.layers.uv.new('UVMap')
    bmesh.ops.create_cube(bm, size=1.0, calc_uvs=True)
    for v in bm.verts:
        v.co.x *= size[0]; v.co.y *= size[1]; v.co.z *= size[2]
    bm.to_mesh(me); bm.free()
    me.materials.append(mat)
    o = bpy.data.objects.new(name, me)
    o.location = loc
    sc.collection.objects.link(o)
    o.bf2.role = 'static'
    return o


def view(name, at, look, kind, **kw):
    VIEWS.append(dict({'name': name, 'at': [float(x) for x in at], 'look': [float(x) for x in look], 'kind': kind}, **kw))


def down(name, centre, kind, **kw):
    """a view almost straight down onto a flat patch"""
    view(name, (centre[0] + 0.35, centre[1] - 0.35, centre[2] + 1.6), centre, kind, **kw)


# ---- world: sky gradient + four coloured markers
H, W = 512, 1024
v, u = np.mgrid[0:H, 0:W]
uu, vv = (u + 0.5) / W, (v + 0.5) / H
az, el = (0.5 - uu) * 2 * math.pi, (0.5 - vv) * math.pi
d = np.stack([-np.sin(az) * np.cos(el), np.cos(az) * np.cos(el), np.sin(el)], -1)          # worldbake.direction
t = np.clip(d[..., 2], 0.0, 1.0)[..., None] ** 0.5
sky = np.array([0.80, 0.85, 0.90]) * (1 - t) + np.array([0.25, 0.45, 0.90]) * t
sky = np.where(d[..., 2:3] < 0.0, np.array([0.10, 0.10, 0.10]), sky)
MARK = {'+X red': ((1, 0, 0), (8.0, 0.3, 0.3)), '+Y green': ((0, 1, 0), (0.3, 8.0, 0.3)),
        '-X blue': ((-1, 0, 0), (0.3, 0.3, 8.0)), '-Y yellow': ((0, -1, 0), (8.0, 8.0, 0.3))}
for tag, (hd, rgb) in MARK.items():
    c = np.array([hd[0] * math.cos(math.radians(25)), hd[1] * math.cos(math.radians(25)), math.sin(math.radians(25))])
    sky = np.where((d @ c)[..., None] > math.cos(math.radians(6.0)), np.array(rgb), sky)
hdr_path = os.path.join(HERE, NAME.lower() + '_world.hdr')
hdr.write(hdr_path, sky.astype(np.float32))
wd = bpy.data.worlds.new('LightMatchWorld'); wd.use_nodes = True
nt = wd.node_tree
bg = next(n for n in nt.nodes if n.type == 'BACKGROUND')
env = nt.nodes.new('ShaderNodeTexEnvironment')
env.image = bpy.data.images.load(hdr_path)
env.interpolation = 'Closest'
nt.links.new(env.outputs['Color'], bg.inputs['Color'])
bg.inputs['Strength'].default_value = 1.0
sc.world = wd

# ---- sun
sun = bpy.data.lights.new('sun', 'SUN'); sun.energy = 3.0; sun.color = (1.0, 0.95, 0.85); sun.angle = math.radians(0.53)
so = bpy.data.objects.new('sun', sun); so.rotation_euler = (math.radians(40.0), 0.0, math.radians(30.0))
sc.collection.objects.link(so)
sdir = np.array(so.rotation_euler.to_matrix() @ __import__('mathutils').Vector((0.0, 0.0, -1.0)))      # the way the light travels
to_sun = -sdir

# ---- ground and the two ramps
M_GROUND = flat('lm_ground', (0.18, 0.18, 0.18))
box('ground', (100.0, 110.0, 1.0), (0.0, 0.0, -0.5), M_GROUND)
M_REFL = [flat('lm_refl_%03d' % int(r * 100), (r, r, r)) for r in REFL]
for i, (r, m) in enumerate(zip(REFL, M_REFL)):
    x = -14.0 + 4.0 * i
    box('sun_%d' % i, (2.4, 2.4, 0.06), (x, 0.0, 0.03), m)
    down('sun_%d' % i, (x, 0.0, 0.06), 'ramp_sun', reflectance=r)
# the shade wall stands between the sun and the second ramp: its shadow runs away from the sun
hz = np.array([to_sun[0], to_sun[1], 0.0]); hz = hz / np.linalg.norm(hz)
perp = np.array([-hz[1], hz[0], 0.0])
base = np.array([0.0, 22.0, 0.0])
wall = box('shade_wall', (36.0, 0.6, 9.0), tuple(base + (0, 0, 4.5)), flat('lm_wall', (0.18, 0.18, 0.18)))
wall.rotation_euler = (0.0, 0.0, math.atan2(perp[1], perp[0]))
for i, (r, m) in enumerate(zip(REFL, M_REFL)):
    c = base - hz * 2.2 + perp * (-14.0 + 4.0 * i)
    b = box('shade_%d' % i, (2.4, 2.4, 0.06), (c[0], c[1], 0.03), m)
    b.rotation_euler = wall.rotation_euler
    down('shade_%d' % i, (c[0], c[1], 0.06), 'ramp_shade', reflectance=r)

# ---- closed bays: 8 x 8 x 4 m inside. Walls nearly black (0.02) so a patch shows the lamp's DIRECT light almost alone
# (Blender's reference still includes what little bounces); a 0.45 patch on the floor and one on the north wall.
M_BAY = flat('lm_bay', (0.02, 0.02, 0.02))
M_PATCH = flat('lm_patch', (0.45, 0.45, 0.45))


def bay(tag, cx, cy, wall_patch=True):
    box('bay_%s_floor' % tag, (9.0, 9.0, 0.5), (cx, cy, 0.25), M_BAY)
    box('bay_%s_roof' % tag, (9.0, 9.0, 0.5), (cx, cy, 4.75), M_BAY)
    for nm, size, off in (('n', (9.0, 0.5, 4.0), (0, 4.25, 2.5)), ('s', (9.0, 0.5, 4.0), (0, -4.25, 2.5)),
                          ('w', (0.5, 8.0, 4.0), (-4.25, 0, 2.5)), ('e', (0.5, 8.0, 4.0), (4.25, 0, 2.5))):
        box('bay_%s_%s' % (tag, nm), size, (cx + off[0], cy + off[1], off[2]), M_BAY)
    box('bay_%s_patch' % tag, (2.4, 2.4, 0.06), (cx, cy - 1.5, 0.53), M_PATCH)
    down('bay_%s_floor' % tag, (cx, cy - 1.5, 0.56), 'bay', bay=tag, reflectance=0.45)
    if wall_patch:
        box('bay_%s_wpatch' % tag, (2.4, 0.06, 2.4), (cx, 3.97 + cy, 2.2), M_PATCH)
        view('bay_%s_wall' % tag, (cx, cy + 2.2, 2.2), (cx, cy + 3.94, 2.2), 'bay', bay=tag, reflectance=0.45)
    return (cx, cy)


def lamp(name, kind, loc, watts, mode, **kw):
    d = bpy.data.lights.new(name, kind); d.energy = watts; d.color = (1.0, 1.0, 1.0)
    d.bf2.mode = mode
    d.shadow_soft_size = 0.0
    d.use_custom_distance = True; d.cutoff_distance = 30.0
    for k, v in kw.items():
        setattr(d, k, v)
    o = bpy.data.objects.new(name, d); o.location = loc
    sc.collection.objects.link(o)
    return o


BX = (-35.0, -25.0, -15.0, -5.0, 5.0, 15.0, 25.0, 35.0)
for k, mode in enumerate(('dynamic', 'baked')):
    cx, cy = bay('point_' + mode, BX[k], 42.0); lamp('lamp_point_' + mode, 'POINT', (cx, cy, 3.6), 100.0, mode)
    cx, cy = bay('spot_' + mode, BX[2 + k], 42.0, wall_patch=False)
    lamp('lamp_spot_' + mode, 'SPOT', (cx, cy - 1.5, 3.9), 100.0, mode, spot_size=math.radians(90.0), spot_blend=0.15)      # straight down on the patch
    cx, cy = bay('area_' + mode, BX[4 + k], 42.0, wall_patch=False)
    lamp('lamp_area_' + mode, 'AREA', (cx, cy - 1.5, 3.9), 100.0, mode, shape='SQUARE', size=1.0)                            # faces down
cx, cy = bay('glow', BX[6], 42.0)
M_GLOW = flat('lm_glow', (0.0, 0.0, 0.0), emission=(1.0, 1.0, 1.0), strength=5.0)
box('bay_glow_panel', (3.0, 3.0, 0.1), (cx, cy, 4.4), M_GLOW)
view('bay_glow_panel', (cx, cy - 2.5, 1.2), (cx, cy, 4.35), 'glow_direct', strength=5.0)
# three small panels of low strength in a lampless room: their own brightness, in the range the sunlit ramp covers
cx, cy = bay('panels', BX[7], 42.0, wall_patch=False)
# black base (what a Blender emitter usually has) and white base, to see whether the game's glow depends on the base colour
for k, (st, base) in enumerate(((0.2, 0.0), (1.0, 0.0), (0.2, 0.8), (1.0, 0.8))):
    m = flat('lm_glow_%03d_%d' % (int(st * 100), int(base * 10)), (base, base, base), emission=(1.0, 1.0, 1.0), strength=st)
    px = cx - 3.0 + 2.0 * k
    box('panel_%d' % k, (1.4, 0.1, 1.4), (px, cy + 3.9, 2.2), m)
    view('panel_%d' % k, (px, cy + 2.6, 2.2), (px, cy + 3.85, 2.2), 'glow_direct', strength=st, base=base)

# ---- sky views (from 3 m up, well away from the wall) and an overview for people
for tag, (hd, rgb) in MARK.items():
    look = (hd[0] * 100.0, -40.0 + hd[1] * 100.0, 3.0 + 100.0 * math.tan(math.radians(25)))
    view('sky_' + tag.split()[0].replace('+', 'p').replace('-', 'm'), (0.0, -40.0, 3.0), look, 'sky_marker', expect=tag, rgb=list(rgb))
view('sky_up', (0.0, -40.0, 3.0), (0.3, -39.7, 103.0), 'sky_plain')
view('overview', (-26.0, -24.0, 9.0), (0.0, 12.0, 1.0), 'picture')
view('overview2', (24.0, -14.0, 5.0), (-4.0, 22.0, 2.0), 'picture')

for i, y in enumerate((-46.0, -44.0, -42.0)):
    for team, x, yaw in (('team1', -36.0, -90.0), ('team2', 36.0, 90.0)):
        o = bpy.data.objects.new('spawn_%s_%d' % (team, i), None)
        o.empty_display_type = 'SINGLE_ARROW'
        o.location = (x, y, 0.0)
        o.rotation_euler = (math.radians(90.0), 0.0, math.radians(yaw))
        sc.collection.objects.link(o)
        o.bf2.role, o.bf2.team = 'spawn', team
bpy.ops.wm.save_as_mainfile(filepath=OUT)
info = {'views': VIEWS, 'reflectances': list(REFL), 'sun': {'strength': 3.0, 'color': [1.0, 0.95, 0.85], 'to_sun': [float(x) for x in to_sun]},
        'world_hdr': hdr_path.replace('\\', '/'), 'view_transform': VIEW}
json.dump(info, open(OUT + '.views.json', 'w'), indent=1)
print('LIGHTMATCH DONE', OUT, 'objects', len(sc.objects), 'views', len(VIEWS))
