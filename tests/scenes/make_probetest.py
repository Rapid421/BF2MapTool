"""P2-1 test scene: the test arena with Sky = Blender World and two Reflection probe objects (one over the grate bridge,
one in the east corridor) of different sizes. blender -b arena.blend --python make_probetest.py -- <out.blend> <addon dir>"""
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, ADDON = argv[0], argv[1]
sys.path.insert(0, ADDON)
import props  # noqa: E402

try:
    props.register()
except ValueError:
    pass
sc = bpy.context.scene
sc.bf2.sky = 'world'
if sc.world is None:
    sc.world = bpy.data.worlds.new('ProbeTestWorld')
w = sc.world
w.use_nodes = True
bg = next((n for n in w.node_tree.nodes if n.type == 'BACKGROUND'), None)
if bg is not None:
    bg.inputs['Color'].default_value = (0.35, 0.45, 0.65, 1.0)
    bg.inputs['Strength'].default_value = 1.0
for name, loc, r in (('Probe bridge', (-8.0, -6.0, 1.8), 12.0), ('Probe corridor', (28.0, 4.0, 1.8), 7.0)):
    o = bpy.data.objects.new(name, None)
    o.empty_display_type, o.empty_display_size = 'SPHERE', r
    o.location = loc
    sc.collection.objects.link(o)
    o.bf2.role = 'reflection_probe'
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=True)
print('MADE', OUT)
