"""Add-on preferences: the machine paths (set once, or found by "Find everything") and the Check setup button."""
import json
import os
import subprocess

import bpy
from bpy.props import CollectionProperty, StringProperty

from . import detect

ADDON = __package__
PATH_KEYS = ('game_path', 'frostycli', 'plugins_path', 'havok_tool', 'havok_template', 'fluxbake')
# what the build needs in its Python, as pip names (scipy and xatlas: collision hulls and the light bake)
PACKAGES = ('numpy', 'pillow', 'scipy', 'xatlas', 'imageio-ffmpeg')      # imageio-ffmpeg: the loading picture's video


class BF2SetupLine(bpy.types.PropertyGroup):
    item: StringProperty()
    ok: bpy.props.BoolProperty()
    message: StringProperty()
    fix: StringProperty()


def _default_runtime():
    here = os.path.dirname(os.path.abspath(__file__))
    for cand in (os.path.join(here, 'runtime'), os.path.normpath(os.path.join(here, '..', '..', 'runtime'))):
        if os.path.isdir(os.path.join(cand, 'bf2maptool')):
            return cand
    return ''


class BF2Preferences(bpy.types.AddonPreferences):
    bl_idname = ADDON

    tools_dir: StringProperty(name='Tool bundle folder', subtype='DIR_PATH',
                              description='The unzipped BF2MapTool bundle (FrostyCLI, FrostyPlugins, HavokCollisionTool, ...). '
                                          '"Find everything" fills the tool paths from it')
    game_path: StringProperty(name='Game folder', subtype='DIR_PATH', description='The folder that holds starwarsbattlefrontii.exe')
    frostycli: StringProperty(name='FrostyCLI.exe', subtype='FILE_PATH')
    plugins_path: StringProperty(name='Frosty plugins folder', subtype='DIR_PATH', description='The Plugins folder of Frosty Editor')
    havok_tool: StringProperty(name='HavokCollisionTool', subtype='FILE_PATH', description='HavokCollisionTool.dll')
    havok_template: StringProperty(name='Collision template', subtype='FILE_PATH', description='template.res (template.resmeta next to it)')
    fluxbake: StringProperty(name='Light baker', subtype='FILE_PATH', description='fluxbake executable')
    work_dir: StringProperty(name='Work folder', subtype='DIR_PATH', description='Where builds are kept: needs several GB free')
    python: StringProperty(name='Python', subtype='FILE_PATH', default='',
                           description='Leave empty to use the Python inside Blender (press "Install Python packages" once). '
                                       'Or pick a python.exe 3.10+ that has numpy, Pillow, scipy and xatlas')
    blender_exe: StringProperty(name='Blender for the export', subtype='FILE_PATH', default='',
                                description='Leave empty to use this Blender. Set it to a blender.exe from blender.org when this '
                                            'Blender cannot be started by other programs (Microsoft Store install)')
    runtime_dir: StringProperty(name='Runtime folder', subtype='DIR_PATH', description='The folder that holds the bf2maptool package', default='')
    author: StringProperty(name='Mod author', default='')
    setup: CollectionProperty(type=BF2SetupLine)

    def draw(self, context):
        col = self.layout.column()
        box = col.box()
        box.label(text='New here? 1 unzip the tool bundle   2 pick it below   3 Find everything   4 Install Python packages   5 Check setup',
                  icon='INFO')
        box.prop(self, 'tools_dir')
        row = box.row(align=True)
        row.operator('bf2.find_paths', icon='VIEWZOOM')
        row.operator('bf2.install_packages', icon='IMPORT')
        row = box.row(align=True)
        row.operator('bf2.import_settings', icon='FILE_FOLDER')
        row.operator('bf2.export_settings', icon='FILE_TICK')
        col.separator()
        for p in ('game_path', 'frostycli', 'plugins_path', 'havok_tool', 'havok_template', 'fluxbake', 'work_dir', 'python', 'blender_exe', 'runtime_dir', 'author'):
            col.prop(self, p)
        if not self.python:
            col.label(text='Python: the one inside Blender (%s)' % (python_exe(self) or 'not found'), icon='INFO')
        col.operator('bf2.check_setup', icon='CHECKMARK')
        for line in self.setup:
            row = col.row()
            row.label(text='%s: %s' % (line.item, line.message), icon='CHECKMARK' if line.ok else 'ERROR')
            if not line.ok and line.fix:
                col.label(text='      ' + line.fix)


def prefs(context=None):
    return (context or bpy.context).preferences.addons[ADDON].preferences


def python_exe(p):
    """the Python the build runs in: the one picked, else Blender's own"""
    return bpy.path.abspath(p.python) if p.python else detect.blender_python()


def runtime_dir(p):
    return bpy.path.abspath(p.runtime_dir) if p.runtime_dir else _default_runtime()


_ver_cache = {}


