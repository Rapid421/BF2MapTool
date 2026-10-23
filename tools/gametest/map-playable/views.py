"""Driven-camera views of a level while it is up under the harness (probe module), HUD hidden, world frozen.

  python views.py <tag> <views.json>        views.json: [["name", [camx, camy, camz], [tgtx, tgty, tgtz]], ...]

take(tag, views) is also used by boundary_test.py (before its fatal outside step: once the player is dead the spawn
screen's dark-room exposure drives every free-camera view). Game-window shots only (shot.py), shots/<tag>_<name>.png.
"""
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, r'F:\Frosty\gamelink')
from gl import send  # noqa: E402
import look  # noqa: E402
from shot import shot  # noqa: E402


def take(tag, views, settle=8.0):
    done = []
    try:
        send('hud 0')
        send('timescale 0')
        for name, cam, tgt in views:
            print(' ', name, look.go(tuple(cam), tuple(tgt))[:80])
            time.sleep(settle)          # auto-exposure settles on the new view
            open(r'F:\Frosty\gamelink\front.request', 'w').write(str(time.time()))
            time.sleep(2.0)
            if shot('%s_%s' % (tag, name)):
                done.append(name)
    finally:
        for cmd in ('timescale 1', 'camlock 0', 'watchdrive 0', 'hud 1'):
            try:
                send(cmd)
            except Exception as ex:
                print(cmd, 'not sent:', ex)
                break
    return done


if __name__ == '__main__':
    take(sys.argv[1], json.load(open(sys.argv[2])))
