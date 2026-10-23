"""Supremacy layout fitted by WALKING time (floor_survey.py output), the way a shipped Battlefront II Supremacy map plays:

  * the two headquarters at OPPOSITE ends of the battle floor (the two sheltered floor points farthest apart on foot);
  * C, the contested post: where both teams' runs from their headquarters are equal (and as long as possible);
  * each team's two home posts (A, B / D, E) at the SAME run from its own headquarters (RUN = a share of the
    headquarters-to-headquarters walk), the same for both teams, well inside the team's own half, >= SEP apart;
  * no spawn closer than RUN x 0.8 on foot to any post: there is always running time between spawning and a post.
Each post sits on floor with cover 4..11 of 16 (sheltered, not boxed in) and room around it.

    python design_fit.py <floor.json> <layout.json> <map.png> [run_share=0.27] [sep=28]
"""
import heapq
import json
import math
import sys

import numpy as np
from PIL import Image, ImageDraw

src, out, png = sys.argv[1:4]
opt = dict(a.split('=') for a in sys.argv[4:] if '=' in a)
RUN_SHARE, SEP = float(opt.get('run_share', 0.27)), float(opt.get('sep', 28.0))
STEP_UP = float(opt.get('step_up', 1.3))            # metres of climb per grid step a soldier walks (stairs, ramps)
F = json.load(open(src))
P = np.array([p[:5] for p in F['points']], np.float64)
step = F['step']
# ---- battle floor graph (8-neighbours, a step < 0.7 m)
cells = {}
for i, p in enumerate(P):
    cells.setdefault((round(p[0] / step), round(p[1] / step)), []).append(i)
nbr = [[] for _ in range(len(P))]
for (gx, gy), ids in cells.items():
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            if dx or dy:
                for j in cells.get((gx + dx, gy + dy), []):
                    for i in ids:
                        if abs(P[i, 2] - P[j, 2]) < STEP_UP * math.hypot(dx, dy):
                            nbr[i].append((j, float(np.linalg.norm(P[i, :3] - P[j, :3]))))


def walk(s):
    d = np.full(len(P), np.inf); d[s] = 0.0; h = [(0.0, s)]
    while h:
        c, i = heapq.heappop(h)
        if c > d[i]:
            continue
        for j, w in nbr[i]:
            if c + w < d[j]:
                d[j] = c + w; heapq.heappush(h, (c + w, j))
    return d


# largest connected piece
seen, comp = np.full(len(P), -1), 0
sizes = []
for s in range(len(P)):
    if seen[s] >= 0:
        continue
    st, n = [s], 0
    seen[s] = comp
    while st:
        i = st.pop(); n += 1
        for j, _ in nbr[i]:
            if seen[j] < 0:
                seen[j] = comp; st.append(j)
    sizes.append(n); comp += 1
main = int(np.argmax(sizes))
on = seen == main
ids = np.where(on)[0]


def room(i):
    d = np.linalg.norm(P[ids, :2] - P[i, :2], axis=1)
    return int(((d < 8.0) & (np.abs(P[ids, 2] - P[i, 2]) < 1.5)).sum())


# ---- headquarters: opposite ENDS of the map (west / east along the battle floor's long axis), the pair farthest apart
# on foot among the sheltered points of each end
c0 = P[ids, :2].mean(0)
w_, v_ = np.linalg.eigh(np.cov((P[ids, :2] - c0).T))
ax = v_[:, np.argmax(w_)]
ax = ax if ax[0] >= 0 else -ax
tt = (P[:, :2] - c0) @ ax
lo_t, hi_t = np.percentile(tt[ids], 12), np.percentile(tt[ids], 88)
west = ids[(tt[ids] <= lo_t) & (P[ids, 3] >= 4)]
east = ids[(tt[ids] >= hi_t) & (P[ids, 3] >= 4)]
best = None
for i in west[np.argsort(tt[west])][:40]:
    di = walk(int(i))
    j = east[np.argmax(np.where(np.isfinite(di[east]), di[east], -1))]
    if np.isfinite(di[j]) and (best is None or di[j] > best[0]):
        best = (float(di[j]), int(i), int(j))
