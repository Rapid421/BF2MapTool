"""run_level_test.py <label> <mod.fbmod> <level> [--mode M] [--hold S] [--no-deploy] [--wait S] [--release]
                     [--after "<script.py> <args...>"]

One in-game test under the shared harness: claim (waiting up to --wait s for another session's claim or game), launch
the mod with the probe module, wait for the verdict, run the --after script while the level is up (e.g.
boundary_test.py), then wait for the harness to close the game. --release frees the harness at the end (otherwise the
claim is kept for the next run; release with `python harness.py --release`). Log: work/run_<label>.log.
"""
import json
import os
import shlex
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import harness  # noqa: E402

ROOT = os.path.dirname(HERE)


def main(argv):
    label, mod, level = argv[:3]
    opt = lambda k, d=None: argv[argv.index(k) + 1] if k in argv else d
    mode = opt('--mode', 'SkirmishBlast')
    hold = int(opt('--hold', '300'))
    after = opt('--after')
    harness.claim(label, wait=int(opt('--wait', '0')))
    print('claimed %s' % time.strftime('%H:%M:%S'), flush=True)
    t0 = harness.launch(os.path.abspath(mod), level, mode=mode, hold=hold, deploy='--no-deploy' not in argv, probe=True)
    v = harness.wait_verdict(os.path.abspath(mod), t0)
    print('verdict:', json.dumps(v)[:700] if v else 'none (timeout)', flush=True)
    if v and v.get('status') == 'LOADED' and after:
        time.sleep(20)
        cmd = [sys.executable] + shlex.split(after)
        if not os.path.isabs(cmd[1]):
            cmd[1] = os.path.join(HERE, cmd[1])
        print('after:', ' '.join(cmd), flush=True)
        subprocess.run(cmd)
    closed = harness.wait_closed(t0, 900 + hold)
    print('game closed' if closed else 'game still running', time.strftime('%H:%M:%S'), flush=True)
    try:
        r = json.load(open(harness.RESULT, encoding='utf-8-sig'))
        print('hold:', r.get('hold'), r.get('hold_detail'))
    except Exception:
        pass
    if '--release' in argv:
        harness.release()


if __name__ == '__main__':
    main(sys.argv[1:])
