"""Every fast test of the tool in one go, with a table at the end (no game, no full build):

    python tests/run_all.py [--blenders 5.2,4.3] [--out <dir>]      exit code 0 = all green

  compile        every .py of the add-on, the runtime and the tests compiles
  unit           tests/unit/*.py (auto-detect, check grouping)
  checks         the pre-flight checks suite (tests/checks: 46 scenes), with THIS Python
  newuser <v>    new profile: install zip, Find everything, Install Python packages, Check setup (per Blender version)
  extension <v>  the extension zip: validate, install, register (per Blender version)
  instances      collection / Geometry Nodes / particle instances are exported with exact triangle counts
  bugreport      Make bug report after a check run
Needs: the extracted tool bundle in tmp/night/bundle (tools/make_bundle.py + unzip), the add-on zips (tools/make_zip.py).
Every step has a time limit; scratch output goes to --out (default E:/BF2MapTool_night/run_all).
"""
import argparse
import glob
import json
import os
import py_compile
import subprocess
import sys
import time

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
ap = argparse.ArgumentParser()
ap.add_argument('--blenders', default='5.2')
ap.add_argument('--out', default='E:/BF2MapTool_night/run_all')
ap.add_argument('--bundle', default=os.path.join(ROOT, 'tmp', 'night', 'bundle', 'BF2MapTool_WIP_2026-10-12'))
ap.add_argument('--dist', default=os.path.join(ROOT, 'tmp', 'night', 'dist'))
a = ap.parse_args()
OUT = a.out
os.makedirs(OUT, exist_ok=True)
rows = []
NOWIN = getattr(subprocess, 'CREATE_NO_WINDOW', 0)


def blender(v):
    return 'C:/Program Files/Blender Foundation/Blender %s/blender.exe' % v


def run(name, cmd, timeout, env=None, ok_text=None, cwd=None):
    t = time.time()
    log = os.path.join(OUT, name.replace(' ', '_') + '.log')
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL, env=env, cwd=cwd,
                           creationflags=NOWIN, encoding='utf-8', errors='replace')
        text, rc = (r.stdout or '') + (r.stderr or ''), r.returncode
    except subprocess.TimeoutExpired:
        text, rc = 'TIMEOUT after %d s' % timeout, 124
    open(log, 'w', encoding='utf-8').write(text)
    ok = rc == 0 and (ok_text is None or ok_text in text)
    last = [l for l in text.splitlines() if l.strip()][-1:] or ['']
    rows.append((name, ok, round(time.time() - t, 1), last[0][:110]))
    print('%-4s %-16s %6.1f s  %s' % ('PASS' if ok else 'FAIL', name, rows[-1][2], rows[-1][3]), flush=True)
    return ok


# compile
t = time.time()
bad = []
for pat in ('addon/bf2_map_tool/**/*.py', 'runtime/bf2maptool/**/*.py', 'tests/**/*.py', 'tools/*.py'):
    for f in glob.glob(os.path.join(ROOT, pat), recursive=True):
        try:
            py_compile.compile(f, cfile=os.path.join(OUT, 'pyc', os.path.basename(f) + 'c'), doraise=True)
        except py_compile.PyCompileError as e:
            bad.append('%s: %s' % (os.path.relpath(f, ROOT), str(e).splitlines()[-1][:80]))
rows.append(('compile', not bad, round(time.time() - t, 1), '; '.join(bad)[:110] or 'all files compile'))
print('%-4s %-16s %6.1f s  %s' % ('PASS' if not bad else 'FAIL', 'compile', rows[-1][2], rows[-1][3]))
# unit tests
for f in sorted(glob.glob(os.path.join(ROOT, 'tests', 'unit', 'test_*.py'))):
    n = os.path.splitext(os.path.basename(f))[0]
    run('unit ' + n[5:], [sys.executable, f] + ([os.path.join(OUT, n)] if 'detect' in n else []), 300)
# checks suite
B0 = blender(a.blenders.split(',')[0])
run('checks', [sys.executable, os.path.join(ROOT, 'tests', 'checks', 'run_checks.py'), os.path.join(ROOT, 'tests', 'checks', 'scenes'),
               os.path.join(ROOT, 'work', 'settings.json'), B0, os.path.join(ROOT, 'addon', 'bf2_map_tool', 'export_scene.py'),
               os.path.join(OUT, 'checks.json')], 1800, ok_text='cases pass')
