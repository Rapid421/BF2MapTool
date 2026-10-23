"""Apply the 2015 Walker Assault FX port to any project derived from H10y.

usage: fx_apply.py <FrostyCLI.exe> <in.fbproject> <out.fbproject> [--mod <out.fbmod> --mod-name N --mod-version V]

Steps (all deterministic; the only project-dependent inputs are read from the input project itself):
  1. graph-import the converted emitters (children first) and effect blueprints (pkg_core/, built by fx_convert.py)
     into the bundle that owns the level root: win32/AddedContent/Hoth2015/Levels/Hoth2015_01/Hoth2015_01;
  2. graph-import FX_Hoth2015 -- a LayerData holding one EffectReferenceObjectData per 2015 placement, at 2015's world
     transform with 2015's reference fields (fx_layer.py builds it from the 2015 inventory);
  3. bundle-add the BFII assets the effects sample (textures, the global effect parameter list, the Hoth ambience
     sound patches of the effects' SoundEffectEntityData) with their resources, same bundle;
  4. repoint the level root's LayerReferenceObjectData that references DICE's inherited Default_FX (BFII Hoth_01's
     effects at BFII Hoth_01 positions: wreck smoke pillars, dogfight lasers, Star Destroyer idle...) to FX_Hoth2015,
     and remove the root connections that targeted objects inside Default_FX (they drove DICE's random-timed FX);
  5. save.
"""
import json, os, subprocess, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
FX = os.path.dirname(HERE)
ROOT = 'AddedContent/Hoth2015/Levels/Hoth2015_01/Hoth2015_01'
ROOT_BUNDLE = 'win32/AddedContent/Hoth2015/Levels/Hoth2015_01/Hoth2015_01'
LAYER = 'AddedContent/Hoth2015/Levels/Hoth2015_01/FX_Hoth2015'
DEFAULT_FX = 'AddedContent/Hoth2015/Levels/Hoth2015_01/Default_FX'
EXCL = 'AddedContent/Hoth2015/Levels/Hoth2015_01/EmitterExclusionVolumes_Hoth2015'
GAME = ['--game', 'swbf2', '--game-path', r'F:\STAR WARS Battlefront II']
SKIP_CLOSURE = ('sound/system/', 'sound/mixers/', 'sound/sp/', 'sound/characters/', 'defaultobjectpatch')


def flatdump(cli, project, assets, outdir):
    rel = os.path.dirname(cli)
    os.makedirs(outdir, exist_ok=True)
    lst = os.path.join(outdir, '_list.txt')
    open(lst, 'w').write('\n'.join(assets) + '\n')
    env = dict(os.environ, FLATDUMP_PROJECT=project)
    subprocess.run([os.path.join(r'I:\HothPort\work\tools\flatdump', 'FlatDump.exe'), rel, 'swbf2', r'F:\STAR WARS Battlefront II',
                    'dump', lst, outdir], env=env, capture_output=True, check=True)
    return {a: json.load(open(os.path.join(outdir, a.lower().replace('/', '__') + '.json'), encoding='utf-8')) for a in assets}