def python_version(exe):
    if exe not in _ver_cache:
        _ver_cache[exe] = detect._python_works(exe)[1]
    return _ver_cache[exe]


def pylib_dir(p):
    """the private package folder of the build's Python (pip --target): <work>/_python/py<major><minor>; '' without a work folder"""
    wd = bpy.path.abspath(p.work_dir) if p.work_dir else ''
    v = python_version(python_exe(p))
    if not wd or not v:
        return ''
    return os.path.join(wd, '_python', 'py' + v.replace('.', '')).replace('\\', '/')


def runtime_env(p):
    """the environment of every runtime process: the runtime and the private packages first on the import path"""
    env = dict(os.environ)
    parts = [runtime_dir(p)]
    lib = pylib_dir(p)
    if lib and os.path.isdir(lib):
        parts.append(lib)
    env['PYTHONPATH'] = os.pathsep.join(parts + ([env['PYTHONPATH']] if env.get('PYTHONPATH') else []))
    env['PYTHONUNBUFFERED'] = '1'
    env.pop('PYTHONHOME', None)                     # Blender's Python must find its own standard library
    return env


def settings_dict(p):
    ab = lambda v: bpy.path.abspath(v).replace('\\', '/').rstrip('/') if v else ''
    return {'game_path': ab(p.game_path), 'frostycli': ab(p.frostycli), 'plugins_path': ab(p.plugins_path), 'havok_tool': ab(p.havok_tool),
            'havok_template': ab(p.havok_template), 'fluxbake': ab(p.fluxbake), 'work_dir': ab(p.work_dir),
            'blender': (ab(p.blender_exe) or bpy.app.binary_path.replace('\\', '/')), 'python': python_exe(p).replace('\\', '/'),
            'pylib': pylib_dir(p), 'author': p.author or 'bf2maptool'}


def write_settings(p):
    """settings.json in the work folder; returns its path or raises ValueError with what is missing"""
    s = settings_dict(p)
    if not s['work_dir']:
        raise ValueError('Set the work folder in the add-on preferences first.')
    os.makedirs(s['work_dir'], exist_ok=True)
    path = os.path.join(s['work_dir'], 'settings.json')
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(s, f, indent=1)
    return path


def find_paths(p):
    """fill every empty or broken path the machine can answer; -> list of (preference, path, how)"""
    cur = {k: bpy.path.abspath(getattr(p, k)) if getattr(p, k) else '' for k in PATH_KEYS}
    roots = [bpy.path.abspath(p.tools_dir)] if p.tools_dir else []
    found = detect.find_all(cur, roots)
    done = []
    for k, (path, how) in found.items():
        setattr(p, k, path)
        done.append((k, path, how))
    if not p.work_dir:
        # next to the bundle when there is one, else on the drive with the most free space
        import shutil
        base = bpy.path.abspath(p.tools_dir) if p.tools_dir else ''
        if not base:
            drives = [d for d in detect._drives() if d.upper() != 'C:/'] or detect._drives()
            best = max(drives, key=lambda d: shutil.disk_usage(d).free if os.path.isdir(d) else 0)
            base = os.path.join(best, 'BF2MapTool_work')
            p.work_dir = base.replace('\\', '/')
        else:
            p.work_dir = os.path.join(base, 'work').replace('\\', '/')
        done.append(('work_dir', p.work_dir, 'proposed (change it if you like)'))
    return done


def install_packages(py, lib, timeout=1200):
    """pip install the build's packages into `lib`, the private folder of the Python `py` (plain strings: this runs in a
    thread, away from Blender's data); -> (ok, message)"""
    if not py or not os.path.isfile(py):
        return False, 'No Python: leave Python empty (Blender\'s own is used) or pick a python.exe.'
    if not lib:
        return False, 'Set the work folder first (the packages are installed into <work folder>/_python).'
    os.makedirs(lib, exist_ok=True)
    cmd = [py, '-m', 'pip', 'install', '--disable-pip-version-check', '--no-input', '--upgrade', '--target', lib] + list(PACKAGES)
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL, env=dict(os.environ, PIP_NO_INPUT='1'),
                           creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    except subprocess.TimeoutExpired:
        return False, 'pip did not finish within %d minutes (no internet?)' % (timeout // 60)
    except OSError as e:
        return False, 'pip could not be started: %s' % e
    with open(os.path.join(lib, 'pip_install.log'), 'w', encoding='utf-8') as f:
        f.write(' '.join(cmd) + '\n\n' + (r.stdout or '') + '\n' + (r.stderr or ''))
    if r.returncode != 0:
        last = [l for l in ((r.stderr or '') + (r.stdout or '')).splitlines() if l.strip()][-1:] or ['?']
        return False, 'pip failed: %s  (log: %s/pip_install.log)' % (last[0][:200], lib)
    return True, 'Installed %s into %s' % (', '.join(PACKAGES), lib)


CLASSES = (BF2SetupLine, BF2Preferences)
