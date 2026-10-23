"""Stage `navmesh`: the bots' walkable mesh, built from the level's own collision (`navmesh build`: collision geometry ->
Recast -> the host shell's Skirmish pathfinding stream).

It is read from the collision shapes themselves (`--collision`), so it follows custom collision meshes and hulls, and
lava and every other no-collision surface are skipped by construction.
The region is the play area; the height band is the play area's solid geometry (a navmesh spanning about a kilometre of
height leaves every bot standing still). Files kept in nav/: <level>_geometry.obj, <level>_navmesh.polys.obj, .bin.
"""
import json
import os
import re

import numpy as np

from .. import chain, cli, config
from ..runner import StageError

CS, CH, CLIMB = 0.2, 0.1, 0.5
# One Recast heightfield holds a polygon mesh of at most 65,535 corners: OG Endor's 2.1 x 2.0 km play area (109 M cells
# at CS) failed with "rcBuildPolyMesh: Too many vertices 283306". Past this many cells the build is tiled (navgen's
# tiled path: TILE x TILE cell tiles stitched into one surface, made for Walker Assault maps); OG Coruscant's 737 x
# 737 m (13.6 M cells) built fine in one piece and stays that way.
TILED_CELLS = 20e6
TILE = 512


def tiled(region_xz):
    x0, z0, x1, z1 = region_xz
    return (x1 - x0) / CS * (z1 - z0) / CS > TILED_CELLS
MAX_BAND = 600.0
BAND_BELOW, BAND_ABOVE = 100.0, 300.0     # the band used around the spawns when the geometry is taller than MAX_BAND


def region(job):
    """(x0, z0, x1, z1) and (y0, y1) in game coordinates, from the play area and the pieces that are solid"""
    with open(job.path('pieces', 'pieces.json'), encoding='utf-8') as f:
        pieces = json.load(f)['pieces']
    with open(job.path('col', 'report.json'), encoding='utf-8') as f:
        col = json.load(f)
    (cx, cy, cz), r = job.play()
    lo, hi = [], []
    for p in pieces:
        if p['name'] not in col:
            continue
        c, s = np.array(p['centre']), np.array(p['size']) / 2
        if abs(c[0] - cx) - s[0] > r or abs(c[1] - cy) - s[1] > r:
            continue
        lo.append(c - s); hi.append(c + s)
    if not lo:
        raise StageError('No solid geometry lies inside the play area, so there is nothing to walk on. '
                         'Check the Collision settings and the play radius.')
    lo, hi = np.min(lo, 0), np.max(hi, 0)
    x0, x1 = max(lo[0], cx - r), min(hi[0], cx + r)
    y0, y1 = max(lo[1], cy - r), min(hi[1], cy + r)
    zr = (job.map.get('navmesh') or {}).get('z_range')          # optional: the play height band, scene metres
    z0, z1 = (float(zr[0]), float(zr[1])) if zr else (float(lo[2]) - 2.0, float(hi[2]) + 2.0)
    if not zr and z1 - z0 > MAX_BAND:
        # towers / pits inside the play radius (a city map: 654 m of geometry over a play floor near z 0): the band is
        # taken around where soldiers are put - the spawns and command posts, else the play centre
        zs = [float(s['pos'][2]) for s in (job.map.get('spawns') or []) + (job.map.get('command_posts') or [])] or [float(cz)]
        z0, z1 = max(z0, min(zs) - BAND_BELOW), min(z1, max(zs) + BAND_ABOVE)
    a, b = job.to_game((x0, y0, z0)), job.to_game((x1, y1, z1))
    return (min(a[0], b[0]), min(a[2], b[2]), max(a[0], b[0]), max(a[2], b[2])), (min(a[1], b[1]), max(a[1], b[1]))


def inputs(job):
    d = {'play': job.play(), 'navmesh': job.map.get('navmesh'), 'offset': job.offset(), 'cs': (CS, CH, CLIMB), 'doors': sum(1 for o in job.map['objects'] if o.get('role') == 'door'), 'band': (BAND_BELOW, BAND_ABOVE), 'v': 5}
    # only where it changes the build (so maps that build in one piece stay up to date)
    try:
        if tiled(region(job)[0]):
            d['tile'] = TILE
    except (OSError, StageError, ValueError, KeyError):
        pass
    return d


def outputs(job):
    return [job.project('navmesh'), job.path('nav', 'navmesh.json')]


def files(job):
    base = '%s/%s_01' % (job.dirs['nav'], job.name)
    return base + '_navmesh.polys.obj', base + '_geometry.obj'


