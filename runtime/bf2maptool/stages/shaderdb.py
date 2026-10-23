"""Stage `shaderdb`: the level's OWN DX11 shader database, when a material class needs a surface the host does not
compile.

A mesh draws only when the level's database holds a program for (render pass, the mesh's vertex declaration) of its
surface. A template whose surface the host lacks therefore needs that surface merged in - and from the database of
the level its template mesh SHIPS in (the template's `home_db`), because only that database is sure to carry every
vertex-declaration permutation (a copy of the same surface in another level's database had 2 of 4 declarations and
drew nothing, with collision intact).

  host database image + donor database image(s)   (exported once per game install, cached in <work_dir>/_cache/db)
  -> merge (host bytes kept, donor closure renumbered; the tool verifies itself: VERIFY PASS required)
  -> imported as a NEW res under the level's own name, bundled; the host's res leaves the level's bundle (the host's
     own file is never edited); exported again and compared byte for byte.
DX12: the level keeps the host's DX12 databases, so merged surfaces do not draw there (stated, not solved).
"""
import json
import os
import shutil
import subprocess
import sys

from .. import cli, config, hosts, photometry
from ..runner import StageError
from . import textures

DB_SUFFIX = '/rvmdatabase_dx11rvmdatabase'
DB_RES_TYPE = 0x8DA16895


def _needs(job, host):
    with open(job.path('tex', 'plan.json'), encoding='utf-8') as f:
        plan = json.load(f)
    need = {}
    for rec in plan.values():
        T = host.TEMPLATES[rec['template']]
        if T['home_db'] != 'host':
            need.setdefault(T['home_db'], set()).add(T['shader'])
    return {k: sorted(v) for k, v in sorted(need.items())}


def _patches(job, host):
    """{patch name: constant} for the templates the map uses that need their surface edited after the merge"""
    with open(job.path('tex', 'plan.json'), encoding='utf-8') as f:
        plan = json.load(f)
    used = {rec['template'] for rec in plan.values()}
    out = {}
    for t in sorted(used):
        p = host.TEMPLATES[t].get('patch')
        if p == 'holo_texcolor':
            # screen value of a texel of 1 = the emissive surface's value for the map's brightest hologram (same radiance
            # units assumed for both surfaces: INFERRED, the hologram is not part of the light match yet)
            out[p] = round(photometry.emissive_per_unit(job, host) * textures.holo_ref(job) * HOLO_CAL, 3)
    return out


# the hologram's brightness against an emissive surface of the same Blender emission (1 = equal; not measured yet)
HOLO_CAL = 1.0 / 150.0          # MatTest v3 (2026-10-23): at 1.0 a glow of strength 0.05 outshone the emissive surface at 1 - the
                                 # red channel put the hologram path about 150 x too bright; verified by the next MatTest build


def inputs(job):
    host = hosts.get(job.host)
    return {'need': _needs(job, host), 'patches': _patches(job, host), 'host': job.host, 'v': 2}


def outputs(job):
    return [job.project('shaderdb'), job.work + '/shaderdb.json']


def _image(job, clix, level):
    """the DX11 database image of a shipped level, exported once"""
    d = os.path.join(job.settings['work_dir'], '_cache', 'db').replace('\\', '/')
    os.makedirs(d, exist_ok=True)
    path = '%s/%s_dx11.full' % (d, level.split('/')[-1].lower())
    if not os.path.exists(path) or os.path.getsize(path) < 1024:
        clix.run('db_export_' + level.split('/')[-1].lower(),
                 [{'op': 'res-export', 'res': level.lower() + DB_SUFFIX, 'out': path, 'framed': True}])
        if not os.path.exists(path) or os.path.getsize(path) < 1024:
            raise StageError('The shader database of %s could not be exported from the game.' % level)
    return path


