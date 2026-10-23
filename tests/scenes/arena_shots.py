"""Viewpoints for the game test of the test arena, given in scene coordinates and converted with the build's own
offset. python arena_shots.py <map.json> <settings.json> <out shots.json>"""
import json
import os
import sys

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime')))
from bf2maptool import config

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
VIEWS = [('overview', (-34.0, -44.0, 30.0), (0.0, 0.0, 0.0)),
         ('grate_bridge', (-8.0, -6.0, 2.5), (0.0, 0.0, -0.5)),
         ('door', (11.0, -3.0, 1.7), (20.0, -3.0, 1.7)),
         ('strips_east_wall', (28.0, 4.0, 1.7), (39.8, 0.0, 4.0)),
         ('crates_wall_normalmap', (5.0, 5.0, 1.7), (11.0, 11.0, 1.0)),
         ('mirrored_crate', (21.0, -8.0, 1.7), (25.0, -12.0, 1.0)),
         ('platform_ramp', (-12.0, 6.0, 1.7), (-20.0, 14.0, 1.5)),
         ('north_wall_close', (0.0, 26.0, 1.7), (-6.0, 30.0, 2.0))]
shots = [{'name': n, 'at': list(job.to_game(a)), 'look': list(job.to_game(b))} for n, a, b in VIEWS]
# the same door view six times, 5 s apart: bots cross its trigger, so some frames should show it raised
shots.append({'name': 'door_watch', 'at': list(job.to_game((9.0, -9.0, 2.2))), 'look': list(job.to_game((20.0, -3.0, 1.7))), 'repeat': 6, 'gap': 5.0})
shots.append({'name': 'spot_down', 'at': list(job.to_game((-22.0, -2.0, 3.0))), 'look': list(job.to_game((-30.0, -8.0, 0.0)))})
shots.append({'name': 'light_dynamic', 'at': list(job.to_game((24.0, -14.0, 2.0))), 'look': list(job.to_game((30.0, -20.0, 0.5)))})
shots.append({'name': 'light_both', 'at': list(job.to_game((4.0, 4.0, 2.0))), 'look': list(job.to_game((10.0, 10.0, 0.5)))})
shots.append({'name': 'light_baked', 'at': list(job.to_game((-16.0, 8.0, 3.5))), 'look': list(job.to_game((-24.0, 14.0, 2.0)))})
for n, at, look in (('calib_left', (-33.5, -23.0, 1.8), (-33.5, -16.0, 1.5)), ('calib_right', (-23.5, -23.0, 1.8), (-23.5, -16.0, 1.5)),
                    ('calib_all', (-28.5, -29.0, 3.0), (-28.5, -16.0, 1.0)), ('calib_back', (-28.5, -6.0, 3.0), (-28.5, -16.0, 1.0)),
                    ('calib_tpl', (8.5, -29.0, 2.0), (8.5, -22.0, 1.5)), ('calib_tpl2', (15.0, -28.5, 2.0), (15.5, -22.0, 1.5)),
                    ('sky_up', (0.0, 0.0, 2.0), (30.0, 20.0, 22.0)), ('sky_sun', (0.0, 0.0, 2.0), (23.0, -40.0, 40.0))):
    shots.append({'name': n, 'at': list(job.to_game(at)), 'look': list(job.to_game(look))})
json.dump(shots, open(sys.argv[3], 'w'), indent=1)
print(len(shots), 'viewpoints, offset', job.offset())
