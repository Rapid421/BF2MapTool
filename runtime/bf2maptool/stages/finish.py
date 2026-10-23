"""Stages `audit` and `mod`.

audit  dependency closure with audit (0 unresolved / 0 missing / 0 root-late / 0 stage-late), with the root-late repair
       loop (a root member's import that resolves only in a later sub-world bundle is added to the root - class
       schematics ONLY together with their class asset: a lone *_class_schematics crashes the client on load);
       then `level validate`, `mod lint` and the post-build checks, each read from the built project, not assumed.
mod    the .fbmod.
"""
import json
import os
import re

from .. import audit, census, chain, cli
from ..runner import StageError
from .importer import group_members

ROOT_LATE_ROUNDS = 5


def inputs(job):       # (the audit also depends on whether a bake is part of the build: the dependency list says so)
    return {'supremacy': job.wants('supremacy'), 'v': 3}


def outputs(job):
    return [job.project('audit'), job.work + '/audit.json']


def schematics_pairs(names):
    """class-schematics assets listed without their class asset"""
    low = {n.lower() for n in names}
    sch = sorted(n for n in low if n.endswith('_class_schematics'))
    return sch, [n for n in sch if n[:-len('_schematics')] not in low]


def with_partner(target):
    t = target.strip()
    low = t.lower()
    if low.endswith('_class_schematics'):
        return [t[:-len('_schematics')], t]
    if low.endswith('_class'):
        return [t, t + '_schematics']
    return [t]


def _closure(clix, job, src, level, bundle, tag):
    """closure + audit of one level with the root-late repair loop; returns (project, last closure, rounds)"""
    rounds = []
    cur = src
    for n in range(ROOT_LATE_ROUNDS + 1):
        nxt = job.project('audit_%sc%d' % (tag, n))
        cl = audit.closure(clix, 'audit_%sclosure%d' % (tag, n), cur, nxt, level)
        rounds.append({k: cl[k] for k in ('unresolved', 'closureAdded', 'auditMissing', 'auditRootLate', 'auditStageLate')})
        if cur != src and os.path.exists(cur):
            os.remove(cur)
        cur = nxt
        if cl['ok'] or not cl['root_late'] or n == ROOT_LATE_ROUNDS:
            break
        steps = [cli.load(cur)]
        for t in cl['root_late']:
            for a in with_partner(re.sub(r' \(only.*', '', t)):
                steps.append({'op': 'cli', 'args': ['bundle', 'add', a, '--to', bundle, '--with-resources'], 'on-error': 'continue'})
        fix = job.project('audit_%sf%d' % (tag, n))
        steps.append(cli.save(fix))
        clix.run('audit_%srootlate%d' % (tag, n), steps, check=False)
        if not os.path.exists(fix):
            raise StageError('The root-late repair did not save its project  [log: %s]' % job.path('logs', 'audit_%srootlate%d.log' % (tag, n)))
        os.remove(cur)
        cur = fix
    return cur, cl, rounds


