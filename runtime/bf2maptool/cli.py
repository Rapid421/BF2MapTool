"""FrostyCLI driver: JSON pipelines (`run`) and direct commands, each with its own log file, output streamed to the
build log, and the results stages need parsed from the log (imported blueprints, error lines, transient clone failures).
"""
import json
import os
import re
import subprocess
import threading
import time

from .runner import StageError

TRANSIENT = ('EBX clone failed', 'Stream length must be non-negative')
DEFAULT_TIMEOUT = 45 * 60        # seconds one tool call may run (the longest measured: an import of 514 pieces, 2.5 min)
QUIET_TIMEOUT = 15 * 60          # seconds without a line of output (the longest measured silence: a closure audit, ~1 min)


def tool_env(job):
    env = dict(os.environ)
    tmp = job.dirs['tmp']
    os.makedirs(tmp, exist_ok=True)
    env['TMP'] = env['TEMP'] = env['TMPDIR'] = tmp
    return env


def _span(seconds):
    return '%d min' % (seconds // 60) if seconds >= 120 else '%d s' % seconds


def kill_tree(pid):
    """stop a tool and everything it started"""
    if os.name == 'nt':
        subprocess.run(['taskkill', '/F', '/T', '/PID', str(pid)], capture_output=True)
    else:
        try:
            os.kill(pid, 9)
        except OSError:
            pass


def spawn(cmd, log_path, log=None, env=None, cwd=None, echo=None, timeout=DEFAULT_TIMEOUT, quiet_timeout=QUIET_TIMEOUT):
    """run a tool, tee its output to log_path (and matching lines to the build log); returns (rc, text).

    No tool may sit stuck: it is stopped (with everything it started) when it runs longer than `timeout` seconds or
    prints nothing for `quiet_timeout` seconds; the caller then gets a StageError that says which limit was hit."""
    os.makedirs(os.path.dirname(log_path), exist_ok=True)
    lines = []
    state = {'last': time.time(), 'why': None}
    with open(log_path, 'w', encoding='utf-8', errors='replace') as lf:
        p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, env=env, cwd=cwd,
                             text=True, encoding='utf-8', errors='replace')
        t0 = time.time()

        def watch():
            while p.poll() is None:
                now = time.time()
                if now - t0 > timeout:
                    state['why'] = 'ran longer than %s' % _span(timeout)
                elif now - state['last'] > quiet_timeout:
                    state['why'] = 'printed nothing for %s' % _span(quiet_timeout)
                if state['why']:
                    kill_tree(p.pid)
                    return
                time.sleep(2)
        w = threading.Thread(target=watch, daemon=True)
        w.start()
        for line in p.stdout:
            state['last'] = time.time()
            lf.write(line)
            lines.append(line)
            if log and echo and echo.search(line):
                log(line.rstrip()[:300])
        p.wait()
        if state['why']:
            lf.write('\n[stopped by the build: %s]\n' % state['why'])
    if state['why']:
        raise StageError('%s %s and was stopped  [log: %s]' % (os.path.basename(str(cmd[0])), state['why'], log_path))
    return p.returncode, ''.join(lines)


class Result:
    def __init__(self, rc, text, log_path):
        self.rc, self.text, self.log_path = rc, text, log_path
        self.errors = [l.strip() for l in text.splitlines() if '] ERROR' in l or 'Unhandled exception' in l]
        self.transient = [l.strip() for l in text.splitlines() if any(t in l for t in TRANSIENT)]

    def blueprints(self, prefix):
        return set(re.findall(r'blueprint:\s*(%s/[^\s]+)' % re.escape(prefix), self.text))

    def grep(self, pattern):
        rx = re.compile(pattern)
        return [l.rstrip() for l in self.text.splitlines() if rx.search(l)]


class Cli:
    ECHO = re.compile(r'\] ERROR|Unhandled exception|Result:|audit: \d+ unresolved|EBX clone failed')   # the rest stays in the call's own log

    def __init__(self, job, log):
        self.job, self.log = job, log
        s = job.settings
        self.exe = s['frostycli']
        self.header = {'game': 'swbf2', 'game-path': s['game_path'], 'handler-plugins': s['plugins_path']}
        if not os.path.exists(self.exe):
            raise StageError('FrostyCLI was not found at %s. Set it in the add-on preferences.' % self.exe)

    def run(self, name, steps, check=True, env=None):
        """a JSON pipeline; `name` names the step file and the log"""
        sp = self.job.path('steps', name + '.json')
        os.makedirs(os.path.dirname(sp), exist_ok=True)
        with open(sp, 'w', encoding='utf-8') as f:
            json.dump(dict(self.header, steps=steps), f, indent=1)
        lp = self.job.path('logs', name + '.log')
        e = tool_env(self.job)
        e.update(env or {})
        self.log('FrostyCLI run %s (%d steps)' % (name, len(steps)))
        rc, text = spawn([self.exe, 'run', sp], lp, self.log, e, echo=self.ECHO)
        r = Result(rc, text, lp)
        # the clone failure is random (seen in import and in doors): a run that STOPPED on it is run again in a fresh
        # process - every pipeline starts from a saved project, so a rerun starts clean
        # only where the caller treats a stop as fatal (check): a stage that sorts out failed steps itself (the piece
        # import: 724 pieces, its own retry rounds) must not have its whole run repeated
        tries = 1
        while check and rc != 0 and r.transient and tries < 4:
            tries += 1
            if os.path.exists(lp):
                os.replace(lp, self.job.path('logs', '%s.try%d.log' % (name, tries - 1)))
            self.log('FrostyCLI run %s stopped on a transient failure (%s) - run %d of 4' % (name, r.transient[0][:80], tries))
            rc, text = spawn([self.exe, 'run', sp], lp, self.log, e, echo=self.ECHO)
            r = Result(rc, text, lp)
        if check and rc != 0:
            tail = ' | '.join(l.strip() for l in text.strip().splitlines()[-4:])
            raise StageError('FrostyCLI stopped in "%s" (exit %d): %s  [log: %s]' % (name, rc, tail[:600], lp))
        return r

    def direct(self, name, args, project=None, save_as=None, check=True, env=None):
        lp = self.job.path('logs', name + '.log')
        cmd = [self.exe, '--game', 'swbf2', '--game-path', self.header['game-path'], '--handler-plugins', self.header['handler-plugins']]
        if project:
            cmd += ['--project', project]
        if save_as:
            cmd += ['--save-as', save_as]
        cmd += list(args)
        e = tool_env(self.job)
        e.update(env or {})
        self.log('FrostyCLI %s' % ' '.join(str(a) for a in args[:6]))
        rc, text = spawn(cmd, lp, self.log, e, echo=self.ECHO)
        r = Result(rc, text, lp)
        if check and rc != 0:
            tail = ' | '.join(l.strip() for l in text.strip().splitlines()[-4:])
            raise StageError('FrostyCLI "%s" failed (exit %d): %s  [log: %s]' % (' '.join(args[:3]), rc, tail[:600], lp))
        return r


def load(project):
    return {'op': 'project-load', 'file': project}


def save(project):
    return {'op': 'project-save', 'file': project}
