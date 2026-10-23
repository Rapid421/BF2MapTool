"""What a build really made, read back from its work folder, in Blender coordinates: the navmesh (split into its
connected parts), the spawn points and the spawn-finder areas the level holds. The viewport overlay draws it
(overlay.py, "Show the build"); this module only reads and measures, so tests run it without Blender.

  <work>/place.json                      the offset the place stage used (game = (x, z, -y) + offset)
  <work>/nav/<level>_navmesh.polys.obj   the navmesh polygons, game coordinates
  <work>/dumps/spawns_*.json             every spawn layer as written: AlternateSpawnEntityData (spawn points),
                                         SpawnLocationFinderShapeData (finder areas), VolumeVectorShapeData (play areas)

A spawn is "on the main part" when it stands on the navmesh part that holds most spawns; "island" = on another part
(bots and players there cannot walk to the rest); "off" = no navmesh under its feet. A finder area finds its spots on
every navmesh floor inside its outline; one that covers floors far apart (Height 0 reaches all of them) is reported.
"""
import glob
import json
import os

import numpy as np

STAND_TOL = 2.5        # metres between a spawn's feet and the navmesh polygon under it (the polygons are coarse: up to ~2.5 m off on slopes)
FLOOR_SPREAD = 4.0     # a finder area covering floors more than this far apart in height is reported
TEAMS = {'Team1': 'team1', 'Team2': 'team2', 'TeamNeutral': 'neutral'}


class NoBuild(Exception):
    pass


def offset(work):
    """the (x, y, z) the place stage added after the axis change"""
    for path, get in ((os.path.join(work, 'place.json'), lambda d: d.get('offset')),
                      (os.path.join(work, 'export', 'map.json'), lambda d: (d.get('placement') or {}).get('offset'))):
        try:
            with open(path, encoding='utf-8') as f:
                o = get(json.load(f))
        except (OSError, ValueError):
            continue
        if o:
            return tuple(float(v) for v in o)
    raise NoBuild('No placed build in %s (place.json is missing): build the map first.' % work)


def to_blender(P, off):
    """game (x, y, z) -> Blender (x - ox, -(z - oz), y - oy); P (..., 3)"""
    P = np.asarray(P, np.float64)
    return np.stack([P[..., 0] - off[0], -(P[..., 2] - off[2]), P[..., 1] - off[1]], -1)


def load_obj(path):
    V, F = [], []
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            if line.startswith('v '):
                V.append([float(t) for t in line.split()[1:4]])
            elif line.startswith('f '):
                F.append([int(t.split('/')[0]) - 1 for t in line.split()[1:]])
    return np.array(V, np.float64).reshape(-1, 3), F


def components(F):
    """connected parts of a polygon mesh: polygons that share an edge are one part (as the spawns stage counts them)"""
    par = list(range(len(F)))

    def find(a):
        while par[a] != a:
            par[a] = par[par[a]]
            a = par[a]
        return a
    edge = {}
    for i, f in enumerate(F):
        for k in range(len(f)):
            e = (min(f[k], f[(k + 1) % len(f)]), max(f[k], f[(k + 1) % len(f)]))
            if e in edge:
                a, b = find(i), find(edge[e])
                if a != b:
                    par[a] = b
            else:
                edge[e] = i
    roots = [find(i) for i in range(len(F))]
    ids = {r: n for n, r in enumerate(dict.fromkeys(roots))}
    return np.array([ids[r] for r in roots], np.int64)


def _area(V, f):
    if len(f) < 3:
        return 0.0
    p = V[f]
    return float(np.linalg.norm(np.sum([np.cross(p[i] - p[0], p[i + 1] - p[0]) for i in range(1, len(f) - 1)], 0)) / 2)


