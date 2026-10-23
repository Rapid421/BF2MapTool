"""Lay out Supremacy command posts and team headquarters from a floor survey (floor_survey.py), the way a shipped
Battlefront II map reads: one connected battle line, posts spaced evenly along it, each post on a floor that has cover
around it but is not a corridor, headquarters behind the end posts in sheltered ground facing the fight.

    python design_layout.py <floor.json> <out_layout.json> <out_map.png> [--posts A,B,C,D,E as x,y;... to pin any]

Rules (each with its number in the output):
  * battle floor = the largest set of floor points joined by walkable steps (< 0.7 m between grid neighbours);
  * battle line = its principal axis; post k sits near the 10 / 30 / 50 / 70 / 90 % point of the floor's extent along
    the line, chosen among floor points with cover 4..11 of 16 (sheltered, not boxed in), maximising room around it;
  * capture radius = 60 % of the distance to the nearest other post, clamped 6..12 m;
  * headquarters = the 12 most sheltered floor points (cover >= 7) behind the end posts (beyond A / E along the line),
    >= 22 m from the end post, spaced >= 4 m, each facing the battle-line centre; hero spawns = 4 per team from the same.
"""
import json
import math
import sys

import numpy as np
from PIL import Image, ImageDraw

src, out, png = sys.argv[1:4]
F = json.load(open(src))
P = np.array([p[:5] for p in F['points']], np.float64)          # x, y, z, cover, sky
step = F['step']
n = len(P)
# --- connected floor
parent = list(range(n))


def find(i):
    while parent[i] != i:
        parent[i] = parent[parent[i]]
        i = parent[i]
    return i


key = {}
for i, (x, y, z, c, s) in enumerate(P):
    key.setdefault((round(x / step), round(y / step)), []).append(i)
for (gx, gy), ids in key.items():
    for dx, dy in ((1, 0), (0, 1), (1, 1), (1, -1)):
        for j in key.get((gx + dx, gy + dy), []):
            for i in ids:
                if abs(P[i, 2] - P[j, 2]) < 0.7:
                    a, b = find(i), find(j)
                    if a != b:
                        parent[a] = b
roots = np.array([find(i) for i in range(n)])
labels, counts = np.unique(roots, return_counts=True)
main = labels[np.argmax(counts)]
M = P[roots == main]
info = {'floor_points': n, 'components': int(len(labels)), 'battle_floor_points': int(len(M)),
        'battle_floor_m2': round(len(M) * step * step), 'battle_floor_z': [round(float(M[:, 2].min()), 1), round(float(M[:, 2].max()), 1)]}
# --- battle line
c0 = M[:, :2].mean(0)
w, v = np.linalg.eigh(np.cov((M[:, :2] - c0).T))
axis = v[:, np.argmax(w)]
if axis[0] < 0:
    axis = -axis                                                    # A at the west / low-x end
t = (M[:, :2] - c0) @ axis
side = (M[:, :2] - c0) @ np.array([-axis[1], axis[0]])
lo, hi = np.percentile(t, 2), np.percentile(t, 98)
info['battle_line'] = {'centre': c0.round(1).tolist(), 'axis': axis.round(3).tolist(), 'length_m': round(float(hi - lo), 1),
                       'width_m': round(float(np.percentile(side, 95) - np.percentile(side, 5)), 1)}


def room(p):
    d = np.linalg.norm(M[:, :2] - p[:2], axis=1)
    return int(((d < 8.0) & (np.abs(M[:, 2] - p[2]) < 1.5)).sum())


posts = []
pinned = {}
if '--posts' in sys.argv:
    for item in sys.argv[sys.argv.index('--posts') + 1].split(';'):
        L, xy = item.split('=')
        pinned[L] = [float(q) for q in xy.split(',')]
