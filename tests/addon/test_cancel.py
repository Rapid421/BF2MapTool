"""P0-8: Cancel in the middle of a build, then Port map again - the second run continues and ends ok; no build process
is left after the cancel; stages finished before the cancel are not built again.
    blender -b <scene.blend> --factory-startup --python test_cancel.py -- <zip> <bundle dir> <work dir> <level name> <out.json>
The scene must already have been built once in <work dir> (so the second press only has the changed stages to do):
the script changes the exposure (an input of the look stage and those after it).
"""
import json
import os
import subprocess
import sys
import time
import traceback

import bpy

zip_path, bundle, work, name, out = sys.argv[sys.argv.index('--') + 1:][:5]
res, ok = {}, False


def feed_stages(st):
    return [(s.name, s.status) for s in st.stages]


try:
    bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)
    bpy.ops.preferences.addon_enable(module='bf2_map_tool')
    from bf2_map_tool import build, ops as O, prefs as P
    p = P.prefs()
    p.tools_dir, p.work_dir = bundle, work
    bpy.ops.bf2.find_paths()
    sc = bpy.context.scene
    sc.bf2.name = name
    sc.bf2.exposure = sc.bf2.exposure + 0.25
    # run 1: cancel once a stage AFTER the export is running
    bpy.ops.bf2.port()
    st = build.state()
    proc, t0 = build._proc['p'], time.time()
    cancelled_in = None
    while proc.poll() is None and time.time() - t0 < 1800:
        time.sleep(1.0)
        build._read_feed(st)
        running = [s.name for s in st.stages if s.status == 'start' and s.name != 'export']
        if running:
            cancelled_in = running[0]
            time.sleep(3.0)
            build.cancel(bpy.context)
            break
    time.sleep(3.0)
    pid = st.pid
    alive = subprocess.run(['tasklist', '/FI', 'PID eq %d' % pid], capture_output=True, text=True).stdout
    res['run1'] = {'cancelled_in': cancelled_in, 'stages': feed_stages(st), 'message': st.message, 'process_left': str(pid) in alive}
    # FrostyCLI children of the cancelled build (taskkill /T takes the tree)
    cli_left = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq FrostyCLI.exe', '/FO', 'CSV', '/NH'], capture_output=True, text=True).stdout
    res['run1']['frostycli_after_cancel'] = cli_left.strip()[:300]
    # run 2: press again
    bpy.ops.bf2.port()
    st = build.state()
    proc, t0 = build._proc['p'], time.time()
    while proc.poll() is None and time.time() - t0 < 3600:
        time.sleep(2.0)
        build._read_feed(st)
    build._read_feed(st)
    res['run2'] = {'stages': feed_stages(st), 'message': st.message, 'ok': st.ok, 'fbmod': st.fbmod, 'seconds': round(time.time() - t0, 1)}
    done_before = [n for n, s in res['run1']['stages'] if s in ('done', 'skip') and n != 'export']
    redone = [n for n, s in res['run2']['stages'] if n in done_before and s == 'done']
    res['redone_after_resume'] = redone
    ok = bool(cancelled_in) and not res['run1']['process_left'] and st.ok and not redone
except Exception:
    res['exception'] = traceback.format_exc()
res['ok'] = ok
json.dump(res, open(out, 'w'), indent=1, default=str)
print('CANCEL_TEST ' + json.dumps(res, default=str)[:2500])
sys.stdout.flush()
os._exit(0 if ok else 1)
