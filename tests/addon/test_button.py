"""Press the add-on's own button on an open scene, the way a user does, in a throw-away profile:
install the zip, Tool bundle folder + Find everything, (optionally) Install Python packages, then bf2.check / bf2.port /
bf2.bake, wait for the build process (Blender's timers do not run in -b, so the feed is read here) and record the result.

    blender -b <scene.blend> --factory-startup --python test_button.py -- <zip> <bundle dir> <work dir> <out.json> <check|port|bake>
            [--pip] [--limit SECONDS] [--name LevelName]
Exit 0 when the run ended with @@DONE ok (for check: also when it ended 'fail' with only expected errors - see out.json).
"""
import json
import os
import sys
import time
import traceback

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
zip_path, bundle, work, out, kind = argv[:5]
pip = '--pip' in argv
limit = float(argv[argv.index('--limit') + 1]) if '--limit' in argv else 3600.0
name = argv[argv.index('--name') + 1] if '--name' in argv else None
res = {'blender': bpy.app.version_string, 'scene': bpy.data.filepath, 'kind': kind}
rc = 1
try:
    bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)
    bpy.ops.preferences.addon_enable(module='bf2_map_tool')
    from bf2_map_tool import build, ops as O, prefs as P
    p = P.prefs()
    p.tools_dir, p.work_dir = bundle, work
    bpy.ops.bf2.find_paths()
    res['paths'] = {k: getattr(p, k) for k in P.PATH_KEYS}
    if pip or not os.path.isdir(P.pylib_dir(p)):
        bpy.ops.bf2.install_packages()
        if O._pip['thread'] is not None:
            O._pip['thread'].join(1500)
        res['pip'] = O._pip['result']
        O._pip['thread'] = None
    if '--import' in argv:
        r = bpy.ops.bf2.import_map(filepath=argv[argv.index('--import') + 1])
        res['import'] = {'result': list(r), 'objects': len(bpy.data.objects), 'name': bpy.context.scene.bf2.name,
                         'notes': [(x.code, x.message[:300]) for x in build.state().results]}
    if '--prepare' in argv:
        r = bpy.ops.bf2.prepare_import()
        res['prepare'] = {'result': list(r), 'notes': [(x.code, x.message[:300]) for x in build.state().results]}
    if name:
        bpy.context.scene.bf2.name = name
    if '--fit-lights' in argv:
        def rt():
            return sum(1 for o in bpy.context.view_layer.objects if o.type == 'LIGHT' and o.data.type != 'SUN' and not o.hide_render
                       and o.data.bf2.mode in ('dynamic', 'both'))
        before = rt()
        r = bpy.ops.bf2.fit_light_budget()
        res['fit_lights'] = {'result': list(r), 'realtime_before': before, 'realtime_after': rt(), 'budget': bpy.context.scene.bf2.dynamic_light_budget}
    t0 = time.time()
    r = {'check': bpy.ops.bf2.check, 'port': bpy.ops.bf2.port, 'bake': bpy.ops.bf2.bake}[kind]()
    res['operator'] = list(r)
    st = build.state()
    proc = build._proc['p']
    while proc is not None and proc.poll() is None and time.time() - t0 < limit:
        time.sleep(2.0)
        build._read_feed(st)
    if proc is not None and proc.poll() is None:
        build.cancel(bpy.context)
        res['timeout'] = limit
    build._read_feed(st)
    res['seconds'] = round(time.time() - t0, 1)
    res['returncode'] = proc.returncode if proc is not None else None
    res['message'], res['ok'], res['fbmod'] = st.message, st.ok, st.fbmod
    res['stages'] = [(s.name, s.status, round(s.seconds, 1), s.summary[:160]) for s in st.stages]
    res['results'] = [(x.severity, x.code, x.message[:300]) for x in st.results]
    res['feed'] = st.feed
    rc = 0 if st.ok else 1
except Exception:
    res['exception'] = traceback.format_exc()
json.dump(res, open(out, 'w', encoding='utf-8'), indent=1, default=str, ensure_ascii=False)
print('BUTTON_TEST ' + json.dumps({k: res.get(k) for k in ('kind', 'ok', 'message', 'seconds', 'fbmod', 'exception')}, default=str, ensure_ascii=False))
sys.stdout.flush()
os._exit(rc)
