"""Stage `spawns`: spawn points, spawn-finder shapes, combat areas and defend areas of Blast / Arcade and Heroes vs
Villains, written onto the host shell's own spawn entities (none is created or deleted: the mode logic counts on them).

Where a spawn goes:
  * spawns placed by hand in the scene are used first, per team (hero spawns for Heroes vs Villains);
  * the rest are generated on the MAIN connected part of the navmesh (auto spawns on), each at least 2.5 m from a
    navmesh edge, with 3 m of headroom, outside every kill volume and "not walkable" box:
      Blast / Arcade  team bases at the 20 % / 80 % points of the walkable area's long axis (or around the hand-placed
                      team spawns), neutral spawns by farthest-point sampling at least 25 m from the bases;
      Heroes vs Villains  starts at the 30 % / 70 % points (or around the hand-placed hero spawns).
  * every generated spawn faces the middle of the map.
Shapes: team finders = discs on the bases; neutral finders = hulls of k-means zones; combat area = the scene's combat
area outline, else the hull of the walkable floor + 40 m. Heroes vs Villains shapes keep the host's Height (0): a height
from the base shuts out every spawn below it.
Output: spawns.json (the plan with every measured distance) and spawn_map.png (top-down, for the panel).
"""
import json
import math
import os

import numpy as np
from PIL import Image, ImageDraw

from .. import chain, cli, hosts
from ..audit import loader
from ..runner import StageError
from . import nav

EDGE_CLEAR, HEADROOM, MIN_TEAM_DISTANCE = 2.5, 3.0, 40.0
MIN_SPOTS = 30                     # safe spots wanted before the clearance is relaxed
CLEAR_LADDER = ((2.5, 3.0), (1.5, 3.0), (1.5, 2.2), (1.0, 2.2), (0.7, 2.2))      # (m from a floor edge, m of headroom)
# Play-area volumes (combat area, the Skirmish out-of-bounds polygon, defend areas) are prisms: an outline at a base
# height plus a Height. The host ships them 0 / 20 / 30 m tall at ITS floor; moving only the outline leaves a thin slab
# that misses the new floors, and every player is "out of bounds" everywhere (seen in the first game test of the
# tool, 2026-10-12: the port had set 400 m by hand in an early build and inherited it ever after). So the Height is
# always written: base AREA_BELOW m under the lowest spawn floor, top at least AREA_ABOVE m over the highest.
AREA_BELOW, AREA_ABOVE = 60.0, 340.0


def inputs(job):
    m = job.map
    return {'spawns': m.get('spawns'), 'kill': m.get('kill_volumes'), 'nw': m.get('not_walkable'), 'combat': m.get('combat_area'),
            'auto': (m.get('options') or {}).get('auto_spawns', True), 'offset': job.offset(), 'area': (AREA_BELOW, AREA_ABOVE), 'v': 8,
            'posts': [c.get('pos') for c in m.get('command_posts') or []]}      # v6: posts pick the floor too


def outputs(job):
    return [job.project('spawns'), job.work + '/spawns.json', job.work + '/spawn_map.png']


def load_obj(p):
    V, F = [], []
    with open(p) as f:
        for l in f:
            if l.startswith('v '):
                V.append([float(t) for t in l.split()[1:4]])
            elif l.startswith('f '):
                F.append([int(t.split('/')[0]) - 1 for t in l.split()[1:]])
    return np.array(V), F


JOIN_MIN_AREA, JOIN_MIN_FRAC, JOIN_DY = 30.0, 0.01, 30.0    # a walkable part joins the floor: m2, share of all, metres of height
                                                             # (LEGO Utapau: its biggest deck 26.5 m over the anchored one; the
                                                             # OG Coruscant backdrop plane 70 m under the temple stays out)
FINDER_HEIGHT = 4.0       # metres: a spawn-finder area reaches this far above its floor. Height 0 (as shipped on flat Kamino) let
                          # soldiers spawn on ANY floor under the outline - LEGO Utapau: the sinkhole floor 60 m below
ROOF_CELL, ROOF_SHARE = 2.0, 0.3   # a part above the anchored floor with 30 % of its polygons over it (2 m cells) is its roof


def anchors(job):
    """game positions of what says which floor is played on: hand-placed spawns AND command posts. The biggest walkable
    part can be a backdrop (OG Coruscant: a 73,000 m2 ground plane 70 m under the temple got every spawn and, through
    Supremacy, every post). Used by `spawns` and `supremacy` alike, so both pick the same floor."""
    return [job.to_game(s['pos']) for s in (job.map.get('spawns') or []) + (job.map.get('command_posts') or [])]