def run(job, log):
    host = hosts.get(job.host)
    clix = cli.Cli(job, log)
    need = _needs(job, host)
    out = job.project('shaderdb')
    info = {'own': bool(need), 'need': need, 'dx12': 'host databases (merged surfaces do not draw under DX12)' if need else 'host databases'}
    if not need:
        shutil.copyfile(job.project('shell'), out)
        with open(job.work + '/shaderdb.json', 'w', encoding='utf-8') as f:
            json.dump(info, f, indent=1)
        log('shaderdb: every surface used is compiled by the host; the level keeps the host database')
        return info
    host_img = _image(job, clix, host.LEVEL)
    merged = job.path('db', 'dx11.merged.full')
    report = job.path('db', 'merge_report.json')
    cmd = [sys.executable, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'vendor', 'rvm', 'rvm_merge.py'),
           '--host', host_img]
    for level, surfaces in need.items():
        cmd += ['--add', '%s=%s' % (_image(job, clix, level), ','.join(surfaces))]
    cmd += ['--out', merged, '--report', report]
    lp = job.path('logs', 'shaderdb_merge.log')
    rc, text = cli.spawn(cmd, lp, log, cli.tool_env(job))
    if rc != 0 or 'VERIFY: PASS' not in text:
        tail = ' | '.join(l.strip() for l in text.strip().splitlines()[-4:])
        raise StageError('The shader database merge did not verify: %s  [log: %s]' % (tail[:500], lp))
    patches = _patches(job, host)
    if 'holo_texcolor' in patches:
        patched = job.path('db', 'dx11.merged_holo.full')
        lp2 = job.path('logs', 'shaderdb_holo.log')
        cmd = [sys.executable, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'vendor', 'rvm', 'holo_texcolor.py'),
               merged, patched, str(patches['holo_texcolor'])]
        rc, text = cli.spawn(cmd, lp2, log, cli.tool_env(job))
        res = next((json.loads(l) for l in text.splitlines() if l.startswith('{')), None)
        if rc != 0 or not res or not res.get('verify'):
            tail = ' | '.join(l.strip() for l in text.strip().splitlines()[-4:])
            raise StageError('The hologram shader could not be made texture-coloured: %s  [log: %s]' % (tail[:500], lp2))
        os.replace(patched, merged)
        info['holo_patch'] = res
        log('shaderdb: hologram surface texture-coloured, K %.1f (%d programs, %d blocks rehashed, references unchanged)' % (
            res['K'], len(res['programs']), res['blocks_rehashed']))
    own = job.level.lower() + DB_SUFFIX
    check = job.path('db', 'dx11.readback.full')
    if os.path.exists(check):
        os.remove(check)
    clix.run('shaderdb_import', [
        cli.load(job.project('shell')),
        {'op': 'res-import', 'file': merged, 'res': own, 'type': str(DB_RES_TYPE), 'framed': True},
        {'op': 'bundle-add', 'res': own, 'bundle': job.bundle},
        {'op': 'bundle-remove', 'res': host.LEVEL.lower() + DB_SUFFIX, 'bundle': job.bundle},
        {'op': 'res-export', 'res': own, 'out': check, 'framed': True},
        cli.save(out)])
    same = os.path.exists(check) and config.file_hash(check) == config.file_hash(merged)
    with open(report, encoding='utf-8') as f:
        rep = json.load(f)
    info.update(merged_bytes=os.path.getsize(merged), host_bytes=os.path.getsize(host_img), readback_identical=same,
                verify='PASS', res=own, surfaces_added=sum(len(v) for v in need.values()),
                merge_summary={k: rep.get(k) for k in ('ok', 'seconds', 'dangling', 'surfaces') if k in rep})
    with open(job.work + '/shaderdb.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    if not same:
        raise StageError('The level\'s own shader database does not read back as written  [log: %s]' % job.path('logs', 'shaderdb_import.log'))
    os.remove(check)
    log('shaderdb: own DX11 database = host %d bytes + %d surface(s) from %s -> %d bytes, VERIFY PASS, read-back identical' % (
        info['host_bytes'], info['surfaces_added'], ', '.join(need), info['merged_bytes']))
    return info
