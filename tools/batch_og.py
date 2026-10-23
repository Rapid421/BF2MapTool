"""Build every OG 2005 map through the tool, one after another, unattended: prep (the add-on's own "guess roles" /
"guess classes", map name + sky) then `bf2maptool build` (no bake, no game test). Every step has a time limit; a map
that fails or runs over is logged and the batch moves on.
    python batch_og.py [--only OGHoth,OGEndor] [--bake draft] [--tag b1]
Results: work/_batch/<tag>/results.json + results.md (one row per map), logs in work/_batch/<tag>/logs/.
A map whose mod already exists in this tag is skipped (re-run = resume).
"""
import json
import os
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = json.load(open(os.path.join(ROOT, 'work', 'settings.json')))
OG = r'I:\OG_Battlefront_2_Maps_Blend-20261004T183729Z-1-001\OG_Battlefront_2_Maps_Blend'
NOWIN = getattr(subprocess, 'CREATE_NO_WINDOW', 0)

# level name, source blend (relative to OG), sky preset, menu name
MAPS = [
    ('OGCoruscant', r'cor\SWBF2_Coruscant_Jedi_Temple_Map.blend', 'sunset', 'Coruscant: Jedi Temple'),
    ('OGDagobah', r'dag1\SWBF2_Dagobah_Map.blend', 'overcast', 'Dagobah: Swamp'),
    ('OGDeathStar', r'dea1\SWBF2_Death_Star_Map.blend', 'space', 'Death Star: Interior'),
    ('OGEndor', r'end1\SWBF2_Endor_Map.blend', 'clear', 'Endor: Bunker'),
    ('OGFelucia', r'fel1\SWBF2_Felucia_Map.blend', 'clear', 'Felucia: Marshland'),
    ('OGGeonosis', r'geo1\SWBF2_Geonosis_Map.blend', 'clear', 'Geonosis: Dust Plains'),
    ('OGHoth', r'hot1\SWBF2_Hoth_Map.blend', 'overcast', 'Hoth: Echo Base'),
    ('OGKamino', r'kam1\SWBF2_Kamino_Map.blend', 'overcast', 'Kamino: Cloning Facility'),
    ('OGMustafar', r'mus1\SWBF2_Mustafar_Map.blend', 'volcanic', 'Mustafar: Refinery'),
    ('OGMygeeto', r'myg1\SWBF2_Mygeeto_Map.blend', 'overcast', 'Mygeeto: War-Torn City'),
    ('OGNaboo', r'nab2\SWBF2_Naboo_2_Theed_Map.blend', 'clear', 'Naboo: Theed'),
    ('OGPolisMassa', r'pol1\SWBF2_Polis_Massa_Map.blend', 'space', 'Polis Massa: Medical Facility'),
    ('OGRhenVar', r'rhn2\SWBF2_Rhen_Var_Map.blend', 'overcast', 'Rhen Var: Citadel'),
    ('OGTantive', r'tan1\SWBF2_Tantive_IV_Map.blend', 'space', 'Tantive IV: Interior'),
    ('OGMosEisley', r'tat2\SWBF2_Tatooine_2_Mos_Eisley.blend', 'clear', 'Tatooine: Mos Eisley'),
    ('OGJabba', r'tat3\SWBF2_Tat_2_Jabba_s_Palace_Map.blend', 'clear', "Tatooine: Jabba's Palace"),
    ('OGUtapau', r'uta1\SWBF2_Utapau_Setup.blend', 'clear', 'Utapau: Sinkhole'),
    ('OGYavin', r'yav1\SWBF_YavinIV_Map.blend', 'clear', 'Yavin 4: Temple'),
    ('OGVenator', r'Venator\Venator_SWBF2006.blend', 'space', 'Venator'),
]
PREP_TIMEOUT = 20 * 60
BUILD_TIMEOUT = 120 * 60


