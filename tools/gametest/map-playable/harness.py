"""The shared in-game harness, used the way the other sessions use it.

claim(tag) / release(): F:\\Frosty\\gamelink\\harness.claim ("owner: map-playable session ...") -- never launch while
another session's claim exists or a game runs. launch(mod, level, ...) writes autotest-args.json and runs the elevated
scheduled task FrostyAutoTest; wait_verdict() returns the autotest-result.json of this run. release() puts
autotest-args.json back to deploy false / hold 0 (no module, no menu mode) and deletes our claim.

  python harness.py --release        release the harness
  python harness.py --status         who holds it, is a game running
"""
import json
import os
import subprocess
import sys
import time

GL = r'F:\Frosty\gamelink'
CLAIM = os.path.join(GL, 'harness.claim')
ARGS = os.path.join(GL, 'autotest-args.json')
RESULT = os.path.join(GL, 'autotest-result.json')
PROBE = r'I:\SullustArchive\gamelink_probe\bin\FrostyGameLink.dll'
OWNER = 'map-playable session'
ENV = dict(os.environ, MSYS_NO_PATHCONV='1')
MODMANAGER = r'I:\FrostyModManager\FrostyModManager\FrostyModManager.exe'
MODSDIR = r'I:\FrostyModManager\FrostyModManager\Mods\starwarsbattlefrontii'


def game_running():
    r = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq starwarsbattlefrontii.exe'], capture_output=True, text=True)
    return 'starwarsbattlefrontii' in r.stdout.lower()


def claim_text():
    try:
        return open(CLAIM).read()
    except OSError:
        return None


def ours():
    t = claim_text()
    return t is not None and OWNER in t


def claim(tag, wait=0):
    """Take the harness. With wait > 0, wait up to that many seconds for another claim / game to go away.
    The claim file is created exclusively (never overwritten) and re-read after a pause, so two sessions claiming in the
    same moment cannot both believe they hold it."""
    t0 = time.time()
    text = 'owner: %s (%s), since %s\n' % (OWNER, tag, time.strftime('%Y-%m-%d %H:%M:%S'))
    while True:
        t = claim_text()
        if t is not None and OWNER in t:
            break                                       # already ours (a kept claim from the previous run)
        if t is None and not game_running():
            try:
                fd = os.open(CLAIM, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                os.write(fd, text.encode())
                os.close(fd)
                time.sleep(3)
                if OWNER in (claim_text() or '') and not game_running():
                    break
            except FileExistsError:
                pass                                    # someone else was quicker
        if time.time() - t0 >= wait:
            raise SystemExit('harness busy: ' + (t.strip() if t else 'a game is running'))
        time.sleep(3)                                   # other sessions release between their runs: a short poll


def release():
    try:
        a = json.load(open(ARGS, encoding='utf-8-sig'))
    except Exception:
        a = {}
    a['deploy'] = False
    a['hold'] = 0
    for k in ('module', 'menu', 'launchonly'):
        a.pop(k, None)
    json.dump(a, open(ARGS, 'w'), indent=1)
    if ours():
        os.remove(CLAIM)
    subprocess.run(['schtasks', '/End', '/TN', 'FrostyAutoTest'], env=ENV, capture_output=True)
    print('released harness (args deploy false / hold 0, claim removed)')


# FrostyAutoTest runs under a 20 min execution limit (ExecutionTimeLimit PT20M): when it is hit, Windows ends the script
# before its own Stop-Game, and the game is left running with nobody to close it (2026-09-25, a 1500 s menu hold). The
# pack apply (up to ~5 min when forced), the load and the hold must fit in it.
MAX_HOLD = 720


def launch(mod, level, mode='SkirmishBlast', hold=300, deploy=True, probe=True, timeout=600, menu=False):
    if hold > MAX_HOLD:
        print('hold %d s capped to %d s: the harness task stops after 20 min' % (hold, MAX_HOLD), flush=True)
        hold = MAX_HOLD
    if not ours():
        raise SystemExit('launch refused: the harness is not claimed by this session')
    if game_running():
        raise SystemExit('launch refused: a game is running')
    a = {'mod': mod, 'level': level, 'mode': mode, 'timeout': timeout, 'settle': 20, 'keep': True, 'deploy': deploy,
         'skipfirst': True, 'hold': hold, 'modmanager': MODMANAGER, 'modsdir': MODSDIR}
    if probe and not menu:
        a['module'] = PROBE
    if menu:
        a.update({'menu': True, 'launchonly': True, 'deploy': False})
    json.dump(a, open(ARGS, 'w'), indent=1)
    t0 = time.time()
    subprocess.run(['schtasks', '/Run', '/TN', 'FrostyAutoTest'], env=ENV, capture_output=True)
    return t0


def wait_verdict(mod, t0, limit=900):
    while time.time() - t0 < limit:
        time.sleep(5)
        if os.path.exists(RESULT) and os.path.getmtime(RESULT) > t0:
            try:
                r = json.load(open(RESULT, encoding='utf-8-sig'))
            except Exception:
                continue
            if r.get('mod', '').lower() == mod.lower() and r.get('status'):
                return r
    return None


def wait_closed(t0, limit):
    while game_running() and time.time() - t0 < limit:
        time.sleep(5)
    subprocess.run(['schtasks', '/End', '/TN', 'FrostyAutoTest'], env=ENV, capture_output=True)
    return not game_running()


if __name__ == '__main__':
    if '--release' in sys.argv:
        release()
    elif '--status' in sys.argv:
        print('claim:', (claim_text() or 'none').strip(), '| game running:', game_running())
