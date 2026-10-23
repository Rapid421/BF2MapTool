"""Hands-off game test with a watchdog (my test driver for THIS PC; not part of the shipped tool).

    python game_test.py <mod.fbmod> <level> <mode> --out <dir> [--shots shots.json] [--hold 240] [--no-deploy]

Limits (nothing may sit stuck):
  BUSY           a game is already running or the harness task is busy (somebody else's test): nothing is touched
  STALL (300 s)  no new line in the harness log AND no growth of the game log for this long -> the task run is ended.
                 Progress, however slow, is not a stall: with drive C: saturated the Mod Manager step alone took 6 min
                 (2026-10-12), and a fixed 10-minute verdict limit cut a run 19 s after the level had loaded.
  TOTAL (1500 s) over everything -> the task run is ended
  every `game ...` call has its own limit.
Once the world is up: `game deploy`, the camera height sampled 4 x 3 s apart (a soldier that falls through the floor
loses height), `game send players` (bots with a soldier), one `game shot` per viewpoint, then hold.release so the
harness closes the game. If the harness is gone but the game this test started still runs, that game (by pid) is
closed. Always written: <out>/result.json, gamelink_new.log, autotest_new.log.
Exit code 0 only when the level loaded and the game log shows no fault.
"""
import argparse
import atexit
import json
import os
import re
import shutil
import subprocess
import sys
import time

GL = 'F:/Frosty/gamelink'
GAMELOG = 'C:/ProgramData/FrostyGameLink/gamelink.log'
TASK = 'FrostyAutoTest'
CLI = ['I:/BF2MapTool/cli-wt/FrostyCLI/bin/x64/Release/FrostyCLI.exe', '--game', 'swbf2', '--game-path', 'F:/STAR WARS Battlefront II',
       '--handler-plugins', 'F:/Frosty/Frosty Editor/Plugins', 'game']
STALL, TOTAL = 480, 1800
LOGS = (GL + '/autotest.log', GL + '/autotest-2.log')     # the harness falls back to the second file whenever a reader
                                                          # holds the first one open at the moment it writes
NOISE = ('Loading', 'Initial', 'PluginRegistry', 'HandlerPlugins', 'SDK init', 'Reflection', 'oo2core', 'LanguageFormat', 'localization')


sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import launcher_dialogs
import screen

def sh(cmd, timeout=30):
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL)
        return r.returncode, (r.stdout or '') + (r.stderr or '')
    except subprocess.TimeoutExpired:
        return 124, 'TIMEOUT after %d s' % timeout


def game(args, timeout=90):
    rc, out = sh(CLI + args, timeout)
    return rc, [l for l in out.splitlines() if l.strip() and not any(n in l for n in NOISE)]


def game_pids():
    out = sh(['tasklist', '/NH', '/FO', 'CSV', '/FI', 'IMAGENAME eq starwarsbattlefrontii.exe'])[1]
    return [int(m) for m in re.findall(r'"starwarsbattlefrontii\.exe","(\d+)"', out, re.I)]


def task_status():
    for l in sh(['schtasks', '/Query', '/TN', TASK, '/FO', 'LIST'])[1].splitlines():
        if l.strip().lower().startswith('status:'):
            return l.split(':', 1)[1].strip()
    return '?'


def end_task():
    return sh(['schtasks', '/End', '/TN', TASK])[1].strip()


def size(p):
    try:
        return os.path.getsize(p)
    except OSError:
        return 0


def harness_processes(since):
    """What the harness is doing when its log is silent: its PowerShell (started by the task after `since`), CPU seconds
    used, and what it has started (csc = compiling its helpers, FrostyModManager, the game). Silence with CPU time
    still rising or a child at work is SLOW, not stuck (2026-10-12: first log line 3 min 20 s after the start)."""
    ps = ("$t=[datetime]::ParseExact('%s','yyyy-MM-dd HH:mm:ss',$null);"
          "Get-CimInstance Win32_Process | Where-Object { $_.CreationDate -ge $t -and $_.Name -match "
          "'^(powershell|csc|cvtres|FrostyModManager|starwarsbattlefrontii|EADesktop)' } | ForEach-Object { '{0}|{1}|{2}|{3:N2}' -f "
          "$_.Name,$_.ProcessId,$_.ParentProcessId,(($_.KernelModeTime+$_.UserModeTime)/1e7) }") % time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(since - 2))
    rc, out = sh(['powershell', '-NoProfile', '-NonInteractive', '-Command', ps], 40)
    rows = []
    for l in out.splitlines():
        q = l.strip().split('|')
        if len(q) == 4:
            try:
                rows.append({'name': q[0], 'pid': int(q[1]), 'ppid': int(q[2]), 'cpu_s': float(q[3].replace(',', '.'))})
            except ValueError:
                pass
    me = os.getpid()
    return [r for r in rows if not (r['name'].lower().startswith('powershell') and r['cpu_s'] < 0.01)]


