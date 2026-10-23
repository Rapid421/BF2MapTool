"""Stage `kill`: kill volumes from the scene's boxes, on the host shell's own kill logic (its shapes are a fixed set of
oriented boxes; no logic and no asset is added).

Rules (each one cost a build once):
  * a kill box must not hold walkable floor: every navmesh polygon corner is tested against every box; a hit stops the
    build and names the box (a box drawn OVER a lava lake killed players on the walkway that runs along it at the
    lake's own height - the box belongs UNDER the surface, and the lava itself has no collision, so a soldier sinks in);
  * the host has only so many boxes: the scene may use them all; when one is left, it becomes the automatic "fell off
    the map" slab under the lowest geometry; boxes still unused are parked on that slab (never left at the host's own
    old position).
Every box is read back from the saved project and compared with the plan.
"""
import json
import re

import numpy as np

from .. import chain, cli, hosts
from ..runner import StageError
from . import nav
from .spawns import Floor, in_box, load_obj

FLOOR_MARGIN = 10.0        # metres under the lowest geometry
FLOOR_DEPTH = 300.0
FLOOR_PAD = 600.0


def inputs(job):
    return {'kill': job.map.get('kill_volumes'), 'offset': job.offset(), 'v': 2}


def outputs(job):
    return [job.project('kill'), job.work + '/kill.json']


def game_box(job, b):
    """scene box (centre, half extents along its own axes, axes as columns) -> game centre, half (x, y, z), basis"""
    ax = np.asarray(b['axes'], float)                 # ax[i] = local axis i in scene coordinates
    g = lambda v: [float(v[0]), float(v[2]), float(-v[1])]
    return {'trans': list(job.to_game(b['centre'])), 'half': [float(b['half'][0]), float(b['half'][2]), float(b['half'][1])],
            'right': g(ax[0]), 'up': g(ax[2]), 'forward': g(-ax[1])}


