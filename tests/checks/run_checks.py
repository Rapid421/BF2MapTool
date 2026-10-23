"""Run the tool's `check` command on every check scene and compare the reported codes with what each scene must trip.
python run_checks.py <scenes dir> <settings.json> <blender.exe> <export script> <out.json>
A case passes when every expected code is reported and, for `clean`, no error or warning is.
"""
import json
import os
import subprocess
import sys
import time

D, SETTINGS, BLENDER, SCRIPT, OUT = [os.path.abspath(a) for a in sys.argv[1:6]]    # the runtime runs in its own folder
RUNTIME = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime'))
cases = json.load(open(os.path.join(D, 'cases.json')))
res, ok_all = {}, True
t0 = time.time()
for name, want in cases.items():
    exp = os.path.join(D, 'export', name)
    os.makedirs(exp, exist_ok=True)
    try:
        r = subprocess.run([sys.executable, '-m', 'bf2maptool', 'check', os.path.join(D, name + '.blend'), '--settings', SETTINGS, '--blender', BLENDER,
                            '--export-script', SCRIPT, '--export-dir', exp], cwd=RUNTIME, capture_output=True, text=True, timeout=300,
                           stdin=subprocess.DEVNULL)
        out = r.stdout + r.stderr
    except subprocess.TimeoutExpired:
        out = '@@DONE timeout'
    got = []
    for line in out.splitlines():
        if line.startswith('@@RESULT '):
            j = json.loads(line[9:])
            got.append((j['severity'], j['code']))
    done = [l for l in out.splitlines() if l.startswith('@@DONE')][-1:] or ['(no @@DONE line)']
    codes = {c for _, c in got}
    missing = [c for c in want if c not in codes]
    extra_bad = sorted({c for s, c in got if s in ('error', 'warning') and c not in want})
    ok = not missing and (name != 'clean' or not [g for g in got if g[0] in ('error', 'warning')])
    if name in ('geo_auto_uv', 'class_guess_fallback') and [g for g in got if g[0] == 'error']:
        ok = False                                   # these two must NOT stop a build: the tool repairs them itself
    if '(no @@DONE' in done[0] or 'timeout' in done[0] or done[0].startswith('@@DONE blocked') or 'check.internal' in codes:
        ok = False
    res[name] = {'pass': ok, 'expected': want, 'reported': sorted(codes), 'missing': missing, 'other_errors_or_warnings': extra_bad, 'done': done[0][:160]}
    ok_all = ok_all and ok
    print('%-5s %-22s expected %-44s reported %s%s' % ('PASS' if ok else 'FAIL', name, want, sorted(codes), ('  MISSING %s' % missing) if missing else ''), flush=True)

# checks the add-on's own controls cannot trip (they guard hand-written or damaged map files): patch an exported map file
sys.path.insert(0, RUNTIME)
from bf2maptool import checks, config                                                       # noqa: E402


def patched(name, src_case, want, patch):
    global ok_all
    src = os.path.join(D, 'export', src_case)
    with open(os.path.join(src, 'map.json'), encoding='utf-8') as f:
        m = json.load(f)
    patch(m)
    path = os.path.join(src, 'map_%s.json' % name)
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(m, f)
    try:
        codes = sorted({r['code'] for r in checks.preflight(config.Job(path, config.load_settings(SETTINGS)))})
    except Exception as e:
        codes = ['EXCEPTION %s: %s' % (type(e).__name__, e)]
    missing = [c for c in want if c not in codes]
    res[name] = {'pass': not missing, 'expected': want, 'reported': codes, 'missing': missing, 'patched_from': src_case}
    ok_all = ok_all and not missing
    print('%-5s %-22s expected %-44s reported %s' % ('PASS' if not missing else 'FAIL', name, want, codes), flush=True)


def _door_axis(m):
    for o in m['objects']:
        if o.get('role') == 'door':
            o['door']['axis'] = ''


def _not_baked(m):
    next(iter(m['materials'].values()))['baked'] = {'notes': [], 'not_baked': ['normal']}


def _broken_geo(m):
    for o in m['objects']:
        if o.get('role') == 'static':
            o['geo'] = 'geo/does_not_exist.npz'


def _odd_class(m):
    next(iter(m['materials'].values()))['class'] = 'water'      # a class no host has a template for


patched('door_direction', 'door_shape', ['door.direction'], _door_axis)
patched('class_unsupported', 'clean', ['class.unsupported'], _odd_class)
patched('tex_bake_incomplete', 'clean', ['tex.bake_incomplete'], _not_baked)
patched('check_internal', 'clean', ['check.internal'], _broken_geo)

json.dump({'ok': ok_all, 'seconds': round(time.time() - t0, 1), 'cases': res}, open(OUT, 'w'), indent=1)
print('%d of %d cases pass in %.0f s' % (sum(r['pass'] for r in res.values()), len(res), time.time() - t0))
sys.exit(0 if ok_all else 1)