class Floor:
    """the playable navmesh component + the collision geometry above it"""

    def __init__(self, polys_obj, geo_obj, anchors=()):
        self.V, F = load_obj(polys_obj)
        GV, GF = load_obj(geo_obj)
        self.T = GV[np.array([(f[0], f[i], f[i + 1]) for f in GF for i in range(1, len(f) - 1)])]
        self.tmin, self.tmax = self.T.min(1), self.T.max(1)
        par = list(range(len(F)))

        def find(a):
            while par[a] != a:
                par[a] = par[par[a]]
                a = par[a]
            return a
        ed = {}
        for i, f in enumerate(F):
            for k in range(len(f)):
                e = tuple(sorted((f[k], f[(k + 1) % len(f)])))
                if e in ed:
                    par[find(i)] = find(ed[e])
                else:
                    ed[e] = i
        area, components = {}, {}
        for i, f in enumerate(F):
            root = find(i)
            area[root] = area.get(root, 0.0) + self._area(f)
            components.setdefault(root, []).append(f)
        # A large roof, exterior shell, or distant platform can have more area
        # than the playable floor. Hand-placed spawns identify which connected
        # component the user intends to play on.
        hits = {root: 0 for root in components}
        for anchor in anchors:
            p = np.asarray(anchor, float)
            for root, faces in components.items():
                for f in faces:
                    P = self.V[f]
                    if np.any(p < P.min(0) - 3.0) or np.any(p > P.max(0) + 3.0):
                        continue
                    if any(np.linalg.norm(_closest_on_tri(p, P[0], P[i], P[i + 1]) - p) <= 3.0
                           for i in range(1, len(f) - 1)):
                        hits[root] += 1
                        break
        main = max(area, key=lambda root: (hits[root], area[root]))
        # every other part a marker stands on joins it: OG Coruscant's posts B and C sit in two rooms (317 and 487 m2)
        # the navmesh does not connect to the temple floor (11,403 m2) - left out, their spawns went to the far side
        chosen = [main] + [root for root in components if root != main and hits[root] > 0]
        # and every sizeable part at the same heights: a detailed rip's floor comes out of the navmesh in many parts
        # (LEGO Utapau: 100 parts, the anchored one 381 of 18,167 m2 - every spawn on two strips and the combat area a
        # sliver of the map). A part far above or below the chosen ones (a backdrop plane, a roof) stays out.
        tot = sum(area.values())

        def ys(r):
            return self.V[np.unique(np.concatenate([np.asarray(f) for f in components[r]]))][:, 1]
        cy = np.concatenate([ys(r) for r in chosen])
        lo_y, hi_y = float(cy.min()), float(cy.max())
        # what the anchored floor covers on the map (2 m cells): a part ABOVE it that lies mostly over it is its roof
        occ = set()
        for r in chosen:
            for f in components[r]:
                c = self.V[f].mean(0)
                occ.add((int(c[0] // ROOF_CELL), int(c[2] // ROOF_CELL)))

        def over_floor(r):
            cs = [self.V[f].mean(0) for f in components[r]]
            return sum(1 for c in cs if (int(c[0] // ROOF_CELL), int(c[2] // ROOF_CELL)) in occ) / max(len(cs), 1)
        self.parts_same_height = 0
        self.join_report = {'anchored_y': [round(lo_y, 1), round(hi_y, 1)], 'parts': []}
        for r in sorted(components, key=lambda r: -area[r]):
            if r in chosen or area[r] < max(JOIN_MIN_AREA, JOIN_MIN_FRAC * tot):
                continue
            m = float(np.median(ys(r)))
            ok = lo_y - JOIN_DY <= m <= hi_y + JOIN_DY
            roof = ok and m > hi_y + 2.0 and over_floor(r) >= ROOF_SHARE
            ok = ok and not roof
            self.join_report['parts'].append([round(area[r], 1), round(m, 1), ok] + (['roof'] if roof else []))
            if ok:
                chosen.append(r)
                self.parts_same_height += 1
        self.anchor_hits = sum(hits[r] for r in chosen)
        self.anchor_count = len(anchors)
        self.all_polys = len(F)
        self.F = [f for r in chosen for f in components[r]]
        # the connected part each polygon belongs to: spawns go on ONE part (see Planner), the joined ones only widen
        # the combat area - bots cannot walk between parts the navmesh does not connect
        self.F_root = [r for r in chosen for f in components[r]]
        self.part_area, self.part_hits = {r: area[r] for r in chosen}, {r: hits[r] for r in chosen}
        self.parts_joined = len(chosen) - 1
        self.components, self.area, self.total_area = len(area), sum(area[r] for r in chosen), sum(area.values())
        ec = {}
        for f in self.F:
            for i in range(len(f)):
                e = tuple(sorted((f[i], f[(i + 1) % len(f)])))
                ec[e] = ec.get(e, 0) + 1
        self.BE = np.array([(self.V[a], self.V[b]) for (a, b), n in ec.items() if n == 1])

    def _area(self, f):
        P = self.V[f]
        return sum(0.5 * np.linalg.norm(np.cross(P[i] - P[0], P[i + 1] - P[0])) for i in range(1, len(f) - 1))

    def heights(self, x, z):
        """heights of the collision geometry on the vertical through (x, z)"""
        m = (self.tmin[:, 0] <= x) & (self.tmax[:, 0] >= x) & (self.tmin[:, 2] <= z) & (self.tmax[:, 2] >= z)
        out = []
        for a, b, c in self.T[m]:
            d = (b[2] - c[2]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[2] - c[2])
            if abs(d) < 1e-9:
                continue
            l1 = ((b[2] - c[2]) * (x - c[0]) + (c[0] - b[0]) * (z - c[2])) / d
            l2 = ((c[2] - a[2]) * (x - c[0]) + (a[0] - c[0]) * (z - c[2])) / d
            if min(l1, l2, 1 - l1 - l2) >= -1e-6:
                out.append(l1 * a[1] + l2 * b[1] + (1 - l1 - l2) * c[1])
        return out

    def edge_dist(self, p):
        a, b = self.BE[:, 0], self.BE[:, 1]
        ab = b - a
        t = np.clip(((p - a) * ab).sum(1) / np.maximum((ab * ab).sum(1), 1e-9), 0, 1)
        d = a + ab * t[:, None] - p
        near = np.abs(d[:, 1]) < 2.0
        return float(np.sqrt((d[near][:, [0, 2]] ** 2).sum(1)).min()) if near.any() else 99.0

    def headroom(self, p):
        above = [h for h in self.heights(p[0], p[2]) if h > p[1] + 0.3]
        return (min(above) - p[1]) if above else 99.0

    def snap(self, p, max_d=3.0):
        """Nearest point of the chosen component to a hand-placed spawn."""
        best, bd = None, 1e9
        for f in self.F:
            P = self.V[f]
            # A polygon's centre can be far from a spawn beside its edge.
            # The box bound skips distant faces without hiding the true distance.
            box_d = np.linalg.norm(np.maximum(np.maximum(P.min(0) - p, p - P.max(0)), 0.0))
            if box_d >= bd:
                continue
            for i in range(1, len(f) - 1):
                q = _closest_on_tri(np.asarray(p, float), P[0], P[i], P[i + 1])
                d = float(np.linalg.norm(q - p))
                if d < bd:
                    best, bd = q, d
        return (best, bd) if bd <= max_d else (None, bd)

    def candidates(self, rng, per_m2=1 / 6.0):
        out, self.cand_root = [], []
        for f, root in zip(self.F, self.F_root):
            P = self.V[f]
            for i in range(1, len(f) - 1):
                a, b, c = P[0], P[i], P[i + 1]
                ar = 0.5 * np.linalg.norm(np.cross(b - a, c - a)) * per_m2
                for _ in range(int(ar) + (1 if rng.random() < (ar % 1.0) else 0)):
                    r1, r2 = rng.random(), rng.random()
                    if r1 + r2 > 1:
                        r1, r2 = 1 - r1, 1 - r2
                    out.append(a + r1 * (b - a) + r2 * (c - a))
                    self.cand_root.append(root)
        return np.array(out)


def _closest_on_tri(p, a, b, c):
    ab, ac, ap = b - a, c - a, p - a
    d1, d2 = ab @ ap, ac @ ap
    if d1 <= 0 and d2 <= 0:
        return a
    bp = p - b; d3, d4 = ab @ bp, ac @ bp
    if d3 >= 0 and d4 <= d3:
        return b
    vc = d1 * d4 - d3 * d2
    if vc <= 0 and d1 >= 0 and d3 <= 0:
        return a + ab * (d1 / (d1 - d3))
    cp = p - c; d5, d6 = ab @ cp, ac @ cp
    if d6 >= 0 and d5 <= d6:
        return c
    vb = d5 * d2 - d1 * d6
    if vb <= 0 and d2 >= 0 and d6 <= 0:
        return a + ac * (d2 / (d2 - d6))
    va = d3 * d6 - d5 * d4
    if va <= 0 and (d4 - d3) >= 0 and (d5 - d6) >= 0:
        return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)))
    den = 1.0 / (va + vb + vc)
    return a + ab * (vb * den) + ac * (vc * den)


def in_box(p_scene, box):
    """is a scene point inside an exported oriented box (centre, half extents, axes as columns)"""
    d = np.asarray(p_scene) - np.asarray(box['centre'])
    ax = np.asarray(box['axes'])
    return all(abs(float(d @ ax[i])) <= box['half'][i] for i in range(3))


def hull(P):
    P = sorted(set(map(tuple, np.round(P, 4))))
    if len(P) < 3:
        return np.array(P)

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lo, up = [], []
    for p in P:
        while len(lo) >= 2 and cross(lo[-2], lo[-1], p) <= 0:
            lo.pop()
        lo.append(p)
    for p in reversed(P):
        while len(up) >= 2 and cross(up[-2], up[-1], p) <= 0:
            up.pop()
        up.append(p)
    return np.array(lo[:-1] + up[:-1])


def resample(poly, n, grow=0.0):
    poly = np.asarray(poly, float)
    c = poly.mean(0)
    if grow:
        poly = np.array([p + (p - c) / max(np.linalg.norm(p - c), 1e-6) * grow for p in poly])
    seg = np.roll(poly, -1, 0) - poly
    Lg = np.linalg.norm(seg, axis=1)
    cum = np.concatenate([[0], np.cumsum(Lg)])
    out = []
    for k in range(n):
        s = cum[-1] * k / n
        i = min(int(np.searchsorted(cum, s, side='right')) - 1, len(poly) - 1)
        out.append(poly[i] + seg[i] * ((s - cum[i]) / max(Lg[i], 1e-9)))
    return np.array(out)


def disc(c, r, n=16):
    return np.array([[c[0] + r * math.cos(a), c[2] + r * math.sin(a)] for a in np.linspace(0, 2 * math.pi, n, endpoint=False)])


def layer_items(dump):
    out = []

    def walk(o):
        if isinstance(o, dict):
            if o.get('__type') in ('AlternateSpawnEntityData', 'SpawnLocationFinderShapeData', 'VolumeVectorShapeData') and o.get('__guid'):
                out.append({'type': o['__type'], 'guid': o['__guid'], 'team': o.get('Team'), 'npts': len(o.get('Points', []))})
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
    walk(dump)
    return out


class Planner:
    def __init__(self, job, floor, rng):
        self.job, self.floor, self.rng = job, floor, rng
        m = job.map
        blocked = list(m.get('kill_volumes') or []) + list(m.get('not_walkable') or [])
        # a small floor is sampled more densely (at most 2 spots / m2), so a tight map still yields enough spots
        cand = floor.candidates(rng, per_m2=float(np.clip(600.0 / max(floor.area, 1.0), 1 / 6.0, 2.0)))
        edge = np.array([floor.edge_dist(p) for p in cand]) if len(cand) else np.zeros(0)
        head = np.array([floor.headroom(p) for p in cand]) if len(cand) else np.zeros(0)
        free = np.array([not (blocked and any(in_box(job.from_game(p), b) for b in blocked)) for p in cand], bool) if len(cand) else np.zeros(0, bool)
        # Clearance ladder: an open map keeps 2.5 m from every floor edge and 3 m of headroom; a tight one (rooms,
        # furniture, narrow walkways - seen on a furnished veranda with 412 m2 of connected floor) steps down until
        # MIN_SPOTS are found. Never under 0.7 m from an edge or 2.2 m of headroom: a soldier needs that much.
        ok = np.zeros(len(cand), bool)
        self.clear, self.headroom = EDGE_CLEAR, HEADROOM
        for ec, hr in CLEAR_LADDER:
            ok = (edge >= ec) & (head >= hr) & free
            self.clear, self.headroom = ec, hr
            if ok.sum() >= MIN_SPOTS:
                break
        self.rejected = {'edge': int((edge < self.clear).sum()), 'headroom': int(((edge >= self.clear) & (head < self.headroom)).sum()),
                         'blocked': int(((edge >= self.clear) & (head >= self.headroom) & ~free).sum())}
        self.n_cand = len(cand)
        self.C = np.array([p for p, k in zip(cand, ok) if k])
        # every safe spot of the joined floor: the combat area and play volumes are drawn round these
        self.C_all = self.C
        # spawns: ONE connected part. LEGO Utapau (2026-10-23): spawns spread over 7 parts the navmesh does not connect,
        # Team 2 mostly on islands - in Blast the bots stood still with no path to anyone. The anchored part (hand-placed
        # spawns / posts on it) when it has room, else the part with the most safe spots.
        roots = np.array(floor.cand_root)[ok] if len(cand) else np.zeros(0)
        count = {r: int((roots == r).sum()) for r in set(roots.tolist())}
        self.spawn_part = None
        if len(count) > 1:
            part = max(count, key=lambda r: (floor.part_hits.get(r, 0) > 0 and count[r] >= MIN_SPOTS, count[r]))
            if count[part] >= 8:
                self.C = self.C[roots == part]
                self.spawn_part = {'area_m2': round(float(floor.part_area[part]), 1), 'spots': count[part], 'parts_with_spots': len(count),
                                   'anchored': floor.part_hits.get(part, 0) > 0}
        self.spacing = 2.5 if self.clear >= EDGE_CLEAR else max(1.2, self.clear)
        if len(self.C) < 8:
            raise StageError('Only %d safe spawn spots were found on the walkable floor (%d m2 in its largest connected part), even at '
                             '%.1f m from the floor edges. The map needs more open, connected floor with collision: set small props '
                             'and furniture to Collision "None" so they do not cut the floor up.' % (len(self.C), floor.area, self.clear))
        self.mid = self.C.mean(0)
        X = self.C[:, [0, 2]] - self.mid[[0, 2]]
        self.axis = np.linalg.eigh(X.T @ X)[1][:, -1]
        self.proj = X @ self.axis

    def yaw_to_mid(self, p):
        return math.atan2(self.mid[0] - p[0], self.mid[2] - p[2])

    def base_at(self, end, n, R, q=0.2, around=None):
        C, proj = self.C, self.proj
        if around is not None:
            order = np.argsort(np.hypot(*(C[:, [0, 2]] - np.asarray(around)[[0, 2]]).T) + 3.0 * np.abs(C[:, 1] - around[1]))
        else:
            target = np.quantile(proj, q if end < 0 else 1 - q)
            order = np.argsort(np.abs(proj - target))
        best = None
        # when no spot has room for every slot, the roomiest spot is taken - but only among the spots nearest this
        # team's end: the roomiest spot of the WHOLE floor is the same for both teams (Hunters Bastion, 2026-10-23:
        # both bases on one spot, "0 m apart", on a navmesh cut into 529 parts)
        # ...and the search itself stays there too: walking the whole list found the one roomy spot of the floor for
        # BOTH teams (12 slots each on the same navmesh, still "0 m apart" after the first fix)
        near_end = max(20, len(order) // 4) if around is None else 600
        for rank, i in enumerate(order[:min(600, near_end)]):
            s = C[i]
            near = C[(np.hypot(*(C[:, [0, 2]] - s[[0, 2]]).T) < R) & (np.abs(C[:, 1] - s[1]) < 1.5)]
            pick = []
            for p in near[np.argsort(np.hypot(*(near[:, [0, 2]] - s[[0, 2]]).T))]:
                if all(np.hypot(*(p - q2)[[0, 2]]) >= self.spacing for q2 in pick):
                    pick.append(p)
                if len(pick) == n:
                    return s, pick
            if (best is None or len(pick) > len(best[1])) and (rank < near_end or around is not None):
                best = (s, pick)
        if best is None or not best[1]:
            raise StageError('No open spot was found for a team base. The walkable floor is too tight; place the team spawns by hand.')
        # a tight map: fewer separate spots than slots - the slots share the spots that are there
        s, pick = best
        self.shared_base_spots = True
        return s, [pick[k % len(pick)] for k in range(n)]

    def spread(self, n, avoid, mind):
        C = self.C
        pool = np.array([p for p in C if all(np.hypot(*(p - a)[[0, 2]]) > mind for a in avoid)])
        if len(pool) < n:
            pool = C
        pick = [pool[int(np.argmin(np.abs(pool[:, [0, 2]] - self.mid[[0, 2]]).sum(1)))]]
        d = np.full(len(pool), 1e9)
        while len(pick) < n:
            d = np.minimum(d, np.sqrt(((pool - pick[-1]) * [1, 2, 1]) ** 2 @ np.ones(3)))
            pick.append(pool[int(np.argmax(d))])
        return pick

    def zones(self, k):
        C = self.C
        X3 = C * [1, 3, 1]
        cent = X3[self.rng.choice(len(X3), k, replace=k > len(X3))]
        lab = np.zeros(len(X3), int)
        for _ in range(40):
            lab = np.argmin(((X3[:, None] - cent[None]) ** 2).sum(-1), 1)
            cent = np.array([X3[lab == j].mean(0) if (lab == j).any() else cent[j] for j in range(k)])
        return [C[lab == j] for j in range(k) if (lab == j).sum() >= 3] or [C]


def run(job, log):
    host = hosts.get(job.host)
    clix = cli.Cli(job, log)
    polys, geo = nav.files(job)
    floor = Floor(polys, geo, anchors(job))
    rng = np.random.default_rng(11)
    P = Planner(job, floor, rng)
    load = loader(job.path('dumps', 'shell'))
    L = job.level_dir + '/'
    steps = [cli.load(chain.base(job, 'spawns'))]
    auto = (job.map.get('options') or {}).get('auto_spawns', True)
    hand = {'team1': [], 'team2': [], 'neutral': [], 'hero_team1': [], 'hero_team2': []}
    off_floor = []
    for s in job.map.get('spawns') or []:
        g = np.array(job.to_game(s['pos']))
        q, d = floor.snap(g)
        if q is None:
            off_floor.append((s['name'], round(d, 1)))
            continue
        hand[('hero_' if s.get('hero') else '') + s['team'] if not (s.get('hero') and s['team'] == 'neutral') else 'neutral'].append(
            (q, math.pi - s.get('yaw', 0.0), s['name']))
    # with auto spawns on, spawns that sit on another floor island (a platform the walkable floor does not reach) are
    # left out as long as every team that had hand-placed spawns keeps one on the main floor: the planner fills the
    # rest around them (LEGO Utapau: 5 of 12 prepared spawns on ledges no path reaches stopped the build)
    teams_had = {('hero_' if s.get('hero') else '') + s['team'] for s in job.map.get('spawns') or [] if s.get('team') in ('team1', 'team2')}
    if off_floor and auto and all(hand.get(t) for t in teams_had):
        log('spawns: WARNING %d spawn(s) not on the connected walkable floor are left out (the others and the auto spawns '
            'are used): %s' % (len(off_floor), ', '.join('%s (%.1f m)' % x for x in off_floor[:8])))
        dropped_off_floor, off_floor = off_floor, []
    else:
        dropped_off_floor = []
    if off_floor:
        raise StageError('%d spawn(s) are not on the selected connected walkable floor (more than 3 m from it): %s. '
                         'Place them on the same connected floor with collision, inside the play area.' %
                         (len(off_floor), ', '.join('%s (%.1f m)' % x for x in off_floor[:8])))

    def st(lay, guid, prop, value):
        steps.append({'op': 'ebx-set', 'ebx': L + lay, 'object': guid, 'prop': prop, 'value': value})

    def put_spawn(lay, guid, p, yaw=None, team=None):
        for a, v in zip('xyz', (p[0], p[1] + 1.0, p[2])):
            st(lay, guid, 'Transform.trans.' + a, round(float(v), 3))
        yaw = P.yaw_to_mid(p) if yaw is None else yaw
        for k, vec in (('forward', (math.sin(yaw), 0.0, math.cos(yaw))), ('right', (math.cos(yaw), 0.0, -math.sin(yaw))), ('up', (0.0, 1.0, 0.0))):
            for a, v in zip('xyz', vec):
                st(lay, guid, 'Transform.%s.%s' % (k, a), round(v, 5))
        if team:
            st(lay, guid, 'Team', team)

    volumes = []                                       # (layer, guid, outline, base y, height) for the read-back check

    def put_shape(lay, guid, poly2d, y, n, grow=0.0, height=None):
        pts = resample(poly2d, n, grow)
        for k, q in enumerate(pts):
            for a, v in zip('xyz', (q[0], y, q[1])):
                st(lay, guid, 'Points[%d].%s' % (k, a), round(float(v), 3))
        if height is not None:
            st(lay, guid, 'Height', round(float(height), 2))
            volumes.append((lay, guid, pts, float(y), float(height)))

    def fill(guids, placed, generated):
        """hand-placed first, then generated; with auto spawns off the placed ones are reused in turn -> [(p, yaw)]"""
        out = [(p, y) for p, y, _ in placed][:len(guids)]
        if len(out) < len(guids):
            if auto or not placed:
                out += [(p, None) for p in generated[:len(guids) - len(out)]]
            k = 0
            while len(out) < len(guids):
                out.append(out[k % max(len(out), 1)]); k += 1
        return out

    if job.map.get('combat_area') and len(job.map['combat_area'].get('points', [])) >= 3:
        pts = np.array([job.to_game((x, y, 0.0)) for x, y in job.map['combat_area']['points']])[:, [0, 2]]
        area_poly, area_grow, area_src = hull(pts), 0.0, 'the scene\'s combat area outline'
    else:
        area_poly, area_grow, area_src = hull(P.C_all[:, [0, 2]]), 40.0, 'hull of the walkable floor + 40 m'
    floor_y = float(np.percentile(P.C_all[:, 1], 5))
    area_base = float(P.C_all[:, 1].min()) - AREA_BELOW
    area_height = float(P.C_all[:, 1].max()) + AREA_ABOVE - area_base
    plan = {'navmesh': {'main_polygons': len(floor.F), 'polygons': floor.all_polys, 'components': floor.components, 'parts_same_height_joined': floor.parts_same_height,
                        'main_area_m2': round(floor.area, 1), 'total_area_m2': round(floor.total_area, 1),
                        'placed_spawns_on_component': floor.anchor_hits, 'placed_spawns': floor.anchor_count},
            'candidates': {'sampled': P.n_cand, 'valid': len(P.C_all), 'spawn_part': P.spawn_part, 'rejected': P.rejected}, 'combat_area': area_src,
            'hand_placed': {k: len(v) for k, v in hand.items()}, 'left_out_off_floor': [n for n, _ in dropped_off_floor]}
    drawn = {}
    # ---- Blast / Arcade
    lay = 'TeamDeathmatch_Global'
    Gl = layer_items(load(lay))
    sp = {t: [i['guid'] for i in Gl if i['type'] == 'AlternateSpawnEntityData' and i['team'] == t] for t in ('Team1', 'Team2', 'TeamNeutral')}
    c1 = np.mean([p for p, _, _ in hand['team1']], 0) if hand['team1'] else None
    c2 = np.mean([p for p, _, _ in hand['team2']], 0) if hand['team2'] else None
    b1, s1 = P.base_at(-1, len(sp['Team1']), 14.0, around=c1)
    b2, s2 = P.base_at(+1, len(sp['Team2']), 14.0, around=c2)
    if c1 is not None:
        b1 = c1
    if c2 is not None:
        b2 = c2

    def own(gen, mine, other):
        """generated spots a team may use: those nearer to its own base than to the other team's (on a tight map the
        only open floor can lie next to the enemy); none left -> the base itself, the slots then share it"""
        keep = [p for p in gen if np.hypot(*(np.asarray(p) - mine)[[0, 2]]) < np.hypot(*(np.asarray(p) - other)[[0, 2]])]
        return keep or [np.asarray(mine)]
    s1, s2 = own(s1, np.asarray(b1), np.asarray(b2)), own(s2, np.asarray(b2), np.asarray(b1))
    neu = P.spread(len(sp['TeamNeutral']), [b1, b2], 25.0)
    # auto spawns off = nobody spawns anywhere but at a placed spawn: without placed neutral spawns the neutral slots
    # (the mode's roaming respawn points) take the two teams' placed spawns in turn
    neutral_placed = hand['neutral']
    if not auto and not neutral_placed:
        neutral_placed = [x for pair in zip(hand['team1'], hand['team2']) for x in pair] or hand['team1'] or hand['team2']
    f1, f2, fn = fill(sp['Team1'], hand['team1'], s1), fill(sp['Team2'], hand['team2'], s2), fill(sp['TeamNeutral'], neutral_placed, neu)
    for g, (p, y) in zip(sp['Team1'], f1): put_spawn(lay, g, p, y, 'Team1')
    for g, (p, y) in zip(sp['Team2'], f2): put_spawn(lay, g, p, y, 'Team2')
    for g, (p, y) in zip(sp['TeamNeutral'], fn): put_spawn(lay, g, p, y, 'TeamNeutral')
    shp = [i for i in Gl if i['type'] == 'SpawnLocationFinderShapeData']
    nz = [i for i in shp if i['team'] == 'TeamNeutral']
    for i in shp:
        if i['team'] in ('Team1', 'Team2'):
            b = b1 if i['team'] == 'Team1' else b2
            put_shape(lay, i['guid'], disc(b, 14.0), b[1] - 0.5, i['npts'], height=FINDER_HEIGHT)
    zs = P.zones(len(nz)) if nz else []
    for k, i in enumerate(nz):
        z = zs[k % len(zs)]
        put_shape(lay, i['guid'], hull(z[:, [0, 2]]), float(np.median(z[:, 1])) - 0.5, i['npts'], grow=3.0, height=FINDER_HEIGHT)
    for i in Gl:
        if i['type'] == 'VolumeVectorShapeData':
            put_shape(lay, i['guid'], area_poly, area_base, i['npts'], grow=area_grow, height=area_height)
    apart = float(np.hypot(*(np.asarray(b1) - np.asarray(b2))[[0, 2]]))
    nps = [p for p, _ in fn]
    plan['blast'] = {'base1': np.round(b1, 1).tolist(), 'base2': np.round(b2, 1).tolist(), 'bases_apart_m': round(apart, 1),
                     'spawns': [len(f1), len(f2), len(fn)],
                     'neutral_min_spacing_m': round(float(min(np.hypot(*(a - b)[[0, 2]]) for i, a in enumerate(nps) for b in nps[i + 1:])), 1) if len(nps) > 1 else None}
    drawn['blast'] = (f1, f2, fn)
    # ---- Arcade defend areas + the Skirmish kill polygon (anything outside it dies)
    for i, b in zip([x for x in layer_items(load('TeamDeathmatch_Skirmish_DefendAreas')) if x['type'] == 'VolumeVectorShapeData'], (b1, b2, P.mid)):
        put_shape('TeamDeathmatch_Skirmish_DefendAreas', i['guid'], disc(b, 18.0), b[1] - 5.0, i['npts'], height=30.0)
    for i in layer_items(load('TeamDeathmatch_Skirmish_Logic')):
        if i['type'] == 'VolumeVectorShapeData':
            put_shape('TeamDeathmatch_Skirmish_Logic', i['guid'], area_poly, area_base, i['npts'], grow=area_grow, height=area_height)
    # ---- Heroes vs Villains
    HL, HS = layer_items(load('HeroArena_Logic')), layer_items(load('HeroArena_Shapes'))
    t1 = [('HeroArena_Logic', i['guid']) for i in HL if i['type'] == 'AlternateSpawnEntityData' and i['team'] == 'Team1']
    t2 = [(ly, i['guid']) for ly, src in (('HeroArena_Logic', HL), ('HeroArena_Shapes', HS)) for i in src
          if i['type'] == 'AlternateSpawnEntityData' and i['team'] == 'Team2']
    hc1 = np.mean([p for p, _, _ in hand['hero_team1']], 0) if hand['hero_team1'] else None
    hc2 = np.mean([p for p, _, _ in hand['hero_team2']], 0) if hand['hero_team2'] else None
    h1, hs1 = P.base_at(-1, len(t1), 12.0, 0.3, around=hc1)
    h2, hs2 = P.base_at(+1, len(t2), 12.0, 0.3, around=hc2)
    if hc1 is not None:
        h1 = hc1
    if hc2 is not None:
        h2 = hc2
    # auto spawns off and no hero spawns placed: the heroes start at their team's placed spawns
    hp1 = hand['hero_team1'] or ([] if auto else hand['team1'])
    hp2 = hand['hero_team2'] or ([] if auto else hand['team2'])
    if not hand['hero_team1'] and hp1:
        h1 = np.mean([p for p, _, _ in hp1], 0)
    if not hand['hero_team2'] and hp2:
        h2 = np.mean([p for p, _, _ in hp2], 0)
    # generated hero starts that fall together (a tight map has one open patch): the heroes start at their team's base
    need_apart = min(MIN_TEAM_DISTANCE, 0.5 * float(P.proj.max() - P.proj.min()))
    if not hp1 and not hp2 and float(np.hypot(*(np.asarray(h1) - np.asarray(h2))[[0, 2]])) < need_apart:
        h1, h2, hs1, hs2 = np.asarray(b1), np.asarray(b2), list(s1), list(s2)
    hs1, hs2 = own(hs1, np.asarray(h1), np.asarray(h2)), own(hs2, np.asarray(h2), np.asarray(h1))
    g1, g2 = fill(t1, hp1, hs1), fill(t2, hp2, hs2)
    for (ly, g), (p, y) in zip(t1, g1): put_spawn(ly, g, p, y)
    for (ly, g), (p, y) in zip(t2, g2): put_spawn(ly, g, p, y)
    hshp = [i for i in HS if i['type'] == 'SpawnLocationFinderShapeData']
    hnz = [i for i in hshp if i['team'] == 'TeamNeutral']
    for i in hshp:
        if i['team'] in ('Team1', 'Team2'):
            b = h1 if i['team'] == 'Team1' else h2
            put_shape('HeroArena_Shapes', i['guid'], disc(b, 12.0), b[1] + 1.5, i['npts'])
    zs = P.zones(len(hnz)) if hnz else []
    for k, i in enumerate(hnz):
        z = zs[k % len(zs)]
        put_shape('HeroArena_Shapes', i['guid'], hull(z[:, [0, 2]]), float(np.median(z[:, 1])) + 1.5, i['npts'], grow=3.0)
    for i in HS:
        if i['type'] == 'VolumeVectorShapeData':
            put_shape('HeroArena_Shapes', i['guid'], area_poly, floor_y - 60.0, i['npts'], grow=area_grow)
    plan['hvv'] = {'start1': np.round(h1, 1).tolist(), 'start2': np.round(h2, 1).tolist(),
                   'starts_apart_m': round(float(np.hypot(*(np.asarray(h1) - np.asarray(h2))[[0, 2]])), 1), 'spawns': [len(g1), len(g2)]}
    # 40 m between the bases on a map that has the room; a small map needs half its own walkable length
    need_apart = min(MIN_TEAM_DISTANCE, 0.5 * float(P.proj.max() - P.proj.min()))
    plan['blast']['bases_apart_needed_m'] = round(need_apart, 1)
    plan['candidates'].update(edge_clearance_m=P.clear, headroom_m=P.headroom, spacing_m=P.spacing,
                              base_spots_shared=bool(getattr(P, 'shared_base_spots', False)))
    if apart < need_apart:
        raise StageError('The two team bases are only %.0f m apart (at least %.0f m is needed on this map). Move the team spawns '
                         'apart or enlarge the walkable area.' % (apart, need_apart))
    out = job.project('spawns')
    steps.append(cli.save(out))
    r = clix.run('spawns', steps)
    if r.errors:
        raise StageError('Writing the spawns reported errors: %s  [log: %s]' % (' | '.join(r.errors[:3])[:500], r.log_path))
    # read-back from the saved project: every play-area volume has the planned height, and every Blast / Arcade spawn
    # lies inside the out-of-bounds polygon and the combat area (inside the outline, between base and top)
    lays = sorted({v[0] for v in volumes})
    rb = clix.run('spawns_readback', [cli.load(out)] + [
        {'op': 'ebx-dump', 'ebx': L + ly, 'deep': True, 'out': job.path('dumps', 'spawns_%s.json' % ly)} for ly in lays])
    got = {}
    for ly in lays:
        with open(job.path('dumps', 'spawns_%s.json' % ly), encoding='utf-8') as f:
            for o in _walk_shapes(json.load(f)):
                got[o['__guid']] = o
    bad = []
    for ly, guid, pts, base, height in volumes:
        o = got.get(guid)
        if o is None or abs(float(o.get('Height', -1)) - height) > 0.05:
            bad.append('%s %s: Height reads back %s, planned %.1f' % (ly, guid[:8], None if o is None else o.get('Height'), height))
    outside = 0
    play = [v for v in volumes if v[0] in ('TeamDeathmatch_Global', 'TeamDeathmatch_Skirmish_Logic') and v[4] >= 100.0]
    for p, _ in list(f1) + list(f2) + list(fn):
        for ly, guid, pts, base, height in play:
            if not (base <= p[1] + 1.0 <= base + height) or not _in_poly(p[0], p[2], pts):
                outside += 1
    plan['play_area'] = {'base_y': round(area_base, 1), 'height': round(area_height, 1), 'volumes_written': len(volumes),
                         'spawns_outside_a_play_volume': outside, 'readback_problems': bad}
    with open(job.work + '/spawns.json', 'w', encoding='utf-8') as f:
        json.dump(plan, f, indent=1)
    if bad or outside:
        raise StageError('The play area does not hold the spawns: %d spawn / volume pairs outside; %s  [log: %s]' % (
            outside, '; '.join(bad[:4]), rb.log_path))
    _draw_map(job, floor, area_poly, area_grow, f1, f2, fn, g1, g2)
    log('spawns: main navmesh part %d of %d polygons, %.0f m2 (%d parts); %d safe spots; Blast bases %.0f m apart (%d + %d + %d neutral); '
        'Heroes vs Villains starts %.0f m apart (%d + %d); hand-placed %s' % (
            len(floor.F), floor.all_polys, floor.area, floor.components, len(P.C), apart, len(f1), len(f2), len(fn),
            plan['hvv']['starts_apart_m'], len(g1), len(g2), plan['hand_placed']))
    return plan


def _walk_shapes(d):
    out = []

    def walk(o):
        if isinstance(o, dict):
            if o.get('__type') in ('VolumeVectorShapeData', 'SpawnLocationFinderShapeData') and o.get('__guid'):
                out.append(o)
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
    walk(d)
    return out


def _in_poly(x, z, pts):
    inside = False
    n = len(pts)
    for i in range(n):
        x1, z1 = pts[i]
        x2, z2 = pts[(i + 1) % n]
        if (z1 > z) != (z2 > z) and x < (x2 - x1) * (z - z1) / (z2 - z1) + x1:
            inside = not inside
    return inside


def _draw_map(job, floor, area_poly, grow, s1, s2, neu, h1, h2):
    V, F = floor.V, floor.F
    used = np.array([V[i] for f in F for i in f])
    lo, hi = used.min(0), used.max(0)
    S = max(1.0, min(4.0, 1400.0 / max(hi[0] - lo[0], hi[2] - lo[2], 1.0)))
    W, H = int((hi[0] - lo[0]) * S) + 80, int((hi[2] - lo[2]) * S) + 80
    img = Image.new('RGB', (W, H), (20, 22, 26))
    dr = ImageDraw.Draw(img)
    px = lambda x, z: (40 + (x - lo[0]) * S, H - 40 - (z - lo[2]) * S)
    for f in F:
        dr.polygon([px(V[k][0], V[k][2]) for k in f], fill=(120, 124, 130))
    ring = resample(area_poly, 64, grow)
    dr.line([px(*q) for q in ring] + [px(*ring[0])], fill=(200, 60, 60), width=2)

    def dot(p, col, r=4):
        x, y = px(p[0], p[2])
        dr.ellipse([x - r, y - r, x + r, y + r], fill=col, outline=(0, 0, 0))
    for p, _ in s1: dot(p, (60, 120, 255))
    for p, _ in s2: dot(p, (255, 70, 60))
    for p, _ in neu: dot(p, (240, 240, 240))
    for p, _ in h1: dot(p, (60, 220, 255), 3)
    for p, _ in h2: dot(p, (255, 170, 40), 3)
    dr.text((10, 10), '%s spawns: blue / red = team 1 / team 2, white = neutral, cyan / orange = hero starts, red line = combat area' % job.name,
            fill=(255, 255, 255))
    img.save(job.work + '/spawn_map.png')
