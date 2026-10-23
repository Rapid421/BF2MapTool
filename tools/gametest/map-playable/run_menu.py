"""run_menu.py <LABEL> <version> <loading image|-> [--hold S] [--no-build]: claim the harness, build the level with its
menu entry, loading screen and Kyber declaration (scripts/build_mp2.sh), then start the game in the harness's menu
mode (launch-only, no injection; the elevated script replays gamelink/menu.request batches for <hold> s).
Drive it with `python menu.py send '<steps>' <shot>`; release with `python harness.py --release` when done.
Log: work/run_<LABEL>.log.
"""
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import harness  # noqa: E402

ROOT = os.path.dirname(HERE)
BASH = r'C:\Program Files\Git\bin\bash.exe'


def main(argv):
    label, ver, img = argv[:3]
    hold = int(argv[argv.index('--hold') + 1]) if '--hold' in argv else 720
    log = open(os.path.join(ROOT, 'work', 'run_%s.log' % label), 'a')
    w = lambda *a: (log.write(time.strftime('%H:%M:%S ') + ' '.join(str(x) for x in a) + '\n'), log.flush(),
                    print(time.strftime('%H:%M:%S '), *a, flush=True))
    w('waiting for the harness')
    harness.claim('%s menu test' % label, wait=7200)
    w('claimed')
    if '--no-build' not in argv:
        p = subprocess.run([BASH, os.path.join(HERE, 'build_mp2.sh'), label, ver, img], cwd=ROOT, capture_output=True,
                           text=True, errors='replace')
        log.write(p.stdout + p.stderr)
        w('build rc', p.returncode)
        for l in (p.stdout + p.stderr).splitlines():
            if any(k in l for k in ('menu entry', 'picker', 'lookup', 'loading-screen', 'kyber', 'Kyber', 'embedded',
                                    'category', 'OK', 'ERR', 'rror', 'Result:', 'combat')):
                w('  ', l.strip()[:300])
    mod = os.path.join(ROOT, 'mods', label + '.fbmod')
    if not os.path.exists(mod):
        w('no mod')
        return 1
    level = 'AddedContent/MapPlayable/Levels/%s/%s' % (label, label)
    t0 = harness.launch(mod, level, hold=hold, menu=True)
    w('launched in menu mode, hold', hold)
    v = harness.wait_verdict(mod, t0, limit=600)
    w('launch verdict:', str(v)[:300] if v else 'none')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
