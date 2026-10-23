"""Watch one game launch that was started outside game_test.py (a hands-off launch for the user): wait for the level
to load, and answer the EA app's known "sync was interrupted" box if it comes up meanwhile.
python watch_launch.py <level name part> <out dir> [limit s, default 420]"""
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import launcher_dialogs as LD

want, out = sys.argv[1], sys.argv[2]
limit = float(sys.argv[3]) if len(sys.argv) > 3 else 420.0
os.makedirs(out, exist_ok=True)
GL = 'C:/ProgramData/FrostyGameLink/gamelink.log'
g0 = os.path.getsize(GL) if os.path.exists(GL) else 0
ea = LD.ea_log()
e0 = os.path.getsize(ea) if ea and os.path.exists(ea) else 0
t0 = time.time()
answered = set()
while time.time() - t0 < limit:
    if ea and os.path.exists(ea) and os.path.getsize(ea) > e0:
        with open(ea, 'rb') as f:
            f.seek(e0); new = f.read().decode('utf-8', 'replace')
        e0 = os.path.getsize(ea)
        for m in LD.LINE.finditer(new):
            ev = {'id': m.group(1), 'code': m.group(2), 'title': m.group(3), 'buttons': int(m.group(4))}
            if ev['id'] + ev['code'] not in answered:
                answered.add(ev['id'] + ev['code'])
                print('%5.0f s  EA dialog: %s' % (time.time() - t0, ev), flush=True)
                print('        ', LD.answer(ev, out, print), flush=True)
    if os.path.exists(GL) and os.path.getsize(GL) > g0:
        with open(GL, 'rb') as f:
            f.seek(g0); txt = f.read().decode('utf-8', 'replace')
        m = re.search(r"LOADED LEVEL IS '([^']*)'", txt)
        if m:
            print('%5.0f s  LOADED %s%s' % (time.time() - t0, m.group(1), '' if want in m.group(1) else '   (NOT the level asked for)'), flush=True)
            f2 = re.findall(r'fault: code (0x[0-9A-Fa-f]+)', txt)
            print('faults so far:', f2 or 'none')
            sys.exit(0)
    time.sleep(2)
print('not loaded within %.0f s' % limit)
sys.exit(1)