a, b = best[1], best[2]
if 'hq1' in opt and 'hq2' in opt:                                # headquarters pinned (e.g. the level's speeder points)
    pin = lambda s: int(ids[np.argmin(np.hypot(P[ids, 0] - float(s.split(',')[0]), P[ids, 1] - float(s.split(',')[1])) - 0.15 * P[ids, 3])])
    a, b = pin(opt['hq1']), pin(opt['hq2'])
da, db = walk(a), walk(b)
H = float(da[b])
RUN = RUN_SHARE * H
# ---- C: equal run for both teams, as deep in the middle as possible, on good floor
mid = ids[(np.abs(da[ids] - db[ids]) <= 6.0) & (P[ids, 3] >= 4) & (P[ids, 3] <= 11)]
if not len(mid):
    mid = ids[np.argsort(np.abs(da[ids] - db[ids]))[:20]]
cC = int(mid[np.argmax([room(i) - 0.5 * abs(da[i] - db[i]) for i in mid])])
if 'c' in opt:                                                   # C pinned near a spot (the map's middle)
    cx_, cy_ = (float(q) for q in opt['c'].split(','))
    near = ids[(np.hypot(P[ids, 0] - cx_, P[ids, 1] - cy_) <= float(opt.get('c_reach', 10.0))) & np.isfinite(da[ids]) & np.isfinite(db[ids])]
    cC = int(near[np.argmin([abs(da[i] - db[i]) - 0.3 * room(i) + 0.3 * math.hypot(P[i, 0] - cx_, P[i, 1] - cy_) for i in near])])
dC = walk(cC)


def home(dh, de, avoid):
    """two posts at RUN from the own headquarters, in the own half, >= SEP apart, >= SEP from C"""
    c = ids[(np.abs(dh[ids] - RUN) <= 0.15 * RUN) & (de[ids] > dh[ids] + 0.25 * H) & (P[ids, 3] >= 4) & (P[ids, 3] <= 11)
            & (dC[ids] >= SEP)]
    c = [int(i) for i in c if all(math.dist(P[i, :2], P[j, :2]) >= SEP for j in avoid)]
    print('home: hq %s run %.0f: %d at run, %d own half, %d candidates' % (P[np.argmin(dh), :2].round(0).tolist(), RUN, int((np.abs(dh[ids] - RUN) <= 0.15 * RUN).sum()), int(((np.abs(dh[ids] - RUN) <= 0.15 * RUN) & (de[ids] > dh[ids] + 0.25 * H)).sum()), len(c)), file=sys.stderr)
    best = None
    for x in range(len(c)):
        for y in range(x + 1, len(c)):
            i, j = c[x], c[y]
            if math.dist(P[i, :2], P[j, :2]) < SEP:
                continue
            cost = abs(dh[i] - RUN) + abs(dh[j] - RUN) + 0.5 * abs(dC[i] - dC[j]) - 0.6 * (room(i) + room(j)) \
                - 0.1 * math.dist(P[i, :2], P[j, :2])
            if best is None or cost < best[0]:
                best = (cost, i, j)
    return best


h1 = home(da, db, [cC])
h2 = home(db, da, [cC])
if h1 is None or h2 is None:
    raise SystemExit('no home posts at a run of %.0f m: try another run_share' % RUN)
# order: A nearer the battle-line north / south? keep A = the one nearer team 2's B mirror: sort by y for a consistent read
A, B = sorted(h1[1:], key=lambda i: -P[i, 1])[::-1]
E, D = sorted(h2[1:], key=lambda i: -P[i, 1])[::-1]
post_ids = {'A': A, 'B': B, 'C': cC, 'D': D, 'E': E}
Dp = {L: walk(i) for L, i in post_ids.items()}
dmin_post = np.min([Dp[L] for L in 'ABCDE'], axis=0)
posts = []
for L, i in post_ids.items():
    others = [math.dist(P[i, :2], P[j, :2]) for M_, j in post_ids.items() if M_ != L]
    posts.append({'letter': L, 'pos': P[i, :3].tolist(), 'cover': int(P[i, 3]), 'room': room(i),
                  'owner': 'team1' if L in 'AB' else ('team2' if L in 'DE' else 'neutral'),
                  'radius': round(max(6.0, min(10.0, 0.3 * min(others))), 1), 'nearest_post_m': round(min(others), 1)})
