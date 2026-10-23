"""FX view tour for run_test.py --client: views_hoth.py's tour, but with game time RUNNING at each view.

  python fx_views_client.py <tag> <views.json>

views_hoth.py freezes game time (gamelink `timescale 0`, OpenGameCamera's F8) before it moves the camera. Frozen time
freezes the emitters too: an effect that was distance-culled when time stopped (CullDistance 15-90 m around the
player) never spawns a particle when the camera arrives, and the VFX schematic's staggered restarts (DelayEntityData,
game time) never fire -- the FX views would come out empty whatever the port did. So here, at each view:
  camera there (look_safe, time running) -> SETTLE s of game time (effects un-cull and fill, exposure settles)
  -> freeze -> shot <tag>_<view>.png -> run PAIR_GAP s -> freeze -> shot <tag>_<view>_b.png -> time runs again.
The two frames share camera, exposure and every static pixel; only moving things differ (particles, AI), which is what
tools/fx_brightness.py measures the particles on.
"""
import json
import os
import sys
import time

sys.path.insert(0, r'F:\Frosty\gamelink')
sys.path.insert(0, r'F:\Frosty\projects\hoth-port\scripts')
from gl import send  # noqa: E402
import look_safe  # noqa: E402
import views_hoth as vh  # noqa: E402  (front / shot / alive; shots land in I:\HothPort\shots)

SETTLE = 9.0
PAIR_GAP = 1.5


def main():
    tag = sys.argv[1]
    views = [(v[0], tuple(v[1]), tuple(v[2])) for v in json.load(open(sys.argv[2]))]
    os.makedirs(vh.OUT, exist_ok=True)
    meta = {'tag': tag, 'time': time.strftime('%Y-%m-%d %H:%M:%S'), 'client': 'fx_views_client.py',
            'settle_s': SETTLE, 'pair_gap_s': PAIR_GAP}
    try:
        meta['sublevels'] = send('sublevels')
    except Exception as ex:
        meta['sublevels'] = 'error: %s' % ex
    print('sublevels:', str(meta['sublevels'])[:400])
    json.dump(meta, open(os.path.join(vh.OUT, tag + '_meta.json'), 'w'), indent=1)
    t_views = {}
    try:
        vh.front()
        vh.shot(tag, 'player_view')
        send('hud 0')
        print('timescale', send('timescale 1'))
        print('arm', send('watchdrive 1'), send('camlock 1'))   # once per session; look_safe never re-arms
        time.sleep(0.5)
        for name, cam, tgt in views:
            t0 = time.time()
            print(name, look_safe.go(cam, tgt))
            time.sleep(SETTLE)
            send('timescale 0')
            time.sleep(0.3)
            vh.front()
            vh.shot(tag, name)
            send('timescale 1')
            time.sleep(PAIR_GAP)
            send('timescale 0')
            time.sleep(0.3)
            vh.front()
            vh.shot(tag, name + '_b')
            send('timescale 1')
            t_views[name] = round(time.time() - t0, 1)
    finally:
        meta['view_seconds'] = t_views
        json.dump(meta, open(os.path.join(vh.OUT, tag + '_meta.json'), 'w'), indent=1)
        for cmd in ('timescale 1', 'camlock 0', 'watchdrive 0', 'hud 1'):
            try:
                send(cmd)
            except Exception as ex:      # the harness closes the game when its hold ends
                print(cmd, 'not sent:', ex)
                break
        print('released')


if __name__ == '__main__':
    main()
