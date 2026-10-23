"""Find the machine paths by themselves, so a new user does not type eight paths: the game (EA app / Origin / Steam /
Epic registry entries, then the usual folders), the tool bundle (FrostyCLI, plugins, HavokCollisionTool, collision
template, light baker) and a Python. No bpy here: the same code runs in Blender and in the tests.

    find_all(current) -> {key: (path, how)} for every key it could fill; keys = the add-on preference names
"""
import glob
import os
import re
import shutil
import string
import subprocess
import sys

GAME_EXE = 'starwarsbattlefrontii.exe'
# the layout of the WIP bundle (tools/make_bundle.py) and of the development PC
BUNDLE = {
    'frostycli': ('FrostyCLI/FrostyCLI.exe', 'FrostyCLI/bin/x64/Release/FrostyCLI.exe', 'cli-wt/FrostyCLI/bin/x64/Release/FrostyCLI.exe'),
    'plugins_path': ('FrostyPlugins', 'Plugins', 'Frosty Editor/Plugins'),
    'havok_tool': ('HavokCollisionTool/HavokCollisionTool.dll', 'HavokCollisionTool/bin/Release/net8.0/HavokCollisionTool.dll'),
    'havok_template': ('collision_template/template.res', 'col/template.res'),
    'fluxbake': ('fluxbake/fluxbake_src.exe', 'fluxbake/fluxbake.exe', 'cli-wt/tools/fluxbake/bin/fluxbake_src.exe'),
}
BUNDLE_NAMES = ('BF2MapTool*', 'BF2_Map_Tool*', 'bf2maptool*')


def _norm(p):
    return os.path.normpath(p).replace('\\', '/') if p else ''


def _ok(key, path):
    """is this a usable value for this preference"""
    if not path:
        return False
    if key == 'game_path':
        return os.path.isfile(os.path.join(path, GAME_EXE))
    if key == 'plugins_path':
        return os.path.isfile(os.path.join(path, 'MeshSetPlugin.dll'))
    if key == 'havok_template':
        return os.path.isfile(path) and os.path.isfile(os.path.splitext(path)[0] + '.resmeta')
    return os.path.isfile(path)


def _drives():
    if os.name != 'nt':
        return ['/']
    return ['%s:/' % d for d in string.ascii_uppercase if os.path.isdir('%s:/' % d)]


def _reg_values(paths_names):
    """registry string values; [] off Windows"""
    out = []
    try:
        import winreg
    except ImportError:
        return out
    for hive, key, name in paths_names:
        for view in (winreg.KEY_WOW64_32KEY, winreg.KEY_WOW64_64KEY):
            try:
                with winreg.OpenKey(hive, key, 0, winreg.KEY_READ | view) as k:
                    v, _ = winreg.QueryValueEx(k, name)
                    if isinstance(v, str) and v:
                        out.append(v)
            except OSError:
                pass
    return out


