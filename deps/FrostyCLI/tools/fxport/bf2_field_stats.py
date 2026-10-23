"""Distribution of every BFII-only field (fields the 2015 SDK type lacks) over ALL shipped BFII emitter packages.
The mode is what DICE's cooker writes when the source does not set it; used as the conversion default."""
import json, glob, collections, sys
s1 = json.load(open('inv/schema_bf1.json'))['types']
s2 = json.load(open('inv/schema_bf2.json'))['types']
only2 = {t: set(v['fields']) - set((s1.get(t) or {}).get('fields') or {}) for t, v in s2.items() if v}
stats = collections.defaultdict(collections.Counter)
cin = collections.Counter()
def walk(v, owner=None):
    if isinstance(v, dict):
        if 'type' in v and 'fields' in v:
            t = v['type']; f = v['fields']
            for k in only2.get(t, ()):
                if k in f: stats[(t, k)][json.dumps(f[k], sort_keys=True)[:120]] += 1
            if t.startswith('QualityScalable') and 'Cinematic' in f:
                cin['cin==ultra' if f['Cinematic'] == f.get('Ultra') else 'cin!=ultra:%s/%s' % (f['Cinematic'], f.get('Ultra'))] += 1
            for x in f.values(): walk(x)
        else:
            for x in v.values(): walk(x)
    elif isinstance(v, list):
        for x in v: walk(x)
files = glob.glob(sys.argv[1] + '/*.json')
for fn in files:
    d = json.load(open(fn))
    for o in d['Objects']: walk(o)
out = {}
for (t, k), c in sorted(stats.items()):
    tot = sum(c.values())
    out['%s.%s' % (t, k)] = {'total': tot, 'values': dict(c.most_common(6))}
    print('%-45s n=%5d  %s' % (t + '.' + k, tot, ' | '.join('%s x%d' % (a, b) for a, b in c.most_common(4))))
print(cin.most_common(10))
json.dump(out, open(sys.argv[2], 'w'), indent=1)