hq, runs = {}, {}
for team, c, dh in (('team1', a, da), ('team2', b, db)):
    pool = ids[(dh[ids] <= float(opt.get('hq_reach', 24.0))) & (dmin_post[ids] >= 0.8 * RUN)]
    pool = pool[np.argsort(dh[pool] - 0.4 * P[pool, 3])]
    pick = []
    for i in pool:
        if all(math.dist(P[i, :2], P[j, :2]) >= 2.9 for j in pick):
            pick.append(int(i))
        if len(pick) >= 16:
            break
    sp = []
    for k, i in enumerate(pick):
        tgt = P[cC, :2] - P[i, :2]
        sp.append({'pos': P[i, :3].tolist(), 'yaw': math.degrees(math.atan2(tgt[1], tgt[0])) - 90.0, 'cover': int(P[i, 3]), 'hero': k >= 12})
    hq[team] = sp
    mine = ('A', 'B') if team == 'team1' else ('D', 'E')
    runs[team] = {'centre': P[c, :3].round(1).tolist(), 'run_m': {L: round(float(dh[post_ids[L]]), 1) for L in mine + ('C',)},
                  'sprint_s': {L: round(float(dh[post_ids[L]]) / 6.5, 1) for L in mine + ('C',)},
                  'spawns': sum(1 for s in sp if not s['hero']), 'hero': sum(1 for s in sp if s['hero']),
                  'closest_spawn_to_a_post_m': round(float(min(dmin_post[i] for i in pick)), 1) if pick else None,
                  'mean_cover': round(float(np.mean([s['cover'] for s in sp])), 1) if sp else None}
info = {'battle_floor_m2': round(int(on.sum()) * step * step), 'hq_to_hq_walk_m': round(H, 1), 'run_target_m': round(RUN, 1),
        'hq': runs, 'posts': {p['letter']: {'pos': [round(x, 1) for x in p['pos']], 'r': p['radius'], 'cover': p['cover'],
                                             'room': p['room'], 'nearest_post_m': p['nearest_post_m']} for p in posts}}
json.dump({'info': info, 'posts': posts, 'hq': hq}, open(out, 'w'), indent=1)
# ---- picture
S = 4.0
x0, y0 = P[:, 0].min() - 10, P[:, 1].min() - 10
W, Hh = int((P[:, 0].max() + 10 - x0) * S), int((P[:, 1].max() + 10 - y0) * S)
img = Image.new('RGB', (W, Hh), (20, 20, 24)); dr = ImageDraw.Draw(img)
for i, p in enumerate(P):
    u, v = (p[0] - x0) * S, Hh - (p[1] - y0) * S
    if on[i] and np.isfinite(da[i]):
        f = float(np.clip((da[i] - db[i]) / H * 2.0, -1, 1))          # blue = team 1 nearer, red = team 2 nearer
        col = (int(110 + 90 * max(f, 0)), 110, int(110 + 90 * max(-f, 0)))
    else:
        col = (70, 40, 40)
    dr.rectangle([u - step * S / 2, v - step * S / 2, u + step * S / 2, v + step * S / 2], fill=col)
for team, sp in hq.items():
    for s in sp:
        u, v = (s['pos'][0] - x0) * S, Hh - (s['pos'][1] - y0) * S
        dr.ellipse([u - 4, v - 4, u + 4, v + 4], fill=((60, 140, 255) if team == 'team1' else (255, 80, 60)) if not s['hero'] else (255, 220, 0))
for p in posts:
    u, v = (p['pos'][0] - x0) * S, Hh - (p['pos'][1] - y0) * S
    r = p['radius'] * S
    dr.ellipse([u - r, v - r, u + r, v + r], outline={'team1': (60, 140, 255), 'team2': (255, 80, 60)}.get(p['owner'], (255, 255, 255)), width=3)
    dr.text((u - 3, v - 6), p['letter'], fill=(255, 255, 0))
img.save(png)
print(json.dumps(info, indent=1))
