"""Material inputs as real-world files wire them (glTF importer, Substance, game rips), with known answers:
  orm          one ORM image -> Separate Color: G -> Roughness, B -> Metallic   (glTF layout)
  orm_rgb      the same with the old Separate RGB node
  inv_rough    a gloss map -> Invert -> Roughness (Unreal / Source "smoothness" maps)
  nm_strength  a Normal Map node at strength 0.5
  emit_tex     an emission image x strength 3
  alpha_blend  base colour alpha in a material with Blend Mode = Alpha Blend (no wired Alpha)
    blender -b --factory-startup --python make_ormtest.py -- <out.blend> <addon dir>   -> <out.blend>.expect.json
"""
import json
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
sc.bf2.name = 'OrmTest'


def img(name, rgba, noncolor=True):
    i = bpy.data.images.new(name, 32, 32, alpha=True)
    px = np.tile(np.array(rgba, np.float32), (32, 32, 1)); px[::4, :, :3] *= 0.9
    i.pixels.foreach_set(px.ravel()); i.pack()
    if noncolor:
        i.colorspace_settings.name = 'Non-Color'
    return i


ORM = img('rock_ORM', (1.0, 0.7, 0.2, 1.0))
GLOSS = img('rock_gloss', (0.25, 0.25, 0.25, 1.0))
NRM = img('rock_nrm', (0.6, 0.45, 1.0, 1.0))
EM = img('panel_emit', (1.0, 0.5, 0.0, 1.0), noncolor=False)
COL = img('rock_col', (0.5, 0.4, 0.3, 1.0), noncolor=False)
DECAL = img('decal_col', (0.9, 0.9, 0.9, 0.3), noncolor=False)
EXPECT = {}


def mat(name):
    m = bpy.data.materials.new(name); m.use_nodes = True
    return m, m.node_tree.nodes, m.node_tree.links, m.node_tree.nodes['Principled BSDF']


def tex(N, image):
    t = N.new('ShaderNodeTexImage'); t.image = image
    return t


m, N, L, p = mat('orm')
c = tex(N, COL); L.new(c.outputs['Color'], p.inputs['Base Color'])
o = tex(N, ORM); s = N.new('ShaderNodeSeparateColor'); L.new(o.outputs['Color'], s.inputs['Color'])
L.new(s.outputs['Green'], p.inputs['Roughness']); L.new(s.outputs['Blue'], p.inputs['Metallic'])
EXPECT['orm'] = {'roughness': ('rock_ORM', 'G', False), 'metallic': ('rock_ORM', 'B', False), 'base_color': ('rock_col', 'RGB', False)}

m, N, L, p = mat('orm_rgb')
o = tex(N, ORM); s = N.new('ShaderNodeSeparateRGB') if hasattr(bpy.types, 'ShaderNodeSeparateRGB') else N.new('ShaderNodeSeparateColor')
L.new(o.outputs['Color'], s.inputs[0]); L.new(s.outputs[1], p.inputs['Roughness']); L.new(s.outputs[2], p.inputs['Metallic'])
EXPECT['orm_rgb'] = {'roughness': ('rock_ORM', 'G', False), 'metallic': ('rock_ORM', 'B', False)}

m, N, L, p = mat('inv_rough')
g = tex(N, GLOSS); inv = N.new('ShaderNodeInvert'); L.new(g.outputs['Color'], inv.inputs['Color']); L.new(inv.outputs['Color'], p.inputs['Roughness'])
EXPECT['inv_rough'] = {'roughness': ('rock_gloss', 'RGB', True)}

m, N, L, p = mat('nm_strength')
n = tex(N, NRM); nm = N.new('ShaderNodeNormalMap'); nm.inputs['Strength'].default_value = 0.5
L.new(n.outputs['Color'], nm.inputs['Color']); L.new(nm.outputs['Normal'], p.inputs['Normal'])
EXPECT['nm_strength'] = {'normal': ('rock_nrm', 'RGB', False)}

m, N, L, p = mat('emit_tex')
e = tex(N, EM); L.new(e.outputs['Color'], p.inputs['Emission Color']); p.inputs['Emission Strength'].default_value = 3.0
EXPECT['emit_tex'] = {'emission': ('panel_emit', 'RGB', False)}

m, N, L, p = mat('alpha_blend')
d = tex(N, DECAL); L.new(d.outputs['Color'], p.inputs['Base Color'])
try:
    m.surface_render_method = 'BLENDED'
except AttributeError:
    m.blend_method = 'BLEND'
EXPECT['alpha_blend'] = {'base_color': ('decal_col', 'RGB', False)}

for i, name in enumerate(EXPECT):
    bpy.ops.mesh.primitive_cube_add(size=2, location=(i * 3, 0, 1)); ob = bpy.context.active_object; ob.name = name
    ob.data.materials.append(bpy.data.materials[name])
for t, x in (('team1', -25), ('team2', 25)):
    s_ = bpy.data.objects.new('Spawn_' + t, None); s_.location = (x, 8, 0.1); s_.bf2.role = 'spawn'; s_.bf2.team = t; sc.collection.objects.link(s_)
bpy.ops.mesh.primitive_plane_add(size=80); bpy.context.active_object.data.materials.append(bpy.data.materials.new('floor'))
sc.world = bpy.data.worlds.new('W')
bpy.ops.wm.save_as_mainfile(filepath=OUT)
json.dump(EXPECT, open(OUT + '.expect.json', 'w'), indent=1)
print('MADE', OUT)
