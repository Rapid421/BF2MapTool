"""Stage `bake`: baked lighting for the level - lightmaps for the lightmapped pieces, light probes over the walkable
floors, sky and local lights and emissive surfaces as light sources - applied to the level as its own assets.

The chain (each step keeps a marker in bake/state.json and is skipped while its inputs are unchanged):
  keys       where every piece of the level is placed (`enlighten keys --list`)
  geo        the pieces' game meshes dumped back (`mesh geo-dump`)
  classify   lightmapped = a piece on a template with a lightmap uv stream; everything else is lit by probes
  charts     one lightmap chart set per lightmapped piece (xatlas), texel density falling with the distance to the
             nearest walkable floor, total held under the quality preset's texel budget
  regeo      the charts written into the level's meshes (`port-regeo`)
  plan       all charts packed into one atlas (`enlighten chart-plan`)
  materials  per piece: albedo = mean of its colour texture over the texels it uses; emissive pieces: what they emit
  probes     probe volumes: 24 m columns over the walkable floors
  sky        the sky the level's own visual environment shows, as the cube the baker reads
  scene      the bake scene (`enlighten flux-scene`)
  flux       the path tracer (fluxbake), at below-normal priority, after a free-memory gate
  leak       the leak filter (texels behind single-sided surfaces take their visible neighbour's light)
  apply      the result written into the level (`enlighten bake-apply`), then END / probe checks and a NaN count

Quality presets (texels per metre near the play floors, texel budget, samples per texel) are first values to be measured
per map; the times of every step are recorded in bake.json.
"""
import ctypes
import json
import math
import os
import re
import shutil
import struct
import time
import uuid

import numpy as np

from .. import chain, cli, config, hdr, hosts, meshgeo as mg, photometry
from ..runner import StageError

QUALITY = {'draft': {'density': 3.0, 'budget': 4.0e6, 'samples': 64, 'filter': 3},
           'normal': {'density': 6.0, 'budget': 12.0e6, 'samples': 256, 'filter': 3},
           'high': {'density': 9.0, 'budget': 24.0e6, 'samples': 512, 'filter': 5}}
R_FALL, D_NEAR, FAR_DENSITY, MAX_SIDE, PAD = 40.0, 10.0, 0.5, 1024, 2
LM_FAR = 40.0       # metres: a piece no part of which comes this close to the walkable floor takes no lightmap
BAKE_TRIS = 2.0e6   # triangles the bake scene may hold (5.4 M aborted the baker, 0.65 M baked; see charts())
LM_OVER = 2.0      # an atlas still this many times over the budget after the density cut drops its dearest pieces
PROBE_CELL, PROBE_SPACING, PROBE_VSPACING, PROBE_ABOVE = 24.0, 6.0, 3.0, 9.0
PROBE_VOLUME_BUDGET, PROBE_BUDGET = 150, 30000      # what has loaded: Rhen Var 142 / 23,008; DICE's Naboo ~33,600 probes
MIN_FREE_GB = 6.0
GROUND_ALBEDO = 0.2                # what lies below the horizon, as far as the bake's sky is concerned
LUM = np.array([0.2126, 0.7152, 0.0722])
VERSION = 9


def quality(job):
    q = ((job.map.get('options') or {}).get('bake') or {}).get('quality') or (job.map.get('options') or {}).get('bake_quality') or 'normal'
    if q not in QUALITY:
        raise StageError('Bake quality "%s" is not one of %s.' % (q, ', '.join(QUALITY)))
    return q


def inputs(job):
    return {'quality': quality(job), 'presets': QUALITY, 'look': job.map.get('look'), 'lights': job.map.get('lights'),
            'materials': {k: (v.get('emission_bake'), v.get('class')) for k, v in job.map['materials'].items()}, 'v': VERSION}


def outputs(job):
    return [job.project('bake'), job.work + '/bake.json']


def free_gb():
    class MS(ctypes.Structure):
        _fields_ = [('dwLength', ctypes.c_ulong), ('dwMemoryLoad', ctypes.c_ulong), ('ullTotalPhys', ctypes.c_ulonglong),
                    ('ullAvailPhys', ctypes.c_ulonglong), ('ullTotalPageFile', ctypes.c_ulonglong), ('ullAvailPageFile', ctypes.c_ulonglong),
                    ('ullTotalVirtual', ctypes.c_ulonglong), ('ullAvailVirtual', ctypes.c_ulonglong), ('ullAvailExtendedVirtual', ctypes.c_ulonglong)]
    m = MS(); m.dwLength = ctypes.sizeof(MS)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m))
    return m.ullAvailPhys / 2 ** 30


class BakerDied(Exception):
    """the light baker gave no result on the full scene and on the lighter one"""

    def __init__(self, tries):
        Exception.__init__(self, 'the light baker stopped without a result %d times' % len(tries))
        self.tries = tries


def unbaked(job, log, src, tries):
    """the build goes on WITHOUT baked lighting rather than failing: the level as it was before the bake becomes this
    stage's project, and the Check tab gets a warning that says so"""
    shutil.copyfile(src, job.project('bake'))
    info = {'unbaked': True, 'why': 'the light baker stopped without a result on the full and on the lighter scene', 'tries': tries}
    with open(job.work + '/bake.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    msg = ('The light bake failed (the baker stopped twice, exit %s - also with the lighter scene), so this mod was made WITHOUT '
           'baked lighting: the sun and lamps still light it, but shade is flat. Try Bake lighting at Draft, lower the map\'s '
           'piece count, or send a bug report.  [log: %s]' % (tries[-1]['exit'], job.path('logs', 'bake_flux_lite.log')))
    log('bake: ' + msg)
    getattr(log, 'feed', log)('@@RESULT ' + json.dumps({'code': 'bake.failed_unbaked', 'severity': 'warning', 'message': msg,
                                                       'objects': [], 'materials': [], 'fix': ''}))
    return {'unbaked': True, 'exits': [t['exit'] for t in tries]}


class Steps:
    """sub-step markers: a step is redone when its key (inputs) changed or a step before it was redone"""

    def __init__(self, job, log):
        self.path = job.path('bake', 'state.json')
        self.log = log
        self.state = {}
        if os.path.exists(self.path):
            try:
                with open(self.path, encoding='utf-8') as f:
                    self.state = json.load(f)
            except ValueError:
                self.state = {}
        self.dirty = False
        self.times = {}

    def run(self, name, key, func, outputs=()):
        k = config.data_hash(key)
        old = self.state.get(name) or {}
        if not self.dirty and old.get('key') == k and all(os.path.exists(o) for o in outputs):
            self.log('bake: %s up to date' % name)
            self.times[name] = {'seconds': old.get('seconds'), 'reused': True}
            return old.get('result')
        self.dirty = True
        t = time.time()
        self.log('bake: %s ...' % name)
        res = func()
        dt = round(time.time() - t, 1)
        self.state[name] = {'key': k, 'seconds': dt, 'result': res}
        self.times[name] = {'seconds': dt, 'reused': False}
        with open(self.path, 'w', encoding='utf-8') as f:
            json.dump(self.state, f, indent=1)
        self.log('bake: %s done in %.1f s%s' % (name, dt, (' - ' + json.dumps(res)[:300]) if res else ''))
        return res


# ------------------------------------------------------------------ geometry helpers
def _read(path):
    g = mg.load(path)
    s = g['lods'][0]['sections'][0]
    return g, s, mg.decode(s, 'Pos')[:, :3].astype(np.float64), mg.decode(s, 'Normal')[:, :3].astype(np.float64), \
        s['indices'].reshape(-1, 3).astype(np.int64)


