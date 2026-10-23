"""Stage `collision`: per piece, the triangles that are solid -> a Havok compressed-mesh shape in slot 0 (stand on) and
slot 1 (weapon fire) through HavokCollisionTool `import mesh`.

Which triangles are solid is decided per OBJECT (its Collision setting) and per MATERIAL class:
  exact    the object's own render triangles (the reversed copies of double-sided faces are left out)
  custom   the triangles of the collision mesh the object names, in place of its own
  hull     ONE convex shape around the object (scipy ConvexHull of its vertices), in place of its own triangles;
           doors get their hull where they are built (a moving part needs a convex shape of its own)
  none     nothing
  lava     never solid: a soldier sinks into it and the kill volume below takes him; the navmesh skips it too
Output: col/<piece>.obj / .res / .resmeta and col/report.json (triangles, quantisation error per piece).
"""
import json
import os
import re
import subprocess
from concurrent.futures import ThreadPoolExecutor

import numpy as np

from .. import cli, config
from ..runner import StageError
from .split import to_game

NO_COLLISION_CLASSES = ('lava', 'holo')


def inputs(job):
    m = job.map
    objs = [(o['name'], o['role'], o.get('collision'), o.get('collision_object'),
             config.file_hash(job.src(o['geo'])) if o['role'] == 'collision' else None) for o in m['objects']]
    return {'objects': objs, 'classes': {k: v['class'] for k, v in m['materials'].items()},
            'template': config.file_hash(job.settings['havok_template']), 'v': 5}


def outputs(job):
    return [job.path('col', 'report.json')]


def _obj_text(tris):
    """triangle soup (T, 3, 3) -> OBJ text with welded vertices"""
    v, inv = np.unique(np.round(tris.reshape(-1, 3), 5), axis=0, return_inverse=True)
    f = inv.reshape(-1, 3) + 1
    f = f[(f[:, 0] != f[:, 1]) & (f[:, 1] != f[:, 2]) & (f[:, 0] != f[:, 2])]
    return ''.join('v %.6f %.6f %.6f\n' % tuple(p) for p in v) + ''.join('f %d %d %d\n' % tuple(t) for t in f), len(f)


def _hull(tris):
    """triangles (H, 3, 3) of the convex hull of a triangle soup, faces turned outwards; None for a flat / empty object"""
    from scipy.spatial import ConvexHull, QhullError
    pts = np.unique(np.round(np.asarray(tris, np.float64).reshape(-1, 3), 4), axis=0)
    if len(pts) < 4:
        return None
    try:
        hull = ConvexHull(pts)
    except QhullError:
        return None
    t = pts[hull.simplices]
    n = np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0])
    inward = ((t.mean(1) - pts.mean(0)) * n).sum(1) < 0
    t[inward] = t[inward][:, ::-1]
    return t


