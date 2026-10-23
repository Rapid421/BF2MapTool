"""Helpers over FlatDump partitions (one JSON per EBX partition, every object once, internal refs as {"$ref": i}).

Used to compare a 2015 effect/emitter with a Battlefront II one (or with our own read-back), field by field.
"""
import json, math, os

META = ('$type', '$i', '$guid', '__Id', '__InstanceGuid', 'Name', 'Flags')


def load(path):
    return json.load(open(path, encoding='utf-8'))


def root(d):
    return d['objects'][d['root']]


def obj(d, ref):
    if isinstance(ref, dict) and '$ref' in ref and ref['$ref'] is not None:
        return d['objects'][ref['$ref']]
    return None


def is_ref(v):
    return isinstance(v, dict) and '$ref' in v


def is_ext(v):
    return isinstance(v, dict) and '$ext' in v


def chain(d, tmpl):
    """Processor objects of one EmitterTemplateData, in chain order."""
    out, p = [], obj(d, tmpl.get('RootProcessor'))
    seen = set()
    while p is not None and p['$i'] not in seen:
        seen.add(p['$i'])
        out.append(p)
        p = obj(d, p.get('NextProcessor'))
    return out


def num_eq(a, b, rel=1e-6):
    if isinstance(a, bool) or isinstance(b, bool):
        return a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        if a == b:
            return True
        if math.isnan(a) and math.isnan(b):
            return True
        return abs(a - b) <= rel * max(1.0, abs(a), abs(b))
    return a == b


def ext_key(v):
    return ((v.get('$fg') or '').lower(), (v.get('$cg') or '').lower())


def diff_values(da, a, db, b, path, out, follow=True, skip=('NextProcessor',), extmap=None, seen=None):
    """Recursive structural diff of two values from partitions da/db. Internal refs are followed (once per pair).

    extmap(ext_a) -> key comparable with ext_key(ext_b); default compares file/instance GUIDs."""
    if seen is None:
        seen = set()
    if is_ref(a) or is_ref(b):
        oa, ob = (obj(da, a) if is_ref(a) else None), (obj(db, b) if is_ref(b) else None)
        if oa is None and ob is None:
            if not (is_ref(a) and is_ref(b)):
                out.append((path, 'kind', a, b))
            return
        if (oa is None) != (ob is None):
            out.append((path, 'null-vs-object', (oa or {}).get('$type'), (ob or {}).get('$type')))
            return
        if oa['$type'] != ob['$type']:
            out.append((path, 'type', oa['$type'], ob['$type']))
            return
        key = (oa['$i'], ob['$i'])
        if key in seen or not follow:
            return
        seen.add(key)
        diff_objects(da, oa, db, ob, path, out, skip=skip, extmap=extmap, seen=seen)
        return
    if is_ext(a) or is_ext(b):
        if not (is_ext(a) and is_ext(b)):
            out.append((path, 'ext-vs-other', a, b))
            return
        ka = extmap(a) if extmap else ext_key(a)
        if ka != ext_key(b):
            out.append((path, 'ext', a.get('$ext'), b.get('$ext')))
        return
    if isinstance(a, dict) and isinstance(b, dict):
        if a.get('$type') != b.get('$type'):
            out.append((path, 'struct-type', a.get('$type'), b.get('$type')))
        diff_objects(da, a, db, b, path, out, skip=skip, extmap=extmap, seen=seen)
        return
    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append((path, 'len', len(a), len(b)))
        for i, (x, y) in enumerate(zip(a, b)):
            diff_values(da, x, db, y, '%s[%d]' % (path, i), out, follow, skip, extmap, seen)
        return
    if not num_eq(a, b):
        out.append((path, 'value', a, b))


def diff_objects(da, oa, db, ob, path, out, skip=('NextProcessor',), extmap=None, seen=None):
    keys = [k for k in oa if k not in META and k not in skip]
    for k in keys:
        if k not in ob:
            out.append((path + '.' + k, 'only-a', oa[k], None))
            continue
        diff_values(da, oa[k], db, ob[k], path + '.' + k, out, True, skip, extmap, seen)
    for k in ob:
        if k not in META and k not in skip and k not in oa:
            out.append((path + '.' + k, 'only-b', None, ob[k]))


def lcs_pairs(ta, tb):
    """Index pairs of the longest common subsequence of two type lists."""
    n, m = len(ta), len(tb)
    L = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(n - 1, -1, -1):
        for j in range(m - 1, -1, -1):
            L[i][j] = L[i + 1][j + 1] + 1 if ta[i] == tb[j] else max(L[i + 1][j], L[i][j + 1])
    i = j = 0
    out = []
    while i < n and j < m:
        if ta[i] == tb[j]:
            out.append((i, j)); i += 1; j += 1
        elif L[i + 1][j] >= L[i][j + 1]:
            i += 1
        else:
            j += 1
    return out


TIERS = ('TemplateDataLow', 'TemplateDataMedium', 'TemplateDataHigh', 'TemplateDataUltra')


def emitter_diff(da, db, extmap=None):
    """Diff two ScalableEmitterDocument partitions: document fields, per-tier template fields, chains (LCS by type)."""
    out, chains = [], {}
    ra, rb = root(da), root(db)
    diff_objects(da, ra, db, rb, 'doc', out, skip=TIERS + ('NextProcessor',), extmap=extmap)
    for tier in TIERS:
        ta, tb = obj(da, ra.get(tier)), obj(db, rb.get(tier))
        if ta is None or tb is None:
            if (ta is None) != (tb is None):
                out.append((tier, 'tier-missing', ta is not None, tb is not None))
            continue
        diff_objects(da, ta, db, tb, tier, out, skip=('RootProcessor',), extmap=extmap)
        ca, cb = chain(da, ta), chain(db, tb)
        tya, tyb = [p['$type'] for p in ca], [p['$type'] for p in cb]
        chains[tier] = (tya, tyb)
        pairs = lcs_pairs(tya, tyb)
        pa = {i for i, _ in pairs}
        pb = {j for _, j in pairs}
        for i, p in enumerate(ca):
            if i not in pa:
                out.append((tier + '.chain[%d]' % i, 'proc-only-a', p['$type'], None))
        for j, p in enumerate(cb):
            if j not in pb:
                out.append((tier + '.chain[%d]' % j, 'proc-only-b', None, p['$type']))
        for i, j in pairs:
            diff_objects(da, ca[i], db, cb[j], '%s.%s#%d' % (tier, ca[i]['$type'], i), out, extmap=extmap)
    return out, chains
