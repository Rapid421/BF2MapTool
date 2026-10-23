"""P1-0 test scene: one material = grey checker image x Color Attribute "Color0" (as LSWTSS exports), on
  Red1, Red2 (same red tint -> share one copy), Green, Blue (one colour each -> own copies), Grad (a gradient -> plain).
    blender -b --factory-startup --python make_tinttest.py -- <out.blend> <addon dir>     -> <out.blend>.expect.json
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
sc.bf2.name = 'TintTest'
img = bpy.data.images.new('checker', 64, 64)
px = np.ones((64, 64, 4), np.float32); px[..., :3] = 0.8
px[(np.add.outer(np.arange(64) // 8, np.arange(64) // 8) % 2) == 1, :3] = 0.6
img.pixels.foreach_set(px.ravel()); img.pack()
m = bpy.data.materials.new('LegoPlastic'); m.use_nodes = True
N, L = m.node_tree.nodes, m.node_tree.links
t = N.new('ShaderNodeTexImage'); t.image = img
vc = N.new('ShaderNodeVertexColor'); vc.layer_name = 'Color0'
mix = N.new('ShaderNodeMix'); mix.data_type = 'RGBA'; mix.blend_type = 'MULTIPLY'; mix.inputs['Factor'].default_value = 1.0
L.new(t.outputs['Color'], mix.inputs[6]); L.new(vc.outputs['Color'], mix.inputs[7])
L.new(mix.outputs[2], N['Principled BSDF'].inputs['Base Color'])
TINTS = {'Red1': (0.8, 0.05, 0.05), 'Red2': (0.8, 0.05, 0.05), 'Green': (0.05, 0.7, 0.05), 'Blue': (0.05, 0.1, 0.8), 'Grad': None}
for i, (name, c) in enumerate(TINTS.items()):
    bpy.ops.mesh.primitive_cube_add(size=2, location=(i * 4 - 8, 0, 1))
    o = bpy.context.active_object; o.name = name
    o.data.materials.append(m)
    ca = o.data.color_attributes.new('Color0', 'FLOAT_COLOR', 'CORNER')
    n = len(ca.data)
    if c is None:
        cols = np.stack([np.linspace(0, 1, n), np.linspace(1, 0, n), np.full(n, 0.5), np.ones(n)], 1)
    else:
        cols = np.tile([c[0], c[1], c[2], 1.0], (n, 1))
    ca.data.foreach_set('color', cols.astype(np.float32).ravel())
    o.data.color_attributes.active_color = ca
bpy.ops.mesh.primitive_plane_add(size=70); f = bpy.context.active_object; f.name = 'Floor'
fm = bpy.data.materials.new('FloorGrey'); fm.use_nodes = True; f.data.materials.append(fm)
for tm, x in (('team1', -25), ('team2', 25)):
    s = bpy.data.objects.new('Spawn_' + tm, None); s.location = (x, 5, 0.1); s.bf2.role = 'spawn'; s.bf2.team = tm; sc.collection.objects.link(s)
sc.world = bpy.data.worlds.new('W')
bpy.ops.wm.save_as_mainfile(filepath=OUT)
json.dump({'variants': 3, 'objects_tinted': 4, 'varying': ['Grad'], 'tints': {k: v for k, v in TINTS.items() if v}}, open(OUT + '.expect.json', 'w'), indent=1)
print('MADE', OUT)
