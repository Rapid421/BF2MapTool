"""Post-build censuses read from the built project (never assumed from what a stage meant to write).

depots(clix, job, project)
    Every placed piece's shader-block depot (`mesh depot-dump`), against the import record:
      * the depot exists (a piece without one draws nothing, or with another mesh's parameters);
      * PARAMETER census: all pieces of one material carry the same texture parameters, and those differ from the
        template mesh's own in at least as many slots as the tool fills (a piece that lost its overrides - `port regeo`
        drops them - shows the template's textures);
      * SURFACE census: the surface permutations a piece draws with (`rvmGuids`) are ones the shipped template mesh
        itself uses, i.e. ones the level's shader database is known to hold programs for (the template's own
        database is where its surface is taken from). Recorded per template; enforced where the template's depot
        could be read.
    Returns the census; `problems` is the list a build must not pass with.
"""
import os
import re

from . import cli, hosts

TEXTURE_PARAM = 'AD0ABFD3'          # type hash of a texture parameter in a persistent parameter block
CHUNK = 60


def _parse(text):
    out, cur = {}, None
    for line in text.splitlines():
        m = re.match(r'^== (\S+)\s+resources=(\d+)', line)
        if m:
            cur = out.setdefault(m.group(1).lower(), {'tex': {}, 'rvm': set(), 'blocks': int(m.group(2))})
            continue
        if cur is None:
            continue
        if 'ShaderPersistentParamDbBlock' in line:
            for nh, th, val in re.findall(r'\{([0-9A-F]{8})/([0-9A-F]{8}):([0-9A-F]*)\}', line):
                if th == TEXTURE_PARAM:
                    cur['tex'][nh] = val
        elif 'ShaderBlockMeshVariationEntry' in line:
            m = re.search(r'rvmGuids=\[([^\]]*)\]', line)
            if m:
                cur['rvm'].update(g.strip() for g in m.group(1).split(',') if g.strip())
    return out


def res_names(job, build):
    host = hosts.get(job.host)
    piece_res = {b['as']: ('%s/%s_mesh_mesh/blocks' % (job.prefix, b['as'])).lower() for b in build}
    tpl_res = {t: (T['mesh'] + '_mesh/blocks').lower() for t, T in host.TEMPLATES.items() if any(b.get('template') == t for b in build)}
    return host, piece_res, tpl_res


def depots(clix, job, project, build, name='audit_depots'):
    host, piece_res, tpl_res = res_names(job, build)
    names = sorted(piece_res.values()) + sorted(tpl_res.values())
    dd = job.path('dumps', 'depots')
    os.makedirs(dd, exist_ok=True)
    for f in os.listdir(dd):
        os.remove(os.path.join(dd, f))
    steps = [cli.load(project)]
    for i in range(0, len(names), CHUNK):       # one res not found ends a call: templates go one by one, pieces in groups
        steps.append({'op': 'cli', 'args': ['mesh', 'depot-dump'] + names[i:i + CHUNK] + ['--out', '%s/d%04d.txt' % (dd, i // CHUNK)], 'on-error': 'continue'})
    r = clix.run(name, steps, check=False)
    got = {}
    for f in sorted(os.listdir(dd)):
        with open(os.path.join(dd, f), encoding='utf-8', errors='replace') as fh:
            got.update(_parse(fh.read()))
    missing_names = [n for n in names if n not in got]
    if missing_names:                           # a chunk that stopped on one missing res: read the rest one by one
        steps = [cli.load(project)]
        for k, n in enumerate(missing_names):
            steps.append({'op': 'cli', 'args': ['mesh', 'depot-dump', n, '--out', '%s/s%05d.txt' % (dd, k)], 'on-error': 'continue'})
        clix.run(name + '_single', steps, check=False)
        for f in sorted(os.listdir(dd)):
            if f.startswith('s'):
                with open(os.path.join(dd, f), encoding='utf-8', errors='replace') as fh:
                    got.update(_parse(fh.read()))
    res = analyse(host, build, piece_res, tpl_res, got)
    res['log'] = r.log_path
    return res


def analyse(host, build, piece_res, tpl_res, got):
    """the census proper, from parsed depot dumps (`got`: res name -> {'tex', 'rvm'})"""
    problems = []
    no_depot = sorted(a for a, n in piece_res.items() if n not in got)
    if no_depot:
        problems.append('%d piece(s) have no shader-block depot in the built project (first: %s)' % (len(no_depot), ', '.join(no_depot[:4])))
    tpl = {t: got.get(n) for t, n in tpl_res.items()}
    by_mat, surfaces = {}, {}
    for b in build:
        d = got.get(piece_res[b['as']])
        if d is None:
            continue
        by_mat.setdefault((b['material'], b.get('template')), []).append((b['as'], d))
        surfaces.setdefault(b.get('template'), {}).setdefault(tuple(sorted(d['rvm'])), []).append(b['as'])
    mixed, as_template, odd_surface = [], [], []
    for (mat, t), lst in sorted(by_mat.items()):
        variants = {}
        for a, d in lst:
            variants.setdefault(tuple(sorted(d['tex'].items())), []).append(a)
        if len(variants) > 1:
            minority = sorted(variants.values(), key=len)[0]
            mixed.append('%s: %d parameter sets over %d pieces (e.g. %s)' % (mat, len(variants), len(lst), minority[0]))
        T = host.TEMPLATES.get(t)
        if T is not None and tpl.get(t) is not None:
            base = tpl[t]['tex']
            for a, d in lst:
                differ = sum(1 for k, v in d['tex'].items() if base.get(k) != v)
                if differ < len(T['slots']):
                    as_template.append('%s (%d of %d textures its own)' % (a, differ, len(T['slots'])))
            for a, d in lst:
                if not d['rvm'] or not d['rvm'] <= tpl[t]['rvm']:
                    odd_surface.append('%s (template %s)' % (a, t))
    if mixed:
        problems.append('pieces of one material carry different texture parameters: ' + '; '.join(mixed[:4]))
    if as_template:
        problems.append('%d piece(s) draw with their template\'s own textures (their material parameters are gone): %s' % (
            len(as_template), ', '.join(as_template[:4])))
    if odd_surface:
        problems.append('%d piece(s) draw with a surface permutation their template mesh does not use (no program for it is known in the '
                        'level\'s shader database): %s' % (len(odd_surface), ', '.join(odd_surface[:4])))
    return {'pieces': len(build), 'depots_read': len(build) - len(no_depot), 'materials': len(by_mat),
            'template_depots_read': {t: v is not None for t, v in tpl.items()},
            'texture_parameters': {'%s|%s' % k: len(v[0][1]['tex']) for k, v in sorted(by_mat.items())},
            'surfaces': {str(t): {', '.join(k) or '(none)': len(v) for k, v in s.items()} for t, s in surfaces.items()},
            'template_surfaces': {t: sorted(v['rvm']) for t, v in tpl.items() if v is not None},
            'mixed': mixed, 'as_template': as_template[:50], 'odd_surface': odd_surface[:50], 'problems': problems}
