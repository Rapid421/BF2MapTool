"""Stage `supremacy`: the Supremacy level `<Name>_02` (only when Supremacy is ticked).

The recipe proven on the Polis Massa port, made general (BFII_EXACT there; here every index is read from the level
that was just created, nothing is carried over from another build):

  1 create   `level create <Name>_02 --terrain-from <Name>_01 --terrain-keep <every non-mode layer> --mode-from <donor>
             --mode Mode1 --also-modes Mode8` in the session that holds the finished `<Name>_01`: the second level shares
             its pieces, textures, collision, lights, sky and bake. Mode8 = offline Instant Action Supremacy.
             Kyber is told about Mode1 only (Mode8 declared to it took the host screen away on Polis Massa).
  2 strip    the donor's intro cinematic STAYS (its finish event starts the round); what it shows is the donor planet's:
             connections into the donor's intro partitions / Mode1_VFX / _VFX_Schematic / Mode1_PA are removed with those
             three layer references, the donor's visual environments in the timeline are nulled or Excluded, the donor's
             objects in Mode1_Cinematics are Excluded (they stay in place, so the wiring stays).
  3 posts    the five capture points onto the scene's command posts A..E (snapped to the walkable floor), each with its
             capture zone and 13 spawns around it; two 12-spawn headquarters; the rest spread over the floor; the two
             ground combat areas around the whole walkable floor; the two TEAM spawn-finder shapes are only MOVED onto
             the headquarters at their own size and point order, y = floor + 3 (rewriting or shrinking them broke the
             round start / left nobody able to spawn); the neutral finder shapes are not touched; the donor's vehicle set
             pieces are NOT Excluded (crash at countdown 0 when they were).
  4 menu     the Instant Action level list gets the level; the donor's superbundle is mounted on it (its audio and
             vehicle chunks live there).
No forced bot fill: online the server's bot settings apply, offline Instant Action fills its own bots.
Output: projects/supremacy.fbproject, supremacy.json (the plan with every snap distance and the read-back).
"""
import json
import math
import os
import re

import numpy as np

from .. import chain, cli, config, hosts
from ..runner import StageError
from . import nav
from .spawns import Floor, Planner, anchors, disc, hull, resample

DONOR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'donors', 'felucia_mode1.json')
LETTERS = 'ABCDE'
ZONE_HEIGHT = 8.0
DUMPS = ('Mode1', 'Mode1_Cinematics_Schematic', 'Mode1_Cinematics', 'Mode1_Logic', 'Mode1_Shapes', 'Mode8_Shapes')


def inputs(job):
    m = job.map
    return {'posts': m.get('command_posts'), 'team_spawns': [(s['team'], s['pos']) for s in m.get('spawns') or [] if not s.get('hero')],
            'display': job.display, 'planet': job.planet, 'donor': config.file_hash(DONOR),
            'navmesh': config.file_hash(job.path('nav', 'navmesh.json')), 'v': 2}      # v2: the floor the posts stand on


def outputs(job):
    return [job.project('supremacy'), job.work + '/supremacy.json']


def donor():
    with open(DONOR, encoding='utf-8') as f:
        return json.load(f)


def _path(x):
    return str(x.get('__path', '')) if isinstance(x, dict) else ''


def _by_guid(dump):
    """every instance of a deep dump by its guid"""
    out = {}

    def walk(o):
        if isinstance(o, dict):
            g = o.get('__guid')
            if g and g not in out:
                out[g] = o
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
    walk(dump)
    return out


def _full(short, pool, what):
    hit = [g for g in pool if g.startswith(short)]
    if len(hit) != 1:
        raise StageError('The Supremacy donor does not match this game: %s %s was found %d times in the new level.' % (what, short, len(hit)))
    return hit[0]