def run(cmd, log, timeout, cwd=None):
    t0 = time.time()
    with open(log, 'w', encoding='utf-8', errors='replace') as f:
        p = subprocess.Popen(cmd, stdout=f, stderr=subprocess.STDOUT, cwd=cwd, creationflags=NOWIN,
                             env=dict(os.environ, TMP=os.path.join(ROOT, 'tmp'), TEMP=os.path.join(ROOT, 'tmp'), TMPDIR=os.path.join(ROOT, 'tmp')))
        try:
            rc = p.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            subprocess.run(['taskkill', '/T', '/F', '/PID', str(p.pid)], capture_output=True, creationflags=NOWIN)
            p.wait()
            rc = 'timeout'
    return rc, round(time.time() - t0)


def write_result(rp, name, r):
    """merge one map's result into results.json under a lock file (another worker may be writing its own)"""
    lock = rp + '.lock'
    for _ in range(600):
        try:
            fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            break
        except FileExistsError:
            time.sleep(0.1)
    else:
        fd = None                                         # a stale lock (a killed worker): write anyway
    try:
        results = json.load(open(rp)) if os.path.exists(rp) else {}
        results[name] = r
        with open(rp + '.tmp', 'w') as f:
            json.dump(results, f, indent=1)
        os.replace(rp + '.tmp', rp)
        return results
    finally:
        if fd is not None:
            os.close(fd)
        try:
            os.remove(lock)
        except OSError:
            pass


def tail(log, n=3):
    try:
        lines = [l.strip() for l in open(log, encoding='utf-8', errors='replace').read().splitlines() if l.strip()]
        return ' | '.join(lines[-n:])[:400]
    except OSError:
        return ''


