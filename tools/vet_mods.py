"""For a list of port mods: is each its OWN level (adds AddedContent/... levels, changes no shipped level) and is
there a record of it loading? The record is searched in the port folder's STATUS.md / *.md (lines naming the mod's tag
with LOADED / loads / plays / crash) and in any review/**/result.json naming the file.
    python vet_mods.py <a.fbmod> <b.fbmod> ...      -> a table; vet.json beside this script's work/_merge/
"""
import glob
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mod_clash

GOOD = re.compile(r'\bLOADED\b|loads and plays|loads\b|plays\b|DEPLOYED|works in game|seen in game', re.I)
BAD = re.compile(r'crash|0xC0000005|fault|does not load|black screen|hang', re.I)


def port_root(mod):
    d = os.path.dirname(os.path.abspath(mod))
    return os.path.dirname(d) if os.path.basename(d).lower() == 'mods' else d


def evidence(mod):
    tag = os.path.splitext(os.path.basename(mod))[0]
    short = re.sub(r'^[A-Z]+_', '', tag)                     # UTAPAU_NL22 -> NL22
    root = port_root(mod)
    good, bad = [], []
    for md in glob.glob(os.path.join(root, '*.md')):
        for i, line in enumerate(open(md, encoding='utf-8', errors='replace')):
            if re.search(r'\b(%s|%s)\b' % (re.escape(tag), re.escape(short)), line):
                (bad if BAD.search(line) and not GOOD.search(line.split('crash')[0]) else good if GOOD.search(line) else []).append(
                    '%s:%d %s' % (os.path.basename(md), i + 1, line.strip()[:160]))
    # only the review folders (a port folder holds thousands of piece files: a full recursive walk took minutes)
    for rj in [p for d in glob.glob(os.path.join(root, '*review*')) + glob.glob(os.path.join(root, 'work', '*review*')) for p in glob.glob(os.path.join(d, '**', 'result.json'), recursive=True)]:
        try:
            j = json.load(open(rj, encoding='utf-8'))
        except (OSError, ValueError):
            continue
        if os.path.basename(str(j.get('mod', ''))).lower() == os.path.basename(mod).lower():
            (good if j.get('ok') or j.get('status') in ('LOADED', 'DEPLOYED') else bad).append('%s: %s' % (os.path.relpath(rj, root), j.get('status')))
    return good, bad


def main(mods):
    rows = []
    for m in mods:
        r = mod_clash.read_mod(m)
        ebx = [x for x in r['resources'] if x['type'] == 'ebx' and x['name']]
        own = sorted({'/'.join(x['name'].split('/')[:4]) for x in ebx if x['added'] and x['name'].startswith('addedcontent/') and '/levels/' in x['name']})
        repl = sorted({'/'.join(x['name'].split('/')[:3]) for x in ebx if x['data'] and not x['added'] and x['name'].startswith('levels/')})
        good, bad = evidence(m)
        rows.append({'mod': m, 'title': r['title'], 'version': r['details'][3], 'own_levels': own, 'shipped_levels_changed': repl,
                     'loaded_records': good[:6], 'problem_records': bad[:6]})
        print('%-28s own %-40s replaces %-40s loaded %d  problems %d' % (os.path.basename(m), ','.join(own)[:40] or '-', ','.join(repl)[:40] or '-',
                                                                       len(good), len(bad)))
        for g in good[:2]:
            print('      + ' + g)
        for b in bad[:2]:
            print('      ! ' + b)
    out = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'work', '_merge', 'vet.json')
    json.dump(rows, open(out, 'w'), indent=1)


if __name__ == '__main__':
    main(sys.argv[1:])
