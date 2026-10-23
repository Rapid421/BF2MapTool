"""The build runs OUTSIDE Blender, as one subprocess; Blender only starts it, reads its progress feed with a timer and
can stop it. So the interface never blocks, however long a stage takes.

  Port map  ->  save a copy of the scene  ->  python -m bf2maptool build <copy.blend> ...  (export headless, then the
                pipeline)  ->  progress lines '@@STAGE ...' / '@@DONE ...' in <work>/<Name>/logs/ui_feed.log
"""
import os
import subprocess
import time

import bpy
from bpy.props import BoolProperty, CollectionProperty, FloatProperty, IntProperty, StringProperty

from . import prefs as P


class BF2StageLine(bpy.types.PropertyGroup):
    name: StringProperty()
    status: StringProperty()        # start / skip / done / fail
    seconds: FloatProperty()
    summary: StringProperty()


class BF2ResultLine(bpy.types.PropertyGroup):
    code: StringProperty()
    severity: StringProperty()      # error / warning / info
    message: StringProperty()
    objects: StringProperty()       # names separated by \n
    materials: StringProperty()
    fix: StringProperty()


class BF2BuildState(bpy.types.PropertyGroup):
    running: BoolProperty()
    pid: IntProperty()
    kind: StringProperty()
    feed: StringProperty()
    feed_pos: IntProperty()
    started: FloatProperty()
    elapsed: FloatProperty()
    message: StringProperty()
    ok: BoolProperty()
    fbmod: StringProperty()
    stages: CollectionProperty(type=BF2StageLine)
    results: CollectionProperty(type=BF2ResultLine)
    results_index: IntProperty()
    tail: StringProperty()


_proc = {'p': None}


def state(context=None):
    return (context or bpy.context).window_manager.bf2_build


def work_folder(context):
    s = P.settings_dict(P.prefs(context))
    name = context.scene.bf2.name
    return os.path.join(s['work_dir'], name).replace('\\', '/') if s['work_dir'] and name else ''


def start(context, kind, extra_args=()):
    """save a copy of the scene and start the runtime on it; returns an error text or None"""
    st = state(context)
    if st.running:
        return 'A build is already running. Cancel it first.'
    p = P.prefs(context)
    py, rt = P.python_exe(p), P.runtime_dir(p)
    if not py or not os.path.exists(py):
        return 'Python was not found. Leave Python empty in the add-on preferences to use Blender\'s own, or pick a python.exe.'
    if not rt or not os.path.isdir(os.path.join(rt, 'bf2maptool')):
        return 'The runtime folder was not found. Set it in the add-on preferences.'
    try:
        settings = P.write_settings(p)
    except ValueError as e:
        return str(e)
    wf = work_folder(context)
    if not wf:
        return 'Give the level a name first (Map tab).'
    os.makedirs(wf + '/logs', exist_ok=True)
    os.makedirs(wf + '/export', exist_ok=True)
    copy = wf + '/export/scene_copy.blend'
    bpy.ops.wm.save_as_mainfile(filepath=copy, copy=True, check_existing=False)
    feed = wf + '/logs/ui_feed.log'
    here = os.path.dirname(os.path.abspath(__file__))
    blender = bpy.path.abspath(p.blender_exe) if getattr(p, 'blender_exe', '') else bpy.app.binary_path
    cmd = [py, '-m', 'bf2maptool', kind, copy, '--settings', settings, '--blender', blender,
           '--export-script', os.path.join(here, 'export_scene.py'), '--export-dir', wf + '/export'] + list(extra_args)
    env = P.runtime_env(p)
    flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
    f = open(feed, 'w', encoding='utf-8')
    _proc['p'] = subprocess.Popen(cmd, stdout=f, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, cwd=rt, env=env, creationflags=flags)
    f.close()
    st.running, st.pid, st.kind, st.feed, st.feed_pos = True, _proc['p'].pid, kind, feed, 0
    st.started, st.elapsed, st.message, st.ok, st.fbmod, st.tail = time.time(), 0.0, 'Starting...', False, '', ''
    st.stages.clear()
    if kind in ('check',):
        st.results.clear()
    if not bpy.app.timers.is_registered(_poll):
        bpy.app.timers.register(_poll, first_interval=0.5)
    return None


def cancel(context):
    st = state(context)
    if not st.running:
        return
    if os.name == 'nt':
        subprocess.run(['taskkill', '/F', '/T', '/PID', str(st.pid)], capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    else:
        try:
            os.kill(st.pid, 9)
        except OSError:
            pass
    st.running = False
    st.message = 'Cancelled. Press the button again to continue where it stopped.'
    _proc['p'] = None


def _read_feed(st):
    try:
        with open(st.feed, 'r', encoding='utf-8', errors='replace') as f:
            f.seek(st.feed_pos)
            text = f.read()
            st.feed_pos = f.tell()
    except OSError:
        return
    for line in text.splitlines():
        if line.startswith('@@STAGE '):
            parts = line.split(' ', 4)
            name, status = parts[1], parts[2]
            row = next((r for r in st.stages if r.name == name), None) or st.stages.add()
            row.name, row.status = name, status
            try:
                row.seconds = float(parts[3])
            except (ValueError, IndexError):
                pass
            row.summary = parts[4][:300] if len(parts) > 4 else ''
            st.message = {'start': 'Working: ', 'done': 'Done: ', 'skip': 'Up to date: ', 'fail': 'Stopped at: '}.get(status, '') + name
        elif line.startswith('@@RESULT '):
            import json
            try:
                j = json.loads(line[9:])
            except ValueError:
                continue
            r = st.results.add()
            r.code, r.severity, r.message = j.get('code', ''), j.get('severity', 'error'), j.get('message', '')
            r.objects, r.materials, r.fix = '\n'.join(j.get('objects', [])), '\n'.join(j.get('materials', [])), j.get('fix', '')
        elif line.startswith('@@MOD '):
            st.fbmod = line[6:].strip()
        elif line.startswith('@@DONE '):
            parts = line.split(' ', 2)
            st.ok = parts[1] == 'ok'
            st.message = 'Finished.' if st.ok else (parts[2] if len(parts) > 2 else 'Stopped.')[:400]
        elif line.strip():
            st.tail = line.strip()[:200]


def _poll():
    st = state()
    if not st.running:
        return None
    _read_feed(st)
    st.elapsed = time.time() - st.started
    p = _proc['p']
    if p is None or p.poll() is not None:
        _read_feed(st)
        st.running = False
        if p is not None and p.returncode not in (0, None) and not st.message.startswith(('Stopped', 'Finished')) and st.ok is False:
            st.message = st.message if st.message and not st.message.startswith('Working') else 'The build stopped (see the log).'
        _proc['p'] = None
        _redraw()
        return None
    _redraw()
    return 0.7


def _redraw():
    for w in bpy.context.window_manager.windows:
        for a in w.screen.areas:
            if a.type == 'VIEW_3D':
                a.tag_redraw()


CLASSES = (BF2StageLine, BF2ResultLine, BF2BuildState)