for k, L in enumerate('ABCDE'):
    tgt = lo + (0.1 + 0.2 * k) * (hi - lo)
    if L in pinned:
        d = np.linalg.norm(M[:, :2] - np.array(pinned[L]), axis=1)
        p = M[np.argmin(d)]
        why = 'pinned'
    else:
        band = M[(np.abs(t - tgt) < 0.06 * (hi - lo)) & (M[:, 3] >= 4) & (M[:, 3] <= 11)]
        if not len(band):
            band = M[np.abs(t - tgt) < 0.1 * (hi - lo)]
        sc = np.array([room(p) for p in band]) - 0.15 * np.abs((band[:, :2] - c0) @ np.array([-axis[1], axis[0]]))
        p = band[int(np.argmax(sc))]
        why = 'best room at %.0f %% of the line' % (100 * (0.1 + 0.2 * k))
    posts.append({'letter': L, 'pos': [float(p[0]), float(p[1]), float(p[2])], 'cover': int(p[3]), 'room': room(p), 'why': why,
                  'owner': 'team1' if L in 'AB' else ('team2' if L in 'DE' else 'neutral')})
for p in posts:
    dn = min(math.dist(p['pos'][:2], q['pos'][:2]) for q in posts if q is not p)
    p['radius'] = round(max(6.0, min(12.0, 0.6 * dn)), 1)
    p['nearest_post_m'] = round(dn, 1)
# --- headquarters, by WALKING distance over the battle floor (not straight lines)
# Rules (user, 2026-10-16): the two headquarters at opposite ends; never next to a command post; each team's run to its
# two home posts about the same; both teams the same run to the contested middle post C; time between spawn and posts.
import heapq

MIN_RUN = float(dict(a.split('=') for a in sys.argv if a.startswith('min_run=')).get('min_run', 30.0)) if any(a.startswith('min_run=') for a in sys.argv) else 30.0
idx = {}
for i, p in enumerate(M):
    idx.setdefault((round(p[0] / step), round(p[1] / step)), []).append(i)
nbr = [[] for _ in range(len(M))]
for (gx, gy), ids in idx.items():
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            if dx == dy == 0:
                continue
            for j in idx.get((gx + dx, gy + dy), []):
                for i in ids:
                    if abs(M[i, 2] - M[j, 2]) < 0.7:
                        nbr[i].append((j, float(np.linalg.norm(M[i, :3] - M[j, :3]))))


def walk(src):
    d = np.full(len(M), np.inf)
    d[src] = 0.0
    h = [(0.0, src)]
    while h:
        c, i = heapq.heappop(h)
        if c > d[i]:
            continue
        for j, w in nbr[i]:
            if c + w < d[j]:
                d[j] = c + w
                heapq.heappush(h, (c + w, j))
    return d


near_i = lambda xy: int(np.argmin(np.linalg.norm(M[:, :2] - np.array(xy[:2]), axis=1)))
D = {p['letter']: walk(near_i(p['pos'])) for p in posts}
own = {'team1': ('A', 'B'), 'team2': ('D', 'E')}
enemy = {'team1': ('D', 'E'), 'team2': ('A', 'B')}
cand = {}
for team in own:
    o1, o2 = own[team]
    e1, e2 = enemy[team]
    dmin_post = np.min([D[L] for L in 'ABCDE'], axis=0)
    ok = (np.isfinite(D['C'])) & (D[o1] + D[o2] + 20.0 < D[e1] + D[e2]) & (dmin_post >= MIN_RUN) & (M[:, 3] >= 4)
    ids = np.where(ok)[0]
    if len(ids) < 8:                                      # relax: farthest own-half points
        ok = (np.isfinite(D['C'])) & (D[o1] + D[o2] < D[e1] + D[e2])
        ids = np.where(ok)[0]
        ids = ids[np.argsort(-dmin_post[ids])][:80]
    bal = np.abs(D[o1][ids] - D[o2][ids])
    ids = ids[np.argsort(bal)][:120]
    cand[team] = ids
best = None
for c1 in cand['team1']:
    w1 = walk(int(c1))
    for c2 in cand['team2']:
        r1 = (D['A'][c1], D['B'][c1], D['C'][c1])
        r2 = (D['E'][c2], D['D'][c2], D['C'][c2])
        cost = (abs(r1[0] - r1[1]) + abs(r2[0] - r2[1])                  # each team: both home posts equally far
                + 1.5 * abs(min(r1[:2]) - min(r2[:2]))                   # both teams: same run to their first post
                + 1.5 * abs(r1[2] - r2[2])                               # both teams: same run to C
                + 4.0 * max(0.0, MIN_RUN - min(r1[:2])) + 4.0 * max(0.0, MIN_RUN - min(r2[:2]))
                - 0.25 * w1[c2]                                          # opposite ends: far apart
                - 0.8 * (M[c1, 3] + M[c2, 3]))                           # sheltered
        if best is None or cost < best[0]:
            best = (cost, int(c1), int(c2))
