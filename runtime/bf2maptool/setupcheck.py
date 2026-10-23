"""Check setup: every external tool is started with a harmless command (each with a time limit) and reported as one
line: what it is, whether it works, and what to do when it does not. Printed as '@@SETUP {json}' for the add-on."""
import importlib
import json
import os
import shutil
import subprocess


def _run(cmd, timeout):
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL)
        return r.returncode, (r.stdout or '') + (r.stderr or '')
    except subprocess.TimeoutExpired:
        return 124, 'no answer within %d s' % timeout
    except OSError as e:
        return 127, str(e)


def run(s):
    out = []

    def add(item, ok, message, fix=''):
        out.append({'item': item, 'ok': bool(ok), 'message': message, 'fix': '' if ok else fix})
    g = s.get('game_path', '')
    exe = os.path.join(g, 'starwarsbattlefrontii.exe')
    add('Game folder', os.path.exists(exe), g if os.path.exists(exe) else 'starwarsbattlefrontii.exe is not in "%s"' % g,
        'Pick the folder the game is installed in (it holds starwarsbattlefrontii.exe).')
    c = s.get('frostycli', '')
    if not os.path.exists(c):
        add('FrostyCLI', False, 'not found: "%s"' % c, 'Pick FrostyCLI.exe.')
    else:
        rc, text = _run([c, 'man'], 90)
        ok = rc == 0 and 'port' in text and 'level' in text
        add('FrostyCLI', ok, 'answers (commands: port, level, navmesh ...)' if ok else 'started but did not list its commands (exit %d)' % rc,
            'Use the FrostyCLI build that came with the tool; .NET must be installed.')
    p = s.get('plugins_path', '')
    dll = os.path.join(p, 'MeshSetPlugin.dll')
    add('Frosty plugins', os.path.exists(dll), p if os.path.exists(dll) else 'MeshSetPlugin.dll is not in "%s"' % p,
        'Pick the Plugins folder of Frosty Editor.')
    dn = shutil.which(s.get('dotnet') or 'dotnet')
    add('.NET runtime', bool(dn), dn or 'the dotnet command was not found', 'Install the .NET 8 runtime from dotnet.microsoft.com.')
    h = s.get('havok_tool', '')
    if not os.path.exists(h):
        add('HavokCollisionTool', False, 'not found: "%s"' % h, 'Pick HavokCollisionTool.dll.')
    elif dn:
        rc, text = _run([dn, h], 60)
        ok = 'HavokCollisionTool' in text
        add('HavokCollisionTool', ok, 'answers' if ok else 'started but did not show its usage (exit %d)' % rc, 'It needs the .NET 8 runtime.')
    t = s.get('havok_template', '')
    meta = os.path.splitext(t)[0] + '.resmeta'
    ok = os.path.exists(t) and os.path.exists(meta)
    add('Collision template', ok, t if ok else 'template .res and .resmeta are needed side by side ("%s")' % t, 'Pick template.res from the tool\'s folder.')
    f = s.get('fluxbake', '')
    add('Light baker', os.path.exists(f), f if os.path.exists(f) else 'not found: "%s"' % f, 'Pick the fluxbake executable (only needed for Bake lighting).')
    b = s.get('blender', '')
    add('Blender', os.path.exists(b), b if os.path.exists(b) else 'not found: "%s"' % b, 'The add-on fills this in by itself; restart Blender.')
    w = s.get('work_dir', '')
    try:
        os.makedirs(w, exist_ok=True)
        probe = os.path.join(w, '.write_test')
        with open(probe, 'w') as fh:
            fh.write('x')
        os.remove(probe)
        free = shutil.disk_usage(w).free / 1e9
        add('Work folder', free >= 10.0, '%s (%.0f GB free)' % (w, free) if free >= 10.0 else '%s has only %.1f GB free' % (w, free),
            'A map build needs about 3 GB, a light bake several more: pick a drive with at least 10 GB free.')
    except OSError as e:
        add('Work folder', False, 'cannot write to "%s": %s' % (w, e), 'Pick a folder you can write to.')
    import sys
    add('Python', sys.version_info >= (3, 10), '%s %d.%d' % (sys.executable.replace('\\', '/'), sys.version_info[0], sys.version_info[1]),
        'Python 3.10 or newer is needed: leave Python empty in the preferences to use Blender\'s own.')
    for mod, why, need in (('numpy', 'all stages', True), ('PIL', 'texture packing', True), ('scipy', 'collision hulls, light bake', False),
                           ('xatlas', 'light bake', False)):
        try:
            m = importlib.import_module(mod)
            add('Python: ' + mod, True, 'version %s' % getattr(m, '__version__', '?'))
        except ImportError:
            pip = {'PIL': 'pillow'}.get(mod, mod)
            add('Python: ' + mod, False, 'not installed (needed for %s)' % why,
                'Press "Install Python packages" in the add-on preferences (or run: python -m pip install %s)' % pip)
    return out


def main(settings):
    lines = run(settings)
    for l in lines:
        print('@@SETUP ' + json.dumps(l), flush=True)
    return 0 if all(l['ok'] for l in lines) else 1