def _steam_libraries():
    roots = []
    try:
        import winreg
        roots += _reg_values([(winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam', 'SteamPath'),
                              (winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\Valve\Steam', 'InstallPath')])
    except ImportError:
        pass
    libs = []
    for r in roots:
        vdf = os.path.join(r, 'steamapps', 'libraryfolders.vdf')
        libs.append(r)
        try:
            with open(vdf, encoding='utf-8', errors='replace') as f:
                libs += vdf_paths(f.read())
        except OSError:
            pass
    return libs


def vdf_paths(text):
    """the library folders listed in Steam's libraryfolders.vdf ("path" "D:\\\\SteamLibrary")"""
    return [p.replace('\\\\', '\\') for p in re.findall(r'"path"\s+"([^"]+)"', text)]


def game_candidates():
    c = []
    try:
        import winreg
        H = winreg.HKEY_LOCAL_MACHINE
        c += _reg_values([(H, r'SOFTWARE\EA Games\STAR WARS Battlefront II', 'Install Dir'),
                          (H, r'SOFTWARE\Origin Games\1053088', 'Install Dir')])
    except ImportError:
        pass
    for lib in _steam_libraries():
        c.append(os.path.join(lib, 'steamapps', 'common', 'STAR WARS Battlefront II'))
    for d in _drives():
        for sub in ('STAR WARS Battlefront II', 'Games/STAR WARS Battlefront II', 'EA Games/STAR WARS Battlefront II',
                    'Program Files/EA Games/STAR WARS Battlefront II', 'Program Files (x86)/Origin Games/STAR WARS Battlefront II',
                    'Epic Games/STARWARSBattlefrontII', 'Program Files/Epic Games/STARWARSBattlefrontII',
                    'SteamLibrary/steamapps/common/STAR WARS Battlefront II'):
            c.append(os.path.join(d, sub))
    return c


def find_game():
    for p in game_candidates():
        if _ok('game_path', p):
            return _norm(p)
    return ''


def bundle_roots(extra=()):
    """folders that may hold the tool bundle: the ones given, the add-on's own neighbourhood, <drive>:/BF2MapTool*"""
    roots = [r for r in extra if r]
    here = os.path.dirname(os.path.abspath(__file__))
    roots += [os.path.join(here, 'tools'), here, os.path.dirname(here)]
    for d in _drives():
        for pat in BUNDLE_NAMES:
            roots += glob.glob(os.path.join(d, pat))
            roots += glob.glob(os.path.join(d, '*', pat))      # e.g. D:/Mods/BF2MapTool_WIP_2026-10-12
    out, seen = [], set()
    for r in roots:
        if os.path.isdir(r) and os.path.normcase(os.path.abspath(r)) not in seen:
            seen.add(os.path.normcase(os.path.abspath(r)))
            out.append(r)
            # an unzipped bundle may sit one folder down (BF2MapTool_WIP_<date>/BF2MapTool_WIP_<date>/...)
            for sub in sorted(glob.glob(os.path.join(r, 'BF2MapTool*'))):
                if os.path.isdir(sub) and os.path.normcase(os.path.abspath(sub)) not in seen:
                    seen.add(os.path.normcase(os.path.abspath(sub)))
                    out.append(sub)
    return out


def find_tools(roots):
    """{key: path} for the bundle tools found under the first root that has each"""
    got = {}
    for key, rels in BUNDLE.items():
        for r in roots:
            hit = next((os.path.join(r, rel) for rel in rels if _ok(key, os.path.join(r, rel))), None)
            if hit:
                got[key] = _norm(hit)
                break
    return got


def _python_works(exe, timeout=20):
    """-> (ok, version) for a python that really runs (the Windows Store stub opens the Store instead)"""
    if not exe or not os.path.isfile(exe) or 'windowsapps' in exe.lower():
        return False, ''
    try:
        r = subprocess.run([exe, '-c', 'import sys; print("%d.%d" % sys.version_info[:2])'], capture_output=True, text=True, timeout=timeout,
                           stdin=subprocess.DEVNULL, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    except (OSError, subprocess.TimeoutExpired):
        return False, ''
    v = (r.stdout or '').strip()
    ok = r.returncode == 0 and bool(re.match(r'3\.(1\d|[2-9]\d)$', v))     # 3.10 or newer
    return ok, v


def blender_python():
    """the Python that ships with Blender (sys.executable inside Blender); '' outside Blender"""
    exe = sys.executable or ''
    return exe if os.path.basename(exe).lower().startswith('python') else ''


def find_python():
    """a working Python 3.10+: python.org installs first, then PATH, then Blender's own"""
    c = []
    la = os.environ.get('LOCALAPPDATA', '')
    if la:
        c += sorted(glob.glob(os.path.join(la, 'Programs', 'Python', 'Python3*', 'python.exe')), reverse=True)
    for pf in (os.environ.get('ProgramFiles', ''), os.environ.get('ProgramFiles(x86)', '')):
        if pf:
            c += sorted(glob.glob(os.path.join(pf, 'Python3*', 'python.exe')), reverse=True)
    c += [shutil.which('python') or '', shutil.which('python3') or '']
    for exe in c:
        if _python_works(exe)[0]:
            return _norm(exe)
    return ''


def find_all(current=None, extra_roots=()):
    """{key: (path, how)} for every empty (or broken) preference that can be filled; values already working are kept"""
    current = current or {}
    out = {}
    if not _ok('game_path', current.get('game_path')):
        g = find_game()
        if g:
            out['game_path'] = (g, 'found from the game\'s install entry')
    want = [k for k in BUNDLE if not _ok(k, current.get(k))]
    if want:
        tools = find_tools(bundle_roots(list(extra_roots)))
        for k in want:
            if k in tools:
                out[k] = (tools[k], 'found in the tool bundle')
    return out
