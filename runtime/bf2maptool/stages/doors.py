"""Stage `doors`: every scene object with the role "Door" becomes a door that slides open when someone is near, on the
host shell's own automatic-door logic (no new logic).

Per door:
  * its plane is measured (normal = the thinnest direction of the mesh); "up / down" slide in the plane along the
    direction closest to the world's up, "left / right" across it;
  * the panel is exported whole in a frame whose x axis is the slide direction (the host's door logic slides its leaves
    along the prefab's local x), origin at the trailing edge, so a door that leans with its wall slides in its own plane;
  * its collision is a convex hull in slot 0 (stand / block) and slot 1 (weapon fire), built on the host panel's own
    physics resource (moving parts cannot use triangle meshes);
  * doors with the same shape, material and travel share one panel and one prefab; each door is one placement.
Pipeline rules applied: sound of the cloned prefab removed (its audio is not copyable); the door logic's class
schematics are bundled only together with their class assets; the panel's group of settings is read back.
"""
import hashlib
import json
import os
import shutil
import subprocess

import numpy as np

from .. import chain, cli, config, fbx, hosts
from ..runner import StageError
from .collision import _obj_text

AXES = ('up', 'down', 'left', 'right')


def door_objects(job):
    return [o for o in job.map['objects'] if o.get('role') == 'door']


def inputs(job):
    return {'doors': [(o['name'], o.get('door'), config.file_hash(job.src(o['geo']))) for o in door_objects(job)],
            'offset': job.offset(), 'host': hosts.get(job.host).DOOR, 'v': 1}


def outputs(job):
    return [job.project('doors'), job.work + '/doors.json']


def measure(pos, axis):
    """world triangles (T, 3, 3) of one door -> frame, local triangles, sizes"""
    V = np.unique(np.round(pos.reshape(-1, 3), 5), axis=0)
    c = V.mean(0)
    w, E = np.linalg.eigh(np.cov((V - c).T))
    n = E[:, 0] / np.linalg.norm(E[:, 0])
    if n[int(np.argmax(np.abs(n)))] < 0:
        n = -n                                             # a fixed sign, so the same door always gives the same frame
    ref = np.array([0.0, 0.0, 1.0])
    r = np.cross(ref, n)
    if np.linalg.norm(r) < 0.2:                            # a hatch lying flat: "up" is the scene's +y
        r = np.cross(np.array([0.0, 1.0, 0.0]), n)
    r = r / np.linalg.norm(r)
    u = np.cross(n, r)
    s = {'up': u, 'down': -u, 'right': r, 'left': -r}[axis]
    y = np.cross(n, s)
    d = V - c
    a, b, cc = d @ s, d @ y, d @ n
    org = c + s * a.min() + y * (b.min() + b.max()) / 2 + n * (cc.min() + cc.max()) / 2
    F = np.stack([s, y, n])                                # rows: the prefab's local x, y, z in scene coordinates
    local = (pos - org) @ F.T
    return {'origin': org, 'frame': F, 'local': local, 'travel_size': float(a.max() - a.min()), 'across': float(b.max() - b.min()),
            'thickness': float(cc.max() - cc.min()), 'flatness': float(w[0] / max(w[1], 1e-12)),
            'lean_deg': float(np.degrees(np.arccos(np.clip(abs(u[2]), 0, 1))))}


def _panel_template(job, clix, host):
    """the host panel's physics resource (res + 16-byte meta), exported from the game once"""
    d = os.path.join(job.settings['work_dir'], '_cache', 'doors').replace('\\', '/')
    os.makedirs(d, exist_ok=True)
    base = '%s/%s_panel' % (d, job.host)
    if not (os.path.exists(base + '.res') and os.path.exists(base + '.resmeta')):
        framed = base + '.framed'
        clix.run('door_panel_export', [{'op': 'res-export', 'res': host.DOOR['panel_physics'].lower(), 'out': framed, 'framed': True}])
        if not os.path.exists(framed) or os.path.getsize(framed) < 64:
            raise StageError('The host door panel\'s physics resource could not be exported from the game.')
        with open(framed, 'rb') as f:
            raw = f.read()
        with open(base + '.resmeta', 'wb') as f:
            f.write(raw[:16])
        with open(base + '.res', 'wb') as f:
            f.write(raw[16:])
    return base + '.res', base + '.resmeta'


