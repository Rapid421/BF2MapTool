"""Tonight's chain (2026-10-18), unattended: (1) wait for OG Mos Eisley's rebuild and the 11-map menu mod, (2) the
collection of the UNBAKED maps (to send now), (3) bake every map (tools/batch_bake.py), (4) the menu mod and the
collection again, from the baked maps. No game is launched. Log: work/_merge/overnight.log
"""
import os
import re
import subprocess
import sys
import time

T = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(T)
W = os.path.join(ROOT, 'work')
LOGS = os.path.join(W, '_batch', 'b1', 'logs')
MAPS = ['OGCoruscant', 'OGDagobah', 'OGDeathStar', 'OGFelucia', 'OGGeonosis', 'OGHoth', 'OGYavin', 'OGKamino', 'OGJabba', 'OGNaboo', 'OGMosEisley']
CLI_MENU = os.path.join(ROOT, 'cli-wt', 'FrostyCLI', 'bin', 'x64', 'ReleaseMenu', 'FrostyCLI.exe')
NOWIN = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
LOG = open(os.path.join(W, '_merge', 'overnight.log'), 'a', encoding='utf-8')


def say(s):
    LOG.write(time.strftime('[%H:%M:%S] ') + s + '\n'); LOG.flush(); print(s, flush=True)


def py(args, timeout):
    p = subprocess.run([sys.executable, '-I'] + args, capture_output=True, text=True, timeout=timeout, creationflags=NOWIN, cwd=ROOT)
    say('  rc %s: %s' % (p.returncode, ' | '.join(l for l in (p.stdout + p.stderr).splitlines() if re.search(r'merge:|REFUSED|CONFLICT|collection|baked|done:|STOP|  OG', l))[:1500]))
    return p.returncode


def built(names):
    return [m for m in names if os.path.exists(os.path.join(W, m, 'mods', m + '.fbmod'))]


def menu_and_collection(tag, names):
    mods = [os.path.join(W, m, 'mods', m + '.fbmod') for m in names]
    menu = os.path.join(W, '_merge', 'og_menu_' + tag, 'OG_Menu.fbmod')
    py([os.path.join(T, 'merge_maps.py'), menu, 'OG_Menu'] + mods + ['--menu-only', '--cli', CLI_MENU, '--timeout', '900'], 1200)
    if os.path.exists(menu):
        py([os.path.join(T, 'make_collection.py'), os.path.join(W, '_merge', 'OG_Collection_%s.zip' % tag), 'OG Battlefront II Maps (%s)' % tag]
           + mods + [menu, '--author', 'Rapid421', '--description', 'The 2005 Battlefront II maps, ported with the BF2 Map Tool. %d maps; '
                     'OG_Menu (last) lists them all in Arcade.' % len(names)], 1800)


# 1 Mos Eisley (re-run started 21:00) - at most 2 h
end = time.time() + 7200
mos = os.path.join(LOGS, 'OGMosEisley_v2_build.log')
while time.time() < end:
    if os.path.exists(mos) and re.search(r'^@@DONE', open(mos, encoding='utf-8', errors='replace').read(), re.M):
        break
    time.sleep(30)
ok = os.path.exists(mos) and '@@DONE ok' in open(mos, encoding='utf-8', errors='replace').read()
names = [m for m in built(MAPS) if m != 'OGMosEisley' or ok]
say('maps ready: %d (%s)%s' % (len(names), ', '.join(names), '' if ok else ' - Mos Eisley did NOT pass'))
time.sleep(240)                                            # the menu build of the chain started at 21:00 finishes first
# 2 the unbaked collection
menu_and_collection('unbaked', names)
# 3 bake
say('bake: %s' % ', '.join(names))
py([os.path.join(T, 'batch_bake.py')] + names + ['--quality', 'normal'], 14 * 3600)
# 4 baked menu + collection
menu_and_collection('baked', names)
say('overnight done')
