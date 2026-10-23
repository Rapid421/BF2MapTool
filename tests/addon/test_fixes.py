"""P1-3: every Fix button really fixes. For each case scene: Check (the code must be reported), the Fix operator the Check
tab draws for that code, Check again (the code must be gone).
    blender -b --factory-startup --python test_fixes.py -- <zip> <bundle dir> <work dir> <check scenes dir> <out.json>
"""
import json
import os
import sys
import time
import traceback

import bpy

zip_path, bundle, work, scenes, out = sys.argv[sys.argv.index('--') + 1:][:5]
CASES = {'light_budget': 'light.dynamic_budget', 'class_guessed': 'class.guessed', 'supremacy_letters': 'supremacy.letters',
         'map_name_clash': 'map.name_clash'}
res, ok = {}, True
try:
    bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)
    bpy.ops.preferences.addon_enable(module='bf2_map_tool')
    from bf2_map_tool import build, ops as O, prefs as P
    p = P.prefs()
    p.tools_dir, p.work_dir = bundle, work
    bpy.ops.bf2.find_paths()
    if not os.path.isdir(P.pylib_dir(p)):
        bpy.ops.bf2.install_packages(); O._pip['thread'] and O._pip['thread'].join(1500); O._pip['thread'] = None

    def check():
        bpy.ops.bf2.check()
        proc, t0 = build._proc['p'], time.time()
        while proc is not None and proc.poll() is None and time.time() - t0 < 600:
            time.sleep(1.0)
        st = build.state()
        build._read_feed(st)
        st.running = False
        build._proc['p'] = None
        return sorted({r.code for r in st.results})

    for case, code in CASES.items():
        bpy.ops.wm.open_mainfile(filepath=os.path.join(scenes, case + '.blend'))
        before = check()
        op = O.FIXES.get(code)
        r = getattr(bpy.ops.bf2, op.split('.', 1)[1])() if op else None
        after = check()
        good = code in before and op is not None and code not in after
        res[case] = {'code': code, 'before': before, 'fix': op, 'fix_result': list(r) if r else None, 'after': after, 'pass': good}
        ok = ok and good
        print('%-5s %-18s %-22s fix %-22s after %s' % ('PASS' if good else 'FAIL', case, code, op, after), flush=True)
except Exception:
    res['exception'] = traceback.format_exc()
    ok = False
res['ok'] = ok
json.dump(res, open(out, 'w'), indent=1, default=str)
print('FIXES_TEST ok=%s' % ok)
sys.stdout.flush()
os._exit(0 if ok else 1)