zips = sorted(glob.glob(os.path.join(a.dist, 'bf2_map_tool-*.zip')), key=os.path.getmtime)
legacy = [z for z in zips if '-extension' not in z][-1]
ext = [z for z in zips if '-extension' in z][-1]
for v in a.blenders.split(','):
    tag = v.replace('.', '')
    base = os.path.join(OUT, 'nu_' + tag)
    for d in ('profile', 'work'):
        os.makedirs(os.path.join(base, d), exist_ok=True)
    env = dict(os.environ, BLENDER_USER_SCRIPTS=os.path.join(base, 'profile'), TMP=OUT, TEMP=OUT)
    run('newuser ' + v, [blender(v), '-b', '--factory-startup', '--python', os.path.join(ROOT, 'tests', 'addon', 'test_newuser.py'), '--',
                         legacy, a.bundle, os.path.join(base, 'work'), os.path.join(base, 'result.json')], 1800, env=env, ok_text='ADDON_TEST')
    eb = os.path.join(OUT, 'ext_' + tag)
    for d in ('scripts', 'config', 'ext', 'work'):
        os.makedirs(os.path.join(eb, d), exist_ok=True)
    eenv = dict(os.environ, BLENDER_USER_SCRIPTS=os.path.join(eb, 'scripts'), BLENDER_USER_CONFIG=os.path.join(eb, 'config'),
                BLENDER_USER_EXTENSIONS=os.path.join(eb, 'ext'), TMP=OUT, TEMP=OUT)
    run('ext validate ' + v, [blender(v), '--command', 'extension', 'validate', ext], 300, env=eenv)
    run('ext install ' + v, [blender(v), '--command', 'extension', 'install-file', '-r', 'user_default', '-e', ext], 300, env=eenv)
    run('extension ' + v, [blender(v), '-b', '--python', os.path.join(ROOT, 'tests', 'addon', 'test_extension.py'), '--', a.bundle,
                           os.path.join(eb, 'work'), os.path.join(eb, 'result.json')], 600, env=eenv, ok_text='EXT_TEST')
# instances
ib = os.path.join(OUT, 'inst')
os.makedirs(ib, exist_ok=True)
settings_nu = os.path.join(OUT, 'nu_' + a.blenders.split(',')[0].replace('.', ''), 'work', 'settings.json')
if run('instances: make', [B0, '-b', '--factory-startup', '--python', os.path.join(ROOT, 'tests', 'scenes', 'make_instancetest.py'), '--',
                           os.path.join(ib, 'insttest.blend'), os.path.join(ROOT, 'addon', 'bf2_map_tool')], 300, ok_text='MADE'):
    if run('instances: export', [sys.executable, '-m', 'bf2maptool', 'check', os.path.join(ib, 'insttest.blend'), '--settings',
                                 settings_nu if os.path.exists(settings_nu) else os.path.join(ROOT, 'work', 'settings.json'), '--blender', B0,
                                 '--export-script', os.path.join(ROOT, 'addon', 'bf2_map_tool', 'export_scene.py'), '--export-dir',
                                 os.path.join(ib, 'export')], 600, cwd=os.path.join(ROOT, 'runtime'), ok_text='@@DONE ok'):
        run('instances', [sys.executable, os.path.join(ROOT, 'tests', 'scenes', 'check_instancetest.py'), os.path.join(ib, 'export', 'map.json'),
                          os.path.join(ib, 'insttest.blend.expect.json')], 60)
# vertex-colour tints
tb = os.path.join(OUT, 'tint')
os.makedirs(tb, exist_ok=True)
if run('tints: make', [B0, '-b', '--factory-startup', '--python', os.path.join(ROOT, 'tests', 'scenes', 'make_tinttest.py'), '--',
                       os.path.join(tb, 'tinttest.blend'), os.path.join(ROOT, 'addon', 'bf2_map_tool')], 300, ok_text='MADE'):
    run('tints: export', [sys.executable, '-m', 'bf2maptool', 'check', os.path.join(tb, 'tinttest.blend'), '--settings',
                          settings_nu if os.path.exists(settings_nu) else os.path.join(ROOT, 'work', 'settings.json'), '--blender', B0,
                          '--export-script', os.path.join(ROOT, 'addon', 'bf2_map_tool', 'export_scene.py'), '--export-dir',
                          os.path.join(tb, 'export')], 600, cwd=os.path.join(ROOT, 'runtime'), ok_text='@@DONE')
    run('tints', [sys.executable, os.path.join(ROOT, 'tests', 'scenes', 'check_tinttest.py'), os.path.join(tb, 'export'),
                  os.path.join(tb, 'tinttest.blend.expect.json')], 60)
