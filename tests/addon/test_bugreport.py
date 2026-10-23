"""P0-9: after a check run in this profile, Make bug report writes a zip with report.json, settings.json, the logs and
the export's map.json - and nothing heavy (no .blend, no textures, no meshes).
    blender -b <scene.blend> --factory-startup --python test_bugreport.py -- <zip> <bundle dir> <work dir> <out.json>
"""
import json
import os
import sys
import time
import traceback
import zipfile

import bpy

zip_path, bundle, work, out = sys.argv[sys.argv.index('--') + 1:][:4]
res = {}
ok = False
try:
    bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)
    bpy.ops.preferences.addon_enable(module='bf2_map_tool')
    from bf2_map_tool import build, ops as O, prefs as P
    p = P.prefs()
    p.tools_dir, p.work_dir = bundle, work
    bpy.ops.bf2.find_paths()
    if not os.path.isdir(P.pylib_dir(p)):
        bpy.ops.bf2.install_packages(); O._pip['thread'] and O._pip['thread'].join(1500); O._pip['thread'] = None
    bpy.ops.bf2.check_setup()
    bpy.ops.bf2.check()
    proc, t0 = build._proc['p'], time.time()
    while proc.poll() is None and time.time() - t0 < 900:
        time.sleep(1.0)
    build._read_feed(build.state())
    r = bpy.ops.bf2.bug_report()
    res['operator'] = list(r)
    zips = sorted(f for f in os.listdir(build.work_folder(bpy.context)) if f.startswith('bug_report_'))
    zp = os.path.join(build.work_folder(bpy.context), zips[-1])
    with zipfile.ZipFile(zp) as z:
        names = z.namelist()
        rep = json.loads(z.read('report.json'))
    res['zip'], res['bytes'], res['names'] = zp, os.path.getsize(zp), names
    res['report_keys'] = sorted(rep)
    heavy = [n for n in names if n.endswith(('.blend', '.png', '.npz', '.dds', '.fbmod', '.hdr'))]
    ok = (res['operator'] == ['FINISHED'] and 'report.json' in names and 'settings.json' in names and 'export/map.json' in names
          and any(n.startswith('logs/') for n in names) and not heavy and rep.get('setup') and rep.get('results') is not None
          and rep['last_run']['kind'] == 'check')
    res['heavy'] = heavy
except Exception:
    res['exception'] = traceback.format_exc()
res['ok'] = bool(ok)
json.dump(res, open(out, 'w'), indent=1, default=str)
print('BUGREPORT_TEST ' + json.dumps(res, default=str)[:1500])
sys.stdout.flush()
os._exit(0 if ok else 1)
