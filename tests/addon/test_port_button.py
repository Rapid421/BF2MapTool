"""Press the real "Port map" button in a windowed Blender and watch it from inside:
  * the build must run outside Blender: a 0.25 s timer keeps ticking in the main thread while it runs; the largest
    gap between ticks is the longest time the interface was blocked;
  * Cancel: when the stage named by --cancel-at starts, the Cancel operator is called; the build must stop; then the
    button is pressed again and the stages that were finished must be skipped (resume);
  * at the end the result (stages, verdict, .fbmod, gaps) is written as JSON and Blender quits.
A hard limit ends the test (Cancel + quit) if the build does not finish.

    set BLENDER_USER_SCRIPTS=<profile with the add-on installed>
    blender --factory-startup <scene.blend> --python test_port_button.py -- <settings.json> <out.json> [--cancel-at <stage>] [--limit 2400]
"""
import json
import sys
import time

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
settings_path, out_path = argv[0], argv[1]
cancel_at = argv[argv.index('--cancel-at') + 1] if '--cancel-at' in argv else None
limit = int(argv[argv.index('--limit') + 1]) if '--limit' in argv else 2400

bpy.ops.preferences.addon_enable(module='bf2_map_tool')
from bf2_map_tool import build

p = bpy.context.preferences.addons['bf2_map_tool'].preferences
s = json.load(open(settings_path))
for k in ('game_path', 'frostycli', 'plugins_path', 'havok_tool', 'havok_template', 'fluxbake', 'work_dir'):
    setattr(p, k, s[k])
R = {'scene': bpy.data.filepath, 'level': bpy.context.scene.bf2.name, 'runs': [], 'ticks': 0, 'max_gap_s': 0.0, 'gaps_over_1s': 0}
T = {'t0': time.time(), 'last': time.time(), 'phase': 'start', 'cancelled': False, 'run_t0': 0.0}


def snapshot(st, note):
    R['runs'].append({'note': note, 'ok': bool(st.ok), 'message': st.message, 'elapsed_s': round(time.time() - T['run_t0'], 1), 'fbmod': st.fbmod,
                      'stages': [(r.name, r.status, round(r.seconds, 1)) for r in st.stages],
                      'results': [(r.severity, r.code, r.message[:120]) for r in st.results]})


def finish(note):
    R['note'] = note
    R['total_s'] = round(time.time() - T['t0'], 1)
    with open(out_path, 'w') as f:
        json.dump(R, f, indent=1)
    bpy.ops.wm.quit_blender()


def tick():
    now = time.time()
    gap = now - T['last']
    T['last'] = now
    R['ticks'] += 1
    if T['phase'] != 'start':
        R['max_gap_s'] = max(R['max_gap_s'], round(gap, 2))
        if gap > 1.0:
            R['gaps_over_1s'] += 1
    st = build.state()
    if now - T['t0'] > limit:
        build.cancel(bpy.context)
        snapshot(st, 'stopped by the test limit')
        finish('LIMIT')
        return None
    if T['phase'] == 'start':
        T['run_t0'] = time.time()
        r = bpy.ops.bf2.port()
        R['port_operator'] = list(r)
        R['port_returned_after_s'] = round(time.time() - T['run_t0'], 2)
        T['phase'] = 'run1'
        T['last'] = time.time()
        return 0.25
    if T['phase'] == 'run1':
        if cancel_at and not T['cancelled'] and any(x.name == cancel_at and x.status == 'start' for x in st.stages):
            bpy.ops.bf2.cancel()
            T['cancelled'] = True
            T['cancel_time'] = time.time()
            snapshot(st, 'cancelled at ' + cancel_at)
            T['phase'] = 'after_cancel'
            return 0.25
        if not st.running:
            snapshot(st, 'first run')
            finish('DONE' if st.ok else 'FAILED')
            return None
    elif T['phase'] == 'after_cancel':
        if time.time() - T['cancel_time'] > 4.0:
            import subprocess
            out = subprocess.run(['tasklist', '/NH', '/FI', 'IMAGENAME eq FrostyCLI.exe'], capture_output=True, text=True).stdout
            R['running_after_cancel'] = bool(st.running)
            R['pid_alive_after_cancel'] = str(st.pid) in subprocess.run(['tasklist', '/NH', '/FI', 'PID eq %d' % st.pid], capture_output=True, text=True).stdout
            T['run_t0'] = time.time()
            R['port_operator_2'] = list(bpy.ops.bf2.port())
            T['phase'] = 'run2'
            T['last'] = time.time()
    elif T['phase'] == 'run2':
        if not st.running:
            snapshot(st, 'second run (resume after cancel)')
            finish('DONE' if st.ok else 'FAILED')
            return None
    return 0.25


bpy.app.timers.register(tick, first_interval=2.0)
