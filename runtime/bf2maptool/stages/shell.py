"""Stage `shell`: the new level (a clone of the host's game-mode shell under the map's own name) with everything of
the host that would show, collide or block the load taken out.

  1 level create                      -> projects/shell_0 (+ the facts the log states: navmesh stream, play space, Kyber)
  2 list + deep-dump every partition  -> shell.json, dumps/shell_0/
  3 host clean-up                     -> projects/shell_1
  4 prune (a NEW process: removed instances stay in memory until written), closure audit -> projects/shell
  5 validate, dump again (dumps/shell/) and CHECK by read-back that nothing refers to a dropped host layer

Every later stage reads dumps/shell/ instead of trusting what a command said it did.
"""
import json
import os
import re

from .. import audit, cli, config, hosts, shipped
from ..runner import StageError


def inputs(job):
    host = hosts.get(job.host)
    return {'name': job.name, 'display': job.display, 'planet': job.planet, 'description': job.description, 'host': job.host,
            'create': host.create_args(job), 'v': 2, 'menu_picture': _picture_hash(job)}


def _picture_hash(job):
    mp = (job.map.get('map') or {}).get('menu_picture')
    p = job.src(mp['file']) if mp else None
    return config.file_hash(p) if p and os.path.exists(p) else None


def outputs(job):
    return [job.project('shell'), job.work + '/shell.json']


def list_assets(clix, name, project, prefix):
    r = clix.direct(name, ['--json', 'ls', prefix], project=project)
    out = {'partitions': [], 'registries': [], 'mvdbs': [], 'other': []}
    for line in r.text.splitlines():
        line = line.strip()
        if not line.startswith('{'):
            continue
        try:
            j = json.loads(line)
        except ValueError:
            continue
        f, t = j.get('file'), j.get('type')
        if not f:
            continue
        if t in ('LevelData', 'SubWorldData', 'LayerData'):
            out['partitions'].append(f)
        elif t == 'NetworkRegistryAsset':
            out['registries'].append(f)
        elif t == 'MeshVariationDatabase':
            out['mvdbs'].append(f)
        else:
            out['other'].append([f, t])
    return out


def ffmpeg_exe():
    """the ffmpeg FrostyCLI encodes the loading picture with: $FFMPEG, else the one imageio-ffmpeg ships (Install Python
    packages adds it), else None (FrostyCLI then looks on PATH itself)"""
    e = os.environ.get('FFMPEG')
    if e and os.path.isfile(e):
        return e
    try:
        import imageio_ffmpeg
        return imageio_ffmpeg.get_ffmpeg_exe()
    except Exception:                                       # noqa: BLE001 - not installed / no binary for this machine
        return None


def loading_screen(job, host, clix, project, log, info):
    """the level's own loading screen from the picture the export rendered (a 10 s slow push-in, VP8 WebM): FrostyCLI
    `level loading-screen`. Any failure keeps the host level's screen (said in the log and shell.json). -> project"""
    mp = (job.map.get('map') or {}).get('menu_picture')
    if not mp:
        info['loading_screen'] = 'host (no menu picture in the export)'
        return project
    img = job.src(mp['file'])
    if not os.path.exists(img):
        info['loading_screen'] = 'host (picture file missing)'
        return project
    ff = ffmpeg_exe()
    out = job.project('shell_0ls')
    if os.path.exists(out):
        os.remove(out)
    r = clix.direct('shell_loading', ['level', 'loading-screen', job.level, '--like', host.LEVEL, '--image', job.native(img), '--seconds', '10'],
                    project=project, save_as=out, check=False, env={'FFMPEG': ff} if ff else None)
    if r.rc != 0 or not os.path.exists(out):
        tail = ' | '.join(l.strip() for l in r.text.strip().splitlines()[-2:])
        log('shell: WARNING the loading picture could not be added (%s) - the level keeps the host\'s loading screen%s' % (
            tail[:300], '' if ff else '; no ffmpeg found: press "Install Python packages" in the add-on preferences'))
        info['loading_screen'] = 'host (failed: %s)' % tail[:200]
        return project
    info['loading_screen'] = 'own: %s from camera "%s"' % (mp['file'], mp.get('camera', '?'))
    log('shell: loading screen made from the picture taken with camera "%s"' % mp.get('camera', '?'))
    return out


