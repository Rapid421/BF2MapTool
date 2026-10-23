"""menu.py: drive the game's frontend through the harness's menu mode (autotest-args "menu": true -> launch-only, then
the elevated script replays input batches written to gamelink/menu.request and answers in gamelink/menu.done).

  python menu.py send '<json steps>' [shot-name]     e.g. '[{"op":"key","scan":28},{"op":"wait","s":2}]'
  python menu.py shot <name>

Steps: {"op":"click","x":0.08,"y":0.93} (fractions of the primary screen), {"op":"key","scan":28} (hardware scan code:
Enter 0x1C=28, Esc 0x01=1, Space 0x39=57, arrows: up 0x48=72 down 0x50=80 left 0x4B=75 right 0x4D=77, Tab 0x0F=15),
{"op":"wait","s":1.5}.
"""
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from shot import shot  # noqa: E402

GL = r'F:\Frosty\gamelink'
REQ = os.path.join(GL, 'menu.request')
DONE = os.path.join(GL, 'menu.done')


def send(steps, timeout=120):
    before = open(DONE).read() if os.path.exists(DONE) else ''
    tmp = REQ + '.tmp'
    json.dump(steps, open(tmp, 'w'))
    os.replace(tmp, REQ)
    t0 = time.time()
    while time.time() - t0 < timeout:
        time.sleep(0.5)
        now = open(DONE).read() if os.path.exists(DONE) else ''
        if now and now != before and not os.path.exists(REQ):
            return now.strip()
    return None


if __name__ == '__main__':
    if sys.argv[1] == 'send':
        r = send(json.loads(sys.argv[2]))
        print('done:', r)
        if len(sys.argv) > 3:
            time.sleep(1.0)
            shot(sys.argv[3])
    elif sys.argv[1] == 'shot':
        shot(sys.argv[2])
    elif sys.argv[1] == 'burst':
        # burst <name> <seconds> <every>: game-window shots every <every> s (catch a loading screen)
        name, secs, every = sys.argv[2], float(sys.argv[3]), float(sys.argv[4])
        t0 = time.time()
        k = 0
        while time.time() - t0 < secs:
            shot('%s_%02d' % (name, k))
            k += 1
            time.sleep(every)
