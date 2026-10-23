"""Stages `import` and `place`.

import   packed textures (each with the shipped template texture of its slot, which gives block format, sRGB flag and
         mip chain) and one `port import` per piece on the single-material template mesh of its material class.
         Pieces fail for two reasons that are not the piece's fault: the random "EBX clone failed", and an importer
         that refuses some files depending on their exact bytes (see fbx.VARIANTS). Failed pieces are imported again
         in a fresh process under <name>_R<n>, each time written in the next lossless encoding, at most RETRIES
         rounds; a piece that still fails stops the build by name with the importer's message.
place    per piece: no-culling LOD group, shader-block depot bundled where the piece has no physics (an unbundled depot
         draws nothing), emissive strength, weapon-trace shape index 1 on both bodies; then ONE static group for the
         render members and one collision entity per solid piece.
Both end with a read-back of what the level now holds (import: blueprints per log; place: group member census).
"""
import json
import os
import shutil

from .. import audit, cli, config, fbx, hosts, photometry
from ..runner import StageError

RETRIES = 6


def _load(job):
    with open(job.path('pieces', 'pieces.json'), encoding='utf-8') as f:
        pieces = json.load(f)['pieces']
    with open(job.path('tex', 'plan.json'), encoding='utf-8') as f:
        plan = json.load(f)
    with open(job.path('col', 'report.json'), encoding='utf-8') as f:
        col = json.load(f)
    return pieces, plan, col


def inputs(job):
    host = hosts.get(job.host)
    return {'pieces': config.file_hash(job.path('pieces', 'pieces.json')), 'plan': config.file_hash(job.path('tex', 'plan.json')),
            'col': config.file_hash(job.path('col', 'report.json')), 'templates': host.TEMPLATES, 'v': 3}


def outputs(job):
    return [job.project('import'), job.work + '/import.json']


def _link(src, dst):
    if os.path.exists(dst):
        os.remove(dst)
    try:
        os.link(src, dst)
    except OSError:
        shutil.copyfile(src, dst)


def package(job, host, p, mat, has_col):
    """pkg/<piece>/: manifest.json + mesh.fbx + the piece's textures + physics.res/.resmeta"""
    T = host.TEMPLATES[mat['template']]
    name = p['name']
    d = job.path('pkg', name)
    if os.path.isdir(d):
        shutil.rmtree(d)
    os.makedirs(d)
    _link(job.path('pieces', p['file']), d + '/mesh.fbx')
    tex = []
    for s in mat['slots']:
        fn = s['texture'] + '.png'
        _link(job.path('tex', fn), d + '/' + fn)
        tex.append({'Param': s['param'], 'Path': '%s/%s' % (job.short, s['texture']), 'Guid': job.sg('tex/' + s['texture']), 'File': fn})
    man = {'Version': 1, 'SourceGame': 'swbf2', 'Blueprint': '%s/%s' % (job.short, name), 'BlueprintGuid': job.sg('bp/' + name),
           'EntityType': None,
           'Mesh': {'Path': '%s/%s_Mesh' % (job.short, name), 'Guid': job.sg('mesh/' + name), 'Type': 'RigidMeshAsset', 'File': 'mesh.fbx',
                    'LodCount': 1, 'SectionNames': [['mesh']], 'SectionMaterials': [[0]],
                    'Materials': [{'Index': 0, 'ClassGuid': None, 'ShaderPath': T['shader'], 'ShaderGuid': T['guid'], 'Textures': tex}]},
           'Physics': None, 'PartBoundingBoxes': [],
           'Notes': ['piece of %s, material %s, class %s, template %s' % (job.name, p['material'], mat['class'], mat['template'])]}
    if has_col:
        shutil.copyfile(job.path('col', name + '.res'), d + '/physics.res')
        shutil.copyfile(job.path('col', name + '.resmeta'), d + '/physics.resmeta')
        with open(d + '/physics.resmeta', 'rb') as f:
            meta = f.read()
        man['Physics'] = {'AssetPath': '%s/%s_Physics_Win32' % (job.short, name), 'Guid': None,
                          'ResName': '%s/%s_physics_win32' % (job.short.lower(), name.lower()), 'File': 'physics.res',
                          'ResType': host.PHYSICS_RES_TYPE, 'MetaHex': meta.hex()}
    with open(d + '/manifest.json', 'w', encoding='utf-8') as f:
        json.dump(man, f, indent=1)
    return d