def run(job, log):
    s = job.settings
    tool, tpl = s['havok_tool'], s['havok_template']
    meta = os.path.splitext(tpl)[0] + '.resmeta'
    for p, what in ((tool, 'HavokCollisionTool'), (tpl, 'the collision template .res'), (meta, 'the collision template .resmeta')):
        if not os.path.exists(p):
            raise StageError('%s was not found at %s. Set it in the add-on preferences.' % (what, p))
    with open(job.path('pieces', 'pieces.json'), encoding='utf-8') as f:
        PJ = json.load(f)
    obj_names = PJ['objects']
    by_name = {o['name']: o for o in job.map['objects']}
    mode = np.array([by_name[n].get('collision', 'exact') for n in obj_names])
    cls = job.map['materials']
    out = job.dirs['col']
    os.makedirs(out, exist_ok=True)
    # custom collision meshes: each goes to the first piece that holds its object, in that piece's frame
    custom_done, custom_missing = set(), []
    jobs, keep, stats = [], set(), {'exact': 0, 'custom': 0, 'hull': 0, 'none': 0}
    # standalone collision (role "Collision mesh" that no object names, e.g. a source game's own collision imported as
    # invisible meshes): every triangle goes to the nearest piece that may carry collision, in that piece's frame
    extra = {}
    loose = [o for o in job.map['objects'] if o['role'] == 'collision' and not o.get('collision_for')]
    if loose:
        from scipy.spatial import cKDTree
        hosts_ = [p for p in PJ['pieces'] if (cls.get(p['material']) or {}).get('class') not in NO_COLLISION_CLASSES]
        if not hosts_:
            raise StageError('The map has standalone collision meshes but no piece that can carry collision.')
        cen = np.array([p['centre'] for p in hosts_], np.float64)
        kd = cKDTree(cen)
        n_loose = 0
        for o in loose:
            pos = np.load(job.src(o['geo']))['pos'].astype(np.float64)
            if not len(pos):
                continue
            _, k = kd.query(pos.mean(1))
            for hi in np.unique(k):
                sel = pos[k == hi]
                extra.setdefault(hosts_[hi]['name'], []).append(to_game(sel - cen[hi]))
            n_loose += len(pos)
        stats['standalone'] = n_loose
        stats['standalone_objects'] = len(loose)
        stats['standalone_pieces'] = len(extra)
    for p in PJ['pieces']:
        z = np.load(job.path('pieces', p['name'] + '.npz'))
        tris = list(extra.get(p['name'], []))
        if (cls.get(p['material']) or {}).get('class') not in NO_COLLISION_CLASSES:
            m = (mode[z['obj']] == 'exact') & ~z['dup']
            if m.any():
                tris.append(z['pos'][m]); stats['exact'] += int(m.sum())
            for oi in np.unique(z['obj']):
                on = obj_names[oi]
                if mode[oi] == 'custom' and on not in custom_done:
                    custom_done.add(on)
                    cn = by_name[on].get('collision_object')
                    crec = by_name.get(cn) if cn else None
                    if crec is None:
                        custom_missing.append(on); continue
                    c = np.load(job.src(crec['geo']))['pos']
                    tris.append(to_game(c - np.asarray(p['centre'])))
                    stats['custom'] += len(c)
                elif mode[oi] == 'hull' and on not in custom_done:
                    custom_done.add(on)
                    h = _hull(np.load(job.src(by_name[on]['geo']))['pos'])
                    if h is not None:
                        tris.append(to_game(h - np.asarray(p['centre'])))
                        stats['hull'] += len(h)
        if not tris:
            stats['none'] += 1
            continue
        text, nf = _obj_text(np.concatenate(tris))
        if nf == 0:
            stats['none'] += 1
            continue
        op = '%s/%s.obj' % (out, p['name'])
        old = open(op).read() if os.path.exists(op) else None
        keep.update(p['name'] + e for e in ('.obj', '.res', '.resmeta'))
        if old == text and os.path.exists(op[:-4] + '.res') and os.path.exists(op[:-4] + '.resmeta'):
            continue
        with open(op, 'w') as f:
            f.write(text)
        jobs.append(p['name'])
    if custom_missing:
        raise StageError('Collision is set to "Custom mesh" but no collision mesh was found for: %s. Pick one in the Collision tab '
                         'or name it <object>_col.' % ', '.join(custom_missing[:8]))
    env = cli.tool_env(job)
    # A piece is only "already converted" when the LAST COMPLETE run reported it. A build cancelled in this stage leaves
    # .res files (possibly half written) without a report; trusting them left the floors of a test map without collision
    # after a Cancel + resume (2026-10-12).
    rp0 = job.path('col', 'report.json')
    try:
        with open(rp0, encoding='utf-8') as f:
            known = set(json.load(f))
    except (OSError, ValueError):
        known = set()
    redo = sorted({k[:-4] for k in keep if k.endswith('.obj')} - known - set(jobs))
    jobs += redo

    def one(name):
        base = '%s/%s' % (out, name)
        try:
            r = subprocess.run([s['dotnet'], tool, 'import', 'mesh', base + '.obj', tpl, meta, base + '.res', base + '.resmeta'],
                               capture_output=True, text=True, env=env, stdin=subprocess.DEVNULL, timeout=300)
        except subprocess.TimeoutExpired:
            return name, False, 'the collision tool did not finish within 5 minutes'
        line = next((l.strip() for l in r.stdout.splitlines() if l.strip().startswith('mesh:')), (r.stderr or r.stdout).strip()[-300:])
        ok = r.returncode == 0 and os.path.exists(base + '.res')
        if not ok:
            for e in ('.res', '.resmeta'):
                if os.path.exists(base + e):
                    os.remove(base + e)
        return name, ok, line
    results = []
    if jobs:
        log('collision: %d pieces to convert (%d unchanged)' % (len(jobs), len(keep) // 3 - len(jobs)))
        with ThreadPoolExecutor(8) as ex:
            results = list(ex.map(one, jobs))
    rp = job.path('col', 'report.json')
    report = json.load(open(rp)) if os.path.exists(rp) else {}
    report = {k: v for k, v in report.items() if k + '.res' in keep}
    failed = []
    for name, ok, line in results:
        if not ok:
            failed.append((name, line)); report.pop(name, None); continue
        e = re.search(r'error ([\d.]+) mm', line); t = re.search(r'mesh: (\d+) triangle', line)
        report[name] = {'triangles': int(t.group(1)) if t else None, 'error_mm': float(e.group(1)) if e else None, 'line': line}
    for f in os.listdir(out):
        if f.endswith(('.obj', '.res', '.resmeta', '.framed')) and f not in keep:
            os.remove(os.path.join(out, f))
    with open(rp, 'w', encoding='utf-8') as f:
        json.dump(report, f, indent=1)
    if failed:
        raise StageError('HavokCollisionTool failed on %d piece(s); first: %s: %s' % (len(failed), failed[0][0], failed[0][1][:300]))
    planned = {k[:-4] for k in keep if k.endswith('.obj')}
    if set(report) != planned:
        raise StageError('Collision is incomplete: %d pieces planned, %d reported (missing: %s)' % (
            len(planned), len(report), ', '.join(sorted(planned - set(report))[:6])))
    errs = [v['error_mm'] for v in report.values() if v.get('error_mm') is not None]
    summary = {'pieces_with_collision': len(report), 'pieces_without': stats['none'], 'converted_now': len(jobs),
               'triangles': sum(v['triangles'] or 0 for v in report.values()), 'max_error_mm': max(errs) if errs else None,
               'exact_triangles': stats['exact'], 'custom_triangles': stats['custom'], 'hull_triangles': stats['hull'],
               'standalone_triangles': stats.get('standalone', 0), 'standalone_objects': stats.get('standalone_objects', 0)}
    log('collision: %(pieces_with_collision)d pieces solid, %(pieces_without)d without; %(triangles)d triangles; '
        'largest quantisation error %(max_error_mm)s mm' % summary)
    return summary