hq = {}
runs = {}
dmin_post = np.min([D[L] for L in 'ABCDE'], axis=0)
for team, c in (('team1', best[1]), ('team2', best[2])):
    wc = walk(c)
    pool = np.where((wc <= 14.0) & (dmin_post >= 0.8 * MIN_RUN))[0]
    pool = pool[np.argsort(wc[pool] - 0.3 * M[pool, 3])]
    pick = []
    for i in pool:
        if all(math.dist(M[i, :2], M[j, :2]) >= 3.0 for j in pick):
            pick.append(int(i))
        if len(pick) >= 16:
            break
    sp = []
    for k, i in enumerate(pick):
        tgt = np.array(posts[2]['pos'][:2]) - M[i, :2]               # face the contested middle
        sp.append({'pos': [float(M[i, 0]), float(M[i, 1]), float(M[i, 2])], 'yaw': math.degrees(math.atan2(tgt[1], tgt[0])) - 90.0,
                   'cover': int(M[i, 3]), 'hero': k >= 12})
    hq[team] = sp
    o1, o2 = own[team]
    runs[team] = {'centre': M[c, :3].round(1).tolist(), 'run_m': {L: round(float(D[L][c]), 1) for L in 'ABCDE'},
                  'run_s_sprint': {L: round(float(D[L][c]) / 6.5, 1) for L in (o1, o2, 'C')},
                  'closest_spawn_to_any_post_m': round(float(min(dmin_post[i] for i in pick)), 1) if pick else None}
info['hq'] = {k: dict(runs[k], spawns=sum(1 for s in v if not s['hero']), hero=sum(1 for s in v if s['hero']),
                      mean_cover=round(float(np.mean([s['cover'] for s in v])), 1) if v else None) for k, v in hq.items()}
info['hq_to_hq_walk_m'] = round(float(walk(best[1])[best[2]]), 1)
info['min_run_m'] = MIN_RUN
json.dump({'info': info, 'posts': posts, 'hq': hq}, open(out, 'w'), indent=1)
# --- picture
S = 4.0
x0, y0 = P[:, 0].min() - 10, P[:, 1].min() - 10
W, H = int((P[:, 0].max() + 10 - x0) * S), int((P[:, 1].max() + 10 - y0) * S)
img = Image.new('RGB', (W, H), (20, 20, 24))
dr = ImageDraw.Draw(img)
zmin, zmax = P[:, 2].min(), P[:, 2].max()
for p, r in zip(P, roots):
    u, vv = (p[0] - x0) * S, H - (p[1] - y0) * S
    g = int(60 + 160 * (p[2] - zmin) / max(zmax - zmin, 1))
    col = (g, g, g) if r == main else (90, 50, 50)
    dr.rectangle([u - step * S / 2, vv - step * S / 2, u + step * S / 2, vv + step * S / 2], fill=col)
for team, sp in hq.items():
    col = (60, 140, 255) if team == 'team1' else (255, 80, 60)
    for s in sp:
        u, vv = (s['pos'][0] - x0) * S, H - (s['pos'][1] - y0) * S
        dr.ellipse([u - 4, vv - 4, u + 4, vv + 4], fill=col if not s['hero'] else (255, 220, 0))
for p in posts:
    u, vv = (p['pos'][0] - x0) * S, H - (p['pos'][1] - y0) * S
    r = p['radius'] * S
    col = {'team1': (60, 140, 255), 'team2': (255, 80, 60)}.get(p['owner'], (240, 240, 240))
    dr.ellipse([u - r, vv - r, u + r, vv + r], outline=col, width=3)
    dr.text((u - 4, vv - 6), p['letter'], fill=(255, 255, 0))
img.save(png)
print('LAYOUT', json.dumps(info))
for p in posts:
    print(p['letter'], [round(x, 1) for x in p['pos']], 'r', p['radius'], 'cover', p['cover'], 'room', p['room'], p['why'])
