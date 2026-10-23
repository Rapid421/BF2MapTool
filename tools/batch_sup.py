"""Give every built OG map its original 2005 command posts and Supremacy, one map after another, unattended:
tests/add_posts.py puts work/_batch/posts/<Map>_posts.json into the prepped scene (-> scenes/<Map>_sup.blend, mode
Supremacy ticked), then `bf2maptool build` rebuilds IN PLACE - only the stages that read posts / spawns re-run (the
pipeline skips the rest). The mod a user may have seen is kept first as <Map>_v<N>.fbmod (never overwritten).
    python batch_sup.py [--only OGHoth,OGEndor] [--tag b1]
Results: work/_batch/<tag>/sup_results.json + sup_results.md. Maps without a posts file or a built mod are skipped.
"""
import json
import os
import shutil
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import batch_og as B

PREP_TIMEOUT = 10 * 60
BUILD_TIMEOUT = 90 * 60


def keep_copy(mod):
    """<Map>.fbmod -> the next free <Map>_v<N>.fbmod beside it"""
    d, base = os.path.split(mod)
    stem = os.path.splitext(base)[0]
    n = 1
    while os.path.exists(os.path.join(d, '%s_v%d.fbmod' % (stem, n))):
        n += 1
    shutil.copy2(mod, os.path.join(d, '%s_v%d.fbmod' % (stem, n)))
    return '%s_v%d.fbmod' % (stem, n)


def main(argv):
    def opt(name, default=None):
        if name in argv:
            i = argv.index(name); v = argv[i + 1]; del argv[i:i + 2]; return v
        return default
    only = opt('--only'); tag = opt('--tag', 'b1')
    out = os.path.join(B.ROOT, 'work', '_batch', tag)
    posts_dir = os.path.join(B.ROOT, 'work', '_batch', 'posts')
    logs = os.path.join(out, 'logs')
    rp = os.path.join(out, 'sup_results.json')
    results = json.load(open(rp)) if os.path.exists(rp) else {}
    addon = os.path.join(B.ROOT, 'addon', 'bf2_map_tool')
    for name, *_ in B.MAPS:
        if only and name not in only.split(','):
            continue
        if results.get(name, {}).get('status') == 'built':
            continue
        posts = os.path.join(posts_dir, name + '_posts.json')
        mod = os.path.join(B.ROOT, 'work', name, 'mods', name + '.fbmod')
        scene = os.path.join(out, 'scenes', name + '.blend')
        if not (os.path.exists(posts) and os.path.exists(mod) and os.path.exists(scene)):
            continue
        r = {'started': time.strftime('%H:%M:%S'), 'kept': keep_copy(mod)}
        print('== %s  %s  (kept %s)' % (name, r['started'], r['kept']), flush=True)
        sup = os.path.join(out, 'scenes', name + '_sup.blend')
        rc, sec = B.run([B.S['blender'], '-b', scene, '--python', os.path.join(B.ROOT, 'tests', 'add_posts.py'), '--', addon, sup, posts],
                        os.path.join(logs, name + '_sup_prep.log'), PREP_TIMEOUT)
        r['prep'] = {'rc': rc, 'seconds': sec}
        if rc != 0 or not os.path.exists(sup):
            r['status'] = 'posts failed'; r['why'] = B.tail(os.path.join(logs, name + '_sup_prep.log'))
        else:
            W = os.path.join(B.ROOT, 'work', name)
            cmd = [sys.executable, '-m', 'bf2maptool', 'build', sup, '--settings', os.path.join(B.ROOT, 'work', 'settings.json'),
                   '--blender', B.S['blender'], '--export-script', os.path.join(addon, 'export_scene.py'), '--export-dir', os.path.join(W, 'export')]
            rc, sec = B.run(cmd, os.path.join(logs, name + '_sup_build.log'), BUILD_TIMEOUT, cwd=os.path.join(B.ROOT, 'runtime'))
            r['build'] = {'rc': rc, 'seconds': sec}
            fresh = os.path.exists(mod) and os.path.getmtime(mod) > time.time() - sec - 5
            r['status'] = 'built' if rc == 0 and fresh else ('build timeout' if rc == 'timeout' else 'build failed')
            if r['status'] != 'built':
                r['why'] = B.tail(os.path.join(logs, name + '_sup_build.log'))
        results[name] = r
        json.dump(results, open(rp, 'w'), indent=1)
        print('   %s  %s' % (r['status'], r.get('why', '')), flush=True)
    with open(os.path.join(out, 'sup_results.md'), 'w', encoding='utf-8') as f:
        f.write('| map | status | build s | kept | why |\n|---|---|---|---|---|\n')
        for name, *_ in B.MAPS:
            r = results.get(name)
            if r:
                f.write('| %s | %s | %s | %s | %s |\n' % (name, r['status'], r.get('build', {}).get('seconds', ''), r.get('kept', ''),
                                                       (r.get('why') or '').replace('|', '/')[:200]))
    print('done:', sum(1 for r in results.values() if r['status'] == 'built'), 'of', len(results), 'with Supremacy')


if __name__ == '__main__':
    main(sys.argv[1:])
