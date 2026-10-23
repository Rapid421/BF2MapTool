"""print chosen keys of a JSON file, one per line, truncated. python show_json.py <file.json> [key ...] [--width 400]"""
import json
import sys

args = [a for a in sys.argv[1:] if not a.startswith('--')]
width = int(sys.argv[sys.argv.index('--width') + 1]) if '--width' in sys.argv else 400
if '--width' in sys.argv:
    args = [a for a in args if a != str(width)]
try:
    d = json.load(open(args[0], encoding='utf-8-sig'))
except OSError as e:
    print('not there yet:', e)
    sys.exit(0)
keys = args[1:] or (list(d) if isinstance(d, dict) else [])
for k in keys:
    v = d
    for part in k.split('.'):
        v = v.get(part) if isinstance(v, dict) else None
    print('%s: %s' % (k, json.dumps(v, default=str)[:width]))
