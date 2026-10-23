"""Run the pre-flight checks on maps that are already exported (work/<Name>/export/map.json) and print what fires.
python preflight_maps.py <settings.json> <map.json> [<map.json> ...]     (used to look for false alarms on real maps)"""
import os
import sys
import time

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime')))
from bf2maptool import checks, config

S = config.load_settings(sys.argv[1])
for path in sys.argv[2:]:
    t = time.time()
    try:
        res = checks.preflight(config.Job(path, S))
    except Exception as e:
        print('%s: EXCEPTION %s: %s' % (path, type(e).__name__, e)); continue
    print('%s: %.1f s, %d error(s), %d warning(s)' % (path, time.time() - t, sum(r['severity'] == 'error' for r in res), sum(r['severity'] == 'warning' for r in res)))
    for r in res:
        if r['severity'] != 'info':
            print('   %-7s %-20s %s' % (r['severity'], r['code'], r['message'][:230]))