def run_audit(job, log):
    clix = cli.Cli(job, log)
    src = chain.base(job, 'audit')
    out = job.project('audit')
    info = {}
    cur, cl, info['rounds'] = _closure(clix, job, src, job.level, job.bundle, '')
    info['closure'] = cl
    cl2 = val2 = None
    if job.wants('supremacy'):                  # the second level is audited the same way, on top of the first one's result
        cur2, cl2, info['rounds_supremacy'] = _closure(clix, job, cur, job.level2, job.bundle2, 's_')
        if cur != src and os.path.exists(cur):
            os.remove(cur)
        cur = cur2
        info['closure_supremacy'] = cl2
    if os.path.exists(out):
        os.remove(out)
    os.replace(cur, out)
    val = audit.validate(clix, 'audit_validate', out, job.level)
    if job.wants('supremacy'):
        val2 = audit.validate(clix, 'audit_validate_s', out, job.level2)
        info['validate_supremacy'] = val2
    lint = audit.lint(clix, 'audit_lint', out)
    info['validate'], info['lint'] = val, lint
    # post-build checks (read from the built project)
    tsv = job.path('dumps', 'bundle_root.tsv')
    r = clix.run('audit_post', [cli.load(out),
                                {'op': 'cli', 'args': ['bundle', 'contents', job.bundle, '--out', tsv], 'on-error': 'continue'},
                                {'op': 'ebx-dump', 'ebx': job.default_layer, 'deep': True, 'out': job.path('dumps', 'audit_Default.json')}], check=False)
    names = []
    if os.path.exists(tsv):
        with open(tsv, encoding='utf-8', errors='replace') as f:
            names = re.findall(r'[A-Za-z0-9_/\.\-\+]+_class(?:_schematics)?\b', f.read())
    sch, lone = schematics_pairs(names)
    with open(job.work + '/import.json', encoding='utf-8') as f:
        build = json.load(f)['pieces']
    with open(job.path('dumps', 'audit_Default.json'), encoding='utf-8') as f:
        groups = group_members(json.load(f))
    want = sorted(('%s/%s_Mesh' % (job.prefix, b['as'])).lower() for b in build)
    have = sorted(m.lower() for g, ms in groups for m in ms)
    post = {'schematics_pairs': {'class_schematics': len(sch), 'without_class': lone, 'listed': bool(names)},
            'group_census': {'members': len(have), 'pieces': len(want), 'match': have == want}}
    # depot census: every piece's own texture parameters and surface permutation, read from the built project
    post['depots'] = census.depots(clix, job, out, build)
    info['post'] = post
    with open(job.work + '/audit.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    problems = []
    if not cl['ok']:
        problems.append('dependency closure: unresolved %s, missing %s, root-late %s, stage-late %s' % (
            cl['unresolved'], cl['auditMissing'], cl['auditRootLate'], cl['auditStageLate']))
    if not val['ok']:
        problems.append('level validate: %s' % (val['result'] or '; '.join(val['issues'][:3])))
    if cl2 is not None and not cl2['ok']:
        problems.append('Supremacy level, dependency closure: unresolved %s, missing %s, root-late %s, stage-late %s' % (
            cl2['unresolved'], cl2['auditMissing'], cl2['auditRootLate'], cl2['auditStageLate']))
    if val2 is not None and not val2['ok']:
        problems.append('Supremacy level, level validate: %s' % (val2['result'] or '; '.join(val2['issues'][:3])))
    if not lint['ok']:
        problems.append('mod lint: %s error(s) %s' % (lint['errors'], lint['error_kinds']))
    if lone:
        problems.append('class schematics bundled without their class (crashes on load): %s' % ', '.join(lone[:5]))
    if not names:
        problems.append('the root bundle could not be listed (schematics-pair check did not run)')
    if not post['group_census']['match']:
        problems.append('the static group holds %d members for %d pieces' % (len(have), len(want)))
    problems += post['depots']['problems']
    if problems:
        raise StageError('The build does not pass its checks:\n  ' + '\n  '.join(problems))
    log('audit: closure %s; %s; lint %s errors / %s warnings; class schematics %d, all paired; group census %d / %d; '
        'depot census %d / %d pieces, %d materials' % (
            info['rounds'][-1], val['result'], lint['errors'], lint['warnings'], len(sch), len(have), len(want),
            post['depots']['depots_read'], post['depots']['pieces'], post['depots']['materials']))
    return {'closure': info['rounds'][-1], 'validate': val['result'], 'lint_errors': lint['errors'], 'lint_warnings': lint['warnings'],
            'class_schematics': len(sch), 'group_members': len(have), 'depots_read': post['depots']['depots_read'],
            'materials_in_census': post['depots']['materials']}


def mod_path(job):
    return job.path('mods', job.name + '.fbmod')


def mod_inputs(job):
    return {'name': job.name, 'display': job.display, 'author': job.settings.get('author'), 'v': 1}


def mod_outputs(job):
    return [mod_path(job)]


def run_mod(job, log):
    clix = cli.Cli(job, log)
    out = mod_path(job)
    if os.path.exists(out):
        os.remove(out)
    clix.run('mod', [cli.load(job.project('audit')),
                     {'op': 'mod-build', 'out': out, 'mod-name': job.display, 'mod-author': job.settings.get('author') or 'bf2maptool',
                      'mod-version': (job.map.get('map') or {}).get('version', '0.1')}])
    if not os.path.exists(out):
        raise StageError('The mod file was not written  [log: %s]' % job.path('logs', 'mod.log'))
    size = os.path.getsize(out)
    with open(job.work + '/shell.json', encoding='utf-8') as f:
        load = json.load(f).get('load')
    log('mod: %s (%d bytes); level %s' % (out, size, job.level))
    res = {'fbmod': out, 'bytes': size, 'level': job.level, 'load': load}
    if job.wants('supremacy'):
        res['supremacy_level'] = job.level2
    return res
