"""Stage runner: one build.json per map records, per stage, the hash of its inputs and its result, so a re-run redoes
only what changed and a killed build resumes at the stage it died in.

Lines starting with ``@@`` on stdout are the machine-readable progress feed the add-on reads:
    @@STAGE <name> start|skip|done|fail <seconds> <one-line summary>
    @@DONE ok|fail|blocked <message>
"""
import json
import os
import sys
import time
import traceback

from . import config


class StageError(Exception):
    """a stage failed for a reason the user can act on; the message is shown as is"""


class Stage:
    def __init__(self, name, func, deps=(), inputs=None, outputs=None, version=1, title=None):
        self.name, self.func, self.deps = name, func, tuple(deps)
        self.inputs = inputs or (lambda job: None)
        self.outputs = outputs or (lambda job: [])
        self.version, self.title = version, title or name


class Log:
    """build log: every line goes to the log file and to stdout (the add-on tails either)"""

    def __init__(self, path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        self.f = open(path, 'a', encoding='utf-8')
        self.t0 = time.time()
        self.stage = '-'

    def __call__(self, *parts):
        line = '[%7.1fs] [%s] %s' % (time.time() - self.t0, self.stage, ' '.join(str(p) for p in parts))
        self.f.write(line + '\n'); self.f.flush()
        print(line, flush=True)

    def feed(self, line):
        self.f.write(line + '\n'); self.f.flush()
        print(line, flush=True)


def _pid_alive(pid):
    if pid <= 0:
        return False
    if os.name == 'nt':
        import ctypes
        h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)        # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            return False
        code = ctypes.c_ulong()
        ok = ctypes.windll.kernel32.GetExitCodeProcess(h, ctypes.byref(code))
        ctypes.windll.kernel32.CloseHandle(h)
        return bool(ok) and code.value == 259                              # STILL_ACTIVE
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


class Lock:
    """one build per work folder (two builds on one folder once raced on a log and a project)"""

    def __init__(self, path):
        self.path = path

    def __enter__(self):
        if os.path.exists(self.path):
            try:
                pid = int(open(self.path).read().strip() or 0)
            except ValueError:
                pid = 0
            if pid != os.getpid() and _pid_alive(pid):
                raise StageError('Another build of this map is already running (process %d). Cancel it first.' % pid)
        with open(self.path, 'w') as f:
            f.write(str(os.getpid()))
        return self

    def __exit__(self, *a):
        try:
            os.remove(self.path)
        except OSError:
            pass


def load_state(job):
    if os.path.exists(job.state_path):
        try:
            with open(job.state_path, 'r', encoding='utf-8') as f:
                return json.load(f)
        except (ValueError, OSError):
            pass
    return {'version': 1, 'map': job.name, 'stages': {}}


def save_state(job, state):
    tmp = job.state_path + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(state, f, indent=1)
    os.replace(tmp, job.state_path)


def stage_hash(job, stage, hashes):
    return config.data_hash({'stage': stage.name, 'version': stage.version, 'inputs': stage.inputs(job),
                             'deps': [hashes[d] for d in stage.deps]})


def decide(job, st, state, hashes, dirty, force=()):
    """(hash, reason or None) for ONE stage, with the files as they are NOW. Called right before the stage would run:
    a stage's inputs are often files an earlier stage of the same run has just rewritten, so hashing everything up
    front would store a stale hash and redo the stage needlessly on the next run."""
    h = stage_hash(job, st, hashes)
    hashes[st.name] = h
    rec = state['stages'].get(st.name) or {}
    reason = None
    if st.name in force or 'all' in force:
        reason = 'forced'
    elif rec.get('status') != 'ok':
        reason = 'not built yet' if not rec else 'last run: %s' % rec.get('status')
    elif rec.get('hash') != h:
        reason = 'inputs changed'
    elif any(d in dirty for d in st.deps):
        reason = 'an earlier stage is rebuilt'
    else:
        missing = [p for p in st.outputs(job) if not os.path.exists(p)]
        if missing:
            reason = 'output missing: ' + os.path.basename(missing[0])
    if reason:
        dirty.add(st.name)
    return h, reason


def plan(job, stages, state, force=()):
    """[(stage, hash, needs_run, reason)] in order, from the files as they are now (a stage after one that will be
    redone is reported as redone too when it depends on it)"""
    hashes, out, dirty = {}, [], set()
    for st in stages:
        try:
            h, reason = decide(job, st, state, hashes, dirty, force)
        except (OSError, ValueError, KeyError) as e:      # its input files do not exist yet
            h, reason = None, 'inputs not built yet'
            hashes[st.name] = 'pending'
            dirty.add(st.name)
        out.append((st, h, reason is not None, reason))
    return out


def run(job, stages, only=None, until=None, force=(), log=None):
    """run the pipeline; returns True when every requested stage is ok"""
    job.makedirs()
    log = log or Log(job.path('logs', 'build.log'))
    try:
        with Lock(job.work + '/build.lock'):
            state = load_state(job)
            names = [st.name for st in stages]
            if until and until not in names:
                raise StageError('unknown stage %r; stages: %s' % (until, ', '.join(names)))
            todo = stages[:names.index(until) + 1] if until else stages
            hashes, dirty = {}, set()
            for st in todo:
                h, reason = decide(job, st, state, hashes, dirty, force)
                needs = reason is not None
                if only and st.name not in only:
                    continue
                log.stage = st.name
                if not needs:
                    log.feed('@@STAGE %s skip 0 up to date' % st.name)
                    continue
                log.feed('@@STAGE %s start 0 %s' % (st.name, reason))
                t0 = time.time()
                state['stages'][st.name] = {'status': 'running', 'hash': h, 'started': time.strftime('%Y-%m-%d %H:%M:%S')}
                save_state(job, state)
                try:
                    summary = st.func(job, log) or {}
                except StageError as e:
                    state['stages'][st.name].update(status='failed', error=str(e), seconds=round(time.time() - t0, 1))
                    save_state(job, state)
                    log.feed('@@STAGE %s fail %.1f %s' % (st.name, time.time() - t0, str(e).replace('\n', ' | ')))
                    log.feed('@@DONE fail %s: %s' % (st.title, str(e).replace('\n', ' | ')))
                    return False
                except Exception as e:                                     # a bug or an unexpected tool failure
                    tb = traceback.format_exc()
                    log(tb)
                    state['stages'][st.name].update(status='failed', error='%s: %s' % (type(e).__name__, e), seconds=round(time.time() - t0, 1))
                    save_state(job, state)
                    log.feed('@@STAGE %s fail %.1f %s: %s' % (st.name, time.time() - t0, type(e).__name__, str(e).replace('\n', ' | ')))
                    log.feed('@@DONE fail %s stopped with an internal error (see the log)' % st.title)
                    return False
                dt = round(time.time() - t0, 1)
                state['stages'][st.name].update(status='ok', seconds=dt, finished=time.strftime('%Y-%m-%d %H:%M:%S'), summary=summary)
                save_state(job, state)
                log.feed('@@STAGE %s done %.1f %s' % (st.name, dt, json.dumps(summary, default=str)[:400]))
            log.stage = '-'
            log.feed('@@DONE ok')
            return True
    except StageError as e:
        log.feed('@@DONE blocked %s' % e)
        return False