def run(job, log):
    host = hosts.get(job.host)
    clix = cli.Cli(job, log)
    D = host.DOOR
    src, out = chain.base(job, 'doors'), job.project('doors')
    doors = door_objects(job)
    if not doors:
        shutil.copyfile(src, out)
        with open(job.work + '/doors.json', 'w', encoding='utf-8') as f:
            json.dump({'doors': 0, 'types': []}, f)
        log('doors: none in the scene')
        return {'doors': 0}
    with open(job.path('tex', 'plan.json'), encoding='utf-8') as f:
        plan = json.load(f)
    with open(job.work + '/import.json', encoding='utf-8') as f:
        imported_tex = set(json.load(f)['textures'])
    s = job.settings
    types, placements, problems = {}, [], []
    for o in doors:
        z = np.load(job.src(o['geo']), allow_pickle=False)
        used = sorted({str(z['slots'][i]) for i in np.unique(z['mat'])})
        if len(used) != 1:
            problems.append('"%s" has %d materials (a door panel takes one)' % (o['name'], len(used))); continue
        mat = used[0]
        if mat not in plan:
            problems.append('"%s": its material "%s" was not packed' % (o['name'], mat)); continue
        axis = (o.get('door') or {}).get('axis') or 'up'
        if axis not in AXES:
            problems.append('"%s": slide direction is not set' % o['name']); continue
        m = measure(z['pos'], axis)
        if m['flatness'] > 0.25:
            problems.append('"%s" is not a flat panel (it is as thick as it is wide): a door must be one slab' % o['name']); continue
        dist = float((o.get('door') or {}).get('distance') or 0.0) or D['open_fraction'] * m['travel_size']
        # same panel = same material, triangle count, sizes (to 2 cm) and travel: instances of one mesh differ by float
        # noise after their world transforms, so a hash of the coordinates would make every door its own type
        sig = hashlib.sha1(json.dumps([mat, int(len(z['pos'])), [round(m[k] / 0.02) for k in ('travel_size', 'across', 'thickness')],
                                       round(dist / 0.02)]).encode()).hexdigest()
        t = types.get(sig)
        if t is None:
            t = types[sig] = {'name': '%s_Door_%02d' % (job.piece_prefix, len(types) + 1), 'material': mat, 'm': m, 'z': z, 'distance': dist,
                              'doors': []}
        t['doors'].append(o['name'])
        placements.append({'door': o['name'], 'type': sig, 'pos': job.to_game(m['origin']), 'basis': [config_to_game_vec(v) for v in m['frame']],
                           'lean_deg': round(m['lean_deg'], 1)})
    if problems:
        raise StageError('Door problems: ' + '; '.join(problems))
    tpl_res, tpl_meta = _panel_template(job, clix, host)
    DP = job.prefix + '/Doors'
    steps = [cli.load(src)]
    tex_done = set()
    report = []
    for sig, t in types.items():
        m, z, name = t['m'], t['z'], t['name']
        rec = plan[t['material']]
        T = host.TEMPLATES[rec['template']]
        d = job.path('pkg', name)
        if os.path.isdir(d):
            shutil.rmtree(d)
        os.makedirs(d)
        F = m['frame']
        nrm = z['nrm'].astype(np.float64) @ F.T
        fbx.write(d + '/mesh.fbx', m['local'], nrm, z['uv'], material=config.safe(t['material']))
        text, nf = _obj_text(m['local'])
        with open(d + '/hull.obj', 'w') as f:
            f.write(text)
        try:
            r = subprocess.run([s['dotnet'], s['havok_tool'], 'import', 'both', '--hull=always', d + '/hull.obj', tpl_res, tpl_meta,
                                d + '/physics.res', d + '/physics.resmeta'], capture_output=True, text=True, env=cli.tool_env(job),
                               stdin=subprocess.DEVNULL, timeout=300)
        except subprocess.TimeoutExpired:
            raise StageError('The collision tool did not finish the door hull of %s within 5 minutes' % name)
        if r.returncode != 0 or not os.path.exists(d + '/physics.res'):
            raise StageError('The door hull of %s could not be built: %s' % (name, (r.stderr or r.stdout).strip()[-300:]))
        tex = []
        for sl in rec['slots']:
            fn = sl['texture'] + '.png'
            shutil.copyfile(job.path('tex', fn), d + '/' + fn)
            tex.append({'Param': sl['param'], 'Path': '%s/%s' % (job.short, sl['texture']), 'Guid': job.sg('tex/' + sl['texture']), 'File': fn})
            if sl['texture'] not in imported_tex and sl['texture'] not in tex_done:
                tex_done.add(sl['texture'])
                steps.append({'op': 'import-texture', 'file': job.path('tex', fn), 'as': '%s/%s' % (job.prefix, sl['texture']), 'template': sl['template']})
                steps.append({'op': 'bundle-add', 'ebx': '%s/%s' % (job.prefix, sl['texture']), 'bundle': job.bundle, 'with_resources': True})
        with open(d + '/physics.resmeta', 'rb') as f:
            meta = f.read()
        man = {'Version': 1, 'SourceGame': 'swbf2', 'Blueprint': '%s/Doors/%s' % (job.short, name), 'BlueprintGuid': job.sg('bp/door/' + name),
               'EntityType': None,
               'Mesh': {'Path': '%s/Doors/%s_Mesh' % (job.short, name), 'Guid': job.sg('mesh/door/' + name), 'Type': 'RigidMeshAsset',
                        'File': 'mesh.fbx', 'LodCount': 1, 'SectionNames': [['mesh']], 'SectionMaterials': [[0]],
                        'Materials': [{'Index': 0, 'ClassGuid': None, 'ShaderPath': T['shader'], 'ShaderGuid': T['guid'], 'Textures': tex}]},
               'Physics': {'AssetPath': '%s/Doors/%s_Physics_Win32' % (job.short, name), 'Guid': None,
                           'ResName': '%s/doors/%s_physics_win32' % (job.short.lower(), name.lower()), 'File': 'physics.res',
                           'ResType': host.PHYSICS_RES_TYPE, 'MetaHex': meta.hex()},
               'PartBoundingBoxes': [], 'Notes': ['door panel of %s, material %s' % (job.name, t['material'])]}
        with open(d + '/manifest.json', 'w', encoding='utf-8') as f:
            json.dump(man, f, indent=1)
        panel = '%s/%s' % (DP, name)
        PF = '%s/PF_%s' % (DP, name)
        t['prefab'] = PF
        half_travel = m['travel_size'] / 2.0
        steps += [
            {'op': 'port-import', 'file': d, 'as': panel, 'mesh_template': T['mesh'], 'bundle': job.bundle, 'mvdb': job.mvdb, 'max_lods': 1,
             'blueprint_template': D['panel_blueprint'], 'physics_template': D['panel_physics']},
            {'op': 'ebx-set-ref', 'ebx': panel + '_Mesh', 'prop': 'LodGroup', 'target': host.NO_CULLING_LOD},
            {'op': 'bundle-add', 'res': (panel + '_Mesh').lower() + '_mesh/blocks', 'bundle': job.bundle, 'on-error': 'continue'},
            {'op': 'ebx-set-ref', 'ebx': panel, 'prop': 'Schematics', 'target': 'null', 'on-error': 'continue'},
            {'op': 'ebx-clone', 'source': D['prefab'], 'as': PF},
            {'op': 'ebx-set', 'ebx': PF, 'prop': 'Name', 'value': PF}]
        for slot in D['leaf_slots']:
            steps.append({'op': 'ebx-set-ref', 'ebx': PF, 'prop': slot, 'target': panel})
        for fld in D['open_fields']:
            steps.append({'op': 'ebx-set', 'ebx': PF, 'prop': fld, 'value': 'Float32 %g' % round(t['distance'], 3)})
        for fld in D['light_fields']:
            steps.append({'op': 'ebx-set', 'ebx': PF, 'prop': fld, 'value': 'Vec4 (0,0,0,1)', 'on-error': 'continue'})
        for ax, v in zip('xyz', (round(half_travel, 3), round(m['across'] / 2 + 0.3, 3), D['trigger_reach'])):
            steps.append({'op': 'ebx-set', 'ebx': PF, 'prop': D['trigger_half'] + '.' + ax, 'value': v})
        for prop in D['trigger_trans']:
            steps.append({'op': 'ebx-set', 'ebx': PF, 'prop': prop + '.x', 'value': round(half_travel, 3)})
            steps.append({'op': 'ebx-set', 'ebx': PF, 'prop': prop + '.y', 'value': 0.0})
        for prop in D['sound_refs']:
            steps.append({'op': 'ebx-set-ref', 'ebx': PF, 'prop': prop, 'target': 'null'})
        for arr, i in D['sound_remove']:
            steps.append({'op': 'ebx-array-remove', 'ebx': PF, 'prop': arr, 'index': i})
        steps.append({'op': 'ebx-set-ref', 'ebx': PF, 'prop': 'Schematics', 'target': 'null'})
        steps.append({'op': 'ebx-dump', 'ebx': PF, 'deep': True, 'out': job.path('dumps', 'door_%s.json' % name)})
        for e in (PF, panel):
            steps.append({'op': 'bundle-add', 'ebx': e, 'bundle': job.bundle, 'with_resources': True, 'on-error': 'continue'})
        steps.append({'op': 'bundle-add', 'ebx': T['shader'], 'bundle': job.bundle, 'with_resources': True, 'on-error': 'continue'})
        report.append({'name': name, 'prefab': PF, 'material': t['material'], 'doors': t['doors'], 'travel_m': round(t['distance'], 3),
                       'size': [round(m['travel_size'], 3), round(m['across'], 3), round(m['thickness'], 3)], 'triangles': int(len(m['local'])),
                       'hull': (r.stdout.strip().splitlines() or [''])[-1][:160]})
    # the door logic and its class schematics: always the class + class_schematics PAIR
    for e in D['logic']:
        for a in (e, e.lower() + '_class', e.lower() + '_class_schematics'):
            steps.append({'op': 'bundle-add', 'ebx': a, 'bundle': job.bundle, 'with_resources': True, 'on-error': 'continue'})
    for idx, cg in D['registry']:
        steps.append({'op': 'ebx-array-add', 'ebx': job.registry, 'prop': 'Objects', 'target': D['logic'][idx], 'class_guid': cg})
    for p in placements:
        steps.append({'op': 'level-add', 'ebx': job.default_layer, 'blueprint': types[p['type']]['prefab'], 'pos': '%.4f,%.4f,%.4f' % tuple(p['pos']),
                      'rot': ','.join('%.6f' % v for row in p['basis'] for v in row)})
    steps.append({'op': 'ebx-dump', 'ebx': job.default_layer, 'deep': True, 'out': job.path('dumps', 'doors_Default.json')})
    steps.append(cli.save(out))
    r = clix.run('doors', steps)
    # read-back: one placement per door, each prefab holds its panel in both leaf slots, no sound left
    with open(job.path('dumps', 'doors_Default.json'), encoding='utf-8') as f:
        txt = f.read()
    placed = {t['prefab']: txt.count('"__path": "%s"' % t['prefab']) for t in types.values()}
    bad = []
    for t in types.values():
        if placed[t['prefab']] < len(t['doors']):
            bad.append('%s: %d placements for %d doors' % (t['name'], placed[t['prefab']], len(t['doors'])))
        with open(job.path('dumps', 'door_%s.json' % t['name']), encoding='utf-8') as f:
            pf = f.read()
        panel = '%s/%s' % (DP, t['name'])
        if pf.count('"__path": "%s"' % panel) < 2:
            bad.append('%s: the panel is not in both leaf slots' % t['name'])
        # the removed sound entity stays in the partition as an orphan (as in the ports whose doors work in game); what
        # must be gone is its place in Objects and its connections
        pj = json.loads(pf)
        if any('Sound' in (ob.get('__type') or '') for ob in pj.get('Objects', []) if isinstance(ob, dict)):
            bad.append('%s: a sound object is still among the prefab\'s objects' % t['name'])
        for arr in ('EventConnections', 'PropertyConnections', 'LinkConnections'):
            for cn in pj.get(arr, []):
                if 'Sound' in json.dumps([cn.get('Source', {}).get('__type'), cn.get('Target', {}).get('__type')]):
                    bad.append('%s: a %s still goes to a sound object' % (t['name'], arr[:-1])); break
    info = {'doors': len(placements), 'types': report, 'placements': [{k: (list(v) if k == 'pos' else v) for k, v in p.items()} for p in placements],
            'errors': r.errors[:10]}
    with open(job.work + '/doors.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    if bad or r.errors:
        raise StageError('The doors did not come out as planned: %s  [log: %s]' % ('; '.join(bad + r.errors[:3])[:600], r.log_path))
    log('doors: %d doors of %d type(s): %s' % (len(placements), len(types), ', '.join(
        '%s x%d (%.2f x %.2f m, opens %.2f m)' % (t['name'], len(t['doors']), t['m']['travel_size'], t['m']['across'], t['distance']) for t in types.values())))
    return {'doors': len(placements), 'types': len(types)}


def config_to_game_vec(v):
    return [float(v[0]), float(v[2]), float(-v[1])]
