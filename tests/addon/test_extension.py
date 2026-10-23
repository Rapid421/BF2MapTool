"""After `blender --command extension install-file -r user_default -e <ext zip>` into a throw-away profile: the
extension form of the add-on is enabled, registers everything, finds its own runtime, and Find everything works.
    blender -b --python test_extension.py -- <bundle dir> <work dir> <out.json>     (same BLENDER_USER_* env as the install)
"""
import json
import os
import sys
import traceback

import bpy

bundle, work, out = sys.argv[sys.argv.index('--') + 1:][:3]
res = {'blender': bpy.app.version_string}
ok = False
try:
    mods = [m for m in bpy.context.preferences.addons.keys() if m.endswith('bf2_map_tool')]
    res['enabled_modules'] = mods
    mod = mods[0]
    import importlib
    pkg = importlib.import_module(mod)
    P = importlib.import_module(mod + '.prefs')
    O = importlib.import_module(mod + '.ops')
    p = P.prefs()
    res['prefs_class'] = P.ADDON
    res['panel'] = hasattr(bpy.types, 'BF2_PT_main')
    res['props'] = all(hasattr(t, 'bf2') for t in (bpy.types.Scene, bpy.types.Object, bpy.types.Material, bpy.types.Light))
    res['operators'] = sorted(n for n in dir(bpy.ops.bf2) if not n.startswith('_'))
    res['runtime_dir'] = P.runtime_dir(p)
    res['runtime_ok'] = os.path.isfile(os.path.join(res['runtime_dir'], 'bf2maptool', '__main__.py'))
    res['export_script'] = os.path.isfile(os.path.join(os.path.dirname(pkg.__file__), 'export_scene.py'))
    p.tools_dir, p.work_dir = bundle, work
    bpy.ops.bf2.find_paths()
    res['found'] = {k: getattr(p, k) for k in P.PATH_KEYS}
    bpy.ops.bf2.add_marker(kind='spawn1')
    res['marker_role'] = bpy.context.active_object.bf2.role
    need = {'check_setup', 'find_paths', 'install_packages', 'port', 'check', 'bake', 'add_marker'}
    ok = (res['panel'] and res['props'] and need <= set(res['operators']) and res['runtime_ok'] and res['export_script']
          and all(res['found'].values()) and res['marker_role'] == 'spawn')
except Exception:
    res['exception'] = traceback.format_exc()
res['ok'] = bool(ok)
json.dump(res, open(out, 'w', encoding='utf-8'), indent=1, default=str)
print('EXT_TEST ' + json.dumps(res, default=str)[:2000])
sys.stdout.flush()
os._exit(0 if ok else 1)