def import_step(job, host, b, as_name):
    T = host.TEMPLATES[b['template']]
    st = {'op': 'port-import', 'file': job.native(b['dir']), 'as': '%s/%s' % (job.prefix, as_name), 'mesh_template': T['mesh'], 'bundle': job.bundle,
          'mvdb': job.mvdb, 'max_lods': 1, 'on-error': 'continue'}
    if b['physics']:
        st['blueprint_template'] = host.BLUEPRINT_PHYSICS
        st['physics_template'] = host.PHYSICS_TEMPLATE
    else:
        st['blueprint_template'] = host.BLUEPRINT_NO_PHYSICS
        st['no_physics'] = True
    return st


def run_import(job, log):
    host = hosts.get(job.host)
    clix = cli.Cli(job, log)
    pieces, plan, col = _load(job)
    build, textures = [], {}
    for p in pieces:
        mat = plan[p['material']]
        phys = p['name'] in col
        d = package(job, host, p, mat, phys)
        for s in mat['slots']:
            textures[s['texture']] = s['template']
        build.append({'name': p['name'], 'as': p['name'], 'dir': d, 'material': p['material'], 'class': mat['class'], 'template': mat['template'],
                      'physics': phys, 'centre': p['centre'], 'tris': p['tris'],
                      'emission': (mat.get('emission_strength', 0.0) * mat.get('emission_screen', 1.0)) if 'emission_strength' in mat else None,
                      'holo': mat.get('holo')})
    steps = [cli.load(job.project('shaderdb'))]
    for t, tpl in sorted(textures.items()):
        steps.append({'op': 'import-texture', 'file': job.path('tex', t + '.png'), 'as': '%s/%s' % (job.prefix, t), 'template': tpl})
        steps.append({'op': 'bundle-add', 'ebx': '%s/%s' % (job.prefix, t), 'bundle': job.bundle, 'with_resources': True})
    deps = list(host.COMMON_DEPS)
    for tn in sorted({b['template'] for b in build}):
        deps += [host.TEMPLATES[tn]['shader']] + list(host.TEMPLATES[tn]['deps'])
    for dep in dict.fromkeys(deps):
        steps.append({'op': 'bundle-add', 'ebx': dep, 'bundle': job.bundle, 'with_resources': True, 'on-error': 'continue'})
    for b in build:
        steps.append(import_step(job, host, b, b['as']))
    cur = job.project('import_r0')
    steps.append(cli.save(cur))
    # pieces fail one by one ('on-error': continue) and the run then exits non-zero: the saved project and the log decide
    r = clix.run('import_r0', steps, check=False)
    if not os.path.exists(cur):
        raise StageError('The import run did not save its project  [log: %s]' % r.log_path)
    tex_err = [e for e in r.errors if 'import-texture' in e]
    ok = r.blueprints(job.prefix)
    rounds = [{'round': 0, 'imported': len(ok), 'transient': len(r.transient)}]
    for n in range(1, RETRIES + 1):
        miss = [b for b in build if '%s/%s' % (job.prefix, b['as']) not in ok]
        if not miss:
            break
        said = sorted({l.split('failed:', 1)[1].strip()[:70] for l in r.text.splitlines() if 'port import:' in l and 'failed:' in l})
        log('import: %d piece(s) not imported (%s) - round %d, encoding %d' % (len(miss), ' | '.join(said)[:200], n, n % len(fbx.VARIANTS)))
        steps = [cli.load(cur)]
        for b in miss:
            # a refused import reverts its assets but can leave the mesh resource and its shader-block depot behind
            # (added, in no bundle: the lint check 'added-res-unbundled'): take them out before the next try
            old = ('%s/%s' % (job.prefix, b['as'])).lower()
            for res in (old + '_mesh', old + '_mesh_mesh/blocks', old + '_physics_win32'):
                steps.append({'op': 'cli', 'args': ['res', 'revert', res], 'on-error': 'continue'})
            b['as'] = '%s_R%d' % (b['name'], n)
            b['encoding'] = n % len(fbx.VARIANTS)
            fbx.rewrite(job.path('pieces', b['name'] + '.fbx'), os.path.join(b['dir'], 'mesh.fbx'), b['encoding'])
            steps.append(import_step(job, host, b, b['as']))
        nxt = job.project('import_r%d' % n)
        steps.append(cli.save(nxt))
        r = clix.run('import_r%d' % n, steps, check=False)
        if not os.path.exists(nxt):
            raise StageError('Import retry round %d did not save its project  [log: %s]' % (n, r.log_path))
        ok |= r.blueprints(job.prefix)
        rounds.append({'round': n, 'tried': len(miss), 'imported': sum(1 for b in miss if '%s/%s' % (job.prefix, b['as']) in ok)})
        os.remove(cur)
        cur = nxt
    miss = [b for b in build if '%s/%s' % (job.prefix, b['as']) not in ok]
    if miss:
        said = sorted({l.split('failed:', 1)[1].strip()[:120] for l in r.text.splitlines() if 'port import:' in l and 'failed:' in l})
        raise StageError('%d piece(s) could not be imported after %d tries in %d encodings (%s). The importer says: %s  [log: %s]' % (
            len(miss), RETRIES + 1, len(fbx.VARIANTS), ', '.join(b['name'] for b in miss[:8]), ' | '.join(said)[:400], r.log_path))
    final = job.project('import')
    if os.path.exists(final):
        os.remove(final)
    os.replace(cur, final)
    with open(job.work + '/import.json', 'w', encoding='utf-8') as f:
        json.dump({'pieces': build, 'textures': textures, 'rounds': rounds}, f, indent=1)
    from collections import Counter
    enc = Counter(b.get('encoding', 0) for b in build)
    summary = {'pieces': len(build), 'with_physics': sum(b['physics'] for b in build), 'textures': len(textures), 'encodings': dict(enc),
               'templates': dict(Counter(b['template'] for b in build)), 'rounds': rounds, 'texture_errors': tex_err[:5]}
    if tex_err:
        raise StageError('Texture import reported errors: %s  [log: %s]' % (' | '.join(tex_err[:3])[:500], job.path('logs', 'import_r0.log')))
    log('import: %(pieces)d pieces (%(with_physics)d with collision), %(textures)d textures, templates %(templates)s, rounds %(rounds)s' % summary)
    return summary


