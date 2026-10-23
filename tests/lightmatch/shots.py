"""The light-match views as game camera shots.  python shots.py <map.json> <settings.json> <out shots.json>
(the views come from <scene.blend>.views.json next to the scene the map was exported from; the export keeps a copy of
the scene, so the file is looked for beside the ORIGINAL named in the environment variable LIGHTMATCH_VIEWS, else
tests/lightmatch/lightmatch.blend.views.json)"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..', 'runtime')))
from bf2maptool import config

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
vf = os.environ.get('LIGHTMATCH_VIEWS') or os.path.join(HERE, 'lightmatch.blend.views.json')
views = json.load(open(vf))['views']
shots = [{'name': 'warm_up', 'at': list(job.to_game((0.0, -30.0, 12.0))), 'look': list(job.to_game((0.0, 10.0, 0.0))), 'repeat': 2, 'gap': 3.0}]
for v in views:
    shots.append({'name': v['name'], 'at': list(job.to_game(v['at'])), 'look': list(job.to_game(v['look']))})
json.dump(shots, open(sys.argv[3], 'w'), indent=1)
print(len(shots), 'viewpoints from', vf)
