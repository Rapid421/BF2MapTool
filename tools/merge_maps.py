"""Put several map mods into ONE mod that keeps every map's menu entries (FrostyCLI `mod merge`).
    python merge_maps.py <out.fbmod> <mod-name> <a.fbmod> <b.fbmod> ... [--report] [--timeout 3600]
Log: <out>.log next to the output. --report only says what would be merged (nothing written).
"""
import json
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = json.load(open(os.path.join(ROOT, 'work', 'settings.json')))


def main(argv):
    report = '--report' in argv
    menu = '--menu-only' in argv
    argv = [a for a in argv if a not in ('--report', '--menu-only')]
    timeout = 3600
    if '--timeout' in argv:
        i = argv.index('--timeout'); timeout = int(argv[i + 1]); del argv[i:i + 2]
    cli = S['frostycli']
    if '--cli' in argv:
        i = argv.index('--cli'); cli = argv[i + 1]; del argv[i:i + 2]
    out, name, mods = argv[0], argv[1], argv[2:]
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    steps = [{'op': 'project-new'}, {'op': 'cli', 'args': ['mod', 'merge'] + mods + (['--report'] if report else []) + (['--menu-only'] if menu else [])}]
    if not report:
        steps.append({'op': 'mod-build', 'out': out, 'mod-name': name, 'mod-author': S.get('author', ''), 'mod-version': '1.0',
                      'mod-description': 'Merged by the BF2 Map Tool: ' + ', '.join(os.path.splitext(os.path.basename(m))[0] for m in mods)})
    sp = os.path.abspath(out) + ('.report' if report else '') + '.steps.json'
    json.dump({'game': 'swbf2', 'game-path': S['game_path'], 'handler-plugins': S['plugins_path'], 'steps': steps}, open(sp, 'w'), indent=1)
    lp = sp[:-len('.steps.json')] + '.log'
    env = dict(os.environ, TMP=os.path.join(ROOT, 'tmp'), TEMP=os.path.join(ROOT, 'tmp'))
    with open(lp, 'w', encoding='utf-8', errors='replace') as log:
        p = subprocess.Popen([cli, 'run', sp], stdout=log, stderr=subprocess.STDOUT, env=env,
                             creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        try:
            rc = p.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            p.kill(); p.wait()
            print('TIMEOUT after %d s, killed  [log: %s]' % (timeout, lp)); return 3
    text = open(lp, encoding='utf-8', errors='replace').read()
    for line in text.splitlines():
        if 'mod merge' in line or ': ' in line and ('REFUSED' in line or 'CONFLICT' in line or 'change(s)' in line) or 'ERR' in line:
            print(line.strip())
    print('rc %d  [log: %s]' % (rc, lp))
    return rc


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
