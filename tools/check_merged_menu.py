"""Does a (merged) mod list every map in the frontend? Applies the mod to a fresh project, deep-dumps the menu assets
and reports, per map key, where it appears; pickers are checked for level / label count alignment.
    python check_merged_menu.py <mod.fbmod> <out dir> <MapName> [MapName ...] [--timeout 1800]
"""
import json
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = json.load(open(os.path.join(ROOT, 'work', 'settings.json')))
ASSETS = ['UI/Metadata/OptionsEnumUIMetaData', 'LevelListReport', 'UI/Data/GameModes/SkirmishBlast', 'UI/Data/GameModes/Mode8',
          'UI/Frontend/Screens/Play/Skirmish/SkirmishSettingsScreen', 'UI/Frontend/Screens/Play/InstantAction/InstantActionSettingsScreen']


def main(argv):
    timeout = 1800
    if '--timeout' in argv:
        i = argv.index('--timeout'); timeout = int(argv[i + 1]); del argv[i:i + 2]
    cli = S['frostycli']
    if '--cli' in argv:
        i = argv.index('--cli'); cli = argv[i + 1]; del argv[i:i + 2]
    report_only = '--report' in argv
    if report_only:
        argv.remove('--report')
    mod, out, names = argv[0], argv[1], argv[2:]
    os.makedirs(out, exist_ok=True)
    # several mods "a.fbmod;b.fbmod;menu.fbmod" are applied in that order, as the mod manager loads them (the map mods,
    # then the small menu mod last - a menu-only mod holds no map's own entry asset)
    steps = [{'op': 'project-new'}] + [{'op': 'mod-apply', 'file': m} for m in mod.split(';') if m.strip()]
    steps += [{'op': 'ebx-dump', 'ebx': a, 'deep': True, 'out': os.path.join(out, a.replace('/', '_') + '.json')} for a in ASSETS]
    for n in names:
        steps.append({'op': 'ebx-dump', 'ebx': 'UI/Data/GameModes/Levels/' + n + '_01', 'deep': True, 'out': os.path.join(out, 'info_' + n + '.json'),
                      'on-error': 'continue'})
    sp = os.path.join(out, 'steps.json')
    json.dump({'game': 'swbf2', 'game-path': S['game_path'], 'handler-plugins': S['plugins_path'], 'steps': steps}, open(sp, 'w'), indent=1)
    lp = os.path.join(out, 'check.log')
    rc = 0
    if not report_only:                 # --report: count again from the dumps a previous run made
        with open(lp, 'w', encoding='utf-8', errors='replace') as log:
            p = subprocess.Popen([cli, 'run', sp], stdout=log, stderr=subprocess.STDOUT,
                                 env=dict(os.environ, TMP=os.path.join(ROOT, 'tmp'), TEMP=os.path.join(ROOT, 'tmp')),
                                 creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            try:
                rc = p.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                p.kill(); p.wait(); print('TIMEOUT  [log: %s]' % lp); return 3
    print('cli rc', rc, ' [log: %s]' % lp)
    # the map entries' file guids
    guids = {}
    for n in names:
        f = os.path.join(out, 'info_' + n + '.json')
        if not os.path.exists(f):
            print('%-14s NO menu entry asset UI/Data/GameModes/Levels/%s_01' % (n, n)); continue
        txt = open(f, encoding='utf-8').read()
        j = json.loads(txt)
        g = j.get('__guid') or j.get('fileGuid') or j.get('guid')
        guids[n] = (g or '').lower()
    report = {}
    for a in ASSETS:
        f = os.path.join(out, a.replace('/', '_') + '.json')
        if not os.path.exists(f):
            print('missing dump', a); continue
        txt = open(f, encoding='utf-8').read().lower()
        for n in names:
            # a pointer to the entry is dumped with its path ("__path": "ui/data/gamemodes/levels/<map>_01")
            report.setdefault(n, {})[a] = (txt.count(guids.get(n) or ('\0' + n)) + txt.count(('"ui/data/gamemodes/levels/%s_01"' % n).lower())
                                           + (txt.count(('"' + n + '_01"').lower()) if a.endswith('Screen') else 0))
    for n in names:
        print('%-14s guid %s  %s' % (n, guids.get(n, '?')[:8], '  '.join('%s=%d' % (a.split('/')[-1], c) for a, c in report.get(n, {}).items())))
    json.dump({'guids': guids, 'report': report}, open(os.path.join(out, 'menu_check.json'), 'w'), indent=1)
    return rc


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