def run(job, log):
    host = hosts.get(job.host)
    clix = cli.Cli(job, log)
    p0, p1, p2 = job.project('shell_0'), job.project('shell_1'), job.project('shell')
    # the level name must not be one the game ships (the list is read from the game once and kept in the work folder)
    levels = shipped.load(job) or shipped.refresh(job, clix)
    taken = shipped.clash(job, levels) if levels else None
    if taken:
        raise StageError('The level name "%s" is taken by a level the game ships (%s). Choose another level name.' % (job.name, taken))
    for p in (p0, p1, p2):
        if os.path.exists(p):
            os.remove(p)
    # 1 create
    r = clix.direct('shell_create', ['--save-as', p0] + host.create_args(job), check=False)
    if r.rc != 0 or not os.path.exists(p0):
        if re.search(r'already exists', r.text, re.I):
            raise StageError('A level named "%s" already exists in the game or in another installed mod. Choose another level name.' % job.name)
        tail = ' | '.join(l.strip() for l in r.text.strip().splitlines()[-4:])
        raise StageError('The level could not be created: %s  [log: %s]' % (tail[:600], r.log_path))
    info = {'host': job.host}
    m = re.search(r'Skirmish navmesh stream\(s\): ([0-9a-f-]{36})', r.text)
    info['navmesh_stream'] = m.group(1) if m else None
    m = re.search(r'play space x ([-\d.]+)\.\.([-\d.]+), z ([-\d.]+)\.\.([-\d.]+)', r.text)
    info['host_play_space'] = [float(x) for x in m.groups()] if m else None
    m = re.search(r'Kyber: .* declared for (.*)$', r.text, re.M)
    info['kyber_modes'] = [x.strip() for x in m.group(1).split(',')] if m else []
    m = re.search(r'^\s*load: hookspawn (\S+) (\S+)', r.text, re.M)
    info['load'] = [m.group(1), m.group(2)] if m else None
    if not info['navmesh_stream']:
        raise StageError('level create did not report the Skirmish navmesh stream  [log: %s]' % r.log_path)
    want = {'SkirmishBlast', 'Blast', 'HeroesVersusVillains'}
    if not want.issubset(set(info['kyber_modes'])):
        raise StageError('The new level was not declared for %s (declared: %s)  [log: %s]' % (
            ', '.join(sorted(want)), ', '.join(info['kyber_modes']) or 'nothing', r.log_path))
    p0 = loading_screen(job, host, clix, p0, log, info)
    # 2 list + dump
    assets = list_assets(clix, 'shell_ls', p0, job.level_dir)
    if not assets['partitions'] or job.level not in assets['partitions']:
        raise StageError('The new level has no partitions under %s  [log: %s]' % (job.level_dir, job.path('logs', 'shell_ls.log')))
    info.update(partitions=assets['partitions'], registries=assets['registries'], mvdbs=assets['mvdbs'])
    load0 = audit.dump_partitions(clix, 'shell_dump0', p0, assets['partitions'], job.path('dumps', 'shell_0'))
    # 3 clean-up
    steps, counts = host.cleanup_steps(job, load0)
    clix.run('shell_cleanup', [cli.load(p0)] + steps + [cli.save(p1)])
    # 4 prune in a new process, then closure audit
    pr = job.project('shell_1p')
    clix.run('shell_prune', [cli.load(p1)] + host.prune_steps(job, assets['partitions'], assets['registries']) + [cli.save(pr)])
    cl = audit.closure(clix, 'shell_closure', pr, p2, job.level)
    info['closure'] = cl
    if not cl['ok']:
        raise StageError('The bare level shell does not close: unresolved %s, missing %s, root-late %s, stage-late %s  [log: %s]' % (
            cl['unresolved'], cl['auditMissing'], cl['auditRootLate'], cl['auditStageLate'], job.path('logs', 'shell_closure.log')))
    # 5 validate + read-back
    val = audit.validate(clix, 'shell_validate', p2, job.level)
    info['validate'] = val['result']
    if not val['ok']:
        raise StageError('The bare level shell does not validate: %s  [log: %s]' % (val['result'] or val['issues'][:3], val['log']))
    load = audit.dump_partitions(clix, 'shell_dump', p2, assets['partitions'], job.path('dumps', 'shell'))
    left = {p.split('/')[-1]: host.leftover_host_refs(load(p.split('/')[-1])) for p in assets['partitions']
            if os.path.exists(job.path('dumps', 'shell', p.split('/')[-1] + '.json'))}
    left = {k: v for k, v in left.items() if v}
    doors_left = sum(1 for lay in host.DOOR_LAYERS for o in host.layer_objects(load(lay)) if o['bp_file'] in host.DOOR_BLUEPRINTS)
    before = {lay: len(host.layer_objects(load0(lay))) for lay in host.CLEAR_LAYERS}
    after = {lay: len(host.layer_objects(load(lay))) for lay in host.CLEAR_LAYERS}
    info['readback'] = {'host_refs_left': left, 'host_doors_left': doors_left, 'layer_objects_before': before, 'layer_objects_after': after,
                        'removed': counts}
    if left or doors_left:
        raise StageError('Host content is still referenced after the clean-up: %s, host doors left %d' % (left, doors_left))
    with open(job.work + '/shell.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    for p in (p0, p1, pr):
        if os.path.exists(p):
            os.remove(p)
    log('shell: %d partitions, %d registries; removed %s; layer objects %s -> %s; closure %s; %s' % (
        len(assets['partitions']), len(assets['registries']), counts, before, after,
        {k: cl[k] for k in ('unresolved', 'auditMissing', 'auditRootLate', 'auditStageLate')}, val['result']))
    return {'partitions': len(assets['partitions']), 'registries': len(assets['registries']), 'removed': counts,
            'navmesh_stream': info['navmesh_stream'], 'kyber_modes': info['kyber_modes'], 'closure_ok': cl['ok'], 'validate': val['result']}