def _tri_samples(a, b, c, lim):
    out = []; st = [(a, b, c)]
    while st:
        a, b, c = st.pop()
        if max(np.linalg.norm(b - a), np.linalg.norm(c - b), np.linalg.norm(a - c)) <= lim:
            out.append((a + b + c) / 3); continue
        ab, bc, ca = (a + b) / 2, (b + c) / 2, (c + a) / 2
        st += [(a, ab, ca), (ab, b, bc), (ca, bc, c), (ab, bc, ca)]
    return out


def _walkable(job, cls, geo_dir, lim=4.0):
    """points 1.7 m over the up-facing triangles of SOLID pieces inside the play area (+ 30 m) - every floor a
    soldier can stand on, whether it takes a lightmap or not"""
    centre, radius = job.play()
    gc = np.array(job.to_game(centre))
    out = []
    for mesh, v in cls.items():
        if not v.get('solid', v['lightmapped']):
            continue
        g, s, pos, nrm, idx = _read('%s/%s.meshgeo' % (geo_dir, mesh.split('/')[-1]))
        for p in {q['key']: q for q in v['placements']}.values():
            w = pos + np.array(p['pos'])
            a, b, c = w[idx[:, 0]], w[idx[:, 1]], w[idx[:, 2]]
            n = np.cross(b - a, c - a); ln = np.linalg.norm(n, axis=1); ok = ln > 1e-9
            up = np.zeros(len(idx), bool); up[ok] = (n[ok, 1] / ln[ok]) > 0.8
            ctr = (a + b + c) / 3
            near = np.linalg.norm((ctr - gc)[:, [0, 2]], axis=1) <= radius + 30.0
            for i in np.nonzero(up & near)[0]:
                out += _tri_samples(a[i], b[i], c[i], lim)
    return np.array(out).reshape(-1, 3) + np.array([0.0, 1.7, 0.0]) if out else np.zeros((0, 3))


# ------------------------------------------------------------------ sky cube (the port's checks/sky_cube.py)
def _atan2_approx_hq(x, y):
    kx, ky = x.copy(), y.copy()
    kz = np.ones_like(x); kw = np.full_like(x, 3.0)
    swap = np.abs(y) < np.abs(x)
    kx2 = np.where(swap, y, kx); ky2 = np.where(swap, x, ky)
    kx, ky = kx2, ky2
    cond = (x - y) > 0
    kz = np.where(cond, -kz, kz); kw = np.where(cond, kw + 4, kw)
    kx = kx / np.abs(ky)
    kx = kx / (kx * kx * 0.2 + 0.8)
    v = ((kx * kz) + kw) * 0.125 + 0.125
    return v - np.floor(v)


def _cube_texel(d, res=16):
    rx, ry, rz = d[..., 0], d[..., 1], -d[..., 2]
    ax, ay, az = np.abs(rx), np.abs(ry), np.abs(rz)
    ma = np.maximum(np.maximum(ax, ay), az)
    fx = (ax > ay) & (ax > az); fy = ~fx & (ay > ax) & (ay > az)
    sc = np.where(fx, np.where(rx > 0, rz, -rz), np.where(fy, rx, np.where(rz > 0, -rx, rx)))
    tc = np.where(fx, ry, np.where(fy, np.where(ry > 0, rz, -rz), ry))
    face = np.where(fx, np.where(rx > 0, 0, 1), np.where(fy, np.where(ry > 0, 2, 3), np.where(rz > 0, 5, 4)))
    u = 0.5 * (sc / ma) + 0.5; v = -0.5 * (tc / ma) + 0.5
    px = np.clip(np.floor(u * res), 0, res - 1).astype(np.int64); py = np.clip(np.floor(v * res), 0, res - 1).astype(np.int64)
    return face, px, py


def _sky_radiance(d, pano, lum):
    """the engine's panoramic sky lookup for view direction d, with the uv range 0..1, no rotation, tile 1 (what the
    look stage writes)"""
    H, W = pano.shape[:2]
    ex, ey, ez = -d[..., 0], -d[..., 1], -d[..., 2]
    # the column: look_stage.pano_u, the exact form of what the engine's fast atan2 approximates. The transcription
    # above (_atan2_approx_hq) is kept for reference only: it is self-consistent in three octants and wrong in five
    # (measured 2026-10-12), which no one could see while every sky was the same all the way round.
    from . import look as look_stage
    sx = look_stage.pano_u(d[..., 0], d[..., 2])
    sy = 1 - np.arccos(np.clip(ey, -1, 1)) / np.pi
    K1, K2 = 1.0, 0.0
    K3 = 1.0 / ((1.0 - 1.0) - (1.0 - 0.0)); K4 = -(1.0 - 0.0) * K3; K3 *= 2.0
    ux = np.clip(sx * K1 + K2, 0, 1); uy = np.clip(sy * -K3 + (1 - K4), 0, 1)
    fx = ux * W - 0.5; fy = uy * H - 0.5
    x0 = np.floor(fx).astype(np.int64); y0 = np.floor(fy).astype(np.int64); tx = (fx - x0)[..., None]; ty = (fy - y0)[..., None]
    x1 = x0 + 1; y1 = np.clip(y0 + 1, 0, H - 1); y0 = np.clip(y0, 0, H - 1); x0 %= W; x1 %= W
    return (pano[y0, x0] * (1 - tx) * (1 - ty) + pano[y0, x1] * tx * (1 - ty) + pano[y1, x0] * (1 - tx) * ty + pano[y1, x1] * tx * ty) * lum


def sky_cube(pano, lum, out_path, res=16, n=1024):
    acc = np.zeros((6, res, res, 3)); cnt = np.zeros((6, res, res))
    for i0 in range(0, n, 64):
        i = np.arange(i0, min(n, i0 + 64))
        y = 1 - 2 * (i + 0.5) / n
        r = np.sqrt(np.clip(1 - y * y, 0, None))
        nphi = 2 * n
        phi = 2 * np.pi * (np.arange(nphi) + 0.5) / nphi
        d = np.stack([r[:, None] * np.cos(phi)[None, :], np.repeat(y[:, None], nphi, 1), r[:, None] * np.sin(phi)[None, :]], -1)
        c = _sky_radiance(d, pano, lum)
        f, px, py = _cube_texel(d, res)
        np.add.at(acc, (f.ravel(), py.ravel(), px.ravel()), c.reshape(-1, 3))
        np.add.at(cnt, (f.ravel(), py.ravel(), px.ravel()), 1)
    cube = np.zeros((6, res, res, 4), np.float32)
    cube[..., :3] = acc / np.maximum(cnt, 1)[..., None]; cube[..., 3] = 1
    rng = np.random.default_rng(1)
    v = rng.normal(size=(200000, 3)); v /= np.linalg.norm(v, axis=1)[:, None]
    f, px, py = _cube_texel(v)
    # Below the horizon the engine's panorama lookup repeats the horizon row: as a light source that would be a ground
    # as bright as the horizon sky shining upwards. The bake treats what lies below the horizon as open ground of
    # albedo GROUND_ALBEDO lit by the upper sky: radiance = albedo x E / pi, in the sky's mean colour.
    up = v[:, 1] > 0
    sky_rgb = cube[f[up], py[up], px[up], :3].astype(np.float64)
    e_over_pi = 2.0 * np.mean(sky_rgb * v[up, 1:2], axis=0)             # (1 / pi) integral of L cos over the upper hemisphere
    ground = GROUND_ALBEDO * e_over_pi
    # a texel is "below" when most of the directions that fall into it point down
    below = np.zeros((6, res, res)); allc = np.zeros((6, res, res))
    np.add.at(below, (f, py, px), (v[:, 1] < 0).astype(np.float64)); np.add.at(allc, (f, py, px), 1.0)
    frac = (below / np.maximum(allc, 1))[..., None]
    cube[..., :3] = cube[..., :3] * (1 - frac) + ground[None, None, None, :] * frac
    cube.tofile(out_path)
    L = cube[..., :3] @ LUM
    Lc = L[f, py, px]
    return {'up_face_mean': round(float(L[2].mean()), 3), 'down_face_mean': round(float(L[3].mean()), 3),
            'open_up_E_over_pi': round(float(4 * np.mean(Lc * np.clip(v[:, 1], 0, None))), 3)}


