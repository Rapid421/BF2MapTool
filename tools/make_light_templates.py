"""One-off: the host level's own light records (frostycli `level export-lights <level> --types lights`) -> one field set
per light type, written next to the host module as <host>_lights.json. The values are the game's shipped ones; the
stage overwrites only what a scene light sets (colour, lumens, radius, cone, size, mode switches).
python make_light_templates.py <exported lights.json> <out json>"""
import json
import sys
from collections import Counter

src, out = sys.argv[1], sys.argv[2]
L = json.load(open(src))['lights']
res = {}
for t in sorted({l['Type'] for l in L}):
    ls = [l for l in L if l['Type'] == t and l['Fields'].get('Enabled') == 'True' and not l.get('Refs')] or [l for l in L if l['Type'] == t]
    # the most common value of every field over the type's shipped lights
    keys = sorted({k for l in ls for k in l['Fields']})
    fields = {}
    for k in keys:
        if k in ('IESProfile',):
            continue
        fields[k] = Counter(l['Fields'][k] for l in ls if k in l['Fields']).most_common(1)[0][0]
    res[t] = {'count': len(ls), 'fields': fields}
json.dump(res, open(out, 'w'), indent=1)
print({t: (v['count'], len(v['fields'])) for t, v in res.items()})
