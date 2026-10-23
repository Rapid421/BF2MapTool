"""Proof for "no hard-coded path or map name in the shipped tool": scan the add-on and the runtime (what goes into the zip)
for drive paths, user names and the names of the maps / ports the tool was developed on.
python grep_paths.py  -> one line per hit; exit 0 when the only hits are the allowed ones (listed with the reason)."""
import os
import re
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
DIRS = [os.path.join(ROOT, 'addon', 'bf2_map_tool'), os.path.join(ROOT, 'runtime', 'bf2maptool')]
PATTERNS = [('drive path', re.compile(r'(?<![A-Za-z%])[A-Za-z]:[\\/](?![\\/])\w{2,}')), ('user name', re.compile(r'rubes', re.I)),
            ('map / port name', re.compile(r'Mustafar|Utapau|Mygeeto|PolisMassa|Polis Massa|Cato|Hoth(?!\w)|Coruscant|RhenVar|Rhen Var|ToolArena|DoorTest|'
                                           r'MapPorts|UtapauPort|BF2MapTool[\\/]', re.I))]
# hits that are meant to be there
ALLOWED = [(re.compile(r'hosts[\\/]kamino_01'), re.compile(r'.*'), 'the host module names shipped game assets (its own job)'),
           (re.compile(r'.*'), re.compile(r'^\s*(#|"""|\'\'\')|^\s+[A-Za-z(].*\b(seen in game|measured|the earlier ports?|as .* used|ports? )'),
            'a comment or docstring telling where a rule was measured'),
           (re.compile(r'prefs\.py|setupcheck\.py'), re.compile(r'Program Files|example|e\.g\.|default'), 'an example / default install location shown to the user')]
hits, allowed = [], []
for d in DIRS:
    for base, _, files in os.walk(d):
        if '__pycache__' in base:
            continue
        for f in files:
            if not f.endswith(('.py', '.json', '.txt', '.md')):
                continue
            p = os.path.join(base, f)
            rel = os.path.relpath(p, ROOT)
            in_doc = False
            for i, line in enumerate(open(p, encoding='utf-8', errors='replace'), 1):
                q = line.count('"""') + line.count("'''")
                doc_line = in_doc or q > 0 or line.lstrip().startswith('#') or '  # ' in line
                if q % 2 == 1:
                    in_doc = not in_doc
                for kind, rx in PATTERNS:
                    m = rx.search(line)
                    if not m:
                        continue
                    why = None
                    if re.search(r'hosts[\\/]kamino_01', rel.replace('\\', '/')) and kind != 'drive path' and kind != 'user name':
                        why = ALLOWED[0][2]
                    elif doc_line and kind == 'map / port name':
                        why = ALLOWED[1][2]
                    elif re.search(r'prefs\.py|setupcheck\.py', rel) and kind == 'drive path' and re.search(r'Program Files', line):
                        why = ALLOWED[2][2]
                    (allowed if why else hits).append((rel, i, kind, m.group(0), line.strip()[:140], why))
print('scanned', DIRS)
print('NOT ALLOWED: %d' % len(hits))
for h in hits:
    print('  %s:%d  [%s] %s | %s' % h[:5])
from collections import Counter
c = Counter((h[2], h[5]) for h in allowed)
print('allowed: %d' % len(allowed))
for (kind, why), n in c.most_common():
    print('  %4d  %s - %s' % (n, kind, why))
sys.exit(0 if not hits else 1)