# material inputs as real files wire them (ORM / gloss / normal strength / emission texture), and MatTest's constants
for tag, maker, checker in (('orm', 'make_ormtest.py', 'check_ormtest.py'), ('mattest', 'make_mattest.py', 'check_mattest_inputs.py')):
    mb = os.path.join(OUT, tag)
    os.makedirs(mb, exist_ok=True)
    blend = os.path.join(mb, tag + '.blend')
    if run('%s: make' % tag, [B0, '-b', '--factory-startup', '--python', os.path.join(ROOT, 'tests', 'scenes', maker), '--', blend,
                              os.path.join(ROOT, 'addon', 'bf2_map_tool')], 300, ok_text='MADE'):
        if run('%s: export' % tag, [sys.executable, '-m', 'bf2maptool', 'check', blend, '--settings',
                                    settings_nu if os.path.exists(settings_nu) else os.path.join(ROOT, 'work', 'settings.json'), '--blender', B0,
                                    '--export-script', os.path.join(ROOT, 'addon', 'bf2_map_tool', 'export_scene.py'), '--export-dir',
                                    os.path.join(mb, 'export')], 900, cwd=os.path.join(ROOT, 'runtime'), ok_text='@@DONE'):
            args = [os.path.join(mb, 'export', 'map.json')] + ([blend + '.expect.json'] if tag == 'orm' else [])
            run(tag, [sys.executable, os.path.join(ROOT, 'tests', 'scenes', checker)] + args, 60)
# Fix buttons on Check results
fb = os.path.join(OUT, 'fixes')
os.makedirs(os.path.join(fb, 'profile'), exist_ok=True)
run('fix buttons', [B0, '-b', '--factory-startup', '--python', os.path.join(ROOT, 'tests', 'addon', 'test_fixes.py'), '--', legacy, a.bundle,
                    os.path.join(OUT, 'nu_' + a.blenders.split(',')[0].replace('.', ''), 'work'), os.path.join(ROOT, 'tests', 'checks', 'scenes'),
                    os.path.join(fb, 'result.json')], 2400,
    env=dict(os.environ, BLENDER_USER_SCRIPTS=os.path.join(fb, 'profile'), TMP=OUT, TEMP=OUT), ok_text='FIXES_TEST ok=True')
# class guess on a ripped map's node group (alpha routed from a channel without holes)
run('guess alpha', [B0, '-b', '--factory-startup', '--python', os.path.join(ROOT, 'tests', 'addon', 'test_guess_alpha.py')], 300, ok_text='PASS')
# bug report
bb = os.path.join(OUT, 'bugrep')
os.makedirs(os.path.join(bb, 'profile'), exist_ok=True)
run('bugreport', [B0, '-b', os.path.join(ROOT, 'tests', 'checks', 'scenes', 'light_budget.blend'), '--factory-startup', '--python',
                  os.path.join(ROOT, 'tests', 'addon', 'test_bugreport.py'), '--', legacy, a.bundle,
                  os.path.join(OUT, 'nu_' + a.blenders.split(',')[0].replace('.', ''), 'work'), os.path.join(bb, 'result.json')], 1200,
    env=dict(os.environ, BLENDER_USER_SCRIPTS=os.path.join(bb, 'profile'), TMP=OUT, TEMP=OUT), ok_text='BUGREPORT_TEST')
n_ok = sum(1 for r in rows if r[1])
print('\n%-16s %-5s %8s  %s' % ('test', 'ok', 'seconds', 'last line'))
for r in rows:
    print('%-16s %-5s %8.1f  %s' % (r[0], 'PASS' if r[1] else 'FAIL', r[2], r[3]))
print('\n%d of %d green' % (n_ok, len(rows)))
json.dump({'pass': n_ok, 'total': len(rows), 'rows': rows, 'when': time.strftime('%Y-%m-%d %H:%M')}, open(os.path.join(OUT, 'run_all.json'), 'w'), indent=1)
sys.exit(0 if n_ok == len(rows) else 1)
