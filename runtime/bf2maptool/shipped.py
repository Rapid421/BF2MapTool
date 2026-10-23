"""The levels the game ships, so a new level cannot take the name of one.

The list comes from the game itself (`ls --type LevelData`) and is kept in the work folder; it is read again when the
game folder changes. The pre-flight check uses the kept list (it must not start the CLI); the `shell` stage makes the
list when it is missing and checks again.
"""
import json
import os
import re


def _path(job):
    return os.path.join(job.settings['work_dir'], 'shipped_levels.json')


FALLBACK = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'data', 'shipped_levels.json')


def load(job):
    """the work folder's own list; before the first build has made one, the list that ships with the runtime (so a
    new user's first Check already catches a taken name instead of the build stopping in the shell stage)"""
    try:
        with open(_path(job), encoding='utf-8') as f:
            d = json.load(f)
        if os.path.normcase(os.path.normpath(d.get('game_path', ''))) == os.path.normcase(os.path.normpath(job.settings['game_path'])):
            return d.get('levels')
    except (OSError, ValueError):
        pass
    try:
        with open(FALLBACK, encoding='utf-8') as f:
            return json.load(f).get('levels')
    except (OSError, ValueError):
        return None


def refresh(job, clix):
    r = clix.direct('shipped_levels', ['ls', '--type', 'LevelData'], check=False)
    levels = sorted(set(re.findall(r'ls -> (\S+)', r.text)))
    if r.rc != 0 or not levels:
        return None
    with open(_path(job), 'w', encoding='utf-8') as f:
        json.dump({'game_path': job.settings['game_path'], 'levels': levels}, f, indent=1)
    return levels


def clash(job, levels=None):
    """the shipped level whose name the job's level name would take, or None (also None when the list is not known yet)"""
    levels = load(job) if levels is None else levels
    want = job.name.lower()
    for path in levels or []:
        last = path.rsplit('/', 1)[-1].lower()
        if want in (last, re.sub(r'_\d+$', '', last)):
            return path
    return None
