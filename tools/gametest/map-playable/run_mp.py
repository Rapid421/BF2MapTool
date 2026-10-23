"""run_mp.py <LABEL> <version> <plan.json|-> [--build-cli] [--hold S] [--mode M] [--after "<script> <args>"] [--no-build]
             [--release] [-- <extra level-create args>]

Claim the shared harness (waiting for other claims / games), optionally build the CLI (+ unit tests), build the level
with scripts/build_level.sh, launch it with the probe module, run boundary_test.py with <plan.json> while it is up, wait
for the game to close. --release frees the harness at the end. Everything goes to work/run_<LABEL>.log.
"""
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import harness  # noqa: E402

ROOT = os.path.dirname(HERE)
WT = os.path.join(ROOT, 'frostycli-wt')
BASH = r'C:\Program Files\Git\bin\bash.exe'


def say(log, *a):
    line = time.strftime('%H:%M:%S ') + ' '.join(str(x) for x in a)
    print(line, flush=True)
    log.write(line + '\n')
    log.flush()


def run(log, cmd, cwd=None, timeout=3600):
    say(log, '$', ' '.join(cmd) if isinstance(cmd, list) else cmd)
    p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, errors='replace', timeout=timeout)
    out = (p.stdout or '') + (p.stderr or '')
    log.write(out + '\n')
    log.flush()
    return p.returncode, out


def main(argv):
    extra = []
    if '--' in argv:
        extra = argv[argv.index('--') + 1:]
        argv = argv[:argv.index('--')]
    label, ver, plan = argv[:3]
    opt = lambda k, d=None: argv[argv.index(k) + 1] if k in argv else d
    hold = int(opt('--hold', '300'))
    log = open(os.path.join(ROOT, 'work', 'run_%s.log' % label), 'w')
    say(log, 'waiting for the harness')
    harness.claim('%s build + test' % label, wait=7200)
    say(log, 'claimed')
    try:
        if '--build-cli' in argv:
            rc, out = run(log, ['dotnet', 'build', 'FrostyCLI.sln', '-c', 'Release', '-p:Platform=x64'], cwd=WT)
            errs = [l for l in out.splitlines() if ' error ' in l]
            say(log, 'CLI build rc', rc, '; errors:', len(errs))
            for l in errs[:20]:
                say(log, '  ', l.strip()[:300])
            if rc != 0:
                return 1
            cache = os.path.join(WT, 'FrostyCLI', 'bin', 'x64', 'Release', 'Caches')
            if not os.path.exists(os.path.join(cache, 'starwarsii.cache')):
                os.makedirs(cache, exist_ok=True)
                src = r'F:\Frosty\projects\terrain-authoring\frostycli-wt\FrostyCLI\bin\x64\Release\Caches'
                for f in os.listdir(src):
                    if f.startswith('starwarsii.cache'):
                        subprocess.run(['cmd', '/c', 'copy', '/y', os.path.join(src, f), cache], capture_output=True)
                say(log, 'cache copied')
            rc, out = run(log, [os.path.join(WT, 'FrostyCLI.Tests', 'bin', 'x64', 'Release', 'FrostyCLI.Tests.exe'), '--unit'])
            say(log, 'unit tests:', ' | '.join(out.strip().splitlines()[-3:]))
        mod = os.path.join(ROOT, 'mods', label + '.fbmod')
        rc = 0
        if '--no-build' not in argv:
            rc, out = run(log, [BASH, os.path.join(HERE, 'build_level.sh'), label, ver] + extra, cwd=ROOT)
            say(log, 'level build rc', rc)
            for l in out.splitlines():
                if any(k in l for k in ('combat', 'OK ', 'ERR', 'FAIL', 'xception', 'Result:', 'rror')):
                    say(log, '  ', l.strip()[:300])
        if rc != 0 or not os.path.exists(mod):
            say(log, 'no mod built')
            return 1
        if plan == '-' and not opt('--after'):
            return 0
        level = 'AddedContent/MapPlayable/Levels/%s/%s' % (label, label)
        t0 = harness.launch(mod, level, mode=opt('--mode', 'SkirmishBlast'), hold=hold, deploy=True, probe=True)
        say(log, 'launched')
        v = harness.wait_verdict(mod, t0)
        say(log, 'verdict:', (str(v)[:600] if v else 'none'))
        if v and v.get('status') == 'LOADED':
            time.sleep(20)
            # the probe: boundary_test.py with <plan.json>, or --after "<script.py> <args>" (e.g. mode_probe.py MP3_blast 90)
            after = opt('--after')
            cmd = ([sys.executable] + [os.path.join(HERE, after.split()[0])] + after.split()[1:]) if after else                   [sys.executable, os.path.join(HERE, 'boundary_test.py'), label, plan]
            rc, out = run(log, cmd, timeout=900)
            for l in out.splitlines()[-30:]:
                say(log, '  ', l[:300])
        closed = harness.wait_closed(t0, 900 + hold)
        say(log, 'game closed' if closed else 'game still running')
        return 0
    finally:
        if '--release' in argv:
            harness.release()
            say(log, 'released')


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
