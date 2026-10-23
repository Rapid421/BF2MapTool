"""Did a button test end with a finished build? python button_ok.py <button result.json>  -> exit 0 only when the last
run is ok and names a .fbmod that exists and is newer than the result's own start."""
import json
import os
import sys

try:
    r = json.load(open(sys.argv[1], encoding='utf-8'))
    last = r['runs'][-1]
    ok = bool(last.get('ok')) and last.get('fbmod') and os.path.exists(last['fbmod'])
    print('last run:', last.get('note'), 'ok' if last.get('ok') else 'FAILED', '|', (last.get('message') or '')[:200])
    sys.exit(0 if ok else 1)
except Exception as e:
    print('no usable result: %s' % e)
    sys.exit(1)