# ------------------------------------------------------------------ place
def place_inputs(job):
    host = hosts.get(job.host)
    return {'offset': job.offset(), 'emissive': photometry.emissive_per_unit(job, host), 'v': 3}


def place_outputs(job):
    return [job.project('place'), job.work + '/place.json']


def layer_file(job, name, subset):
    d = job.path('layers', name)
    os.makedirs(d, exist_ok=True)
    with open(d + '/layer.json', 'w', encoding='utf-8') as f:
        json.dump({'Version': 1, 'SourceGame': 'swbf2', 'Level': job.short, 'Placements': [
            {'Blueprint': '%s/%s' % (job.prefix, b['as']), 'Guid': None, 'Group': 0, 'Member': -1, 'Instance': -1, 'Source': 'ebx',
             'Right': [1, 0, 0], 'Up': [0, 1, 0], 'Forward': [0, 0, 1], 'Trans': [b['centre'][0], b['centre'][2], -b['centre'][1]]}
            for b in subset]}, f, indent=1)
    return d


EMISSIVE_CEILING = 60000.0


def emissive_scale(job, host):
    return photometry.emissive_per_unit(job, host)


def group_members(dump):
    """[(group guid, [mesh path per MemberDatas entry])] of a deep layer dump"""
    out = []

    def walk(o):
        if isinstance(o, dict):
            if o.get('__type') == 'StaticModelGroupEntityData':
                out.append((o.get('__guid'), [((m.get('MeshAsset') or {}).get('__path') or '') for m in o.get('MemberDatas', [])]))
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
    walk(dump)
    return out


