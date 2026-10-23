"""Build the tool's own small test map from nothing (no port data, no other game): an 80 x 60 m arena with a raised
platform and ramp, crates, a wall with a doorway and a sliding door, a cut-out grate over a pit with a kill volume
under it, an emissive strip, four materials with generated (packed) textures incl. an OpenGL normal map, point lights
in the three light modes, a few hand-placed spawns. Everything is tagged with the tool's own properties, as a user
would do in the panel.

    blender -b --factory-startup --python make_arena.py -- <out.blend> [level name]
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
NAME = argv[1] if len(argv) > 1 else 'ToolArena'
SUPREMACY = len(argv) > 2 and argv[2] == 'sup'      # + five command posts and the Supremacy mode ticked
bpy.ops.wm.read_factory_settings(use_empty=True)
props.register()
sc = bpy.context.scene
sc.bf2.name = NAME
sc.bf2.display_name = 'Tool Test Arena'
sc.bf2.planet = 'Test'
sc.bf2.sky = 'clear'                    # a clear day: the sun is strong enough to read the normal-map calibration slabs
sc.bf2.description = 'The BF2 Map Tool\'s own test arena.'
rng = np.random.default_rng(3)


def image(name, rgba, colorspace='sRGB'):
    h, w = rgba.shape[:2]
    img = bpy.data.images.new(name, w, h, alpha=True)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(np.ascontiguousarray(rgba[::-1], np.float32).ravel())
    img.pack()
    return img


def material(name, cls, base=None, normal=None, rough=0.6, metal=0.0, emission=None, strength=0.0, alpha_from_base=False):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    p = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
    p.inputs['Roughness'].default_value = rough
    p.inputs['Metallic'].default_value = metal
    if base is not None:
        t = nt.nodes.new('ShaderNodeTexImage'); t.image = base
        nt.links.new(t.outputs['Color'], p.inputs['Base Color'])
        if alpha_from_base:
            nt.links.new(t.outputs['Alpha'], p.inputs['Alpha'])
    if normal is not None:
        t = nt.nodes.new('ShaderNodeTexImage'); t.image = normal
        nm = nt.nodes.new('ShaderNodeNormalMap')
        nt.links.new(t.outputs['Color'], nm.inputs['Color'])
        nt.links.new(nm.outputs['Normal'], p.inputs['Normal'])
    if emission is not None:
        (p.inputs.get('Emission Color') or p.inputs.get('Emission')).default_value = (*emission, 1.0)
        p.inputs['Emission Strength'].default_value = strength
    m.bf2.cls = cls
    return m


def checker(n=512, a=(0.32, 0.33, 0.35), b=(0.22, 0.23, 0.25), cells=8):
    y, x = np.mgrid[0:n, 0:n]
    k = ((x * cells // n) + (y * cells // n)) % 2
    img = np.where(k[..., None] == 0, np.array(a), np.array(b)).astype(np.float32)
    img += rng.normal(0, 0.012, img.shape).astype(np.float32)
    return np.dstack([np.clip(img, 0, 1), np.ones((n, n), np.float32)])


def bumps_gl(n=512, cells=4):
    """an OpenGL-convention normal map of raised DOMES, one per cell (green = +dh/dv with v up the image): lit from
    above, a dome is bright on its upper side; read with the wrong green sign it looks like a dent"""
    y, x = np.mgrid[0:n, 0:n].astype(np.float32)
    c = n / cells
    dx0 = (x % c) - c / 2; dy0 = (y % c) - c / 2
    r = c * 0.36
    h = np.sqrt(np.clip(r * r - dx0 * dx0 - dy0 * dy0, 0.0, None)) / r * 0.35
    dx = np.gradient(h, axis=1) * 40.0
    dy = np.gradient(h, axis=0) * 40.0         # image rows run DOWN: +row = -v
    nx, ny, nz = -dx, dy, np.ones_like(h)       # OpenGL: n.y = -dh/dv = +dh/drow
    l = np.sqrt(nx * nx + ny * ny + nz * nz)
    return np.dstack([nx / l * 0.5 + 0.5, ny / l * 0.5 + 0.5, nz / l * 0.5 + 0.5, np.ones_like(h)])


def grate(n=256):
    y, x = np.mgrid[0:n, 0:n]
    bar = ((x % 32) < 8) | ((y % 32) < 8)
    rgb = np.where(bar[..., None], np.array([0.25, 0.25, 0.27]), np.array([0.0, 0.0, 0.0])).astype(np.float32)
    return np.dstack([rgb, bar.astype(np.float32)])


M_FLOOR = material('arena_floor', 'opaque', image('arena_floor_c', checker()), rough=0.55)
M_WALL = material('arena_wall', 'opaque', image('arena_wall_c', checker(512, (0.42, 0.36, 0.30), (0.36, 0.31, 0.26), 4)),
                  image('arena_wall_n', bumps_gl(), 'Non-Color'), rough=0.8)
M_STRIP = material('arena_strip', 'emissive', image('arena_strip_c', checker(64, (0.9, 0.9, 0.85), (0.8, 0.8, 0.75), 2)), rough=0.4,
                   emission=(1.0, 0.8, 0.5), strength=5.0)
M_GRATE = material('arena_grate', 'cutout', image('arena_grate_c', grate()), rough=0.5, alpha_from_base=True)
M_DOOR = material('arena_door', 'opaque', image('arena_door_c', checker(256, (0.25, 0.30, 0.38), (0.20, 0.24, 0.30), 2)), rough=0.4, metal=0.8)


def box(name, size, loc, mat, rot_z=0.0, role='static', collision='exact'):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0, calc_uvs=True)
    for v in bm.verts:
        v.co.x *= size[0]; v.co.y *= size[1]; v.co.z *= size[2]
    uv = bm.loops.layers.uv.verify()
    for f in bm.faces:                               # texel density: 1 uv unit = 4 m, from the face's own plane
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        u_ax, v_ax = [(1, 2), (0, 2), (0, 1)][ax]
        for l in f.loops:
            l[uv].uv = (l.vert.co[u_ax] / 4.0, l.vert.co[v_ax] / 4.0)
    bm.to_mesh(me); bm.free()
    me.materials.append(mat)
    o = bpy.data.objects.new(name, me)
    o.location = loc
    o.rotation_euler = (0.0, 0.0, rot_z)
    sc.collection.objects.link(o)
    o.bf2.role, o.bf2.collision = role, collision
    return o


# floors: two slabs with a 6 m pit between them, bridged by a cut-out grate
box('floor_west', (37.0, 60.0, 1.0), (-21.5, 0.0, -0.5), M_FLOOR)
box('floor_east', (37.0, 60.0, 1.0), (21.5, 0.0, -0.5), M_FLOOR)
box('pit_bottom', (6.0, 60.0, 1.0), (0.0, 0.0, -8.5), M_FLOOR)
g = box('grate_bridge', (6.4, 8.0, 0.1), (0.0, 0.0, -0.05), M_GRATE)
box('bridge_south', (6.4, 6.0, 1.0), (0.0, -20.0, -0.5), M_FLOOR)
# perimeter walls
for nm, size, loc in (('wall_n', (80.0, 1.0, 5.0), (0.0, 30.5, 2.5)), ('wall_s', (80.0, 1.0, 5.0), (0.0, -30.5, 2.5)),
                      ('wall_w', (1.0, 62.0, 5.0), (-40.5, 0.0, 2.5)), ('wall_e', (1.0, 62.0, 5.0), (40.5, 0.0, 2.5))):
    box(nm, size, loc, M_WALL)
# a raised platform with a ramp (slope 22 deg)
box('platform', (12.0, 12.0, 2.0), (-24.0, 14.0, 1.0), M_FLOOR)
r = box('ramp', (5.4, 4.0, 0.3), (-15.6, 14.0, 0.92), M_FLOOR)
r.rotation_euler = (0.0, math.radians(21.8), 0.0)
# crates, one of them mirrored (negative scale) and one with an unapplied scale: both must come out right
for i, (x, y) in enumerate(((10.0, 10.0), (12.5, 10.0), (11.0, 12.5), (25.0, -12.0), (-12.0, -15.0), (28.0, 18.0))):
    c = box('crate_%d' % i, (2.0, 2.0, 2.0), (x, y, 1.0), M_WALL, rot_z=0.3 * i)
    if i == 3:
        c.scale = (-1.0, 1.0, 1.0)
    if i == 4:
        c.scale = (1.5, 1.5, 1.5); c.location.z = 1.5
# an inner wall with a doorway and a door that slides up
box('inner_wall_a', (1.0, 24.0, 5.0), (20.0, -17.0, 2.5), M_WALL)
box('inner_wall_b', (1.0, 30.0, 5.0), (20.0, 14.0, 2.5), M_WALL)
box('inner_wall_top', (1.0, 4.0, 1.5), (20.0, -3.0, 4.25), M_WALL)
d = box('door_main', (0.4, 3.9, 3.4), (20.0, -3.0, 1.7), M_DOOR, role='door')
d.bf2.door_axis = 'up'
# light strips: emissive, no collision
for i, y in enumerate((-20.0, 0.0, 20.0)):
    box('strip_%d' % i, (0.2, 6.0, 0.4), (39.8, y, 4.0), M_STRIP, collision='none')
# gameplay markers
kv = box('Kill pit', (6.0, 60.0, 4.0), (0.0, 0.0, -6.0), M_FLOOR, role='kill_volume')
kv.display_type = 'WIRE'; kv.hide_render = True


def empty(name, loc, role, team, yaw=0.0):
    o = bpy.data.objects.new(name, None)
    o.empty_display_type = 'SINGLE_ARROW'
    o.location = loc
    o.rotation_euler = (math.radians(90.0), 0.0, yaw)
    sc.collection.objects.link(o)
    o.bf2.role, o.bf2.team = role, team
    return o


# team 1 stands 2 m off the calibration slabs (y -16): a spawn at (-34, -16) sat in the end of calib_auto_a (check spawn.in_geometry)
for i in range(3):
    empty('spawn_t1_%d' % i, (-34.0, -22.0 + 4.0 * i, 0.0), 'spawn', 'team1', math.radians(-90.0))
    empty('spawn_t2_%d' % i, (34.0, 18.0 - 4.0 * i, 0.0), 'spawn', 'team2', math.radians(90.0))
# normal-map calibration: free-standing slabs facing south into the sun, a pole whose shadow shows where the sun is.
# Result of the first run (2026-10-12, review/t4): green UP reads as raised domes on the plain and on the mirrored
# face, so the packer now keeps OpenGL maps. `calib_auto_*` = the tool by itself (must be raised); `calib_keep_*` =
# the same map declared DirectX, so its green is flipped (must read as dents - the control). Of each pair one slab
# is turned half round, so its sunlit face is the one with MIRRORED UVs.
# Second finding of that run: on the opaque template the relief only shows where the UVs lie inside the 0..1 square
# (the colour tiles, the normal map does not). `calib_emis` / `calib_cut` carry the same dome map on the emissive and
# the cut-out template to see whether THEIR normal map tiles.
M_WALL_KEEP = material('arena_wall_keepgreen', 'opaque', image('arena_wallk_c', checker(512, (0.30, 0.36, 0.44), (0.25, 0.30, 0.38), 4)),
                       bpy.data.images['arena_wall_n'], rough=0.8)
M_WALL_KEEP.bf2.normal_format = 'directx'
for nm, x, mat, rot in (('calib_auto_a', -36.0, M_WALL, 0.0), ('calib_auto_b', -31.0, M_WALL, math.pi),
                        ('calib_keep_a', -26.0, M_WALL_KEEP, 0.0), ('calib_keep_b', -21.0, M_WALL_KEEP, math.pi)):
    box(nm, (4.0, 0.5, 3.0), (x, -16.0, 1.5), mat, rot_z=rot)
box('calib_pole', (0.2, 0.2, 3.0), (-28.5, -20.0, 1.5), M_FLOOR)
M_CAL_E = material('arena_cal_emis', 'emissive', image('arena_cale_c', checker(512, (0.40, 0.30, 0.30), (0.33, 0.25, 0.25), 4)),
                   bpy.data.images['arena_wall_n'], rough=0.8, emission=(0.02, 0.02, 0.02), strength=0.05)


def holes(n=512):
    """the wall checker with a small see-through square in the corner of every cell"""
    rgb = checker(n, (0.30, 0.40, 0.30), (0.25, 0.33, 0.25), 4)[..., :3]
    y, x = np.mgrid[0:n, 0:n]
    c = n // 4
    a = ~(((x % c) < c // 8) & ((y % c) < c // 8))
    return np.dstack([rgb, a.astype(np.float32)])


M_CAL_C = material('arena_cal_cut', 'cutout', image('arena_calc_c', holes()), bpy.data.images['arena_wall_n'], rough=0.8, alpha_from_base=True)
box('calib_emis', (4.0, 0.5, 3.0), (6.0, -22.0, 1.5), M_CAL_E)
M_CAL_2 = material('arena_cal_2uv', 'opaque', image('arena_cal2_c', checker(512, (0.40, 0.40, 0.28), (0.33, 0.33, 0.23), 4)),
                   bpy.data.images['arena_wall_n'], rough=0.8)
M_CAL_2.bf2.template = 'opaque2'                 # the 2UV array preset: does ITS normal map tile?
box('calib_2uv', (4.0, 0.5, 3.0), (16.0, -22.0, 1.5), M_CAL_2)
box('calib_cut', (4.0, 0.5, 3.0), (11.0, -22.0, 1.5), M_CAL_C)
# lights in the three modes
for nm, loc, col, w, mode in (('light_baked', (-24.0, 14.0, 6.0), (1.0, 0.9, 0.8), 800.0, 'baked'), ('light_dynamic', (30.0, -20.0, 4.0), (0.6, 0.7, 1.0), 400.0, 'dynamic'),
                             ('light_both', (10.0, 10.0, 5.0), (1.0, 1.0, 1.0), 600.0, 'both'), ('light_baked_2', (-10.0, -20.0, 5.0), (1.0, 0.95, 0.9), 800.0, 'baked')):
    ld = bpy.data.lights.new(nm, 'POINT')
    ld.color, ld.energy = col, w
    ld.bf2.mode = mode
    lo = bpy.data.objects.new(nm, ld)
    lo.location = loc
    sc.collection.objects.link(lo)
sd = bpy.data.lights.new('light_spot_down', 'SPOT')
sd.color, sd.energy, sd.spot_size, sd.spot_blend = (1.0, 0.2, 0.2), 2000.0, math.radians(50.0), 0.2
sd.bf2.mode = 'dynamic'
so2 = bpy.data.objects.new('light_spot_down', sd)
so2.location = (-30.0, -8.0, 7.0)                  # a Blender spot shines along its local -Z: straight down
sc.collection.objects.link(so2)
if SUPREMACY:
    sc.bf2.mode_supremacy = True
    for letter, loc, team in (('A', (-30.0, 12.0, 0.0), 'team1'), ('B', (-16.0, -10.0, 0.0), 'team1'), ('C', (0.0, -20.0, 0.0), 'neutral'),
                              ('D', (14.0, 12.0, 0.0), 'team2'), ('E', (32.0, -12.0, 0.0), 'team2')):
        cp = empty('cp_' + letter, loc, 'command_post', team if team in ('team1', 'team2') else 'team1')
        cp.bf2.cp_letter = letter
        cp.bf2.cp_radius = 6.0
sun = bpy.data.lights.new('sun', 'SUN'); sun.energy = 3.0
so = bpy.data.objects.new('sun', sun); so.rotation_euler = (math.radians(50.0), 0.0, math.radians(30.0))
sc.collection.objects.link(so)
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=OUT)
print('ARENA DONE', OUT, 'objects', len(sc.objects), 'materials', len(bpy.data.materials), 'images', len(bpy.data.images))