def tail_from(p, offset):
    try:
        with open(p, 'rb') as f:
            f.seek(offset)
            return f.read().decode('utf-8', 'replace')
    except OSError:
        return ''


QLOCK = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'logs', 'game_test.lock'))
SHOT_TMP = os.path.dirname(QLOCK)          # an ASCII folder for shots whose --out path has accents


def unlock():
    try:
        if os.path.exists(QLOCK + '/pid') and open(QLOCK + '/pid').read().strip() not in (str(os.getpid()), ''):
            if pid_alive(int(open(QLOCK + '/pid').read().strip())):
                return                                    # somebody else's lock
        if os.path.exists(QLOCK + '/pid'):
            os.remove(QLOCK + '/pid')
        os.rmdir(QLOCK)
    except OSError:
        pass


def pid_alive(pid):
    rc, out = sh(['tasklist', '/FI', 'PID eq %d' % pid, '/NH'])
    return str(pid) in out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('mod'); ap.add_argument('level'); ap.add_argument('mode')
    ap.add_argument('--out', required=True)
    ap.add_argument('--hold', type=int, default=240)
    ap.add_argument('--shots')
    ap.add_argument('--no-deploy', action='store_true')
    ap.add_argument('--play', action='store_true', help='launch for a person to play: once the level is up, leave the game running '
                    '(use with a long --hold); no deploy, no shots, no closing')
    ap.add_argument('--load-shots', type=int, default=0, help='take up to this many screen pictures while the level loads (every 10 s)')
    ap.add_argument('--then', help='a command line run after the shots while the game is still up (a probe); 240 s limit')
    ap.add_argument('--then-limit', type=int, default=240, help='time limit of --then in seconds (default 240)')
    ap.add_argument('--fov', type=float, default=0.0, help='force this field of view (degrees) for the pictures, to match a Blender camera')
    ap.add_argument('--wait', type=int, default=0, help='queue: wait up to this many seconds while another test or game is on, instead of BUSY')
    ap.add_argument('--attempt', type=int, default=1, help='set by the test itself when it retries a launch that died')
    ap.add_argument('--leftover-ok', action='store_true', help='a Mod Manager / game left by MY OWN ended run may be up: the harness closes it')
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    res = {'mod': a.mod, 'level': a.level, 'mode': a.mode, 'started': time.strftime('%Y-%m-%d %H:%M:%S'), 'status': 'ERROR', 'notes': []}
    t0 = time.time()
    gl0, al0 = size(GAMELOG), [size(x) for x in LOGS]
    mine = []

    def finish(status, note=''):
        res['status'] = status
        if note:
            res['notes'].append(note)
        # the harness is gone but the game this test started is still up: close that game (by pid)
        time.sleep(3)
        left = [p for p in game_pids() if p in mine]
        if left and status != 'BUSY':
            for p in left:
                res['notes'].append('closing the game this test started (pid %d): %s' % (p, sh(['taskkill', '/F', '/PID', str(p)])[1].strip()[:120]))
            time.sleep(3)
        gl = tail_from(GAMELOG, gl0)
        with open(a.out + '/gamelink_new.log', 'w', encoding='utf-8') as f:
            f.write(gl)
        with open(a.out + '/autotest_new.log', 'w', encoding='utf-8') as f:
            f.write(''.join(tail_from(x, o) for x, o in zip(LOGS, al0)))
        res['loaded_lines'] = [l.strip()[:200] for l in gl.splitlines() if 'LOADED LEVEL' in l][:5]
        # 0x40010006 / 0x4001000A are Windows' "a debug string was printed" exceptions (DBG_PRINTEXCEPTION_C / _WIDE_C):
        # the process goes on; they are listed apart and do not fail a test
        allf = [l.strip()[:200] for l in gl.splitlines() if 'fault: code' in l]
        res['debug_prints'] = [l for l in allf if re.search(r'code 0x(40010006|4001000A)\b', l, re.I)][:5]
        res['faults'] = [l for l in allf if not re.search(r'code 0x(40010006|4001000A)\b', l, re.I)][:5]
        res['game_still_running'] = bool(game_pids())
        res['seconds'] = round(time.time() - t0, 1)
        res['finished'] = time.strftime('%Y-%m-%d %H:%M:%S')
        ok = bool(res['loaded_lines']) and not res['faults'] and status in ('HELD', 'RELEASED', 'LOADED', 'CLOSED_BY_TEST')
        res['ok'] = ok
        res['attempt'] = a.attempt
        if status in ('CRASH', 'STALL') and not res['loaded_lines'] and a.attempt < 2:
            # the LAUNCH died (no level was ever loaded): look at the screen, wait, try once more
            try:
                res['screen_at_failure'] = a.out + '/screen_attempt%d.png' % a.attempt
                screen.shot(res['screen_at_failure'])
                res['launcher_windows_at_failure'] = [w['rect'] for w in launcher_dialogs.dialog_windows()]
            except Exception as e:
                res['notes'].append('screenshot failed: %s' % e)
            with open(a.out + '/result_attempt%d.json' % a.attempt, 'w', encoding='utf-8') as f:
                json.dump(res, f, indent=1)
            print('launch died (%s) - retrying once in 60 s' % status, flush=True)
            time.sleep(60)
            cmd = [sys.executable, os.path.abspath(__file__)] + [x for x in sys.argv[1:] if x != '--leftover-ok'] + ['--attempt', str(a.attempt + 1), '--leftover-ok']
            sys.exit(subprocess.call(cmd))
        with open(a.out + '/result.json', 'w', encoding='utf-8') as f:
            json.dump(res, f, indent=1)
        print(json.dumps(res, indent=1))
        sys.exit(0 if ok else 1)

    # queue: one game test at a time. The lock is a directory (creating it is atomic) holding the owner's pid; a lock
    # whose owner is gone is cleared. A game that is up and is not this queue's is never touched - only waited for.
    if a.wait and a.attempt == 1:
        tw = time.time()
        while True:
            owner = None
            try:
                os.mkdir(QLOCK)
                with open(QLOCK + '/pid', 'w') as f:
                    f.write(str(os.getpid()))
                if not game_pids() and task_status().lower() == 'ready':
                    atexit.register(unlock)
                    break
                unlock()                                      # something not of this queue is running: step back
            except FileExistsError:
                try:
                    owner = int(open(QLOCK + '/pid').read().strip())
                except Exception:
                    owner = None
                if owner is not None and not pid_alive(owner):
                    unlock()
                    continue
            if time.time() - tw > a.wait:
                finish('BUSY', 'waited %d s in the queue (lock owner %s): gave up' % (a.wait, owner))
            time.sleep(10)
        res['queued_s'] = round(time.time() - tw, 1)
        t0 = time.time()
        gl0, al0 = size(GAMELOG), [size(x) for x in LOGS]
    if game_pids() and not a.leftover_ok:
        finish('BUSY', 'a game is already running (not started by this test): not touched')
    try:
        stale = launcher_dialogs.dialog_windows()
        if stale:
            screen.shot(a.out + '/stale_dialog.png')
            res['notes'].append('a launcher dialog was already on screen before the test (%s): closing it with Cancel - %s' % (
                stale[0]['rect'], 'closed' if launcher_dialogs.dismiss(stale[0]) else 'STILL OPEN'))
    except Exception as e:
        res['notes'].append('stale dialog check failed: %s' % e)
    st = task_status()
    if st.lower() != 'ready':
        finish('BUSY', 'the harness task is %s (another test): not touched' % st)
    # several mods: "a.fbmod;b.fbmod" in load order (the harness puts them into the pack in that order)
    if not all(os.path.exists(m) for m in a.mod.split(';') if m):
        finish('ERROR', 'mod not found')
    args = {'mod': a.mod.replace('/', '\\'), 'level': a.level, 'mode': a.mode, 'timeout': TOTAL - 300, 'settle': 20, 'keep': True,
            'deploy': False, 'skipfirst': True, 'hold': a.hold}
    with open(GL + '/autotest-args.json', 'w') as f:
        json.dump(args, f, indent=1)
    result_file = GL + '/autotest-result.json'
    # launcher dialogs (the EA app's "sync was interrupted" ...) stop a launch without any line in the harness log:
    # watch the launcher's own log from here on and answer the known ones with the mouse / keyboard
    dialogs = launcher_dialogs.Watcher()
    res['launcher_dialogs'] = []
    r0 = os.path.getmtime(result_file) if os.path.exists(result_file) else 0
    rc, out = sh(['schtasks', '/Run', '/TN', TASK])
    res['notes'].append('task run: ' + out.strip()[:120])
    if rc != 0:
        finish('ERROR', 'the harness task could not be started')
    # wait for the world, watching for progress
    last_sizes, last_change = (tuple(al0), gl0), time.time()
    log, last_ps, last_cpu, last_cpu_change = '', 0.0, -1.0, time.time()
    res['harness_checks'] = []
    while True:
        cur_sizes = (tuple(size(x) for x in LOGS), size(GAMELOG))
        if cur_sizes != last_sizes or not log:
            log = ''.join(tail_from(x, o) for x, o in zip(LOGS, al0))
        for m in re.finditer(r'injected into pid (\d+)', log):
            if int(m.group(1)) not in mine:
                mine.append(int(m.group(1)))
        if 'first_line_after_s' not in res and log.strip():
            res['first_line_after_s'] = round(time.time() - t0, 1)
        if 'RESULT: LOADED' in log or 'hold: watching the world' in log:
            break
        # --load-shots: the screen while the level loads (its loading screen), every 10 s once the game is up
        if a.load_shots and 'first_line_after_s' in res and len(res.setdefault('load_shots', [])) < a.load_shots and \
                time.time() - res.get('_last_load_shot', 0) >= 10:
            res['_last_load_shot'] = time.time()
            f = a.out + '/loading_%02d.png' % (len(res['load_shots']) + 1)
            try:
                screen.shot(f)
                res['load_shots'].append({'file': os.path.basename(f), 't': time.strftime('%H:%M:%S')})
            except Exception as e:                       # noqa: BLE001 - a picture is an extra
                res['load_shots'].append({'error': str(e)[:120]})
        for ev in dialogs.new_dialogs():
            ev['answered'] = launcher_dialogs.answer(ev, a.out, res['notes'].append)
            ev['t'] = time.strftime('%H:%M:%S')
            res['launcher_dialogs'].append(ev)
            last_change = time.time()
        if 'RESULT:' in log:
            line = [l for l in log.splitlines() if 'RESULT:' in l][-1]
            finish(line.split('RESULT:', 1)[1].split('-')[0].strip() or 'ERROR', line.strip()[:300])
        if cur_sizes != last_sizes:
            last_sizes, last_change = cur_sizes, time.time()
        quiet = time.time() - last_change
        if quiet > 60 and time.time() - last_ps > 60:
            # the log is silent: look at the harness's own processes before calling it stuck
            last_ps = time.time()
            rows = harness_processes(t0)
            cpu = sum(r['cpu_s'] for r in rows)
            kids = [r['name'] for r in rows if not r['name'].lower().startswith('powershell')]
            if cpu > last_cpu + 0.05 or kids:
                last_cpu_change = time.time()
            last_cpu = max(last_cpu, cpu)
            res['harness_checks'].append({'t': time.strftime('%H:%M:%S'), 'quiet_s': int(quiet), 'cpu_s': round(cpu, 2), 'working': kids,
                                          'processes': len(rows)})
        # stuck = the logs silent for STALL s AND the harness's processes idle (no CPU time added, nothing started) for 180 s
        if quiet > STALL and time.time() - last_cpu_change > 180:
            finish('STALL', 'no progress in the harness logs or the game log for %d s and its processes idle; task run ended: %s' % (int(quiet), end_task()))
        if time.time() - t0 > TOTAL:
            finish('TOTAL_TIMEOUT', 'limit %d s; task run ended: %s' % (TOTAL, end_task()))
        time.sleep(5)
    res['world_up_after_s'] = round(time.time() - t0, 1)
    for p in game_pids():
        if p not in mine:
            mine.append(p)
    if a.play:
        # --play: the level is up - hand it to the person at the PC. Nothing more is sent to the game (no deploy, no
        # camera, no shots) and it is not closed; the in-game harness was given a hold of a.hold seconds
        res['status'] = 'PLAYING'
        res['notes'].append('--play: the game is left running for the player; nothing is sent to it and it is not closed')
        with open(a.out + '/result.json', 'w', encoding='utf-8') as f:
            json.dump(res, f, indent=1)
        print(json.dumps(res, indent=1))
        sys.exit(0)
    try:
        if a.fov > 0:
            # BEFORE the deploy: forcefov takes the local player out of his soldier (seen in the gamelink log of the MatTest
            # runs, 2026-10-23) - sent after the deploy, every shot then had to redeploy, and that failed twice
            rc, o = game(['send', 'forcefov', '%g' % a.fov], 30)
            res['fov'] = {'asked': a.fov, 'said': (o[-1][:120] if o else 'rc %d' % rc), 'when': 'before the deploy'}
        if not a.no_deploy:
            rc, o = game(['deploy', '--timeout', '60'], 110)
            res['deploy'] = o[-1][:200] if o else 'rc %d' % rc
            samples = []
            for _ in range(4):
                t = time.time()
                rc, o = game(['where'], 40)
                m = re.search(r'camera pos ([-\d.]+) ([-\d.]+) ([-\d.]+)', '\n'.join(o))
                samples.append([float(x) for x in m.groups()] if m else None)
                time.sleep(max(0.0, 3.0 - (time.time() - t)))
            res['stand_samples'] = samples
            ys = [s[1] for s in samples if s]
            res['stand_height_drop'] = round(max(ys) - min(ys), 3) if ys else None
        rc, o = game(['send', 'players'], 40)
        pl = '\n'.join(o)
        res['bots_with_a_soldier'] = len(re.findall(r"localId 255[^\n]*\n\s*controlled 0x[0-9A-Fa-f]*[1-9A-Fa-f]", pl))
        shots = json.load(open(a.shots)) if a.shots else []
        res['shots'] = []
        for s in shots:
            # "repeat": n takes the same view n times, "gap" seconds apart (a door opening as a bot passes, a light flicker)
            for k in range(int(s.get('repeat', 1))):
                if time.time() - t0 > TOTAL - 150:
                    res['notes'].append('shots cut short by the total limit'); break
                name = s['name'] if int(s.get('repeat', 1)) == 1 else '%s_%02d' % (s['name'], k)
                png = '%s/%s.png' % (a.out, name)
                if os.path.exists(png):
                    os.remove(png)
                # the in-game writer cannot open a path with accents ('awk wörk/...': every shot missing, 2026-10-23):
                # such shots go to an ASCII temp file first
                tmp = png if png.isascii() else os.path.join(SHOT_TMP, 'bf2_shot_%d_%s.png' % (os.getpid(), re.sub(r'[^A-Za-z0-9_]', '_', name)))
                if tmp != png and os.path.exists(tmp):
                    os.remove(tmp)
                rc, o = game(['shot', '--at', ','.join('%.2f' % v for v in s['at']), '--look', ','.join('%.2f' % v for v in s['look']), '--out', tmp], 90)
                if tmp != png and os.path.exists(tmp):
                    shutil.move(tmp, png)
                res['shots'].append({'name': name, 'ok': os.path.exists(png), 't': time.strftime('%H:%M:%S')})
                if k + 1 < int(s.get('repeat', 1)):
                    time.sleep(float(s.get('gap', 4.0)))
        if a.then and time.time() - t0 < TOTAL - 180 - a.then_limit:
            env = dict(os.environ, BF2_TEST_OUT=a.out)
            try:
                with open(a.out + '/then.log', 'w', encoding='utf-8') as lf:
                    rc = subprocess.call(a.then, shell=True, stdout=lf, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, env=env, timeout=a.then_limit)
                res['then'] = {'cmd': a.then, 'rc': rc}
            except subprocess.TimeoutExpired:
                res['then'] = {'cmd': a.then, 'rc': 'TIMEOUT 240 s'}
    finally:
        with open(GL + '/hold.release', 'w') as f:
            f.write(time.strftime('%H:%M:%S'))
    # the harness closes the game and writes its final verdict
    t1 = time.time()
    while True:
        if os.path.exists(result_file) and os.path.getmtime(result_file) > r0:
            try:
                v = json.load(open(result_file, encoding='utf-8-sig'))
            except ValueError:
                v = {}
            # the harness keeps status LOADED and adds "hold": RELEASED / HELD / HOLD_CRASH / HOLD_EXIT when the hold ends
            hold = v.get('hold')
            if hold in ('HELD', 'RELEASED', 'HOLD_CRASH', 'HOLD_EXIT') or v.get('status') in ('ERROR', 'CRASH', 'TIMEOUT'):
                res['harness'] = v
                finish(hold or v['status'], (v.get('hold_detail') or v.get('detail', ''))[:300])
        if time.time() - t1 > 120 or time.time() - t0 > TOTAL:
            end_task()
            finish('CLOSED_BY_TEST', 'the harness did not close the game within 120 s of the release; this test closed it')
        time.sleep(3)


if __name__ == '__main__':
    main()
