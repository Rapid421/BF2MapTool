"""Install the packaged add-on the way a user does (Preferences > Add-ons > Install from Disk), enable it, and exercise
what can be exercised without a window: registration, properties, the Add-menu operators, Guess classes, the setup
check with real paths. Run with a throw-away user-scripts folder so the user's own Blender profile is not touched:

    set BLENDER_USER_SCRIPTS=<tmp dir>
    blender -b --factory-startup --python test_install.py -- <addon zip> <settings.json>
"""
import json
import sys

import bpy

zip_path, settings_path = sys.argv[sys.argv.index('--') + 1:][:2]
res = {}
bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)
bpy.ops.preferences.addon_enable(module='bf2_map_tool')
res['enabled'] = 'bf2_map_tool' in bpy.context.preferences.addons
res['panel'] = hasattr(bpy.types, 'BF2_PT_main')
res['props'] = all(hasattr(t, 'bf2') for t in (bpy.types.Scene, bpy.types.Object, bpy.types.Material, bpy.types.Light))
ops = ('check_setup', 'port', 'check', 'bake', 'rebuild_mod', 'cancel', 'open_log', 'select_result', 'guess_classes', 'add_marker',
       'mark_selected', 'set_collision')
res['operators'] = {o: hasattr(bpy.ops.bf2, o) and bpy.ops.bf2.__getattr__(o).get_rna_type() is not None for o in ops}
p = bpy.context.preferences.addons['bf2_map_tool'].preferences
s = json.load(open(settings_path))
for k in ('game_path', 'frostycli', 'plugins_path', 'havok_tool', 'havok_template', 'fluxbake', 'work_dir'):
    setattr(p, k, s[k])
from bf2_map_tool import prefs as P, ops as O
res['runtime_dir'] = P.runtime_dir(p)
res['python'] = P.python_exe(p)
# gameplay objects from the Add menu
for kind in ('spawn1', 'spawn2', 'hero1', 'cp', 'kill', 'nowalk', 'combat', 'probe', 'figure'):
    bpy.ops.bf2.add_marker(kind=kind)
res['roles'] = sorted({o.bf2.role for o in bpy.context.scene.objects})
# a material for the class guess
m = bpy.data.materials.new('test_glow'); m.use_nodes = True
pb = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
pb.inputs['Emission Strength'].default_value = 5.0
(pb.inputs.get('Emission Color') or pb.inputs.get('Emission')).default_value = (1.0, 0.5, 0.1, 1.0)
res['guess_emissive'] = O.guess_class(m)
m2 = bpy.data.materials.new('plain'); m2.use_nodes = True
res['guess_plain'] = O.guess_class(m2)
# the setup check through the operator
r = bpy.ops.bf2.check_setup()
res['setup'] = [(l.item, l.ok, l.message[:60]) for l in p.setup]
print('ADDON_TEST ' + json.dumps(res, default=str))