def floor_slab(job):
    with open(job.path('pieces', 'pieces.json'), encoding='utf-8') as f:
        pieces = json.load(f)['pieces']
    lo = np.min([np.array(p['centre']) - np.array(p['size']) / 2 for p in pieces], 0)
    hi = np.max([np.array(p['centre']) + np.array(p['size']) / 2 for p in pieces], 0)
    top = float(lo[2]) - FLOOR_MARGIN
    c = ((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, top - FLOOR_DEPTH / 2)
    return {'name': '(automatic) below the map', 'centre': [float(x) for x in c],
            'half': [float((hi[0] - lo[0]) / 2 + FLOOR_PAD), float((hi[1] - lo[1]) / 2 + FLOOR_PAD), FLOOR_DEPTH / 2],
            'axes': [[1, 0, 0], [0, 1, 0], [0, 0, 1]], 'automatic': True}


def _json_blocks(text):
    out, cur = [], None
    for line in text.splitlines():
        if cur is None and line.rstrip() == '{':
            cur = [line]
        elif cur is not None:
            cur.append(line)
            if line.rstrip() == '}':
                try:
                    out.append(json.loads('\n'.join(cur)))
                except ValueError:
                    pass
                cur = None
    return out


def run(job, log):
    host = hosts.get(job.host)
    clix = cli.Cli(job, log)
    user = list(job.map.get('kill_volumes') or [])
    slots = list(host.KILL_OBBS)
    if len(user) > len(slots):
        raise StageError('The scene has %d kill volumes; this host shell has %d kill boxes. Merge some volumes (one box can '
                         'cover several pits).' % (len(user), len(slots)))
    boxes = list(user)
    if len(boxes) < len(slots):
        boxes.append(floor_slab(job))
    # walkable floor inside a kill box?
    # "Walkable floor" = the MAIN connected part of the navmesh, where people spawn and walk. A separate island inside a
    # kill box is the bottom of the pit the box is there for (the tool's own test arena: a pit with a solid bottom) -
    # reported, not an error.
    V, F_all = load_obj(nav.files(job)[0])
    floor = Floor(*nav.files(job))
    main_idx = sorted({i for f in floor.F for i in f})
    isl_idx = sorted(set(range(len(V))) - set(main_idx))
    scene_v = np.array([job.from_game(v) for v in V])
    bad, islands = [], []
    for b in user:
        inside = [i for i in main_idx if in_box(scene_v[i], b)]
        if inside:
            pts = scene_v[inside]
            bad.append('"%s" holds walkable floor (%d navmesh corners, e.g. at x %.1f, y %.1f, z %.1f)' % (
                b.get('name', '?'), len(inside), pts[0][0], pts[0][1], pts[0][2]))
        n_isl = sum(1 for i in isl_idx if in_box(scene_v[i], b))
        if n_isl:
            islands.append('"%s": %d corners of isolated floor (a pit bottom)' % (b.get('name', '?'), n_isl))
    if bad:
        raise StageError('Kill volume over walkable floor: ' + '; '.join(bad) + '. Lower the box under the surface people '
                         'fall into (lava has no collision: they sink in), or move it off the walkway.')
    plan = []
    L = job.level_dir + '/' + host.KILL_LAYER
    steps = [cli.load(chain.base(job, 'kill'))]
    for i, guid in enumerate(slots):
        b = boxes[min(i, len(boxes) - 1)]                     # unused boxes are parked on the last one
        g = game_box(job, b)
        plan.append({'guid': guid, 'name': b.get('name', ''), 'parked': i >= len(boxes), **g})
        for key in ('right', 'up', 'forward', 'trans'):
            for ax, v in zip('xyz', g[key]):
                steps.append({'op': 'ebx-set', 'ebx': L, 'object': guid, 'prop': 'Transform.%s.%s' % (key, ax), 'value': round(float(v), 5)})
        for ax, v in zip('xyz', g['half']):
            steps.append({'op': 'ebx-set', 'ebx': L, 'object': guid, 'prop': 'HalfExtents.' + ax, 'value': round(float(v), 4)})
    out = job.project('kill')
    steps.append(cli.save(out))
    clix.run('kill', steps)
    # read-back from the saved project
    r = clix.run('kill_readback', [cli.load(out)] + [{'op': 'cli', 'args': ['ebx', 'dump', L, '--object', g]} for g in slots])
    got = {b.get('__guid'): b for b in _json_blocks(r.text) if b.get('__type') == 'OBBData'}
    worst = 0.0
    for p in plan:
        b = got.get(p['guid'])
        if b is None:
            raise StageError('Kill box %s could not be read back  [log: %s]' % (p['guid'], r.log_path))
        t, h = b['Transform'], b['HalfExtents']
        for key in ('right', 'up', 'forward', 'trans'):
            worst = max(worst, max(abs(t[key][ax] - v) for ax, v in zip('xyz', p[key])))
        worst = max(worst, max(abs(h[ax] - v) for ax, v in zip('xyz', p['half'])))
    info = {'boxes': plan, 'user_volumes': len(user), 'automatic_floor': len(user) < len(slots), 'readback_max_diff': round(worst, 5),
            'navmesh_corners_tested': len(main_idx), 'isolated_floor_inside': islands}
    with open(job.work + '/kill.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    if worst > 1e-2:
        raise StageError('The kill boxes do not read back as planned (largest difference %.3f)  [log: %s]' % (worst, r.log_path))
    log('kill: %d scene volume(s)%s on %d host boxes; %d corners of the main walkable floor tested, none inside%s; read-back difference %.5f' % (
        len(user), ' + the automatic floor slab' if info['automatic_floor'] else '', len(slots), len(main_idx),
        ('; ' + '; '.join(islands)) if islands else '', worst))
    return {k: info[k] for k in ('user_volumes', 'automatic_floor', 'readback_max_diff', 'navmesh_corners_tested', 'isolated_floor_inside')}