def main():
    cli, pin, pout = sys.argv[1], os.path.abspath(sys.argv[2]), os.path.abspath(sys.argv[3])
    args = sys.argv[4:]
    opt = {args[i][2:]: args[i + 1] for i in range(0, len(args) - 1, 2) if args[i].startswith('--')}
    work = os.path.join(FX, 'apply_work')
    root = flatdump(cli, pin, [ROOT], os.path.join(work, 'root'))[ROOT]
    objs = root['objects']
    ro = objs[root['root']]
    # (4a) the Objects slot of the layer reference to Default_FX
    slot = None
    for k, ref in enumerate(ro['Objects']):
        if isinstance(ref, dict) and '$ref' in ref:
            o = objs[ref['$ref']]
            bp = o.get('Blueprint') or {}
            if o['$type'] == 'LayerReferenceObjectData' and (bp.get('$ext') or '').lower() in (DEFAULT_FX.lower(), LAYER.lower()):
                slot = (k, (bp.get('$ext') or ''))
    if slot is None:
        sys.exit('no LayerReferenceObjectData for Default_FX in the level root')
    # (4c) the root's EmitterSystemComponent (its ExclusionVolumes asset is DICE's Hoth_01 Echo Base boxes)
    comp_slot = None
    for k, ref in enumerate(ro.get('Components') or []):
        if isinstance(ref, dict) and '$ref' in ref and objs[ref['$ref']]['$type'] == 'EmitterSystemComponent':
            comp_slot = k
    if comp_slot is None:
        sys.exit('no EmitterSystemComponent on the level root')
    # (4b) connections into Default_FX
    kill = []
    for kind, key in (('event', 'EventConnections'), ('property', 'PropertyConnections'), ('link', 'LinkConnections')):
        for i, c in enumerate(ro.get(key) or []):
            t = c.get('Target') or {}
            s = c.get('Source') or {}
            if (t.get('$ext') or '').lower() == DEFAULT_FX.lower() or (s.get('$ext') or '').lower() == DEFAULT_FX.lower():
                kill.append((kind, i))
    man = json.load(open(os.path.join(FX, 'pkg_core', 'manifest.json')))
    clos = json.load(open(os.path.join(FX, 'inv', 'closure_core.json')))['steps']
    steps = [{'op': 'project-load', 'file': pin}]
    already = slot[1].lower() == LAYER.lower()
    if not already:
        # 2015 atlases BFII does not ship (fx_textures.py): chunk + res + AtlasTextureAsset, before the emitters using them
        ts = os.path.join(FX, 'tex_import', 'texture_steps.json')
        if os.path.exists(ts):
            steps += json.load(open(ts))['steps']
        for dest, fn in man['emitters'] + man['blueprints']:
            steps.append({'op': 'ebx-graph-import', 'file': os.path.join(FX, fn), 'as': dest, 'bundle': ROOT_BUNDLE,
                          'strict': True, 'unsealed': True})
        steps.append({'op': 'ebx-graph-import', 'file': os.path.join(FX, 'pkg_core', 'layer_fx_hoth2015.json'), 'as': LAYER,
                      'bundle': ROOT_BUNDLE, 'strict': True, 'unsealed': True})
        steps.append({'op': 'ebx-graph-import', 'file': os.path.join(FX, 'pkg_core', 'exclusion_hoth2015.json'), 'as': EXCL,
                      'bundle': ROOT_BUNDLE, 'strict': True, 'unsealed': True})
        for s in clos:
            if s['ebx'].lower().startswith(SKIP_CLOSURE):
                continue
            steps.append({'op': 'bundle-add', 'ebx': s['ebx'], 'bundle': ROOT_BUNDLE, 'with_resources': True})
        steps.append({'op': 'ebx-set-ref', 'ebx': ROOT, 'prop': 'Objects[%d].Blueprint' % slot[0], 'target': LAYER})
        steps.append({'op': 'ebx-set-ref', 'ebx': ROOT, 'prop': 'Components[%d].ExclusionVolumes' % comp_slot, 'target': EXCL})
        for kind, i in sorted(kill, key=lambda x: -x[1]):
            steps.append({'op': 'connection-remove', 'ebx': ROOT, 'index': i, 'kind': kind})
    steps.append({'op': 'project-save', 'file': pout})
    if 'mod' in opt:
        steps.append({'op': 'mod-build', 'out': opt['mod'], 'mod-name': opt.get('mod-name', 'HOTH_FX'),
                      'mod-author': 'rubes', 'mod-version': opt.get('mod-version', '1.0')})
    pipe = os.path.join(work, 'apply_pipeline.json')
    json.dump({'game': 'swbf2', 'game-path': r'F:\STAR WARS Battlefront II', 'steps': steps}, open(pipe, 'w'), indent=1)
    print('slot Objects[%d] -> %s; %d connections into Default_FX removed; %d steps' % (slot[0], LAYER, len(kill), len(steps)))
    log = os.path.join(work, 'apply.log')
    with open(log, 'w') as lf:
        rc = subprocess.run([cli, 'run', pipe], stdout=lf, stderr=subprocess.STDOUT).returncode
    txt = open(log, errors='replace').read()
    fails = [l for l in txt.splitlines() if 'FAIL' in l or 'issue:' in l or 'error' in l.lower()]
    print('rc', rc, 'log', log)
    for l in fails[:40]:
        print('  ', l)
    sys.exit(rc)


if __name__ == '__main__':
    main()
