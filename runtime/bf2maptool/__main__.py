"""Command line of the runtime.

    python -m bf2maptool port <map.json> --settings <settings.json> [--until <stage>] [--force <stage>|all]
    python -m bf2maptool plan <map.json> --settings <settings.json>      what a build would redo, and why
    python -m bf2maptool build <scene.blend> --settings <s> --blender <exe> --export-script <py> --export-dir <dir>
                                                                          export the scene headless, then port
    python -m bf2maptool check <scene.blend> ...same options...          export, then the pre-flight checks only
    python -m bf2maptool check-setup --settings <settings.json>           one line per external tool

Progress is printed as '@@STAGE ...' / '@@RESULT {json}' / '@@MOD <path>' / '@@DONE ok|fail|blocked <message>' lines.
"""
import argparse
import json
import os
import sys
import time

try:
    from . import cli, config, pipeline, runner
except ImportError as _e:              # a package of the add-on's private folder is missing (a new work folder has none)
    print('@@DONE blocked The Python package "%s" is missing. In Blender: Edit > Preferences > Add-ons > BF2 Map Tool > '
          '"Install Python packages", then build again.' % (_e.name or _e), flush=True)
    sys.exit(2)


def _export(a, settings):
    """the scene export: the one place Blender runs, as a tool, on a saved copy of the scene"""
    blender = a.blender or settings.get('blender')
    if not blender or not os.path.exists(blender):
        raise runner.StageError('Blender was not found (%s).' % blender)
    if not os.path.isfile(a.scene):
        # Blender opens a missing file as an empty scene and the export would then "succeed" with nothing in it
        raise runner.StageError('The scene file was not found (%s).' % os.path.abspath(a.scene))
    os.makedirs(a.export_dir, exist_ok=True)
    mj = os.path.join(a.export_dir, 'map.json')
    if os.path.exists(mj):
        os.remove(mj)
    t0 = time.time()
    print('@@STAGE export start 0 reading the scene', flush=True)
    env = dict(os.environ)
    tmp = os.path.join(settings['work_dir'], '_tmp')
    os.makedirs(tmp, exist_ok=True)
    env['TMP'] = env['TEMP'] = env['TMPDIR'] = tmp
    try:
        # --factory-startup: the export registers its OWN properties (the version that matches this runtime). Without it
        # the add-ons of the user's profile load first, and an older installed copy of this add-on then answers for the
        # scene's settings (seen 2026-10-12: a new setting was missing and the export stopped).
        rc, text = cli.spawn([blender, '-b', '--factory-startup', a.scene, '--python', a.export_script, '--', a.export_dir],
                             os.path.join(a.export_dir, 'export.log'), env=env, timeout=3 * 60 * 60, quiet_timeout=10 * 60)   # very large ripped maps export for over an hour; the quiet limit catches a hang
    except runner.StageError as e:
        print('@@STAGE export fail %.1f %s' % (time.time() - t0, e), flush=True)
        raise
    except OSError as e:
        # Windows refuses to start some Blender installs from another program (the Microsoft Store build lives in a
        # protected folder: "[WinError 5] Access is denied"; an antivirus can do the same)
        msg = ('Blender could not be started for the export (%s): "%s". A Blender from the Microsoft Store cannot be run by other '
               'programs - install Blender from blender.org, or set "Blender for the export" in the add-on preferences to a '
               'blender.exe from blender.org.' % (e, blender))
        print('@@STAGE export fail %.1f %s' % (time.time() - t0, msg), flush=True)
        raise runner.StageError(msg)
    if 'Cannot read blend file' in text:
        # Blender then carries on with its empty start-up scene: never export that as the map
        old = 'newer version of Blender' in text
        msg = ('Blender could not open the scene (%s)%s' % (a.scene, ': the scene was saved by a newer Blender than "%s". Leave "Blender for the '
               'export" empty in the add-on preferences, or set it to this Blender (or a newer one).' % blender if old else '.'))
        print('@@STAGE export fail %.1f %s' % (time.time() - t0, msg), flush=True)
        if os.path.exists(mj):
            os.remove(mj)
        raise runner.StageError(msg)
    if not os.path.exists(mj):
        tail = ' | '.join(l.strip() for l in text.strip().splitlines()[-3:])
        print('@@STAGE export fail %.1f the scene could not be exported' % (time.time() - t0), flush=True)
        raise runner.StageError('The scene could not be exported: %s' % tail[:400])
    line = next((l for l in text.splitlines() if l.startswith('EXPORT DONE')), '')
    print('@@STAGE export done %.1f %s' % (time.time() - t0, line[12:260]), flush=True)
    return mj


def _use_pylib(lib):
    """the add-on's private package folder ("Install Python packages"): first on the import path of this process and
    of the scripts it starts (the shader tools run as their own Python processes)"""
    if lib and os.path.isdir(lib):
        if lib not in sys.path:
            sys.path.insert(1, lib)
        pp = os.environ.get('PYTHONPATH', '')
        if lib not in pp.split(os.pathsep):
            os.environ['PYTHONPATH'] = os.pathsep.join([p for p in (pp, lib) if p])