def _inside(x, y, poly):
    """point in polygon (2D, poly (N, 2))"""
    n, ins, j = len(poly), False, len(poly) - 1
    for i in range(n):
        xi, yi = poly[i]; xj, yj = poly[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / ((yj - yi) or 1e-12) + xi:
            ins = not ins
        j = i
    return ins


def _walk(o, out):
    if isinstance(o, dict):
        t = o.get('__type')
        if t in ('AlternateSpawnEntityData', 'SpawnLocationFinderShapeData', 'VolumeVectorShapeData'):
            out.append(o)
        for v in o.values():
            _walk(v, out)
    elif isinstance(o, list):
        for v in o:
            _walk(v, out)


def _vec(v):
    return [float(v.get('x', 0.0)), float(v.get('y', 0.0)), float(v.get('z', 0.0))]


class Navmesh:
    def __init__(self, V, F):
        self.V, self.F = V, F
        self.comp = components(F) if F else np.zeros(0, np.int64)
        self.area = np.array([_area(V, f) for f in F])
        self.cen = np.array([V[f].mean(0) for f in F]).reshape(-1, 3)
        self.lo = np.array([V[f].min(0) for f in F]).reshape(-1, 3)
        self.hi = np.array([V[f].max(0) for f in F]).reshape(-1, 3)
        self.n_comp = int(self.comp.max()) + 1 if len(self.comp) else 0
        self.comp_area = np.bincount(self.comp, self.area, minlength=self.n_comp) if len(self.comp) else np.zeros(0)

    def height_at(self, i, x, z):
        """y of polygon i's plane at (x, z) (game axes)"""
        p = self.V[self.F[i]]
        n = np.cross(p[1] - p[0], p[2] - p[0])
        if abs(n[1]) < 1e-9:
            return float(p[:, 1].mean())
        return float(p[0, 1] - (n[0] * (x - p[0, 0]) + n[2] * (z - p[0, 2])) / n[1])

    def under(self, g, tol=STAND_TOL):
        """polygon a game point stands on (its feet within tol of the polygon), or -1"""
        x, y, z = g
        cand = np.nonzero((self.lo[:, 0] <= x) & (self.hi[:, 0] >= x) & (self.lo[:, 2] <= z) & (self.hi[:, 2] >= z))[0]
        best, bd = -1, tol
        for i in cand:
            if not _inside(x, z, self.V[self.F[i]][:, [0, 2]]):
                continue
            d = abs(self.height_at(i, x, z) - y)
            if d <= bd:
                best, bd = int(i), d
        return best

    def inside(self, outline):
        """polygons whose centre lies inside a 2D outline (game x, z)"""
        if not len(self.cen):
            return np.zeros(0, np.int64)
        lo, hi = outline.min(0), outline.max(0)
        cand = np.nonzero((self.cen[:, 0] >= lo[0]) & (self.cen[:, 0] <= hi[0]) & (self.cen[:, 2] >= lo[1]) & (self.cen[:, 2] <= hi[1]))[0]
        return np.array([i for i in cand if _inside(self.cen[i, 0], self.cen[i, 2], outline)], np.int64)

    def triangles(self, sel):
        """fan triangles (T, 3, 3) of the polygons sel, game coordinates"""
        t = [self.V[[f[0], f[k], f[k + 1]]] for i in sel for f in (self.F[i],) for k in range(1, len(f) - 1)]
        return np.array(t, np.float64).reshape(-1, 3, 3)


def load(work):
    """everything the overlay draws, Blender coordinates; raises NoBuild when the work folder holds no build"""
    work = work.replace('\\', '/')
    off = offset(work)
    objs = [p for p in sorted(glob.glob(work + '/nav/*_navmesh.polys.obj'))]
    if objs:
        V, F = load_obj(objs[0])
    else:
        V, F = np.zeros((0, 3)), []
    nav = Navmesh(V, F)
    items, seen = [], set()
    for p in sorted(glob.glob(work + '/dumps/spawns_*.json')):
        try:
            with open(p, encoding='utf-8') as f:
                found = []
                _walk(json.load(f), found)
        except (OSError, ValueError):
            continue
        layer = os.path.basename(p)[len('spawns_'):-len('.json')]
        for o in found:
            g = o.get('__guid') or id(o)
            if g in seen:
                continue
            seen.add(g)
            items.append((layer, o))
    if not objs and not items:
        raise NoBuild('No navmesh and no spawn layers in %s yet: build the map first.' % work)
    R = {'work': work, 'offset': list(off), 'navmesh_file': objs[0] if objs else '', 'spawns': [], 'finders': [], 'areas': [], 'problems': []}
    # spawn points
    pts = []
    for layer, o in items:
        if o['__type'] != 'AlternateSpawnEntityData':
            continue
        g = _vec(((o.get('Transform') or {}).get('trans')) or {})
        pts.append((layer, o, g, nav.under(g)))
    main = -1
    if nav.n_comp:
        per_comp = np.bincount(np.array([nav.comp[i] for *_, i in pts if i >= 0], np.int64), minlength=nav.n_comp)
        main = int(np.lexsort((nav.comp_area, per_comp))[-1])          # most spawns, then the biggest area
    for layer, o, g, i in pts:
        c = int(nav.comp[i]) if i >= 0 else -1
        status = 'off' if i < 0 else ('main' if c == main else 'island')
        fw = _vec(((o.get('Transform') or {}).get('forward')) or {'z': 1.0})
        fb = np.array([fw[0], -fw[2], fw[1]])                              # a direction: the axis change without the offset
        R['spawns'].append({'pos': to_blender(g, off).tolist(), 'game': g, 'team': TEAMS.get(o.get('Team'), 'neutral'), 'layer': layer,
                            'status': status, 'part': c, 'enabled': bool(o.get('Enabled', True)),
                            'fwd': (fb / max(np.linalg.norm(fb), 1e-9)).tolist()})
    # finder areas and play areas
    for layer, o in items:
        if o['__type'] == 'AlternateSpawnEntityData':
            continue
        P = np.array([_vec(v) for v in o.get('Points') or []], np.float64).reshape(-1, 3)
        if len(P) < 3:
            continue
        h = float(o.get('Height', 0.0))
        rec = {'layer': layer, 'height': h, 'outline': to_blender(P, off).tolist(), 'y': float(P[:, 1].mean())}
        if o['__type'] == 'SpawnLocationFinderShapeData':
            sel = nav.inside(P[:, [0, 2]])
            if h > 0:                                   # a set height keeps the search between the outline and outline + height
                sel = np.array([i for i in sel if rec['y'] - 0.5 <= nav.cen[i, 1] <= rec['y'] + h], np.int64)
            ys = nav.cen[sel, 1] if len(sel) else np.zeros(0)
            parts = sorted({int(nav.comp[i]) for i in sel})
            rec.update(team=TEAMS.get(o.get('Team'), 'neutral'), polygons=sel.tolist(), parts=parts,
                       floor_range=[float(ys.min()), float(ys.max())] if len(ys) else None,
                       floor_range_blender=[float(ys.min() - off[1]), float(ys.max() - off[1])] if len(ys) else None)
            k = len(R['finders']) + 1
            if not len(sel):
                R['problems'].append('Finder area %d (%s, %s): no navmesh inside it - its spawns have nowhere to go' % (k, rec['team'], layer))
            else:
                spread = float(ys.max() - ys.min())
                if spread > FLOOR_SPREAD:
                    R['problems'].append('Finder area %d (%s, %s): Height %g reaches floors %.0f m apart (%.1f to %.1f m)' % (
                        k, rec['team'], layer, h, spread, ys.min() - off[1], ys.max() - off[1]))
                off_main = [p for p in parts if p != main]
                if off_main:
                    R['problems'].append('Finder area %d (%s, %s): covers %d navmesh part(s) not connected to the main one' % (
                        k, rec['team'], layer, len(off_main)))
            R['finders'].append(rec)
        else:
            R['areas'].append(rec)
    # the navmesh, by part: the main part, parts that hold spawns or finder areas, the rest
    used = {s['part'] for s in R['spawns'] if s['part'] >= 0} | {p for f in R['finders'] for p in f['parts']}
    kind = np.array(['main' if c == main else ('island' if c in used else 'other') for c in nav.comp]) if nav.n_comp else np.zeros(0, str)
    R['nav'] = {k: to_blender(nav.triangles(np.nonzero(kind == k)[0]), off) for k in ('main', 'island', 'other')}
    R['nav_parts'] = int(nav.n_comp)
    R['nav_polygons'] = len(F)
    R['main_part'] = main
    R['main_area_m2'] = round(float(nav.comp_area[main]), 1) if main >= 0 else 0.0
    st = {}
    for s in R['spawns']:
        st[(s['team'], s['status'])] = st.get((s['team'], s['status']), 0) + 1
    R['spawn_counts'] = {'%s %s' % k: v for k, v in sorted(st.items())}
    for team in ('team1', 'team2', 'neutral'):
        isl = [s for s in R['spawns'] if s['team'] == team and s['status'] == 'island']
        off_ = [s for s in R['spawns'] if s['team'] == team and s['status'] == 'off']
        if isl:
            R['problems'].insert(0, '%d %s spawn(s) on %d navmesh part(s) not connected to the main one' % (
                len(isl), team, len({s['part'] for s in isl})))
        if off_:
            R['problems'].insert(0, '%d %s spawn(s) with no navmesh under their feet' % (len(off_), team))
    return R


def summary(R):
    """short lines for the panel"""
    lines = ['Navmesh: %d polygons in %d part(s); main part %.0f m2' % (R['nav_polygons'], R['nav_parts'], R['main_area_m2']),
             'Spawns: %d (%s)' % (len(R['spawns']), ', '.join('%d %s' % (v, k) for k, v in R['spawn_counts'].items()) or 'none'),
             'Finder areas: %d, play areas: %d' % (len(R['finders']), len(R['areas']))]
    return lines + R['problems']
