"""The three audits every build must pass, read from the FULL tool output (never from a truncated summary):
`level closure --audit` (0 unresolved / 0 missing / 0 root-late / 0 stage-late), `level validate` (no issues),
`mod lint` (0 errors)."""
import json
import os
import re

from .runner import StageError


def closure(clix, name, project, save_as, level):
    """dependency closure + audit; the closure may ADD members, so the result is saved as a new project"""
    r = clix.direct(name, ['level', 'closure', level, '--audit'], project=project, save_as=save_as, check=False)
    out = {'rc': r.rc}
    for k in ('closureAdded', 'closureRes', 'auditMissing', 'auditRootLate', 'auditStageLate', 'chunksWithH32Zero'):
        m = re.search(r'^\s*%s:\s*(\d+)' % k, r.text, re.M)
        out[k] = int(m.group(1)) if m else None
    m = re.search(r'audit: (\d+) unresolved', r.text)
    out['unresolved'] = int(m.group(1)) if m else None
    out['root_late'] = sorted({l.split(' -> ', 1)[1].strip() for l in r.text.splitlines() if 'ROOT-LATE' in l and ' -> ' in l})
    out['missing'] = [l.strip() for l in r.text.splitlines() if 'MISSING' in l][:50]
    if not os.path.exists(save_as):
        tail = ' | '.join(l.strip() for l in r.text.strip().splitlines()[-4:])
        raise StageError('The dependency closure did not finish: %s  [log: %s]' % (tail[:500], r.log_path))
    out['ok'] = (out['auditMissing'] == 0 and out['auditRootLate'] == 0 and out['auditStageLate'] == 0 and not out['unresolved'])
    return out


def validate(clix, name, project, level):
    r = clix.direct(name, ['level', 'validate', level], project=project, check=False)
    res = next((l.strip() for l in reversed(r.text.splitlines()) if l.strip().startswith('Result:')), '')
    chk = next((l.strip() for l in r.text.splitlines() if l.strip().startswith('Checked:')), '')
    issues = [l.strip() for l in r.text.splitlines() if re.match(r'\s+(ERROR|MUST-FIX|WARNING)\s', l)]
    return {'ok': 'no issues found' in res, 'result': res, 'checked': chk, 'issues': issues[:200], 'log': r.log_path}


def lint(clix, name, project):
    r = clix.direct(name, ['mod', 'lint', '--max-issues', '100000'], project=project, check=False)
    m = re.search(r'Result:.*?(\d+) error\(s\), (\d+) warning\(s\)', r.text)
    errs = {}
    for l in r.text.splitlines():
        mm = re.match(r'\s+ERROR\s+(\S+)\s+(.*)', l)
        if mm:
            errs.setdefault(mm.group(1), []).append(mm.group(2).strip())
    return {'ok': bool(m) and int(m.group(1)) == 0, 'errors': int(m.group(1)) if m else None, 'warnings': int(m.group(2)) if m else None,
            'error_kinds': {k: len(v) for k, v in errs.items()}, 'first_errors': {k: v[:5] for k, v in errs.items()}, 'log': r.log_path}


def dump_partitions(clix, name, project, partitions, out_dir, extra=()):
    """deep ebx dumps of level partitions -> out_dir/<partition name>.json; returns a loader"""
    os.makedirs(out_dir, exist_ok=True)
    steps = [{'op': 'project-load', 'file': project}]
    for p in list(partitions) + list(extra):
        steps.append({'op': 'ebx-dump', 'ebx': p, 'deep': True, 'out': '%s/%s.json' % (out_dir, p.split('/')[-1]), 'on-error': 'continue'})
    clix.run(name, steps)
    return loader(out_dir)


def loader(out_dir):
    cache = {}

    def load(layer):
        if layer not in cache:
            p = '%s/%s.json' % (out_dir, layer)
            if not os.path.exists(p):
                raise StageError('The level has no layer "%s" (dump %s is missing).' % (layer, p))
            with open(p, encoding='utf-8') as f:
                cache[layer] = json.load(f)
        return cache[layer]
    return load