# ------------------------------------------------------------------ END check (the port's checks/end_check.py)
def end_check(path, plan, volumes):
    """Offline check of the END block bake-apply wrote (framed dump): mesh records sorted, unique, equal to the plan's
    keys and uv transforms; probe sets in the shipped volume form (unit box, transform and its inverse), positions
    inside their world box, counts = dims product, global offsets contiguous. -> dict; 'problems' lists what is wrong."""
    b = open(path, 'rb').read()
    vols = {v['guid'].lower(): v for v in volumes}
    base = None
    for cand in range(0, 0x2000, 4):
        if cand + 0x58 > len(b):
            break
        nps, = struct.unpack_from('<I', b, cand + 0x4C)
        pps, = struct.unpack_from('<Q', b, cand + 0x50)
        if nps == 0 or nps > 100000 or cand + pps + nps * 240 > len(b):
            continue
        o = cand + pps
        d = struct.unpack_from('<3I', b, o + 0xB4); cnt, = struct.unpack_from('<I', b, o + 0xC4)
        if d[0] * d[1] * d[2] == cnt and cnt > 0:
            base = cand
            break
    if base is None:
        return {'problems': ['the data start of the END block was not found']}
    nps, = struct.unpack_from('<I', b, base + 0x4C); pps, = struct.unpack_from('<Q', b, base + 0x50)
    keys = sorted(int(r['key'], 16) for r in plan['records'])
    mo = b.find(struct.pack('<II', keys[0], 1), base)
    recs = []
    o = mo
    while mo >= 0 and o + 32 <= len(b):
        h, n = struct.unpack_from('<II', b, o)
        if n != 1:
            break
        uvp, = struct.unpack_from('<Q', b, o + 8)
        recs.append((h, struct.unpack_from('<6f', b, base + uvp))); o += 32
    hs = [r[0] for r in recs]
    pk = {int(r['key'], 16): r['uv'] for r in plan['records']}
    bad_uv = sum(1 for h, uv in recs if h in pk and max(abs(x - y) for x, y in zip(uv, pk[h])) > 1e-6)
    dark = form_ok = form_bad = pos_out = goff_bad = nprobes = box_bad = 0
    expect = 0
    for i in range(nps):
        o = base + pps + i * 240
        gs = str(uuid.UUID(bytes_le=b[o:o + 16]))
        T1 = np.array(struct.unpack_from('<16f', b, o + 0x10)).reshape(4, 4); T2 = np.array(struct.unpack_from('<16f', b, o + 0x50)).reshape(4, 4)
        mn = np.array(struct.unpack_from('<3f', b, o + 0x90)); mx = np.array(struct.unpack_from('<3f', b, o + 0xA0))
        pr, cnt, go = struct.unpack_from('<3I', b, o + 0xC0)
        corners = np.array([[x, y, z, 1] for x in (mn[0], mx[0]) for y in (mn[1], mx[1]) for z in (mn[2], mx[2])])
        wc = (corners @ T1)[:, :3]
        wmn, wmx = wc.min(0), wc.max(0)
        if go != expect:
            goff_bad += 1
        expect = go + cnt; nprobes += cnt
        unit = np.allclose(mn, -0.5) and np.allclose(mx, 0.5)
        inv_ok = np.allclose((np.c_[T1[:, :3], [0, 0, 0, 1]] @ np.c_[T2[:, :3], [0, 0, 0, 1]]), np.eye(4), atol=1e-4)
        if wmx[1] < -2000:
            dark += 1
        if unit and inv_ok:
            form_ok += 1
        else:
            form_bad += 1
        pp, = struct.unpack_from('<Q', b, o + 0xD0)
        P = np.frombuffer(b, np.float32, cnt * 4, base + pp).reshape(cnt, 4)[:, :3]
        pos_out += int(((P < wmn - 0.01) | (P > wmx + 0.01)).any(1).sum())
        if gs in vols:
            bx = vols[gs]['aabb']
            if not (np.allclose(wmn, bx[:3], atol=1e-2) and np.allclose(wmx, bx[3:], atol=1e-2)):
                box_bad += 1
    res = {'mesh_records': len(recs), 'plan_records': len(keys), 'sorted': hs == sorted(hs), 'unique': len(set(hs)) == len(hs),
           'plan_keys_present': sum(1 for k in keys if k in set(hs)), 'uv_mismatches': bad_uv, 'probe_sets': int(nps), 'volume_form': form_ok,
           'other_form': form_bad, 'darkroom_sets_kept': dark, 'probes': int(nprobes), 'positions_outside_their_box': pos_out,
           'global_offset_gaps': goff_bad, 'volume_boxes_differ_from_plan': box_bad}
    prob = []
    if len(recs) != len(keys) or res['plan_keys_present'] != len(keys):
        prob.append('%d mesh records for %d planned charts' % (len(recs), len(keys)))
    if not res['sorted'] or not res['unique']:
        prob.append('mesh records not sorted / not unique')
    for k, text in (('uv_mismatches', 'chart uv transforms differ from the plan'), ('other_form', 'probe sets not in the volume form'),
                    ('positions_outside_their_box', 'probes outside their volume'), ('global_offset_gaps', 'probe offsets not contiguous'),
                    ('volume_boxes_differ_from_plan', 'volume boxes differ from the plan')):
        if res[k]:
            prob.append('%d %s' % (res[k], text))
    res['problems'] = prob
    return res


