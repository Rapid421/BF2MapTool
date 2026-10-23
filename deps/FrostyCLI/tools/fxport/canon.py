"""Put a graph package's objects in the order EbxGraphPackage.CaptureAsset produces (first reach from the root, fields
in ordinal name order, lists in order, nested structs depth-first), so a converted package and the capture of the asset
built from it can be compared index by index."""
import json


def canonical(pkg):
    objs = pkg['Objects']
    order, seen = [], {}

    def visit_value(v, q):
        if isinstance(v, list):
            for x in v:
                visit_value(x, q)
        elif isinstance(v, dict):
            if 'ref' in v and len(v) == 1:
                r = v['ref']
                if r not in seen:
                    seen[r] = len(order); order.append(r); q.append(r)
            elif 'type' in v and 'fields' in v:
                for k in sorted(v['fields']):
                    visit_value(v['fields'][k], q)
    seen[0] = 0; order.append(0)
    i = 0
    while i < len(order):
        o = objs[order[i]]
        q = []
        for k in sorted(o['fields']):
            visit_value(o['fields'][k], q)
        i += 1
    for j in range(len(objs)):
        if j not in seen:
            seen[j] = len(order); order.append(j); objs[j]['orphan'] = True
    remap = {old: new for new, old in enumerate(order)}

    def fix(v):
        if isinstance(v, list):
            return [fix(x) for x in v]
        if isinstance(v, dict):
            if 'ref' in v and len(v) == 1:
                return {'ref': remap[v['ref']]}
            return {k: fix(x) for k, x in v.items()}
        return v
    new = []
    for old in order:
        o = dict(objs[old])
        o['fields'] = {k: fix(o['fields'][k]) for k in sorted(o['fields'])}
        new.append(o)
    pkg['Objects'] = new
    return pkg
