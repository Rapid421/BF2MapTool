"""Combine maps: one small MENU mod that lists every map of several map mods.

Each map mod brings its own copy of the shared menu / registration assets (LevelListReport, the game-mode lists,
OptionsEnumUIMetaData, the Skirmish / Instant Action screens). Loaded together, the last mod's copy wins and only its
map shows in the menu. FrostyCLI `mod merge <mods> --menu-only` merges just those assets into one project; mod-build
writes it as a ~50 KB mod. Load it LAST (below every map) in Frosty Mod Manager or in the Kyber mod list.
Measured 2026-10-18: 4 map mods + the menu mod listed and loaded every map; the full unpack + rebuild route took 11 min
and lost bundles (black loading screen).

    python -m bf2maptool menu-mod --settings settings.json --out Maps_Menu.fbmod --mods a.fbmod;b.fbmod;...
"""
import json
import os
import re

from . import cli as cli_mod


def run(settings, out, mods, name=None, log=print, timeout=1800):
    """-> dict with 'ok', 'out', 'maps', 'changes', 'log'"""
    mods = [os.path.abspath(m) for m in mods]
    missing = [m for m in mods if not os.path.isfile(m)]
    if missing:
        return {'ok': False, 'error': 'mod file(s) not found: %s' % ', '.join(missing)}
    if len(mods) < 2:
        return {'ok': False, 'error': 'pick at least two map mods to combine'}
    out = os.path.abspath(out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    maps = [os.path.splitext(os.path.basename(m))[0] for m in mods]
    name = name or 'Maps menu (%s)' % ', '.join(maps)
    steps = [{'op': 'project-new'},
             {'op': 'cli', 'args': ['mod', 'merge'] + mods + ['--menu-only']},
             {'op': 'mod-build', 'out': out, 'mod-name': name[:60], 'mod-author': settings.get('author', ''), 'mod-version': '1.0',
              'mod-description': 'Menu of: %s. Made by the BF2 Map Tool - load it LAST, below these map mods.' % ', '.join(maps)}]
    sp = out + '.steps.json'
    with open(sp, 'w', encoding='utf-8') as f:
        json.dump({'game': 'swbf2', 'game-path': settings['game_path'], 'handler-plugins': settings['plugins_path'], 'steps': steps}, f, indent=1)
    lp = out + '.log'
    tmp = os.path.join(os.path.dirname(out), '_menumod_tmp')
    os.makedirs(tmp, exist_ok=True)
    env = dict(os.environ, TMP=tmp, TEMP=tmp, TMPDIR=tmp)
    if os.path.exists(out):
        os.remove(out)
    rc, text = cli_mod.spawn([settings['frostycli'], 'run', sp], lp, log, env, timeout=timeout, quiet_timeout=900)
    changes = [l.strip()[:200] for l in text.splitlines() if re.search(r'mod merge|change\(s\)|CONFLICT|REFUSED', l)]
    ok = rc == 0 and os.path.isfile(out)
    return {'ok': ok, 'out': out if ok else None, 'maps': maps, 'changes': changes[-12:], 'log': lp, 'rc': rc,
            'bytes': os.path.getsize(out) if ok else 0,
            'error': None if ok else 'FrostyCLI could not make the menu mod (exit %s)  [log: %s]' % (rc, lp)}
