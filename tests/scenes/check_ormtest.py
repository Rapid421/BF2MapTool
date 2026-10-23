"""python check_ormtest.py <map.json> <expect.json>: each listed input must be (image, channel, inverted) as expected."""
import json
import sys

m = json.load(open(sys.argv[1], encoding='utf-8'))['materials']
ex = json.load(open(sys.argv[2]))
fails, seen = [], []
for mat, keys in ex.items():
    for key, (image, channel, inv) in keys.items():
        r = (m.get(mat) or {}).get('inputs', {}).get(key) or {}
        got = ((r.get('image') or {}).get('name'), r.get('channel'), bool(r.get('invert')))
        seen.append('%s.%s=%s' % (mat, key, got))
        if r.get('baked'):
            got = ('(baked)', r.get('channel'), bool(r.get('invert')))
        if got[0] != image or got[1] != channel or got[2] != inv:
            fails.append('%s %s: got %s, expected %s' % (mat, key, got, (image, channel, inv)))
print('\n'.join(seen))
print('PASS' if not fails else 'FAIL', fails)
sys.exit(1 if fails else 0)
