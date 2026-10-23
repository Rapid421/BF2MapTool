"""Bake every built OG map, one after another, unattended (overnight): `bf2maptool bake --quality normal` on the scene
the map was last built from (scenes/<Map>_sup.blend where the posts went in, else scenes/<Map>.blend), so every stage
the bake does not touch is reused. Before a map is baked its current mod is kept as <Map>_unbaked.fbmod (never
overwritten). A map whose normal bake fails is tried once at draft. No game is launched; the bake stage has its own
free-memory gate and runs at below-normal priority.
    python batch_bake.py <Map> <Map> ... [--quality normal] [--tag b1]
Results: work/_batch/<tag>/bake_results.json + bake_results.md
"""
import json
import os
import shutil
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import batch_og as B

BAKE_TIMEOUT = 150 * 60


def main(argv):
    def opt(name, default=None):
        if name in argv:
            i = argv.index(name); v = argv[i + 1]; del argv[i:i + 2]; return v
        return default
    quality = opt('--quality', 'normal'); tag = opt('--tag', 'b1')
    out = os.path.join(B.ROOT, 'work', '_batch', tag)
    logs, scenes = os.path.join(out, 'logs'), os.path.join(out, 'scenes')
    rp = os.path.join(out, 'bake_results.json')
    addon = os.path.join(B.ROOT, 'addon', 'bf2_map_tool')
    for name in argv:
        results = json.load(open(rp)) if os.path.exists(rp) else {}
        if results.get(name, {}).get('status') == 'baked':
            continue
        if shutil.disk_usage(B.ROOT).free / 1e9 < 4.0:
            print('STOP: under 4 GB free on %s' % B.ROOT[:2], flush=True)
            break
        W = os.path.join(B.ROOT, 'work', name)
        mod = os.path.join(W, 'mods', name + '.fbmod')
        scene = next((s for s in (os.path.join(scenes, name + '_sup.blend'), os.path.join(scenes, name + '.blend')) if os.path.exists(s)), None)
        if not (scene and os.path.exists(mod)):
            print('skip %s: no scene or no built mod' % name, flush=True)
            continue
        keep = os.path.join(W, 'mods', name + '_unbaked.fbmod')
        if not os.path.exists(keep):
            shutil.copy2(mod, keep)
        r = {'started': time.strftime('%H:%M:%S'), 'scene': os.path.basename(scene)}
        print('== %s  %s' % (name, r['started']), flush=True)
        for q in ([quality, 'draft'] if quality != 'draft' else ['draft']):
            cmd = [sys.executable, '-m', 'bf2maptool', 'bake', scene, '--settings', os.path.join(B.ROOT, 'work', 'settings.json'),
                   '--blender', B.S['blender'], '--export-script', os.path.join(addon, 'export_scene.py'), '--export-dir', os.path.join(W, 'export'),
                   '--quality', q]
            t0 = time.time()
            rc, sec = B.run(cmd, os.path.join(logs, '%s_bake_%s.log' % (name, q)), BAKE_TIMEOUT, cwd=os.path.join(B.ROOT, 'runtime'))
            fresh = os.path.exists(mod) and os.path.getmtime(mod) > t0
            r[q] = {'rc': rc, 'seconds': sec}
            if rc == 0 and fresh:
                r['status'] = 'baked'; r['quality'] = q; r['mb'] = round(os.path.getsize(mod) / 1e6, 1)
                break
            r['status'] = 'bake timeout' if rc == 'timeout' else 'bake failed'
            r['why'] = B.tail(os.path.join(logs, '%s_bake_%s.log' % (name, q)))
        results = B.write_result(rp, name, r)
        print('   %s %s  %s' % (r['status'], r.get('quality', ''), r.get('why', '') if r['status'] != 'baked' else ''), flush=True)
    results = json.load(open(rp)) if os.path.exists(rp) else {}
    with open(os.path.join(out, 'bake_results.md'), 'w', encoding='utf-8') as f:
        f.write('| map | status | quality | minutes | MB | why |\n|---|---|---|---|---|---|\n')
        for name in argv:
            r = results.get(name)
            if r:
                mins = sum(v['seconds'] for k, v in r.items() if isinstance(v, dict) and 'seconds' in v) / 60
                f.write('| %s | %s | %s | %.0f | %s | %s |\n' % (name, r['status'], r.get('quality', ''), mins, r.get('mb', ''),
                                                              (r.get('why') or '').replace('|', '/')[:160]))
    print('done:', sum(1 for n in argv if results.get(n, {}).get('status') == 'baked'), 'of', len(argv), 'baked')


if __name__ == '__main__':
    main(sys.argv[1:])
