"""Run tests/survey_og_markers.py over every OG blend (batch_og.MAPS), 2 min limit each, no windows.
    python survey_og.py <out dir>
"""
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import batch_og

ROOT = batch_og.ROOT
out = sys.argv[1]
os.makedirs(out, exist_ok=True)
summary = {}
for name, src, *_ in batch_og.MAPS:
    j = os.path.join(out, name + '.json')
    try:
        subprocess.run([batch_og.S['blender'], '-b', os.path.join(batch_og.OG, src), '--python',
                        os.path.join(ROOT, 'tests', 'survey_og_markers.py'), '--', j],
                       capture_output=True, timeout=120, creationflags=batch_og.NOWIN)
        d = json.load(open(j))
        summary[name] = {'markers': d['markers'], 'stems': d['stems'][:12], 'collections': d['collections'][:20]}
    except Exception as e:
        summary[name] = {'error': str(e)[:200]}
    print(name, json.dumps(summary[name])[:400], flush=True)
json.dump(summary, open(os.path.join(out, 'summary.json'), 'w'), indent=1)
