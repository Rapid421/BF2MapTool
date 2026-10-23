"""A new user's first minutes, headless: install the add-on zip into an EMPTY profile, point "Tool bundle folder" at an
unzipped bundle, press Find everything, Install Python packages (Blender's own Python, private folder) and Check setup.
Nothing typed by hand except the bundle folder and the work folder. Run with a throw-away profile:

    set BLENDER_USER_SCRIPTS=<new empty dir>
    blender -b --factory-startup --python test_newuser.py -- <addon zip> <bundle dir> <work dir> <out.json>

Prints ADDON_TEST {json}; exit code 0 only when every setup line is ok.
"""
import json
import os
import sys
import time
import traceback

import bpy

zip_path, bundle, work, out = sys.argv[sys.argv.index('--') + 1:][:4]
res = {'blender': bpy.app.version_string, 'steps': {}}
ok = True
try:
    bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)
    bpy.ops.preferences.addon_enable(module='bf2_map_tool')
    res['enabled'] = 'bf2_map_tool' in bpy.context.preferences.addons
    from bf2_map_tool import ops as O, prefs as P
    p = P.prefs()
    res['empty_before'] = {k: getattr(p, k) for k in P.PATH_KEYS + ('work_dir', 'python')}
    p.tools_dir = bundle
    p.work_dir = work
    t = time.time()
    r = bpy.ops.bf2.find_paths()
    res['steps']['find_paths'] = {'result': list(r), 's': round(time.time() - t, 1), 'lines': [(l.item, l.ok, l.message[:120]) for l in p.setup]}
    res['found'] = {k: getattr(p, k) for k in P.PATH_KEYS}
    res['python'] = P.python_exe(p)
    res['pylib'] = P.pylib_dir(p)
    t = time.time()
    r = bpy.ops.bf2.install_packages()
    th = O._pip['thread']
    if th is not None:
        th.join(1500)                                   # the operator's own thread (Blender's timers do not run in -b)
    res['steps']['install_packages'] = {'result': list(r), 's': round(time.time() - t, 1), 'outcome': O._pip['result'],
                                        'alive': bool(th and th.is_alive())}
    O._pip['thread'] = None
    t = time.time()
    r = bpy.ops.bf2.check_setup()
    lines = [(l.item, l.ok, l.message[:140], l.fix[:140]) for l in p.setup]
    res['steps']['check_setup'] = {'result': list(r), 's': round(time.time() - t, 1), 'lines': lines}
    ok = (res['enabled'] and all(bool(v) for v in res['found'].values()) and bool((O._pip['result'] or [False])[0])
          and bool(lines) and all(l[1] for l in lines))
    # the sidebar's operators and the new marker
    bpy.ops.bf2.add_marker(kind='wall')
    w = bpy.context.active_object
    res['wall'] = {'name': w.name, 'role': w.bf2.role, 'hide_render': w.hide_render, 'dims': [round(x, 2) for x in w.dimensions]}
    ok = ok and w.bf2.role == 'collision'
except Exception:
    res['exception'] = traceback.format_exc()
    ok = False
res['ok'] = bool(ok)
with open(out, 'w', encoding='utf-8') as f:
    json.dump(res, f, indent=1, default=str)
print('ADDON_TEST ' + json.dumps(res, default=str)[:3000])
sys.stdout.flush()
os._exit(0 if ok else 1)