# ------------------------------------------------------------------ the stage
def run(job, log):
    host = hosts.get(job.host)
    clix = cli.Cli(job, log)
    q = quality(job)
    Q = QUALITY[q]
    B = job.path('bake')
    os.makedirs(B, exist_ok=True)
    src = chain.base(job, 'bake')
    S = Steps(job, log)
    with open(job.work + '/import.json', encoding='utf-8') as f:
        imp = json.load(f)
    with open(job.path('tex', 'plan.json'), encoding='utf-8') as f:
        tplan = json.load(f)
    piece = {'%s/%s_Mesh' % (job.prefix, b['as']): b for b in imp['pieces']}
    geo_dir, lm_dir = B + '/geo', B + '/geo_lm'
    src_hash = config.file_hash(src)

    # ---- keys
    def keys():
        r = clix.direct('bake_keys', ['enlighten', 'keys', job.level, '--list'], project=src, check=False)
        place, part = {}, None
        for line in r.text.splitlines():
            m = re.match(r'\s+(AddedContent/\S+): \d+ placement', line)
            if m:
                part = m.group(1).split('/')[-1]
            m = re.search(r'^\s+([0-9a-f]{8}) (group|object|prefab) (\S+) \(([-\d.eE]+), ([-\d.eE]+), ([-\d.eE]+)\) (\S+)', line)
            if m and m.group(3).startswith(job.prefix + '/'):
                place.setdefault(m.group(3), []).append({'key': m.group(1), 'kind': m.group(2), 'pos': [float(m.group(i)) for i in (4, 5, 6)],
                                                         'override': m.group(7), 'partition': part})
        if not place:
            raise StageError('The level lists no placed pieces for the bake (enlighten keys)  [log: %s]' % r.log_path)
        with open(B + '/placements.json', 'w', encoding='utf-8') as f:
            json.dump(place, f)
        return {'meshes': len(place), 'placements': sum(len(v) for v in place.values())}
    S.run('keys', [src_hash], keys, [B + '/placements.json'])
    with open(B + '/placements.json', encoding='utf-8') as f:
        place = json.load(f)
    meshes = sorted(m for m in place if m in piece)

    # ---- geo
    def geo():
        shutil.rmtree(geo_dir, ignore_errors=True)
        os.makedirs(geo_dir)
        lst = B + '/meshes.txt'
        with open(lst, 'w') as f:
            f.write('\n'.join(meshes) + '\n')
        clix.direct('bake_geo', ['mesh', 'geo-dump', '--list', lst, '--outdir', geo_dir], project=src, check=False)
        got = [m for m in meshes if os.path.exists('%s/%s.meshgeo' % (geo_dir, m.split('/')[-1]))]
        if len(got) != len(meshes):
            raise StageError('%d of %d piece meshes could not be dumped for the bake' % (len(meshes) - len(got), len(meshes)))
        return {'meshes': len(got)}
    S.run('geo', [src_hash, meshes], geo, [geo_dir])

    # ---- classify
    def classify():
        out = {}
        for mesh in meshes:
            b = piece[mesh]
            g, s, pos, nrm, idx = _read('%s/%s.meshgeo' % (geo_dir, mesh.split('/')[-1]))
            a, bb, c = pos[idx[:, 0]], pos[idx[:, 1]], pos[idx[:, 2]]
            area = float(np.linalg.norm(np.cross(bb - a, c - a), axis=1).sum() / 2)
            has_rad = mg.element(s, 'RadiosityTexCoord') is not None
            T = host.TEMPLATES[b['template']]
            why = None
            if b['class'] == 'lava':
                why = 'lava (a light source in the bake; lit by probes)'
            elif b['class'] == 'emissive':
                why = 'emissive (a light source in the bake; lit by probes)'
            elif b['class'] in ('cutout', 'cutout2s'):
                why = 'cut-out (lit by probes)'
            elif b['class'] == 'glass':
                why = 'glass (light passes through it; not in the bake)'
            elif b['class'] == 'holo':
                why = 'hologram (additive and see-through; not in the bake)'
            elif not T.get('lightmapped'):
                why = 'its surface template takes no lightmap (lit by probes; still blocks and bounces light)'
            elif not has_rad:
                why = 'its mesh has no lightmap uv stream'
            elif area <= 1e-6:
                why = 'no area'
            out[mesh] = {'class': b['class'], 'template': b['template'], 'material': b['material'], 'tris': int(len(idx)), 'area_m2': round(area, 2),
                         'placements': place[mesh], 'lightmapped': why is None, 'why_not': why,
                         'emitter': b['class'] in ('emissive', 'lava') and bool(b.get('emission')), 'solid': bool(b.get('physics')),
                         'occluder': why is not None and b['class'] == 'opaque'}
        with open(B + '/classes.json', 'w', encoding='utf-8') as f:
            json.dump(out, f)
        lm = [v for v in out.values() if v['lightmapped']]
        if not lm:
            raise StageError('No piece of this map can take a lightmap, so there is nothing to bake.')
        return {'lightmapped': len(lm), 'probe_lit': len(out) - len(lm), 'emitters': sum(v['emitter'] for v in out.values()),
                'lightmapped_area_m2': round(sum(v['area_m2'] for v in lm))}
    S.run('classify', [meshes, {m: (piece[m]['class'], piece[m]['template'], piece[m].get('emission'), piece[m].get('physics')) for m in meshes}, 4], classify, [B + '/classes.json'])
    with open(B + '/classes.json', encoding='utf-8') as f:
        cls = json.load(f)
    lm = {k: v for k, v in cls.items() if v['lightmapped']}

    # ---- charts
    def charts():
        import xatlas
        from scipy.spatial import cKDTree
        walk = _walkable(job, cls, geo_dir)
        if not len(walk):
            raise StageError('The bake found no walkable floor (no up-facing lightmapped surface inside the play area).')
        tree = cKDTree(walk)
        geo_c = {m: _read('%s/%s.meshgeo' % (geo_dir, m.split('/')[-1])) for m in lm}

        def pack(D0):
            rep = {}
            for mesh, v in sorted(lm.items()):
                g, s, pos, nrm, idx = geo_c[mesh]
                w = pos + np.array(v['placements'][0]['pos'])
                a, b, c = w[idx[:, 0]], w[idx[:, 1]], w[idx[:, 2]]
                area = np.linalg.norm(np.cross(b - a, c - a), axis=1) / 2
                d, _ = tree.query((a + b + c) / 3)
                o = np.argsort(d); cum = np.cumsum(area[o])
                d25 = float(d[o][min(np.searchsorted(cum, 0.25 * cum[-1]), len(d) - 1)])
                dens = max(FAR_DENSITY, D0 * R_FALL / (R_FALL + max(0.0, d25 - D_NEAR)))
                tries = 0
                while True:
                    at = xatlas.Atlas(); at.add_mesh(pos.astype(np.float32), idx.astype(np.uint32), nrm.astype(np.float32))
                    co = xatlas.ChartOptions(); po = xatlas.PackOptions()
                    po.texels_per_unit = dens; po.padding = PAD; po.bilinear = True; po.blockAlign = True; po.resolution = 0
                    at.generate(co, po)
                    side = max(at.width, at.height)
                    if side <= MAX_SIDE or tries > 12:
                        break
                    dens *= (MAX_SIDE - 8) / side * 0.98; tries += 1
                vm, ni, uv = at[0]
                side_s = max(8, (side + 3) // 4 * 4)
                rep[mesh] = {'side': int(side_s), 'density': round(dens, 3), 'd25': round(d25, 1), 'charts': int(at.chart_count), 'capped': tries,
                             'placements': len({p['key'] for p in v['placements']}), '_atlas': (vm, ni, uv, at.width, at.height)}
            return rep
        # pieces far from every walkable floor take no lightmap: the light probes light them and they still block and
        # bounce light (LEGO Utapau: 1 km2 of sinkhole walls cut into a million charts made an 88 M texel atlas the
        # baker died on, without a word)
        dropped = {}
        for mesh, v in sorted(lm.items()):
            g, s, pos, nrm, idx = geo_c[mesh]
            w = pos + np.array(v['placements'][0]['pos'])
            d, _ = tree.query((w[idx[:, 0]] + w[idx[:, 1]] + w[idx[:, 2]]) / 3)
            if float(np.min(d)) > LM_FAR:
                dropped[mesh] = 'more than %.0f m from the walkable floor (lit by probes)' % LM_FAR
        if len(dropped) >= len(lm):
            dropped = {}
        for m in dropped:
            lm.pop(m)
        D0 = Q['density']
        rep = pack(D0)
        total = sum(r['side'] ** 2 * r['placements'] for r in rep.values())
        scaled = None
        if total > Q['budget']:
            scaled = D0 * math.sqrt(Q['budget'] / total) * 0.97
            log('bake: %.1f M lightmap texels at %.1f texels / m is over the "%s" budget of %.0f M - packing again at %.2f texels / m' % (
                total / 1e6, D0, q, Q['budget'] / 1e6, scaled))
            rep = pack(scaled)
            total = sum(r['side'] ** 2 * r['placements'] for r in rep.values())
        # still far over: finely cut pieces cost texels a lower density cannot take away (every chart keeps its padding) -
        # the dearest per square metre are lit by probes until the atlas fits
        if total > LM_OVER * Q['budget'] and len(rep) > 1:
            cost = sorted(rep, key=lambda m: -(rep[m]['side'] ** 2 * rep[m]['placements']) / max(cls[m]['area_m2'], 1.0))
            for m in cost:
                if total <= LM_OVER * Q['budget'] or len(rep) <= 1:
                    break
                total -= rep[m]['side'] ** 2 * rep[m]['placements']
                rep.pop(m); lm.pop(m, None)
                dropped[m] = 'cut into too many lightmap charts for the budget (lit by probes)'
        # the baker aborts (0xC0000409) on a heavy scene: LEGO Utapau's 641 meshes / 5.4 M triangles died every time, its
        # 186 lightmapped ones (0.65 M) baked in 70 s. Light-blocking pieces join the scene cheapest first (fewest
        # triangles per square metre: walls and slabs before stud detail) until the scene holds BAKE_TRIS
        for m, why in dropped.items():
            # far pieces leave the bake scene altogether (no lightmap AND no light blocking); near ones still block light
            cls[m].update(lightmapped=False, why_not=why, occluder=cls[m]['class'] == 'opaque' and not why.startswith('more'))
        used =sum(cls[m]['tris'] for m in rep) + sum(v['tris'] for v in cls.values() if v['emitter'])
        blockers = sorted((m for m, v in cls.items() if v.get('occluder') and not v['emitter']),
                          key=lambda m: cls[m]['tris'] / max(cls[m]['area_m2'], 1.0))
        thinned = 0
        for m in blockers:
            if used + cls[m]['tris'] > BAKE_TRIS:
                cls[m]['occluder'] = False
                cls[m]['why_not'] = (cls[m].get('why_not') or '') + '; left out of the bake scene (triangle budget)'
                thinned += 1
                if m not in dropped:
                    dropped[m] = cls[m]['why_not']
            else:
                used += cls[m]['tris']
        if thinned:
            log('bake: %d light-blocking piece(s) left out of the bake scene to keep it under %.1f M triangles' % (thinned, BAKE_TRIS / 1e6))
        if dropped:
            with open(B + '/classes.json', 'w', encoding='utf-8') as f:
                json.dump(cls, f)
            log('bake: %d piece(s) lit by probes instead of a lightmap (%d far from the floor, %d too finely cut); %.1f M texels' % (
                len(dropped), sum(1 for w in dropped.values() if w.startswith('more')), sum(1 for w in dropped.values() if w.startswith('cut')), total / 1e6))
        shutil.rmtree(lm_dir, ignore_errors=True)
        os.makedirs(lm_dir)
        sides = {}
        v0 = v1 = 0
        for mesh, r in rep.items():
            g, s, pos, nrm, idx = geo_c[mesh]
            vm, ni, uv, aw, ah = r.pop('_atlas')
            if not np.array_equal(vm[ni], idx):
                raise StageError('Lightmap charts: xatlas changed the triangles of %s' % mesh.split('/')[-1])
            ruv = (uv * np.array([aw, ah]) / r['side']).astype(np.float16)
            fmt, off = mg.element(s, 'RadiosityTexCoord')
            vb = np.frombuffer(s['verts'], np.uint8).reshape(s['vcount'], s['stride'])[vm].copy()
            vb[:, off:off + 4] = ruv.view(np.uint8).reshape(-1, 4)
            s2 = dict(s, verts=vb.tobytes(), vcount=len(vm), indices=ni.reshape(-1).astype(np.uint32))
            g2 = dict(g, lods=[dict(g['lods'][0], sections=[s2] + g['lods'][0]['sections'][1:])] + g['lods'][1:])
            mg.save(g2, '%s/%s.meshgeo' % (lm_dir, mesh.split('/')[-1]))
            sides[mesh] = r['side']
            v0 += int(s['vcount']); v1 += len(vm)
        with open(B + '/charts.json', 'w', encoding='utf-8') as f:
            json.dump({'density': scaled or D0, 'texels': total, 'pieces': rep}, f)
        with open(B + '/sides.json', 'w', encoding='utf-8') as f:
            json.dump(sides, f, indent=1)
        dn = sorted(r['density'] for r in rep.values())
        return {'pieces': len(rep), 'probe_lit_instead': len(dropped), 'charts': sum(r['charts'] for r in rep.values()), 'texels_M': round(total / 1e6, 2),
                'density_t_per_m': [dn[0], dn[len(dn) // 2], dn[-1]], 'scaled_to_budget': bool(scaled), 'walkable_samples': int(len(walk)),
                'vertices': [v0, v1], 'side_max': max(r['side'] for r in rep.values())}
    S.run('charts', [sorted(lm), Q['density'], Q['budget'], job.play(), LM_FAR, LM_OVER, BAKE_TRIS], charts, [lm_dir, B + '/sides.json'])
    with open(B + '/classes.json', encoding='utf-8') as f:       # charts may have moved pieces to probe lighting
        cls = json.load(f)
    with open(B + '/sides.json', encoding='utf-8') as f:
        sides = json.load(f)

    # ---- regeo
    p_geo = job.project('bake_geo')

    def regeo():
        steps = [cli.load(src)]
        for mesh in sorted(sides):
            b = piece[mesh]
            steps.append({'op': 'port-regeo', 'file': b['dir'], 'mesh': mesh, 'geo': '%s/%s.meshgeo' % (lm_dir, mesh.split('/')[-1])})
            if not b['physics']:      # regeo rebuilds the shader-block depot: a piece without physics needs it bundled again
                steps.append({'op': 'bundle-add', 'res': mesh.lower() + '_mesh/blocks', 'bundle': job.bundle, 'on-error': 'continue'})
        steps.append(cli.save(p_geo))
        r = clix.run('bake_regeo', steps)
        if not os.path.exists(p_geo):
            raise StageError('The lightmap charts could not be written into the level  [log: %s]' % r.log_path)
        return {'meshes': len(sides)}
    S.run('regeo', [src_hash, config.file_hash(B + '/sides.json')], regeo, [p_geo])

    # ---- plan
    plan_f = B + '/plan.json'

    def plan():
        args = ['enlighten', 'chart-plan', job.level, '--match', '%s/%s_' % (job.prefix, job.piece_prefix), '--sides', B + '/sides.json',
                '--also-host-records', '--out', plan_f]
        r = clix.direct('bake_plan', args, project=p_geo, check=False)
        if not os.path.exists(plan_f):
            raise StageError('The lightmap atlas could not be planned  [log: %s]' % r.log_path)
        said = [l.strip() for l in r.text.splitlines() if 'chart' in l.lower() and ('x' in l or 'filled' in l)][-2:]
        return {'said': said}
    S.run('plan', [config.file_hash(B + '/sides.json')], plan, [plan_f])

    # ---- materials
    mats_f = B + '/materials.json'
    _tex = {}

    def tex(path):
        if path not in _tex:
            from PIL import Image
            im = Image.open(path).convert('RGB')
            if max(im.size) > 512:
                im = im.resize((512, 512), Image.BILINEAR)
            a = np.asarray(im).astype(np.float32) / np.float32(255.0)
            _tex[path] = np.where(a <= 0.04045, a / 12.92, ((a + 0.055) / 1.055) ** 2.4)
        return _tex[path]
    rng = np.random.default_rng(7)

    def footprint_mean(img, uv, idx, area, n=24):
        r1 = np.sqrt(rng.random(n)); r2 = rng.random(n)
        bw = np.stack([1 - r1, r1 * (1 - r2), r1 * r2], 1)
        p = np.einsum('nk,tkc->tnc', bw, uv[idx])
        h, w = img.shape[:2]
        x = np.floor((p[..., 0] % 1.0) * w).astype(int) % w; y = np.floor((p[..., 1] % 1.0) * h).astype(int) % h
        s = img[y, x].mean(1)
        return (s * area[:, None]).sum(0) / max(area.sum(), 1e-12)

    def materials():
        K = photometry.emission_bake_per_unit(job, host)
        res, em = {}, []
        for mesh, v in sorted(cls.items()):
            if not (v['lightmapped'] or v['emitter'] or v['occluder']):
                continue
            b = piece[mesh]
            rec = tplan[b['material']]
            files = {s['layout']: job.path('tex', s['texture'] + '.png') for s in rec['slots']}
            d = lm_dir if v['lightmapped'] else geo_dir
            g, s, pos, nrm, idx = _read('%s/%s.meshgeo' % (d, mesh.split('/')[-1]))
            uv = mg.decode(s, 'TexCoord0')[:, :2].astype(np.float64)
            area = np.linalg.norm(np.cross(pos[idx[:, 1]] - pos[idx[:, 0]], pos[idx[:, 2]] - pos[idx[:, 0]]), axis=1) / 2
            alb = np.clip(footprint_mean(tex(files['CS']), uv, idx, area), 0.02, 0.9)
            e = 0.0
            if v['emitter'] and 'E' in files:
                eimg = tex(files['E'])
                if rec.get('emission_peak') is not None and eimg.shape == tex(files['CS']).shape:
                    eimg = eimg * tex(files['CS'])          # the surface shows emission x colour (textures stage)
                erad = footprint_mean(eimg, uv, idx, area)
                target = float(erad @ LUM) * float(rec.get('emission_strength', 0.0)) * float(rec.get('emission_bake', 1.0)) * K
                e = target / float(alb @ LUM)
                em.append(target)
            res[mesh] = {'albedo': [round(float(x), 4) for x in alb], 'emissive': round(e, 2)}
        with open(mats_f, 'w', encoding='utf-8') as f:
            json.dump(res, f, indent=1)
        al = sorted(float(np.array(r['albedo']) @ LUM) for r in res.values())
        return {'materials': len(res), 'albedo_luminance': [round(al[len(al) // 10], 3), round(al[len(al) // 2], 3), round(al[len(al) * 9 // 10], 3)],
                'emitters': len(em), 'emitter_radiance_nits': [round(min(em), 1), round(max(em), 1)] if em else None}
    S.run('materials', [config.file_hash(job.path('tex', 'plan.json')), sorted(cls), inputs(job)['materials'], photometry.emission_bake_per_unit(job, host)],
          materials, [mats_f])

    # ---- probes
    probes_f = B + '/probes.json'

    def probes():
        centre, radius = job.play()
        gc = np.array(job.to_game(centre))
        floor_pts = []
        for mesh, v in cls.items():
            if not v.get('solid', v['lightmapped']):      # probes stand over every floor a soldier can walk on
                continue
            g, s, pos, nrm, idx = _read('%s/%s.meshgeo' % (geo_dir, mesh.split('/')[-1]))
            for p in {x['key']: x for x in v['placements']}.values():
                w = pos + np.array(p['pos'])
                a, b, c = w[idx[:, 0]], w[idx[:, 1]], w[idx[:, 2]]
                n = np.cross(b - a, c - a); ln = np.linalg.norm(n, axis=1); ok = ln > 1e-9
                up = np.zeros(len(idx), bool); up[ok] = (n[ok, 1] / ln[ok]) > 0.8
                ctr = (a + b + c) / 3
                m = up & (np.linalg.norm((ctr - gc)[:, [0, 2]], axis=1) <= radius + 30.0)
                for i in np.nonzero(m)[0]:
                    floor_pts += [a[i], b[i], c[i], ctr[i], (a[i] + b[i]) / 2, (b[i] + c[i]) / 2, (c[i] + a[i]) / 2]
        if not floor_pts:
            raise StageError('The bake found no walkable floor to put light probes over.')
        P = np.array(floor_pts)

        def layout(cell, vspacing):
            """probe volumes over the floor: one per cell x cell column, PROBE_CELL / PROBE_SPACING probes a side"""
            keys = np.floor(P[:, [0, 2]] / cell).astype(np.int64)
            cols = {}
            for (i, j), y in zip(map(tuple, keys), P[:, 1]):
                lo, hi = cols.get((i, j), (1e9, -1e9)); cols[(i, j)] = (min(lo, y), max(hi, y))
            sets, total = [], 0
            nx = nz = max(1, int(round(PROBE_CELL / PROBE_SPACING)))
            for (i, j), (lo, hi) in sorted(cols.items()):
                x0, z0 = i * cell, j * cell; y0, y1 = lo - 1.0, hi + PROBE_ABOVE
                ny = max(2, min(16, int(math.ceil((y1 - y0) / vspacing))))
                pos = []
                for zi in range(nz):
                    for yi in range(ny):
                        for xi in range(nx):
                            pos += [x0 + (xi + 0.5) / nx * cell, y0 + (yi + 0.5) / ny * (y1 - y0), z0 + (zi + 0.5) / nz * cell]
                sets.append({'guid': str(uuid.uuid5(uuid.NAMESPACE_URL, '%s.bf2maptool://probe-volume/%d/%d' % (job.name.lower(), i, j))),
                             'aabb': [x0, y0, z0, x0 + cell, y1, z0 + cell], 'dims': [nx, ny, nz], 'priority': 10, 'blend': 2.0,
                             'positions': [round(float(v), 3) for v in pos]})
                total += nx * ny * nz
            return sets, total
        # The engine has a limit: OG Naboo's first bake (344 volumes, 58,448 probes) crashed on the loading screen while
        # its unbaked build loaded; Rhen Var (142 / 23,008) loads, and DICE's own Naboo ships ~33,600 static probes.
        # Over budget, the columns grow (same probes a side, so farther apart) until both counts fit.
        cell, vsp = PROBE_CELL, PROBE_VSPACING
        sets, total = layout(cell, vsp)
        while (len(sets) > PROBE_VOLUME_BUDGET or total > PROBE_BUDGET) and cell < 96.0:
            cell, vsp = cell * 1.25, vsp * 1.12
            sets, total = layout(cell, vsp)
        if len(sets) > PROBE_VOLUME_BUDGET or total > PROBE_BUDGET:
            raise StageError('Light probes over budget even at %.0f m columns: %d volumes / %d probes (budget %d / %d). '
                             'Lower the play radius.' % (cell, len(sets), total, PROBE_VOLUME_BUDGET, PROBE_BUDGET))
        with open(probes_f, 'w', encoding='utf-8') as f:
            json.dump(sets, f)
        return {'volumes': len(sets), 'probes': total, 'column_m': round(cell, 1), 'vertical_spacing_m': round(vsp, 2)}
    S.run('probes', [sorted(k for k, v in cls.items() if v.get('solid')), job.play(), PROBE_CELL, PROBE_SPACING, 2,
                     PROBE_VOLUME_BUDGET, PROBE_BUDGET], probes, [probes_f])

    # ---- sky
    cube_f = B + '/sky_cube.f32'
    pano_f = job.path('layers', 'sky', 'sky_pano.hdr')

    def sky():
        if not os.path.exists(pano_f):
            raise StageError('The level has no sky of its own to bake with (the "Sky, fog, exposure" stage has not run).')
        from . import look as look_stage
        return sky_cube(hdr.read(pano_f).astype(np.float64), look_stage.LSCALE, cube_f)
    S.run('sky', [config.file_hash(pano_f) if os.path.exists(pano_f) else None, GROUND_ALBEDO], sky, [cube_f])

    # ---- scene
    scene_f = B + '/scene.fxs'

    def bake_lights():
        """the level's local lights as the bake takes them: the records the level holds, each lamp's strength times the
        host's bake factor and the lamp's own \"Brightness in the bake\" (the real-time light is not touched)"""
        raw, out_f = B + '/lights_level.json', B + '/lights_bake.json'
        for f in (raw, out_f):
            if os.path.exists(f):
                os.remove(f)
        r = clix.direct('bake_lights', ['level', 'export-lights', job.level, '--max-depth', '6', '--types', 'lights', '--out', raw],
                        project=p_geo, check=False)
        if not os.path.exists(raw):
            raise StageError('The lights of the level could not be read for the bake  [log: %s]' % r.log_path)
        with open(raw, encoding='utf-8') as f:
            doc = json.load(f)
        recs = doc['lights'] if isinstance(doc, dict) else doc
        KS = getattr(host, 'BAKE_LAMP_SCALE', 1.0)
        own = [(np.array(job.to_game(l['pos']), float), float(l.get('bake_strength', 1.0))) for l in job.map.get('lights') or []
               if l.get('type') != 'sun']
        n_own = 0
        for rec in recs:
            F = rec.get('Fields') or {}
            if 'Intensity' not in F:
                continue
            k = float(KS.get(rec.get('Type'), 1.0)) if isinstance(KS, dict) else float(KS)
            t = rec.get('Trans')
            if t is not None and own:
                d = [float(np.linalg.norm(p - np.array(t, float))) for p, _ in own]
                i = int(np.argmin(d))
                if d[i] < 0.05 and own[i][1] != 1.0:
                    k *= own[i][1]; n_own += 1
            F['Intensity'] = repr(float(F['Intensity']) * k)
            # The engine's spot and area lamps shine along -Forward (stages/lights.py); the baker shines them along
            # +Forward: in game the floor under a down-pointing lamp was lit real-time and black baked (LightMatch lm2).
            # Turn the record half round about its Up for the bake only.
            if rec.get('Type') in ('PbrSpotLightEntityData', 'PbrRectangularLightEntityData') and rec.get('Forward') and rec.get('Right'):
                rec['Forward'] = [-float(x) for x in rec['Forward']]
                rec['Right'] = [-float(x) for x in rec['Right']]
        with open(out_f, 'w', encoding='utf-8') as f:
            json.dump(doc, f)
        return out_f, len(recs), n_own

    def scene(lite=False, out=scene_f):
        # lite: the second try after the baker died - the light-blocking pieces without a lightmap are left out (only
        # the glowing ones stay). LEGO Utapau: 0.65 M triangles with no blockers baked, every scene with them aborted.
        extra = sorted(m.split('/')[-1][:-5] for m, v in cls.items() if v['emitter'] or (v['occluder'] and not lite))
        lights_f, n_lamps, n_own = bake_lights()
        args = ['enlighten', 'flux-scene', job.level, '--out', out, '--plan', plan_f, '--materials', mats_f, '--probes', probes_f,
                '--sky-cube', cube_f, '--report', B + '/scene_report.json']
        if extra:
            # the list is matched as substrings of the mesh path. Piece names start "<prefix>_<number>_", which is unique,
            # so that head stands for the piece: a map with a thousand glowing pieces otherwise makes a command line
            # Windows refuses (over 32,000 characters - seen on a user's city map)
            heads = [re.match(r'[A-Za-z0-9]+_[0-9]+_', n) for n in extra]
            short = [h.group(0) for h in heads] if all(heads) and len(set(h.group(0) for h in heads)) == len(extra) else extra
            if len(','.join(short)) > 30000:
                raise StageError('The map has %d glowing / light-blocking pieces without a lightmap - more than the bake can be told about '
                                 'in one go. Join small glowing objects in Blender, or set some to a class that is not emissive.' % len(extra))
            args += ['--emitters', ','.join(short)]
        if not bool((job.map.get('look') or {}).get('sun_on', True)):
            args.append('--no-sun')
        args += ['--lights', lights_f]
        if os.path.exists(out):
            os.remove(out)
        r = clix.direct('bake_scene_lite' if lite else 'bake_scene', args, project=p_geo, check=False)
        if not os.path.exists(out):
            raise StageError('The bake scene could not be written  [log: %s]' % r.log_path)
        said = [l.strip()[:200] for l in r.text.splitlines() if re.search(r'mesh(es)?[, ]|instance|emitter|light', l) and 'WARN' not in l][-6:]
        return {'bytes': os.path.getsize(out), 'non_lightmapped_in_scene': len(extra), 'said': said, 'lamps': n_lamps,
                'lamps_with_own_bake_strength': n_own, 'lamp_scale': getattr(host, 'BAKE_LAMP_SCALE', 1.0)}
    S.run('scene', [config.file_hash(plan_f), config.file_hash(mats_f), config.file_hash(probes_f), config.file_hash(cube_f),
                    (job.map.get('look') or {}).get('sun_on', True), job.map.get('lights'), getattr(host, 'BAKE_LAMP_SCALE', 1.0)], scene, [scene_f])

    # ---- flux
    out_d, out_f = B + '/out', B + '/out_f'

    def flux():
        exe = job.settings.get('fluxbake') or ''
        if not os.path.exists(exe):
            raise StageError('The light baker (fluxbake) was not found: "%s". Set it in the add-on preferences.' % exe)
        free = free_gb()
        if free < MIN_FREE_GB:
            raise StageError('Only %.1f GB of memory is free; the bake needs at least %.0f GB (it would die without a message). '
                             'Close the game or other programs and press Bake again.' % (free, MIN_FREE_GB))
        tries = []
        for lite in (False, True):
            sf = scene_f
            if lite:
                # the baker died on the full scene (0xC0000409 on heavy scenes, no message): once more without the
                # light-blocking pieces that have no lightmap
                sf = B + '/scene_lite.fxs'
                log('bake: the light baker stopped without a result (exit %s); trying again with a lighter scene '
                    '(no light-blocking pieces without a lightmap)' % tries[-1]['exit'])
                tries[-1]['lite_scene'] = scene(lite=True, out=sf)
            shutil.rmtree(out_d, ignore_errors=True)
            cmd = ['cmd', '/c', 'start', '/belownormal', '/b', '/wait', exe.replace('/', '\\'), sf.replace('/', '\\'), out_d.replace('/', '\\'),
                   '--samples', str(Q['samples']), '--filter', str(Q['filter'])]
            rc, text = cli.spawn(cmd, job.path('logs', 'bake_flux%s.log' % ('_lite' if lite else '')), log, cli.tool_env(job), echo=None,
                                 timeout=6 * 3600, quiet_timeout=3600)
            tries.append({'scene': sf, 'exit': rc, 'tail': [l.strip()[:160] for l in text.strip().splitlines()[-3:]]})
            if os.path.exists(out_d + '/irradiance.f32'):
                return {'free_gb_before': round(free, 1), 'exit': rc, 'scene': sf, 'lite': lite, 'tries': tries}
        raise BakerDied(tries)

    try:
        fx = S.run('flux', [config.file_hash(scene_f), Q['samples'], Q['filter']], flux, [out_d + '/irradiance.f32'])
    except BakerDied as e:
        return unbaked(job, log, src, e.tries)
    used_scene = (fx or {}).get('scene') or scene_f

    # ---- leak filter
    def leak():
        exe = job.settings.get('leakmask') or os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'bin', 'leakmask.exe'))
        if not os.path.exists(exe):
            raise StageError('The leak filter (leakmask) was not found: "%s". Set it in the add-on preferences.' % exe)
        shutil.rmtree(out_f, ignore_errors=True)
        rc, text = cli.spawn([exe, used_scene, out_d, out_f], job.path('logs', 'bake_leak.log'), log, cli.tool_env(job), echo=None, timeout=3600, quiet_timeout=1800)
        if not os.path.exists(out_f + '/irradiance.f32'):
            raise StageError('The leak filter stopped without a result (exit %s)  [log: %s]' % (rc, job.path('logs', 'bake_leak.log')))
        st = {}
        if os.path.exists(out_f + '/leak_stats.json'):
            with open(out_f + '/leak_stats.json', encoding='utf-8') as f:
                st = json.load(f)
        return {'stats': {k: v for k, v in st.items() if not isinstance(v, (list, dict))}}
    S.run('leak', [S.state.get('flux', {}).get('key')], leak, [out_f + '/irradiance.f32'])

    # ---- statistics of the filtered result (NaN count)
    def stats():
        with open(plan_f, encoding='utf-8') as f:
            pl = json.load(f)
        n = os.path.getsize(out_f + '/irradiance.f32') // 16
        W = int(pl.get('width') or pl.get('Width') or pl.get('atlasWidth') or 8192)
        H = int(pl.get('height') or pl.get('Height') or pl.get('atlasHeight') or n // W)
        if W * H != n:
            H = n // W
        I = np.fromfile(out_f + '/irradiance.f32', np.float32).reshape(H, W, 4)
        cov = I[..., 3] > 0
        L = (I[..., :3] @ LUM)[cov]
        nan = int((~np.isfinite(I[..., :3][cov])).sum())
        fin = L[np.isfinite(L)]
        from PIL import Image
        ref = float(np.percentile(fin, 95)) if len(fin) else 1.0
        img = np.clip(np.nan_to_num(I[..., :3] / max(ref, 1e-9)) ** (1 / 2.2), 0, 1)
        img[~cov] = [0.15, 0, 0.15]
        k = max(1, W // 2048)
        Image.fromarray((img * 255).astype(np.uint8)).resize((W // k, max(1, H // k)), Image.BOX).save(B + '/preview.png')
        return {'atlas': [W, H], 'covered_texels': int(cov.sum()), 'nan': nan,
                'irradiance_over_pi': [round(float(x), 3) for x in np.percentile(fin, [1, 10, 50, 90, 99])] if len(fin) else None,
                'zero_percent': round(100 * float((fin <= 0).mean()), 2) if len(fin) else None}
    st = S.run('stats', [S.state.get('leak', {}).get('key')], stats, [B + '/preview.png'])
    if st and st.get('nan'):
        raise StageError('The baked lightmap holds %d non-finite values; it was not applied.' % st['nan'])

    # ---- apply
    out_p = job.project('bake')
    apply_d = B + '/apply'

    def apply():
        shutil.rmtree(apply_d, ignore_errors=True)
        args = ['enlighten', 'bake-apply', job.level, '--bake', host.BAKE_NAME, '--plan', plan_f, '--probes', probes_f, '--flux', out_f, '--out', apply_d,
                '--prefix', job.level_dir + '/Lighting_Bake']
        r = clix.direct('bake_apply', args, project=p_geo, save_as=out_p, check=False)
        if not os.path.exists(out_p):
            raise StageError('The bake could not be written into the level  [log: %s]' % r.log_path)
        said = [l.strip()[:240] for l in r.text.splitlines() if l.strip().lower().startswith('bake-apply')][-6:]
        return {'said': said, 'files': sorted(os.listdir(apply_d)) if os.path.isdir(apply_d) else []}
    ap = S.run('apply', [S.state.get('leak', {}).get('key'), config.file_hash(plan_f), config.file_hash(probes_f), host.BAKE_NAME], apply, [out_p])

    # ---- checks on what was written
    checks = {}
    fls = apply_d + '/fls.framed.bin'
    if os.path.exists(fls):
        b = open(fls, 'rb').read()
        n, = struct.unpack_from('<I', b, 16 + 8)
        A = np.frombuffer(b, np.float32, n * 36, 48).reshape(n, 9, 4)
        checks['fls'] = {'probes': int(n), 'non_finite': int((~np.isfinite(A)).sum()), 'dc_median_rgb': [round(float(x), 3) for x in np.median(A[:, 0, :3], 0)]}
        if checks['fls']['non_finite']:
            raise StageError('The baked light probes hold %d non-finite values.' % checks['fls']['non_finite'])
    else:
        checks['fls'] = 'no fls.framed.bin written by bake-apply: NOT checked'
    end = apply_d + '/end.framed.bin'
    if os.path.exists(end):
        with open(plan_f, encoding='utf-8') as f:
            pl = json.load(f)
        with open(probes_f, encoding='utf-8') as f:
            vols = json.load(f)
        checks['end'] = end_check(end, pl, vols)
        if checks['end']['problems']:
            raise StageError('The baked lighting data does not pass its checks: %s.' % '; '.join(checks['end']['problems']))
    else:
        checks['end'] = 'no end.framed.bin written by bake-apply: NOT checked'
    info = {'quality': q, 'preset': Q, 'steps': S.times, 'results': {k: v.get('result') for k, v in S.state.items()}, 'checks': checks}
    with open(job.work + '/bake.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    total = sum((v.get('seconds') or 0) for v in S.times.values())
    log('bake: quality "%s": %s lightmapped pieces, %s M texels, %s probes, baker %s s, all steps %.0f s' % (
        q, (S.state['classify']['result'] or {}).get('lightmapped'), (S.state['charts']['result'] or {}).get('texels_M'),
        (S.state['probes']['result'] or {}).get('probes'), S.state['flux'].get('seconds'), total))
    return {'quality': q, 'seconds': round(total, 1), 'flux_seconds': S.state['flux'].get('seconds'), 'nan': (st or {}).get('nan'),
            'texels_M': (S.state['charts']['result'] or {}).get('texels_M'), 'checks': checks}