def strip_steps(L2d, name2, mode1, cin_sch, cin, D):
    """step 2 of the docstring; returns (steps, counts)"""
    steps = []
    drop = re.compile(r'Partition_Intro|%s/(%s)$' % (re.escape(name2), '|'.join(re.escape(x) for x in D['strip_layers'])))
    nconn = 0
    for arr in ('EventConnections', 'PropertyConnections', 'LinkConnections'):
        bad = [i for i, c in enumerate(mode1.get(arr) or []) if drop.search(_path(c.get('Source'))) or drop.search(_path(c.get('Target')))]
        for i in sorted(bad, reverse=True):
            steps.append({'op': 'ebx-array-remove', 'ebx': L2d + '/Mode1', 'prop': arr, 'index': i})
        nconn += len(bad)
    refs = [i for i, o in enumerate(mode1.get('Objects') or [])
            if isinstance(o, dict) and _path(o.get('Blueprint')).rsplit('/', 1)[-1] in D['strip_layers']]
    if len(refs) != len(D['strip_layers']):
        raise StageError('The new Supremacy level holds %d of the %d donor layers that must be taken out (%s).' % (
            len(refs), len(D['strip_layers']), ', '.join(D['strip_layers'])))
    for i in sorted(refs, reverse=True):
        steps.append({'op': 'ebx-set-ref', 'ebx': L2d + '/Mode1', 'prop': 'Objects[%d].Blueprint' % i, 'target': 'null'})
        steps.append({'op': 'ebx-array-remove', 'ebx': L2d + '/Mode1', 'prop': 'Objects', 'index': i})
    prefix = D['level'].split('/')[0] + '/'                       # the donor's content root, e.g. S8/

    ve = []

    def walk(o, path):
        if isinstance(o, dict):
            if o.get('__type') == 'PointerRef' and _path(o).startswith(prefix) and '/Lighting/' in _path(o):
                ve.append(path); return
            for k, v in o.items():
                if not k.startswith('__'):
                    walk(v, path + '.' + k if path else k)
        elif isinstance(o, list):
            for i, v in enumerate(o):
                walk(v, '%s[%d]' % (path, i))
    walk(cin_sch, '')
    seen = set()
    for p in ve:
        if p in seen:
            continue
        seen.add(p)
        mo = re.match(r'^Objects\[(\d+)\]\.Blueprint$', p)
        if mo:      # a placed reference: exclude it (a live reference object with a null blueprint is its own crash risk)
            steps.append({'op': 'ebx-set', 'ebx': L2d + '/Mode1_Cinematics_Schematic', 'prop': 'Objects[%s].Excluded' % mo.group(1), 'value': True})
        else:
            steps.append({'op': 'ebx-set-ref', 'ebx': L2d + '/Mode1_Cinematics_Schematic', 'prop': p, 'target': 'null', 'on-error': 'continue'})
    nex = 0
    for i, o in enumerate(cin.get('Objects') or []):
        if isinstance(o, dict) and _path(o.get('Blueprint')).startswith(prefix) and 'Excluded' in o:
            steps.append({'op': 'ebx-set', 'ebx': L2d + '/Mode1_Cinematics', 'prop': 'Objects[%d].Excluded' % i, 'value': True})
            nex += 1
    return steps, {'connections_removed': nconn, 'layer_refs_removed': len(refs), 've_refs': len(seen), 'cinematic_objects_excluded': nex}


