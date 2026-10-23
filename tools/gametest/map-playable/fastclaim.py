"""fastclaim.py <tag> [max seconds]: take the shared harness in the gap another session leaves between its runs.

The pathfinding session's loop (navmesh-authoring/scripts/run_bots.py) releases after each map "so another session can
take a turn in between" and re-claims as soon as it finds the claim gone and no game running -- a gap of well under a
second. So the game state is sampled continuously in a thread (tasklist every 0.3 s), the claim file every 0.05 s; when
the claim is gone and the last game sample (at most 1 s old) says no game, our claim is created exclusively (never
overwriting anyone's), then verified a second later with a fresh game check -- if it is not ours or a game runs, we
back off (our file is removed only if it is ours). Logs every claim change it sees.
"""
import os
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import harness  # noqa: E402

tag = sys.argv[1]
limit = float(sys.argv[2]) if len(sys.argv) > 2 else 7200
state = {'running': True, 'at': 0.0}


def watch_game():
    while True:
        r = harness.game_running()
        state['running'], state['at'] = r, time.time()
        time.sleep(0.3)


threading.Thread(target=watch_game, daemon=True).start()
t0 = time.time()
last = object()
while time.time() - t0 < limit:
    t = harness.claim_text()
    if t != last:
        print(time.strftime('%H:%M:%S'), 'claim:', (t or '(none)').strip()[:120], flush=True)
        last = t
    if t is not None and harness.OWNER in t:
        print(time.strftime('%H:%M:%S'), 'ours', flush=True)
        sys.exit(0)
    if t is None and not state['running'] and time.time() - state['at'] < 1.0:
        text = 'owner: %s (%s), since %s\n' % (harness.OWNER, tag, time.strftime('%Y-%m-%d %H:%M:%S'))
        try:
            fd = os.open(harness.CLAIM, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            os.write(fd, text.encode())
            os.close(fd)
            time.sleep(1.0)
            mine = harness.OWNER in (harness.claim_text() or '')
            if mine and not harness.game_running():
                print(time.strftime('%H:%M:%S'), 'claimed', flush=True)
                sys.exit(0)
            print(time.strftime('%H:%M:%S'), 'backed off (%s)' % ('a game runs' if mine else 'claim overwritten'), flush=True)
            if mine:
                os.remove(harness.CLAIM)
        except FileExistsError:
            pass
    time.sleep(0.05)
print('gave up', flush=True)
sys.exit(1)