def main(argv):
    def opt(name, default=None):
        if name in argv:
            i = argv.index(name); v = argv[i + 1]; del argv[i:i + 2]; return v
        return default
    only = opt('--only'); bake = opt('--bake'); tag = opt('--tag', 'b1')
    out = os.path.join(ROOT, 'work', '_batch', tag)
    logs = os.path.join(out, 'logs'); scenes = os.path.join(out, 'scenes')
    os.makedirs(logs, exist_ok=True); os.makedirs(scenes, exist_ok=True)
    rp = os.path.join(out, 'results.json')
    results = json.load(open(rp)) if os.path.exists(rp) else {}
    maps = [m for m in MAPS if not only or m[0] in only.split(',')]
    if '--reverse' in argv:                               # a second worker works from the other end of the list
        maps = maps[::-1]
    addon = os.path.join(ROOT, 'addon', 'bf2_map_tool')
    claims = os.path.join(out, 'claims')
    os.makedirs(claims, exist_ok=True)
    for name, src, sky, shown in maps:
        mod = os.path.join(ROOT, 'work', name, 'mods', name + '.fbmod')
        results = json.load(open(rp)) if os.path.exists(rp) else {}      # another worker may have written since
        if results.get(name, {}).get('status') == 'built' and os.path.exists(mod):
            continue
        # a map's work folder takes 0.6 - 2.3 GB (measured on the OG batch): no new map below 4 GB free on the work drive
        free_gb = shutil.disk_usage(ROOT).free / 1e9
        if free_gb < 4.0:
            print('   STOP: only %.1f GB free on %s - no new map started' % (free_gb, ROOT[:2]), flush=True)
            break
        # several workers may run: whoever creates the claim file first builds the map, the others skip it
        try:
            os.close(os.open(os.path.join(claims, name), os.O_CREAT | os.O_EXCL | os.O_WRONLY))
        except FileExistsError:
            continue
        r = {'source': src, 'sky': sky, 'started': time.strftime('%H:%M:%S')}
        print('== %s  %s' % (name, r['started']), flush=True)
        scene = os.path.join(scenes, name + '.blend')
        rc, sec = run([S['blender'], '-b', os.path.join(OG, src), '--python', os.path.join(ROOT, 'tests', 'prep_scene.py'), '--',
                       scene, addon, os.path.join(out, name + '_prep.json'), name, sky, shown], os.path.join(logs, name + '_prep.log'), PREP_TIMEOUT)
        r['prep'] = {'rc': rc, 'seconds': sec}
        # the map's original 2005 command posts (tools/og_posts.py) go in BEFORE the build: the play area - and with it
        # the level's placement - is measured from the posts, so adding them after a build re-runs nearly every stage
        posts = os.path.join(ROOT, 'work', '_batch', 'posts', name + '_posts.json')
        # the posts were read from the blend as saved; prep may have rescaled the scene about the origin (OG Geonosis
        # x10) - the posts move with it
        try:
            k = float(json.load(open(os.path.join(out, name + '_prep.json'))).get('rescaled') or 1.0)
        except (OSError, ValueError):
            k = 1.0
        if k != 1.0 and os.path.exists(posts):
            scaled = os.path.join(scenes, name + '_posts_scaled.json')
            json.dump([[l, [round(v * k, 3) for v in p], t] for l, p, t in json.load(open(posts))], open(scaled, 'w'), indent=1)
            posts = scaled
            r['rescaled'] = k
        if rc == 0 and os.path.exists(scene) and os.path.exists(posts):
            sup = os.path.join(scenes, name + '_sup.blend')
            prc, psec = run([S['blender'], '-b', scene, '--python', os.path.join(ROOT, 'tests', 'add_posts.py'), '--', addon, sup, posts]
                            + ([] if '--supremacy' in argv else ['--no-mode']),
                            os.path.join(logs, name + '_posts.log'), PREP_TIMEOUT)
            r['posts'] = {'rc': prc, 'seconds': psec}
            if prc == 0 and os.path.exists(sup):
                scene = sup
        if rc != 0 or not os.path.exists(scene):
            r['status'] = 'prep failed'; r['why'] = tail(os.path.join(logs, name + '_prep.log'))
        else:
            W = os.path.join(ROOT, 'work', name)
            os.makedirs(os.path.join(W, 'export'), exist_ok=True)
            cmd = [sys.executable, '-m', 'bf2maptool', 'bake' if bake else 'build', scene, '--settings', os.path.join(ROOT, 'work', 'settings.json'),
                   '--blender', S['blender'], '--export-script', os.path.join(addon, 'export_scene.py'), '--export-dir', os.path.join(W, 'export')]
            if bake:
                cmd += ['--quality', bake]
            rc, sec = run(cmd, os.path.join(logs, name + '_build.log'), BUILD_TIMEOUT, cwd=os.path.join(ROOT, 'runtime'))
            r['build'] = {'rc': rc, 'seconds': sec}
            if rc == 0 and os.path.exists(mod):
                r['status'] = 'built'; r['mod'] = mod; r['mb'] = round(os.path.getsize(mod) / 1e6, 1)
            else:
                r['status'] = 'build timeout' if rc == 'timeout' else 'build failed'; r['why'] = tail(os.path.join(logs, name + '_build.log'))
        try:
            r['prep_report'] = {k: v for k, v in json.load(open(os.path.join(out, name + '_prep.json'))).items() if k in ('meshes_left', 'lights', 'largest_left')}
            r['missing_images'] = len(json.load(open(os.path.join(out, name + '_prep.json'))).get('missing_images', []))
        except (OSError, ValueError):
            pass
        results = write_result(rp, name, r)
        print('   %s  %s' % (r['status'], r.get('why', '')), flush=True)
    with open(os.path.join(out, 'results.md'), 'w', encoding='utf-8') as f:
        f.write('| map | status | prep s | build s | MB | missing images | why |\n|---|---|---|---|---|---|---|\n')
        for name, *_ in MAPS:
            r = results.get(name)
            if not r:
                continue
            f.write('| %s | %s | %s | %s | %s | %s | %s |\n' % (name, r['status'], r['prep']['seconds'], r.get('build', {}).get('seconds', ''),
                                                             r.get('mb', ''), r.get('missing_images', ''), (r.get('why') or '').replace('|', '/')[:200]))
    print('done:', sum(1 for r in results.values() if r['status'] == 'built'), 'of', len(results), 'built')


if __name__ == '__main__':
    main(sys.argv[1:])