def post_steps(job, L2d, D, logic, shapes1, P, log):
    """step 3 of the docstring; returns (steps, plan)"""
    J = D['data']
    C, mid = P.C, P.mid
    steps = []
    inst = _by_guid(logic)
    shp = dict(_by_guid(shapes1))
    missing = [g for g in list(J['spawns']) + [g for g, v in J['pref'].items() if v[0] == 'pf_capturepoint_mode1'] if g not in inst]
    if missing:
        raise StageError('The Supremacy donor does not match this game: %d of its spawns / capture points are not in the new level '
                         '(first %s).' % (len(missing), missing[0]))

    def st(layer, guid, prop, value):
        steps.append({'op': 'ebx-set', 'ebx': '%s/%s' % (L2d, layer), 'object': guid, 'prop': prop, 'value': value})

    def put_tf(layer, guid, p, face, field='Transform', lift=0.0):
        for a, v in zip('xyz', (p[0], p[1] + lift, p[2])):
            st(layer, guid, '%s.trans.%s' % (field, a), round(float(v), 3))
        yaw = math.atan2(face[0] - p[0], face[2] - p[2])
        for k, vec in (('forward', (math.sin(yaw), 0.0, math.cos(yaw))), ('right', (math.cos(yaw), 0.0, -math.sin(yaw))), ('up', (0.0, 1.0, 0.0))):
            for a, v in zip('xyz', vec):
                st(layer, guid, '%s.%s.%s' % (field, k, a), round(v, 5))

    def put_shape(layer, guid, poly2d, y, n, grow=0.0):
        for k, q in enumerate(resample(poly2d, n, grow)):
            for a, v in zip('xyz', (q[0], y, q[1])):
                st(layer, guid, 'Points[%d].%s' % (k, a), round(float(v), 3))

    def snap(p):
        """nearest safe spot to a marker (vertical distance weighted x3: stay on the marker's floor)"""
        d = np.sqrt((((C - p) * [1, 3, 1]) ** 2).sum(1))
        i = int(np.argmin(d))
        return C[i], float(np.hypot(*(C[i] - p)[[0, 2]])), float(C[i][1] - p[1])

    def ring(centre, n, used, rmin=3.0, mind=2.5):
        pick = []
        for R, dy in ((16, 1.5), (24, 1.5), (32, 3.0), (45, 5.0), (70, 8.0), (140, 12.0), (1e6, 1e6)):
            sel = C[(np.hypot(*(C[:, [0, 2]] - centre[[0, 2]]).T) < R) & (np.abs(C[:, 1] - centre[1]) < dy)]
            pick = []
            for p in sel[np.argsort(np.hypot(*(sel[:, [0, 2]] - centre[[0, 2]]).T))]:
                if np.hypot(*(p - centre)[[0, 2]]) < rmin:
                    continue
                if all(np.hypot(*(p - q)[[0, 2]]) >= mind or abs(p[1] - q[1]) > 2 for q in pick + used):
                    pick.append(p)
                if len(pick) == n:
                    return pick, R
        raise StageError('Only %d of the %d spawn spots a command post needs were found on the walkable floor. Supremacy needs a larger '
                         'connected floor (160 spawns, 2.5 m apart).' % (len(pick), n))

    plan = {'cp': {}, 'groups': {}}
    posts = {str(c.get('letter', '')).upper(): c for c in job.map.get('command_posts') or []}
    used, cp_new = [], {}
    cp_guid = {L: _full(D['capture_points'][L], J['pref'], 'capture point') for L in LETTERS}
    for L in LETTERS:
        g = cp_guid[L]
        raw = np.array(job.to_game(posts[L]['pos']), float)
        p, dxz, dy = snap(raw)
        cp_new[L] = p
        put_tf('Mode1_Logic', g, p, mid, 'BlueprintTransform')
        radius = float(posts[L].get('radius') or 9.0)
        plan['cp'][L] = {'object': posts[L]['name'], 'marker': raw.round(2).tolist(), 'placed': p.round(2).tolist(), 'snap_xz_m': round(dxz, 2),
                         'snap_dy_m': round(dy, 2), 'zone_radius_m': radius}
        if dxz > 6.0 or abs(dy) > 3.0:
            log('supremacy: command post %s (%s) was moved %.1f m sideways / %.1f m in height to reach the walkable floor' % (L, posts[L]['name'], dxz, dy))
        for sg, links in J['shape_links'].items():      # the capture zone: the shape linked to this post's Geometry
            if any(l[1] == g[:8] and l[2] == 'Geometry' for l in links):
                put_shape(J['shapes'][sg]['layer'], sg, disc(p, radius, 16), p[1] - 1.0, J['shapes'][sg]['n'])
                st(J['shapes'][sg]['layer'], sg, 'Height', ZONE_HEIGHT)
        grp = next(k for k, v in J['group_cp'].items() if v == g)
        pts, R = ring(p, len(J['groups'][grp]), used, rmin=4.0)
        used += pts
        for sg, q in zip(J['groups'][grp], pts):
            put_tf('Mode1_Logic', sg, q, p, lift=1.0)
        plan['groups'][L] = {'n': len(pts), 'radius_m': R if R < 1e5 else None}
        if L == 'D':
            put_tf('Mode1_Logic', J['marker_D'], p, mid)        # post D's static marker moves with it
    # headquarters: the placed team spawns' middle when there are any, else between the team's two outer posts
    placed = {t: [np.array(job.to_game(s['pos']), float) for s in job.map.get('spawns') or [] if s['team'] == t and not s.get('hero')]
              for t in ('team1', 'team2')}
    hq_new = {}
    grouped = set(x for l in J['groups'].values() for x in l)
    for team, pair in (('team1', 'AB'), ('team2', 'DE')):
        src = D['hq'][team]
        raw = np.mean(placed[team], 0) if placed[team] else (cp_new[pair[0]] + cp_new[pair[1]]) / 2
        p, dxz, dy = snap(raw)
        hq_new[team] = p
        members = J['hq'][src]
        pts, R = ring(p, len(members), used, rmin=0.0)
        used += pts
        for sg, q in zip(members, pts):
            put_tf('Mode1_Logic', sg, q, cp_new['C'], lift=1.0)
        plan['hq_' + team] = {'from': 'placed %s spawns' % team if placed[team] else 'between posts %s and %s' % tuple(pair),
                              'placed': p.round(2).tolist(), 'snap_xz_m': round(dxz, 2), 'n': len(pts), 'radius_m': R if R < 1e5 else None}
    rest = [g for g in J['spawns'] if g not in grouped and not any(g in m for m in J['hq'].values())]
    pool = np.array([p for p in C if all(np.hypot(*(p - a)[[0, 2]]) > 20.0 for a in hq_new.values())
                     and all(np.hypot(*(p - q)[[0, 2]]) > 2.5 or abs(p[1] - q[1]) > 2 for q in used)])
    if len(pool) < len(rest):
        pool = C
    pick = [pool[int(np.argmin(np.abs(pool[:, [0, 2]] - mid[[0, 2]]).sum(1)))]]
    d = np.full(len(pool), 1e9)
    while len(pick) < len(rest):
        d = np.minimum(d, np.sqrt((((pool - pick[-1]) * [1, 2, 1]) ** 2).sum(1)))
        pick.append(pool[int(np.argmax(d))])
    for sg, q in zip(rest, pick):
        put_tf('Mode1_Logic', sg, q, mid, lift=1.0)
    plan['fallback'] = len(rest)
    for team in ('team1', 'team2'):                       # spawn-location weight givers follow the headquarters
        put_tf('Mode1_Logic', _full(D['weightgivers'][team], J['weightgivers'].values(), 'weight giver'), hq_new[team], mid)
    # ground combat areas: the walkable hull + 40 m, 60 m under the floor (Height 0 = unbounded, as shipped)
    area = hull(C[:, [0, 2]])
    floor_y = float(np.percentile(C[:, 1], 5))
    for short in D['combat_areas']:
        g = _full(short, J['shapes'], 'combat area')
        put_shape(J['shapes'][g]['layer'], g, area, floor_y - 60.0, J['shapes'][g]['n'], grow=40.0)
    plan['combat_area'] = {'hull_points': int(len(area)), 'grown_m': 40.0, 'floor_y': round(floor_y - 60.0, 2)}
    # the two TEAM spawn-finder shapes: moved only, at their own size and point order, y = headquarters floor + 3
    for g, v in J['shapes'].items():
        if v['type'] != 'SpawnLocationFinderShapeData' or v['team'] not in ('1', '2'):
            continue
        o = shp.get(g)
        pts = [(float(q['x']), float(q['z'])) for q in (o or {}).get('Points') or []]
        if not pts:
            raise StageError('The team spawn area %s of the Supremacy donor was not found in the new level.' % g[:8])
        cx, cz = sum(q[0] for q in pts) / len(pts), sum(q[1] for q in pts) / len(pts)
        home = hq_new['team' + v['team']]
        for i, (x, z) in enumerate(pts):
            st(v['layer'], g, 'Points[%d].x' % i, round(home[0] + (x - cx), 3))
            st(v['layer'], g, 'Points[%d].z' % i, round(home[2] + (z - cz), 3))
            st(v['layer'], g, 'Points[%d].y' % i, round(float(home[1]) + 3.0, 3))
        plan['team_area_' + v['team']] = {'centre': [round(float(home[0]), 1), round(float(home[2]), 1)],
                                          'size_m': round(2 * max(math.hypot(q[0] - cx, q[1] - cz) for q in pts), 1), 'points': len(pts)}
    plan['cp_guid'] = cp_guid
    return steps, plan