def main(argv=None):
    ap = argparse.ArgumentParser(prog='bf2maptool')
    sub = ap.add_subparsers(dest='cmd', required=True)
    for name in ('port', 'plan', 'census'):
        p = sub.add_parser(name)
        p.add_argument('map')
        p.add_argument('--settings', required=True)
        p.add_argument('--until')
        p.add_argument('--only', action='append', default=[])
        p.add_argument('--bake', choices=('draft', 'normal', 'high'))
        p.add_argument('--force', action='append', default=[])
    for name in ('build', 'check', 'bake'):
        p = sub.add_parser(name)
        p.add_argument('scene')
        p.add_argument('--settings', required=True)
        p.add_argument('--blender')
        p.add_argument('--export-script', required=True)
        p.add_argument('--export-dir', required=True)
        p.add_argument('--until')
        p.add_argument('--force', action='append', default=[])
        p.add_argument('--quality', default='normal')
    p = sub.add_parser('check-setup')
    p.add_argument('--settings', required=True)
    p = sub.add_parser('menu-mod')
    p.add_argument('--settings', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--mods', required=True, help='map .fbmod files, separated by ;')
    p.add_argument('--name')
    a = ap.parse_args(argv)
    if a.cmd == 'menu-mod':
        try:
            settings = config.load_settings(a.settings)
        except (config.ConfigError, OSError, ValueError) as e:
            print('@@DONE blocked %s' % e, flush=True)
            return 2
        from . import menumod
        r = menumod.run(settings, a.out, [m for m in a.mods.split(';') if m.strip()], a.name, log=lambda m: print(m, flush=True))
        print('@@RESULT ' + json.dumps(r), flush=True)
        if r['ok']:
            print('@@MOD %s' % r['out'], flush=True)
        print('@@DONE %s %s' % ('ok' if r['ok'] else 'fail', 'menu mod for %d maps, %d bytes' % (len(r.get('maps') or []), r.get('bytes') or 0)
                                 if r['ok'] else r['error']), flush=True)
        return 0 if r['ok'] else 1
    try:
        settings = config.load_settings(a.settings) if a.cmd != 'check-setup' else json.load(open(a.settings, encoding='utf-8'))
    except (config.ConfigError, OSError, ValueError) as e:
        print('@@DONE blocked %s' % e, flush=True)
        return 2
    _use_pylib(settings.get('pylib'))
    if a.cmd == 'check-setup':
        from . import setupcheck
        return setupcheck.main(settings)
    try:
        map_path = _export(a, settings) if a.cmd in ('build', 'check', 'bake') else a.map
        job = config.Job(map_path, settings)
    except (config.ConfigError, runner.StageError, OSError, ValueError) as e:
        print('@@DONE blocked %s' % e, flush=True)
        return 2
    if a.cmd == 'check':
        from . import checks
        results = checks.preflight(job)
        for r in results:
            print('@@RESULT ' + json.dumps(r), flush=True)
        errs = sum(1 for r in results if r['severity'] == 'error')
        print('@@DONE %s %d error(s), %d warning(s)' % ('ok' if not errs else 'fail', errs, sum(1 for r in results if r['severity'] == 'warning')), flush=True)
        return 0 if not errs else 1
    if a.cmd == 'bake' or getattr(a, 'bake', None):
        # a bake build = the port build with the bake stage between the kill volumes and the checks
        job.map.setdefault('options', {})['bake'] = {'quality': getattr(a, 'quality', None) or getattr(a, 'bake', None) or 'normal'}
    if a.cmd == 'census':                 # the post-build censuses on a finished build (the audit stage runs them too)
        from . import census, cli
        with open(job.work + '/import.json', encoding='utf-8') as f:
            build = json.load(f)['pieces']
        res = census.depots(cli.Cli(job, lambda m: print(m, flush=True)), job, job.project('audit'), build, name='census_depots')
        print(json.dumps(res, indent=1))
        return 1 if res['problems'] else 0
    stages = pipeline.port_stages(with_bake=job.wants('bake'), with_supremacy=job.wants('supremacy'))
    if a.cmd == 'plan':
        state = runner.load_state(job)
        for st, h, needs, reason in runner.plan(job, stages, state, a.force):
            print('%-14s %s' % (st.name, ('REDO: ' + reason) if needs else 'up to date'))
        return 0
    if a.cmd in ('build', 'bake'):
        from . import checks
        results = checks.preflight(job)
        for r in results:
            print('@@RESULT ' + json.dumps(r), flush=True)
        errs = [r for r in results if r['severity'] == 'error']
        if errs:
            print('@@DONE blocked %d check(s) failed - see the Check tab. First: %s' % (len(errs), errs[0]['message'][:240]), flush=True)
            return 1
    ok = runner.run(job, stages, only=getattr(a, 'only', None) or None, until=a.until, force=a.force)
    if ok and not a.until and not (getattr(a, 'only', None) or None):       # a part build made no new mod: name none
        from .stages import finish
        mp = finish.mod_path(job)
        if os.path.exists(mp):
            print('@@MOD ' + mp, flush=True)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
