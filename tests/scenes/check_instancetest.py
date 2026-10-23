"""Compare the export of make_instancetest.py's scene with its .expect.json.  python check_instancetest.py <map.json> <expect.json>"""
import json
import sys
from collections import Counter

m = json.load(open(sys.argv[1], encoding='utf-8'))
e = json.load(open(sys.argv[2], encoding='utf-8'))
fails = []
by_name = {o['name']: o for o in m['objects']}
for n, tris in e['plain_objects'].items():
    got = by_name.get(n, {}).get('tris', 0)
    if got != tris:
        fails.append('object %s: %s tris, expected %s' % (n, got, tris))
inst = [o for o in m['objects'] if o.get('instance_of')]
cnt = Counter(o['instance_of'].split('.')[0] for o in inst)
for n, k in e['instances'].items():
    if cnt.get(n, 0) != k:
        fails.append('instances of %s: %d, expected %d' % (n, cnt.get(n, 0), k))
for o in inst:
    want = e['tris_per_instance'].get(o['instance_of'].split('.')[0])
    if want and o['tris'] != want:
        fails.append('%s: %d tris, expected %d' % (o['name'], o['tris'], want))
sk = sum((m.get('instances') or {}).get('skipped', {}).values())
if sk != e['skipped_instances']:
    fails.append('skipped instances %d, expected %d (%s)' % (sk, e['skipped_instances'], (m.get('instances') or {}).get('skipped')))
if sorted(m['materials']) != e['materials']:
    fails.append('materials %s, expected %s' % (sorted(m['materials']), e['materials']))
# instances must sit where their instancer put them (not all at the origin)
import numpy as np  # noqa: E402
print(json.dumps({'instances': dict(cnt), 'summary': m.get('instances'), 'fails': fails}, indent=1))
sys.exit(1 if fails else 0)