def run_place(job, log):
    host = hosts.get(job.host)
    clix = cli.Cli(job, log)
    with open(job.work + '/import.json', encoding='utf-8') as f:
        build = json.load(f)['pieces']
    K = emissive_scale(job, host)
    clamped = 0
    steps = [cli.load(job.project('import'))]
    for b in build:
        mesh = '%s/%s_Mesh' % (job.prefix, b['as'])
        bp = '%s/%s' % (job.prefix, b['as'])
        steps.append({'op': 'ebx-set-ref', 'ebx': mesh, 'prop': 'LodGroup', 'target': host.NO_CULLING_LOD})
        if not b['physics']:
            steps.append({'op': 'bundle-add', 'res': mesh.lower() + '_mesh/blocks', 'bundle': job.bundle})
        ep = host.TEMPLATES[b['template']].get('emissive_param')
        if ep and b.get('emission') is not None:
            # the surface's brightness is a half-precision number in the engine: above 65,504 it overflows and the surface
            # turns BLACK (LightMatch lm3: strength 1 showed, strength 5 at daylight exposure did not). Stay under it.
            mult = min(b['emission'] * K, EMISSIVE_CEILING)
            clamped += int(b['emission'] * K > EMISSIVE_CEILING)
            steps.append({'op': 'material-set', 'ebx': mesh, 'index': 0, 'prop': ep, 'value': '%g,0,0,0' % round(mult, 2)})
        T = host.TEMPLATES[b['template']]
        params = dict(T.get('params') or {})
        if T.get('speed_param') and (b.get('holo') or {}).get('tiling'):
            tl = b['holo']['tiling']
            params['UV_Coord'] = '%g,%g,0,0' % (round(float(tl[0]), 4), round(float(tl[1]), 4))
        for prop, val in params.items():
            steps.append({'op': 'material-set', 'ebx': mesh, 'index': 0, 'prop': prop, 'value': val, 'add_missing': True})
        if T.get('speed_param'):
            sp = (b.get('holo') or {}).get('speed') or [0.0, 0.0]
            steps.append({'op': 'material-set', 'ebx': mesh, 'index': 0, 'prop': T['speed_param'],
                          'value': '%g,%g,0,0' % (round(float(sp[0]), 4), round(float(sp[1]), 4)), 'add_missing': True})
        if b['physics']:
            for body in (0, 1):
                steps.append({'op': 'ebx-set', 'ebx': bp, 'prop': 'Object.Components[1].PhysicsBodies[%d].RaycastRootShapeIndex' % body, 'value': 1})
            steps.append({'op': 'ebx-set-ref', 'ebx': bp, 'prop': 'Schematics', 'target': 'null', 'on-error': 'continue'})
    steps.append({'op': 'level-add-group', 'ebx': job.default_layer, 'template': host.GROUP_TEMPLATE, 'template_group': 0})
    o = '%.2f,%.2f,%.2f' % job.offset()
    ents = [b for b in build if b['physics']]
    if ents:
        steps.append({'op': 'port-import-layer', 'file': layer_file(job, 'entities', ents), 'into': job.default_layer, 'registry': job.registry,
                      'no_bundle': True, 'offset': o, 'mvdb': job.mvdb})
    steps.append({'op': 'port-import-layer', 'file': layer_file(job, 'members', build), 'into': job.default_layer, 'group': '0', 'as_members': True,
                  'no_collision': True, 'offset': o, 'bundle': job.bundle, 'mvdb': job.mvdb})
    out = job.project('place')
    steps.append({'op': 'ebx-dump', 'ebx': job.default_layer, 'deep': True, 'out': job.path('dumps', 'place_Default.json')})
    steps.append(cli.save(out))
    r = clix.run('place', steps)
    # read-back: the group must draw exactly the meshes the plan says, one member per piece
    with open(job.path('dumps', 'place_Default.json'), encoding='utf-8') as f:
        groups = group_members(json.load(f))
    want = sorted(('%s/%s_Mesh' % (job.prefix, b['as'])).lower() for b in build)
    have = sorted(m.lower() for g, ms in groups for m in ms)
    info = {'groups': [(g, len(ms)) for g, ms in groups], 'members': len(have), 'pieces': len(build), 'entities': len(ents),
            'offset': list(job.offset()), 'emissive_scale': K, 'emissive_at_ceiling': clamped, 'errors': r.errors[:10]}
    with open(job.work + '/place.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    if have != want:
        missing = sorted(set(want) - set(have))[:6]
        extra = sorted(set(have) - set(want))[:6]
        raise StageError('The static group does not hold the planned pieces: %d members for %d pieces; missing %s; unexpected %s' % (
            len(have), len(want), missing, extra))
    if r.errors:
        raise StageError('Placement reported errors: %s  [log: %s]' % (' | '.join(r.errors[:3])[:500], r.log_path))
    log('place: %d members in %d group(s), %d collision entities, offset %s, emissive scale %g' % (len(have), len(groups), len(ents), o, K))
    return info
