"""Pre-flight checks that need the scene's triangles: where things stand against the solid world.

  spawn.in_geometry   a placed spawn's body (0.5 .. 1.5 m over its point) is inside or pressed against something solid
  spawn.no_floor      nothing solid under a placed spawn within 3 m
  kill.walkable       a kill volume holds floor at play height (the case that killed soldiers on a walkway)
  light.in_surface    a light sits inside or within 0.5 m of a drawn surface
  door.travel         a door's travel distance leaves it in the doorway, or throws it far out of it
  geo.flipped         a closed object whose faces point inwards (it is drawn inside-out: invisible from outside)

"Solid" follows the collision stage: an object's own triangles (exact), its collision mesh (custom), its convex hull
(hull), nothing (none), and never a lava material. Doors are left out: they move.
"""
import numpy as np

from . import R
from ..stages import collision as collision_stage
from ..stages import doors as doors_stage

BODY = (0.5, 1.0, 1.5)          # heights over a spawn point that must be free, metres
BODY_CLEAR = 0.25               # ... by this much
FLOOR_REACH = 3.0               # a spawn must have floor this close under it (the spawn stage's own limit)
LIGHT_CLEAR = 0.5
KILL_FLOOR_AREA = 2.0           # m2 of play-height floor inside a kill volume before it is reported
PLAY_BELOW = 1.0                # floor lower than the lowest placed spawn by more than this is a pit, not a walkway


