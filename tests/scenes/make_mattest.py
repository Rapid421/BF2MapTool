"""MatTest: one row of test objects per material property, for Blender-against-game comparison (PM-2).
    blender -b --factory-startup --python make_mattest.py -- <out.blend> <addon dir>
Writes <out.blend>, <out.blend>.views.json (one camera per row: at / look, the lightmatch format) and
<out.blend>.panels.json (every object: row, centre, what it tests).

  smooth    grey 0.5 spheres, roughness 1.0 / 0.6 / 0.3 / 0.0
  metal     gold r 0.3, steel r 0.15, the same grey NON-metal r 0.3, gold r 0.8
  colour    boxes white 0.8, grey 0.18, red, blue
  normal    a box with a dome normal map (OpenGL), the same box flat, a box with a Bump node, a box whose map is DirectX
  emission  red strength 1, red 5, white 20, cyan 5
  alpha     cut-out yellow checker (flat colour: any cloth / detail pattern is the tool's), cut-out double-sided
            ("leaf" name), emissive cut-out (cyan glow + holes), glass
"""
import json
import math
import sys

import bpy
import numpy as np

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, ADDON = argv[0], argv[1]
sys.path.insert(0, ADDON)
import props  # noqa: E402

bpy.ops.wm.read_factory_settings(use_empty=True)
props.register()
sc = bpy.context.scene
sc.bf2.name = 'MatTest'
sc.bf2.sky = 'world'
sc.bf2.mode_hvv = False
sc.view_settings.view_transform = 'Standard'
w = bpy.data.worlds.new('MatSky'); w.use_nodes = True
w.node_tree.nodes['Background'].inputs['Color'].default_value = (0.32, 0.42, 0.6, 1.0)
w.node_tree.nodes['Background'].inputs['Strength'].default_value = 1.0
sc.world = w
sun = bpy.data.lights.new('Sun', 'SUN'); sun.energy = 3.0; sun.angle = math.radians(1.0)
so = bpy.data.objects.new('Sun', sun); so.rotation_euler = (math.radians(40), 0, math.radians(-30)); sc.collection.objects.link(so)


def principled(name, base=(0.5, 0.5, 0.5), rough=0.5, metal=0.0, emit=None, strength=0.0, alpha_img=None, normal_img=None, bump=False):
    m = bpy.data.materials.new(name); m.use_nodes = True
    N, L = m.node_tree.nodes, m.node_tree.links
    p = N['Principled BSDF']
    p.inputs['Base Color'].default_value = (*base, 1.0)
    p.inputs['Roughness'].default_value = rough
    p.inputs['Metallic'].default_value = metal
    if emit is not None:
        p.inputs['Emission Color'].default_value = (*emit, 1.0)
        p.inputs['Emission Strength'].default_value = strength
    if alpha_img is not None:
        t = N.new('ShaderNodeTexImage'); t.image = alpha_img
        L.new(t.outputs['Color'], p.inputs['Base Color']); L.new(t.outputs['Alpha'], p.inputs['Alpha'])
    if normal_img is not None:
        t = N.new('ShaderNodeTexImage'); t.image = normal_img; t.image.colorspace_settings.name = 'Non-Color'
        nm = N.new('ShaderNodeNormalMap'); L.new(t.outputs['Color'], nm.inputs['Color']); L.new(nm.outputs['Normal'], p.inputs['Normal'])
    if bump:
        tx = N.new('ShaderNodeTexChecker'); tx.inputs['Scale'].default_value = 6.0
        b = N.new('ShaderNodeBump'); b.inputs['Strength'].default_value = 0.8
        L.new(tx.outputs['Fac'], b.inputs['Height']); L.new(b.outputs['Normal'], p.inputs['Normal'])
    return m


def img(name, px):
    h, w_ = px.shape[:2]
    i = bpy.data.images.new(name, w_, h, alpha=True)
    i.pixels.foreach_set(px.astype(np.float32).ravel()); i.pack()
    return i