def run(job, log):
    host = hosts.get(job.host)
    clix = cli.Cli(job, log)
    D = donor()
    posts = {str(c.get('letter', '')).upper(): c for c in job.map.get('command_posts') or []}
    if sorted(posts) != list(LETTERS) or len(job.map.get('command_posts') or []) != 5:
        raise StageError('Supremacy needs five command posts lettered A to E; the scene has %s.' % (', '.join(sorted(posts)) or 'none'))
    base = chain.base(job, 'supremacy')
    with open(job.work + '/shell.json', encoding='utf-8') as f:
        shell = json.load(f)
    keep = [p.rsplit('/', 1)[-1] for p in shell['partitions']]
    keep = [p for p in keep if p != job.name + '_01' and not p.startswith(tuple(host.MODE_PARTITIONS))]
    L2, L2d, name2 = job.level2, job.level2_dir, job.name + '_02'
    title = job.display + ' Supremacy'
    create = ['level', 'create', L2, '--terrain-from', job.level, '--terrain-keep', ','.join(keep), '--mode-from', D['level'],
              '--mode', D['mode'], '--mode-subworld', D['mode'], '--mode-keep', ','.join(D['mode_keep']), '--also-modes', D['offline_mode'],
              '--ve', 'keep', '--menu-name', job.display, '--planet-name', job.planet, '--kyber', title]
    dd = job.path('dumps', 'supremacy')
    os.makedirs(dd, exist_ok=True)
    p0 = job.project('supremacy_0')
    for p in (p0, job.project('supremacy')):
        if os.path.exists(p):
            os.remove(p)
    # 1 create + what the edits are planned from
    steps = [cli.load(base), {'op': 'cli', 'args': create},
             {'op': 'cli', 'args': ['kyber', 'declare', '--level', L2, '--name', title, '--modes', D['mode']]}]
    for lay in DUMPS:
        steps.append({'op': 'ebx-dump', 'ebx': '%s/%s' % (L2d, lay), 'deep': True, 'out': '%s/%s.json' % (dd, lay)})
    steps += [{'op': 'ebx-dump', 'ebx': 'UI/Metadata/OptionsEnumUIMetaData', 'deep': True, 'out': dd + '/OptionsEnumUIMetaData.json'}, cli.save(p0)]
    r = clix.run('supremacy_create', steps, check=False)
    if not os.path.exists(p0):
        if re.search(r'already exists', r.text, re.I):
            raise StageError('A level named "%s" already exists in the game or in another installed mod.' % name2)
        tail = ' | '.join(l.strip() for l in r.text.strip().splitlines()[-4:])
        raise StageError('The Supremacy level could not be created: %s  [log: %s]' % (tail[:600], r.log_path))

    def load(lay):
        path = '%s/%s.json' % (dd, lay)
        if not os.path.exists(path):
            raise StageError('The new Supremacy level has no %s layer (dump missing)  [log: %s]' % (lay, r.log_path))
        with open(path, encoding='utf-8') as f:
            return json.load(f)
    # 2 strip
    s_steps, s_info = strip_steps(L2d, name2, load('Mode1'), load('Mode1_Cinematics_Schematic'), load('Mode1_Cinematics'), D)
    # 3 posts
    polys, geo = nav.files(job)
    P = Planner(job, Floor(polys, geo, anchors(job)), np.random.default_rng(7))      # the floor the posts stand on
    p_steps, plan = post_steps(job, L2d, D, load('Mode1_Logic'), load('Mode1_Shapes'), P, log)
    # 4 Instant Action list + the donor's superbundle
    opt = load('OptionsEnumUIMetaData')
    i_ia = int(D['instant_action_item'])
    items = opt.get('Items') or []
    donor_data = 'UI/Data/GameModes/Levels/' + D['level'].rsplit('/', 1)[-1]
    refs = [_path(x) for x in (items[i_ia].get('ItemLevelReferences') or [])] if i_ia < len(items) else []
    if not any(x.lower() == donor_data.lower() for x in refs):
        hit = [i for i, it in enumerate(items) if any(_path(x).lower() == donor_data.lower() for x in it.get('ItemLevelReferences') or [])]
        raise StageError('The Instant Action level list was not found where it is expected (item %d does not list %s; items that do: %s).' % (
            i_ia, donor_data, hit))
    m_steps = [{'op': 'ebx-array-add', 'ebx': 'UI/Metadata/OptionsEnumUIMetaData', 'prop': 'Items[%d].ItemLevelReferences' % i_ia,
                'target': 'UI/Data/GameModes/Levels/' + name2},
               {'op': 'ebx-array-add', 'ebx': L2 + '/description', 'prop': 'SuperBundles', 'value': D['level']}]
    out = job.project('supremacy')
    after = dd + '/after_Mode1_Logic.json'
    if os.path.exists(after):
        os.remove(after)
    r2 = clix.run('supremacy_edit', [cli.load(p0)] + s_steps + p_steps + m_steps +
                  [{'op': 'ebx-dump', 'ebx': L2d + '/Mode1_Logic', 'deep': True, 'out': after}, cli.save(out)], check=False)
    if not os.path.exists(out):
        tail = ' | '.join(l.strip() for l in r2.text.strip().splitlines()[-4:])
        raise StageError('The Supremacy level could not be edited: %s  [log: %s]' % (tail[:600], r2.log_path))
    os.remove(p0)
    # read back: the five capture points are where the plan put them
    back = _by_guid(json.load(open(after, encoding='utf-8')))
    off = {}
    for L in LETTERS:
        t = ((back.get(plan['cp_guid'][L]) or {}).get('BlueprintTransform') or {}).get('trans') or {}
        want = plan['cp'][L]['placed']
        got = [t.get('x'), t.get('y'), t.get('z')]
        if None in got or max(abs(float(a) - b) for a, b in zip(got, want)) > 0.01:
            off[L] = got
    failed = len(re.findall(r'^\s*\[?\d*.*\bERR\b', r2.text, re.M))
    info = {'level': L2, 'kept_layers': len(keep), 'strip': s_info, 'plan': plan, 'edit_steps': len(s_steps) + len(p_steps) + len(m_steps),
            'safe_spots': int(len(P.C)), 'readback_posts_off': off, 'edit_errors': failed, 'instant_action_item': i_ia}
    with open(job.work + '/supremacy.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    if off:
        raise StageError('The capture points did not read back where they were put: %s  [log: %s]' % (off, r2.log_path))
    log('supremacy: %s created (%d layers shared); %d donor connections and %d layers taken out, %d cinematic objects excluded; '
        '5 posts, %d spawns, %d safe spots; %d edits' % (L2, len(keep), s_info['connections_removed'], s_info['layer_refs_removed'],
                                                        s_info['cinematic_objects_excluded'], len(D['data']['spawns']), len(P.C), info['edit_steps']))
    return {'level': L2, 'posts': {L: plan['cp'][L]['snap_xz_m'] for L in LETTERS}, 'edits': info['edit_steps'], 'strip': s_info}