def run(job, log):
    clix = cli.Cli(job, log)
    with open(job.work + '/shell.json', encoding='utf-8') as f:
        shell = json.load(f)
    (x0, z0, x1, z1), (y0, y1) = region(job)
    if y1 - y0 > MAX_BAND:
        raise StageError('The walkable geometry spans %.0f m of height; bots stop moving on a navmesh that tall. '
                         'Lower the play radius or set the play height range.' % (y1 - y0))
    out = job.project('navmesh')
    for f in os.listdir(job.dirs['nav']):
        os.remove(os.path.join(job.dirs['nav'], f))
    args = ['navmesh', 'build', job.level, '--region', '%.1f,%.1f,%.1f,%.1f' % (x0, z0, x1, z1), '--y-range', '%.1f,%.1f' % (y0, y1),
            '--cs', str(CS), '--ch', str(CH), '--climb', str(CLIMB), '--stream', shell['navmesh_stream'], '--keep', job.dirs['nav'],
            # the walkable world is read from the level's COLLISION, not from the visible meshes of solid pieces: the
            # two differ wherever an object has a custom collision mesh or a hull (seen on the collision check scene:
            # the navmesh sat on the visible box 1 m under the surface soldiers really stand on)
            '--collision']
    # Closed doors must not cut the navmesh: bots open them through the door's own trigger. `navmesh build` leaves out
    # leaves "whose logic moves them", but measured on 18 doors it left out 25 of the 36 leaf slots - the 11 it kept
    # closed 11 doorways and halved the main walkable part (1,103 -> 501 polygons). Door panels are skipped by name.
    if any(o.get('role') == 'door' for o in job.map['objects']):
        args += ['--skip', job.piece_prefix + '_Door_']
    if tiled((x0, z0, x1, z1)):
        args += ['--tile-size', str(TILE)]
        log('navmesh: %.0f x %.0f m = %.0f M cells: tiled build (%d-cell tiles)' % (x1 - x0, z1 - z0, (x1 - x0) * (z1 - z0) / CS / CS / 1e6, TILE))
    r = clix.direct('navmesh', args, project=chain.base(job, 'navmesh'), save_as=out, check=False)
    polys, geo = files(job)
    # Recast can fail on one awkward region in a single heightfield (OG Yavin: "rcBuildContours: Bad outline for region
    # 33, contour simplification is likely too aggressive"): the tiled build, where every tile is small, is tried next
    if (r.rc != 0 or not os.path.exists(polys)) and '--tile-size' not in args and re.search(r'rcBuild\w+:', r.text):
        log('navmesh: the one-piece build failed in Recast (%s) - building in %d-cell tiles' % (
            re.search(r'rcBuild\w+:[^|\r\n]*', r.text).group(0)[:120], TILE))
        for f in os.listdir(job.dirs['nav']):
            os.remove(os.path.join(job.dirs['nav'], f))
        args += ['--tile-size', str(TILE)]
        r = clix.direct('navmesh_tiled', args, project=chain.base(job, 'navmesh'), save_as=out, check=False)
    if r.rc != 0 or not os.path.exists(out) or not os.path.exists(polys):
        tail = ' | '.join(l.strip() for l in r.text.strip().splitlines()[-4:])
        raise StageError('The navmesh could not be built: %s  [log: %s]' % (tail[:500], r.log_path))
    # solo build: "recast  N polygon(s)"; tiled build: "stitch  N polygon(s)" (the joined surface)
    m = re.search(r'stitch\s+(\d+) polygon', r.text) or re.search(r'recast\s+(\d+) polygon', r.text)
    g = re.search(r'triangles: (\d+) (?:collision triangle|from placements)', r.text)
    nc = re.search(r'no collision: (\d+) parts of (\d+) render-only meshes left out', r.text)
    info = {'region': [round(v, 1) for v in (x0, z0, x1, z1)], 'y_range': [round(y0, 1), round(y1, 1)],
            'polygons': int(m.group(1)) if m else None, 'collision_triangles': int(g.group(1)) if g else None,
            'no_collision_meshes_left_out': int(nc.group(2)) if nc else None, 'stream': shell['navmesh_stream']}
    if not info['polygons']:
        raise StageError('The navmesh is empty: nothing in the play area is walkable (floors need collision and less than '
                         '45 degrees of slope).  [log: %s]' % r.log_path)
    with open(job.path('nav', 'navmesh.json'), 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    log('navmesh: %s polygons from %s collision triangles; region %s, height %s' % (info['polygons'], info['collision_triangles'],
                                                                                 info['region'], info['y_range']))
    return info