# dome normal map (tangent space, OpenGL = green up) and the same with green flipped (DirectX)
n = 256
yy, xx = (np.mgrid[0:n, 0:n] + 0.5) / n * 4.0 % 1.0 - 0.5
r2 = xx ** 2 + yy ** 2
inside = r2 < 0.16
nz = np.sqrt(np.clip(0.16 - r2, 0, None))
v = np.stack([np.where(inside, xx, 0), np.where(inside, yy, 0), np.where(inside, nz, 1.0)], -1)
v /= np.linalg.norm(v, axis=-1, keepdims=True)
gl = np.dstack([v * 0.5 + 0.5, np.ones((n, n, 1))])
dx = gl.copy(); dx[..., 1] = 1.0 - dx[..., 1]
dome_gl, dome_dx = img('dome_normal_gl', gl), img('dome_normal_dx', dx)
# yellow with checker holes
c = np.ones((64, 64, 4)); c[..., :3] = (0.9, 0.75, 0.1)
c[..., 3] = ((np.add.outer(np.arange(64) // 8, np.arange(64) // 8) % 2) == 0).astype(float)
holes = img('holes_yellow', c)
c2 = c.copy(); c2[..., :3] = (0.1, 0.9, 0.9)
holes_cyan = img('holes_cyan', c2)

ROWS = [
    ('smooth', 'sphere', [('rough_1.0', dict(rough=1.0)), ('rough_0.6', dict(rough=0.6)), ('rough_0.3', dict(rough=0.3)), ('rough_0.0', dict(rough=0.0))]),
    ('metal', 'sphere', [('gold_r0.3', dict(base=(1.0, 0.766, 0.336), metal=1.0, rough=0.3)), ('steel_r0.15', dict(base=(0.56, 0.57, 0.58), metal=1.0, rough=0.15)),
                         ('grey_nonmetal_r0.3', dict(base=(0.56, 0.57, 0.58), metal=0.0, rough=0.3)), ('gold_r0.8', dict(base=(1.0, 0.766, 0.336), metal=1.0, rough=0.8))]),
    ('colour', 'box', [('white_0.8', dict(base=(0.8, 0.8, 0.8), rough=0.7)), ('grey_0.18', dict(base=(0.18, 0.18, 0.18), rough=0.7)),
                       ('red', dict(base=(0.8, 0.05, 0.05), rough=0.7)), ('blue', dict(base=(0.05, 0.1, 0.8), rough=0.7))]),
    ('normal', 'box', [('dome_opengl', dict(normal_img=dome_gl, rough=0.6)), ('flat_control', dict(rough=0.6)), ('bump_node', dict(bump=True, rough=0.6)),
                       ('dome_directx', dict(normal_img=dome_dx, rough=0.6))]),
    ('emission', 'box', [('red_e1', dict(base=(0.2, 0.2, 0.2), emit=(1, 0.1, 0.05), strength=1.0)), ('red_e5', dict(base=(0.2, 0.2, 0.2), emit=(1, 0.1, 0.05), strength=5.0)),
                         ('white_e20', dict(base=(0.2, 0.2, 0.2), emit=(1, 1, 1), strength=20.0)), ('cyan_e5', dict(base=(0.2, 0.2, 0.2), emit=(0.1, 0.9, 0.9), strength=5.0))]),
    ('alpha', 'box', [('cutout_yellow', dict(alpha_img=holes, rough=0.7)), ('leaf_cutout_2sided', dict(alpha_img=holes, rough=0.7)),
                      ('emissive_cutout_cyan', dict(alpha_img=holes_cyan, emit=(0.1, 0.9, 0.9), strength=5.0)), ('glass_pane', dict(rough=0.05))]),
    ('probe', 'box', [('probe_emissive_cutout', dict(alpha_img=holes, base=(0.9, 0.4, 0.1), rough=0.5))]),
    # rough grey metals: where single-scatter GGX loses the most (PM-5, the multiscatter lift)
    ('metal2', 'sphere', [('steel_r0.5', dict(base=(0.56, 0.57, 0.58), metal=1.0, rough=0.5)), ('steel_r0.8', dict(base=(0.56, 0.57, 0.58), metal=1.0, rough=0.8)),
                          ('steel_r1.0', dict(base=(0.56, 0.57, 0.58), metal=1.0, rough=1.0)), ('silver_r0.8', dict(base=(0.97, 0.96, 0.91), metal=1.0, rough=0.8))]),
    # the glow layer of glowing cut-outs against the emissive surface at the same strength (HOLO_CAL calibration)
    ('glowcal', 'box', [('glowcut_e0.05', dict(alpha_img=holes_cyan, emit=(0.1, 0.9, 0.9), strength=0.05)),
                        ('glowcut_e0.25', dict(alpha_img=holes_cyan, emit=(0.1, 0.9, 0.9), strength=0.25)),
                        ('glowcut_e1', dict(alpha_img=holes_cyan, emit=(0.1, 0.9, 0.9), strength=1.0)),
                        ('emit_ref_cyan_e1', dict(base=(0.2, 0.2, 0.2), emit=(0.1, 0.9, 0.9), strength=1.0))]),
]
bpy.ops.mesh.primitive_plane_add(size=1); g = bpy.context.active_object; g.name = 'Ground'; g.scale = (260, 40, 1)
g.data.materials.append(principled('ground_grey', base=(0.3, 0.3, 0.3), rough=0.8))
bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 5.0, 2.5)); wall = bpy.context.active_object; wall.name = 'BackWall'; wall.scale = (260, 0.5, 5)
wall.data.materials.append(principled('wall_grey', base=(0.45, 0.45, 0.45), rough=0.8))
views, panels = [], []
for ri, (row, shape, items) in enumerate(ROWS):
    cx = -50.0 + ri * 20.0
    for k, (name, kw) in enumerate(items):
        x = cx + (k - 1.5) * 2.6
        if shape == 'sphere':
            bpy.ops.mesh.primitive_uv_sphere_add(radius=0.9, segments=48, ring_count=24, location=(x, 0, 1.0))
            bpy.ops.object.shade_smooth()
        else:
            bpy.ops.mesh.primitive_cube_add(size=1, location=(x, 0, 1.0)); bpy.context.active_object.scale = (1.8, 0.3, 1.8)
            bpy.ops.object.transform_apply(scale=True)
            bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.uv.cube_project(cube_size=1.8); bpy.ops.object.mode_set(mode='OBJECT')
        o = bpy.context.active_object; o.name = '%s_%s' % (row, name)
        m = principled(name, **kw)
        if name == 'glass_pane':
            m.bf2.cls = 'glass'
        if name.startswith('probe_'):
            m.bf2.cls, m.bf2.cls_auto, m.bf2.template = 'cutout', False, 'cutout_emissive_probe'
        o.data.materials.append(m)
        panels.append({'row': row, 'name': name, 'object': o.name, 'centre': [x, -0.3 if shape == 'box' else -0.9, 1.0], 'material': m.name})
    views.append({'name': row, 'at': [cx, -7.5, 1.6], 'look': [cx, 0.0, 1.0], 'kind': 'picture', 'row': row})
for t, x in (('team1', -60.0), ('team2', 60.0)):
    s = bpy.data.objects.new('Spawn_' + t, None); s.location = (x, -10.0, 0.1); s.bf2.role = 'spawn'; s.bf2.team = t; sc.collection.objects.link(s)
bpy.ops.wm.save_as_mainfile(filepath=OUT)
json.dump({'views': views}, open(OUT + '.views.json', 'w'), indent=1)
json.dump({'panels': panels, 'fov_vertical': 55.0}, open(OUT + '.panels.json', 'w'), indent=1)
print('MADE', OUT, len(panels), 'panels')
