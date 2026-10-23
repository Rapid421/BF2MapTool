"""run_test.py for one mod, then --release, as ONE command for harness_lock.py.
Waits (up to 6 min) for a game left over by the previous lock holder to close first: run_test.py refuses to start
while one runs ('a game is running'), and the brief says never to kill the game ourselves.

`--views v.json` is handed to run_test.py as `--client tools/fx_views_client.py --client-arg v.json`: the default
tour (views_hoth.py) freezes game time before moving the camera, which freezes the emitters, so distance-culled
effects never spawn at the views. fx_views_client.py keeps time running at each view and shoots a frame pair; it
needs a longer hold (MIN_HOLD, still under the 600 s cap)."""
import os, subprocess, sys, time
RT = r'F:\Frosty\projects\hoth-port\scripts\run_test.py'
CLIENT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'fx_views_client.py')
MIN_HOLD = 420


def game_running():
    r = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq starwarsbattlefrontii.exe'], capture_output=True, text=True)
    return 'starwarsbattlefrontii' in r.stdout.lower()


mod, tag = sys.argv[1], sys.argv[2]
extra = sys.argv[3:]
if '--views' in extra:
    i = extra.index('--views')
    extra[i:i + 2] = ['--client', CLIENT, '--client-arg', extra[i + 1]]
    if extra and extra[0].isdigit() and int(extra[0]) < MIN_HOLD:
        extra[0] = str(MIN_HOLD)
t0 = time.time()
while game_running() and time.time() - t0 < 360:
    time.sleep(10)
print('game running before start: %s (waited %.0f s)' % (game_running(), time.time() - t0), flush=True)
print('run_test.py', mod, tag, ' '.join(extra), flush=True)
rc = subprocess.run([sys.executable, RT, mod, 'AddedContent/Hoth2015/Levels/Hoth2015_01/Hoth2015_01', tag] + extra).returncode
subprocess.run([sys.executable, RT, '--release'])
sys.exit(rc)