class Tris:
    """a triangle soup with per-triangle owner and bounding boxes"""

    def __init__(self, parts):
        parts = [(n, np.asarray(t, np.float64).reshape(-1, 3, 3)) for n, t in parts if len(t)]
        self.names = [n for n, _ in parts]
        self.t = np.concatenate([t for _, t in parts]) if parts else np.zeros((0, 3, 3))
        self.owner = np.concatenate([np.full(len(t), i) for i, (_, t) in enumerate(parts)]) if parts else np.zeros(0, int)
        self.lo, self.hi = (self.t.min(1), self.t.max(1)) if len(self.t) else (np.zeros((0, 3)), np.zeros((0, 3)))
        n = np.cross(self.t[:, 1] - self.t[:, 0], self.t[:, 2] - self.t[:, 0]) if len(self.t) else np.zeros((0, 3))
        self.area = np.linalg.norm(n, axis=1) / 2
        self.n = n / np.maximum(2 * self.area, 1e-12)[:, None]

    def near(self, p, r):
        """indices of the triangles whose box comes within r of point p"""
        p = np.asarray(p, np.float64)
        return np.nonzero(np.all(self.lo <= p + r, 1) & np.all(self.hi >= p - r, 1))[0]

    def distance(self, p, r):
        """(distance, owner name) of the nearest triangle within r of p, or (None, None)"""
        i = self.near(p, r)
        if not len(i):
            return None, None
        d = tri_distance(np.asarray(p, np.float64), self.t[i])
        k = int(np.argmin(d))
        return (float(d[k]), self.names[int(self.owner[i[k]])]) if d[k] <= r else (None, None)

    def inside(self, p):
        """name of a CLOSED object the point lies inside (a ray straight up crosses its surface an odd number of times),
        or None. Open surfaces - a roof over the point - do not count."""
        p = np.asarray(p, np.float64) + (3.7e-4, 6.1e-4, 0.0)            # off the edges two triangles share
        if not hasattr(self, '_closed'):
            self._closed, self._obox = {}, {}
            for i in range(len(self.names)):
                m = self.owner == i
                self._obox[i] = (self.lo[m].min(0), self.hi[m].max(0))
        for i, (lo, hi) in self._obox.items():
            if np.any(p < lo) or np.any(p > hi):
                continue
            t = self.t[self.owner == i]
            if i not in self._closed:
                vol = closed_volume(t)
                self._closed[i] = vol is not None and vol > 0          # faces inwards = a room: its inside is free
            if not self._closed[i]:
                continue
            a, b, c = t[:, 0], t[:, 1], t[:, 2]
            d = (b[:, 0] - a[:, 0]) * (c[:, 1] - a[:, 1]) - (c[:, 0] - a[:, 0]) * (b[:, 1] - a[:, 1])
            ok = np.abs(d) > 1e-12
            d = np.where(ok, d, 1.0)
            u = ((p[0] - a[:, 0]) * (c[:, 1] - a[:, 1]) - (c[:, 0] - a[:, 0]) * (p[1] - a[:, 1])) / d
            v = ((b[:, 0] - a[:, 0]) * (p[1] - a[:, 1]) - (p[0] - a[:, 0]) * (b[:, 1] - a[:, 1])) / d
            z = a[:, 2] + u * (b[:, 2] - a[:, 2]) + v * (c[:, 2] - a[:, 2])
            if int((ok & (u >= 0) & (v >= 0) & (u + v <= 1) & (z > p[2])).sum()) % 2 == 1:
                return self.names[i]
        return None

    def floor_under(self, p, reach):
        """height of the highest up-facing triangle straight under p (scene z up) within `reach`, or None"""
        p = np.asarray(p, np.float64)
        i = np.nonzero((self.lo[:, 0] <= p[0]) & (self.hi[:, 0] >= p[0]) & (self.lo[:, 1] <= p[1]) & (self.hi[:, 1] >= p[1]) &
                       (self.n[:, 2] > 0.3) & (self.lo[:, 2] <= p[2] + 0.05) & (self.hi[:, 2] >= p[2] - reach))[0]
        best = None
        for k in i:
            a, b, c = self.t[k]
            d = (b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1])
            if abs(d) < 1e-12:
                continue
            u = ((p[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (p[1] - a[1])) / d
            v = ((b[0] - a[0]) * (p[1] - a[1]) - (p[0] - a[0]) * (b[1] - a[1])) / d
            if u < -1e-6 or v < -1e-6 or u + v > 1 + 1e-6:
                continue
            z = a[2] + u * (b[2] - a[2]) + v * (c[2] - a[2])
            if p[2] - reach <= z <= p[2] + 0.05 and (best is None or z > best):
                best = float(z)
        return best


def closed_volume(t):
    """signed volume of a triangle soup (T, 3, 3) when it is a closed, consistently wound surface, else None"""
    t = np.asarray(t, np.float64)
    if len(t) < 4:
        return None
    v, inv = np.unique(np.round(t.reshape(-1, 3), 4), axis=0, return_inverse=True)
    f = inv.reshape(-1, 3)
    f = f[(f[:, 0] != f[:, 1]) & (f[:, 1] != f[:, 2]) & (f[:, 0] != f[:, 2])]
    if len(f) < 4:
        return None
    e = np.concatenate([f[:, [0, 1]], f[:, [1, 2]], f[:, [2, 0]]])
    key = e[:, 0].astype(np.int64) * (len(v) + 1) + e[:, 1]
    rev = e[:, 1].astype(np.int64) * (len(v) + 1) + e[:, 0]
    # closed and consistently wound: every directed edge is there once, and so is its opposite
    if len(np.unique(key)) != len(key) or not np.array_equal(np.sort(key), np.sort(rev)):
        return None
    q = v[f] - v.mean(0)
    return float((np.cross(q[:, 0], q[:, 1]) * q[:, 2]).sum() / 6.0)


def _seg_d2(p, a, b):
    ab = b - a
    t = np.clip(((p - a) * ab).sum(1) / np.maximum((ab * ab).sum(1), 1e-20), 0.0, 1.0)
    q = a + ab * t[:, None]
    return ((p - q) ** 2).sum(1)


def tri_distance(p, T):
    """distance from point p to each triangle of T (N, 3, 3)"""
    a, b, c = T[:, 0], T[:, 1], T[:, 2]
    n = np.cross(b - a, c - a)
    ln = np.linalg.norm(n, axis=1)
    nn = n / np.maximum(ln, 1e-12)[:, None]
    h = ((p - a) * nn).sum(1)
    q = p - nn * h[:, None]

    def side(u, v):
        return (np.cross(v - u, q - u) * nn).sum(1) >= 0
    inside = (ln > 1e-12) & side(a, b) & side(b, c) & side(c, a)
    edge = np.sqrt(np.minimum(np.minimum(_seg_d2(p, a, b), _seg_d2(p, b, c)), _seg_d2(p, c, a)))
    return np.where(inside, np.abs(h), edge)


def solid(job, sc):
    """Tris of everything a soldier collides with, in scene coordinates (cached on the scene)"""
    if getattr(sc, '_solid', None) is None:
        by_name = {o['name']: o for o in job.map['objects']}
        cls = job.map['materials']
        parts = []
        for o in sc.objects('static'):
            mode = o.get('collision', 'exact')
            g = sc.geo(o)
            if mode == 'exact':
                slots = [str(s) for s in g['slots']]
                keep = np.array([(cls.get(slots[i]) or {}).get('class') not in collision_stage.NO_COLLISION_CLASSES for i in g['mat']], bool)
                parts.append((o['name'], g['pos'][keep]))
            elif mode == 'custom':
                c = by_name.get(o.get('collision_object') or '')
                if c is not None:
                    parts.append((o['name'], sc.geo(c)['pos']))
            elif mode == 'hull':
                h = collision_stage._hull(g['pos'])
                if h is not None:
                    parts.append((o['name'], h))
        sc._solid = Tris(parts)
    return sc._solid


def drawn(job, sc):
    if getattr(sc, '_drawn', None) is None:
        sc._drawn = Tris([(o['name'], sc.geo(o)['pos']) for o in sc.objects('static') + sc.objects('door')])
    return sc._drawn


def check_spawn_space(job, sc):
    out = []
    spawns = job.map.get('spawns') or []
    if not spawns or not sc.objects('static'):
        return out
    S = solid(job, sc)
    inside, nofloor = [], []
    for s in spawns:
        p = np.asarray(s['pos'], np.float64)
        hit = None
        for h in BODY:
            q = p + (0.0, 0.0, h)
            hit = S.inside(q) or S.distance(q, BODY_CLEAR)[1]
            if hit:
                break
        if hit:
            inside.append((s['name'], hit)); continue
        if S.floor_under(p + (0.0, 0.0, 0.5), FLOOR_REACH + 0.5) is None:
            nofloor.append(s['name'])
    # with auto spawns on, a bad hand-placed spawn is LEFT OUT (warning) as long as every team keeps a good one: the
    # planner fills the rest on the walkable floor (LEGO Utapau: prepared spawns on rooftops stopped the build)
    bad = {n for n, _ in inside} | set(nofloor)
    teams = {s['team'] for s in spawns if s.get('team') in ('team1', 'team2')}
    keeps = all(any(s['team'] == t and s['name'] not in bad for s in spawns) for t in teams)
    if bad and (job.map.get('options') or {}).get('auto_spawns', True) and keeps:
        job.map['spawns'] = [s for s in spawns if s['name'] not in bad]
        out.append(R('spawn.left_out', 'warning', '%d hand-placed spawn(s) are left out - %s; the auto spawns take their place.' % (
            len(bad), '; '.join(['%s inside %s' % x for x in inside[:4]] + ['%s with no floor under it' % n for n in nofloor[:4]])),
            sorted(bad), fix='Nothing to do, or move them onto open floor to keep them.'))
        return out
    if inside:
        out.append(R('spawn.in_geometry', 'error', '%d spawn(s) are inside or pressed against something solid: %s.' % (
            len(inside), ', '.join('%s (%s)' % x for x in inside[:6])), [n for n, _ in inside] + sorted({w for _, w in inside}),
            fix='Move the spawn onto open floor: its point is where the feet go, with 0.25 m of free space around the body.'))
    if nofloor:
        out.append(R('spawn.no_floor', 'error', '%d spawn(s) have no solid floor within %.0f m under them: %s.' % (len(nofloor), FLOOR_REACH, ', '.join(nofloor[:8])),
                     nofloor, fix='Put the spawn on a floor that has collision (Collision tab: not "None", not a lava material).'))
    return out


def check_kill_floor(job, sc):
    out = []
    kills = job.map.get('kill_volumes') or []
    if not kills or not sc.objects('static'):
        return out
    S = solid(job, sc)
    refs = [s['pos'][2] for s in job.map.get('spawns') or []] + [c['pos'][2] for c in job.map.get('command_posts') or [] if c.get('pos')]
    up = S.n[:, 2] > 0.7
    if not up.any():
        return out
    ctr = S.t.mean(1)
    if refs:
        level, how, sev = min(refs) - PLAY_BELOW, 'the lowest placed spawn', 'error'
    else:
        # nothing placed to say where soldiers walk: take the level under which a fifth of the floor area lies
        c, r = job.play()
        m = up & (np.linalg.norm(ctr[:, :2] - np.asarray(c)[:2], axis=1) <= r)
        if not m.any():
            return out
        order = np.argsort(ctr[m, 2])
        acc = np.cumsum(S.area[m][order])
        level = float(ctr[m, 2][order][int(np.searchsorted(acc, 0.2 * acc[-1]))]) - PLAY_BELOW
        how, sev = 'most of the map\'s floor', 'warning'
    for k in kills:
        c, half, ax = np.asarray(k['centre'], np.float64), np.asarray(k['half'], np.float64), np.asarray(k['axes'], np.float64)
        reach = float(np.linalg.norm(half))
        cand = np.nonzero(up & (S.hi[:, 2] >= level) & np.all(S.lo <= c + reach, 1) & np.all(S.hi >= c - reach, 1))[0]
        m = np.zeros(len(ctr), bool)
        area, zs = 0.0, []
        for i in cand:                                   # a floor is often two huge triangles: measure by sample points
            t = S.t[i]
            n = int(min(64, max(1, np.ceil(max(np.linalg.norm(t[1] - t[0]), np.linalg.norm(t[2] - t[0]), np.linalg.norm(t[2] - t[1])) / 0.5))))
            u, v = np.meshgrid(np.arange(n) + 1 / 3, np.arange(n) + 1 / 3)
            u, v = np.concatenate([u.ravel(), u.ravel() + 1 / 3]) / n, np.concatenate([v.ravel(), v.ravel() + 1 / 3]) / n
            keep = u + v < 1.0
            pts = t[0] + u[keep, None] * (t[1] - t[0]) + v[keep, None] * (t[2] - t[0])
            inside = np.all(np.abs((pts - c) @ ax.T) <= half, axis=1) & (pts[:, 2] >= level)      # as spawns.in_box
            if inside.any():
                m[i] = True
                area += float(S.area[i]) * float(inside.sum()) / max(1, len(pts))
                zs += [float(pts[inside, 2].min()), float(pts[inside, 2].max())]
        if area >= KILL_FLOOR_AREA:
            owners = sorted({S.names[i] for i in np.unique(S.owner[m])})
            z = np.asarray(zs)
            out.append(R('kill.walkable', sev, 'Kill volume "%s" holds %.0f m2 of floor at play height (%.1f .. %.1f m; %s is at %.1f m): '
                         'soldiers walking there die.' % (k['name'], area, float(z.min()), float(z.max()), how, level + PLAY_BELOW),
                         [k['name']] + owners, fix='Lower or shrink the kill volume so it lies under the walkways, not across them.'))
    return out


def check_light_space(job, sc):
    out = []
    lights = [l for l in job.map.get('lights') or [] if l['type'] != 'sun' and not l.get('hidden')]
    if not lights or not sc.objects('static'):
        return out
    D = drawn(job, sc)
    close = []
    for l in lights:
        d, who = D.distance(l['pos'], LIGHT_CLEAR)
        if d is not None:
            close.append((l['name'], d, who))
    if close:
        out.append(R('light.in_surface', 'warning', '%d light(s) sit inside or within %.1f m of a surface: %s. Such a light burns a hot spot into '
                     'the surface or is swallowed by it.' % (len(close), LIGHT_CLEAR, ', '.join('%s (%.2f m from %s)' % c for c in close[:5])),
                     [c[0] for c in close], fix='Move the light at least 0.5 m off the surface (lamp models glow through an emissive material, not a light inside them).'))
    return out


def check_door_travel(job, sc):
    out = []
    for o in sc.objects('door'):
        d = o.get('door') or {}
        if d.get('axis') not in doors_stage.AXES:
            continue                                     # check door.direction says so
        g = sc.geo(o)
        if len({str(g['slots'][i]) for i in np.unique(g['mat'])}) != 1:
            continue
        size = doors_stage.measure(g['pos'], d['axis'])['travel_size']
        dist = float(d.get('distance') or 0.0)
        if dist <= 0.0 or size <= 0.0:
            continue                                     # 0 = automatic: 94 % of the panel
        if dist < 0.5 * size:
            out.append(R('door.travel', 'error', 'Door "%s" slides only %.2f m but is %.2f m long that way: open, it still fills the doorway.' % (
                o['name'], dist, size), [o['name']], fix='Set Distance to 0 (automatic) or to about the panel\'s size along the slide direction.'))
        elif dist > 3.0 * size:
            out.append(R('door.travel', 'warning', 'Door "%s" slides %.1f m, more than three times its size (%.2f m).' % (o['name'], dist, size), [o['name']],
                         fix='Set Distance to 0 (automatic) unless the long travel is meant.'))
    return out


def check_flipped(job, sc):
    """closed objects with every face pointing inwards: negative volume"""
    out = []
    bad = []
    for o in sc.objects('static') + sc.objects('door'):
        vol = closed_volume(sc.geo(o)['pos'])
        if vol is None:
            continue
        if vol < -1e-6:
            bad.append(o['name'])
    if bad:
        out.append(R('geo.flipped', 'warning', '%d closed object(s) have their faces pointing inwards: %s. In game they are seen from the inside only.' % (
            len(bad), ', '.join(bad[:8])), bad, fix='Edit mode: select all, Mesh > Normals > Recalculate Outside. (Leave it if the object is a room seen from inside.)'))
    return out


CHECKS = (check_spawn_space, check_kill_floor, check_light_space, check_door_travel, check_flipped)
