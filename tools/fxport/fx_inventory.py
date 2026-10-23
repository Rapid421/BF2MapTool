"""Inventory every particle effect 2015 Walker Assault (levels/arctic/arctic_01) can show.

Walks the WA sub-level tree exactly like the placement census (inventory/placements/_work/census.py): sub-level roots,
WorldPartReferenceObjectData layers, and every SpatialPrefab/Prefab reference to closure (transforms composed down the
chain). Two things the census does not do and this does:
  * it descends into the ObjectBlueprint of every STANDALONE ObjectReferenceObjectData placement and collects the
    EffectReferenceObjectData inside it (group members never run their blueprint's extra objects -- 2015's build moved
    those into *_nongroupable_autogen prefabs, which the walk already expands);
  * it records, for every effect reference, its instance guid, AutoStart, EffectParameters, Excluded and every event /
    property / link connection in its own partition that targets it (so gameplay-triggered effects are separable from
    ambient ones).
It also collects the EmitterExclusionVolumeData entities (the volumes that keep weather particles out of the base).

usage: fx_inventory.py <out.json>
"""
import json, os, sys, collections
sys.path.insert(0, r'I:/HothPort/inventory/placements/_work')
from flatlib import load, root, deref, ext, guidmap, fnv  # noqa: E402

SUBLEVELS = collections.OrderedDict([
    ('Levels/Arctic/Arctic_01/Arctic_01', ('WA', 'always')),
    ('Levels/Arctic/Arctic_01/StreamedLevel', ('WA', 'always')),
    ('Levels/Arctic/Arctic_01/GameModes', ('WA', 'always')),
    ('Levels/Arctic/Arctic_01/WalkerAssault', ('WA', 'WA')),
    ('Levels/Arctic/Arctic_01/StreamedAssets_BattleBeyond', ('WA', 'always')),
    ('Levels/Arctic/Arctic_01/StreamedAssets_BattleBeyond_IonCannon', ('WA', 'always')),
    ('Levels/Arctic/Arctic_01/StreamedAssets_BattleBeyond_IonCannon_Default', ('WA', 'always')),
    ('Levels/Arctic/Arctic_01/StreamedAssets_BattleBeyond_PowerGenerator', ('WA', 'always')),
    ('Levels/Arctic/Arctic_01/StreamedAssets_BattleBeyond_PowerGenerator_Default', ('WA', 'always until the generator is destroyed')),
    ('Levels/Arctic/Arctic_01/StreamedAssets_BattleBeyond_PowerGenerator_Destruction', ('WA-event', 'loaded + exploded by a WalkerAssault event')),
    ('Levels/Arctic/Arctic_01/StreamedAssets_BattleBeyond_IonCannon_Destruction', ('WA-event', 'ion cannon Destroy event')),
    ('Levels/Arctic/Arctic_01/StreamedAssets_BattleBeyond_IonCannon_Ruin', ('WA-event', 'ion cannon Destroy event')),
    ('Levels/Arctic/Arctic_01/StreamedAssets_BattleBeyond_StarDestroyer_Destruction', ('WA-event', 'battle-beyond logic')),
    ('Levels/Arctic/Arctic_01/StreamedAssets_BattleBeyond_Transport', ('WA-event', 'battle-beyond logic')),
])

IDENT = [[1, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0]]


def T(lt):
    return [[lt[a]['x'], lt[a]['y'], lt[a]['z']] for a in ('right', 'up', 'forward', 'trans')]


def mulrow(v, b):
    return [v[0] * b[0][j] + v[1] * b[1][j] + v[2] * b[2][j] for j in range(3)]


def compose(local, parent):
    b = parent[:3]
    t = mulrow(local[3], b)
    return [mulrow(local[0], b), mulrow(local[1], b), mulrow(local[2], b), [t[j] + parent[3][j] for j in range(3)]]


def resolve_obj(d, ref):
    if isinstance(ref, dict) and '$ref' in ref:
        return d, (d['objects'][ref['$ref']] if ref['$ref'] is not None else None)
    if isinstance(ref, dict) and '$ext' in ref:
        g = guidmap().get((ref.get('$cg') or '').lower())
        if g:
            return load(g[0]), g[1]
    return None, None


_conn_cache = {}


def incoming(d, obj):
    """Every connection in partition d whose target is obj (by internal index)."""
    key = d['path']
    if key not in _conn_cache:
        by_target = collections.defaultdict(list)
        for o in d['objects']:
            for kind in ('EventConnections', 'PropertyConnections', 'LinkConnections'):
                for c in o.get(kind) or []:
                    tgt = c.get('Target')
                    if isinstance(tgt, dict) and '$ref' in tgt:
                        src = c.get('Source')
                        st = None
                        if isinstance(src, dict) and '$ref' in src and src['$ref'] is not None:
                            st = d['objects'][src['$ref']]['$type']
                        elif isinstance(src, dict) and '$ext' in src:
                            st = 'ext:' + (src.get('$etype') or '?')
                        ev = c.get('TargetEvent') or c.get('TargetFieldId') or c.get('TargetField')
                        sev = c.get('SourceEvent') or c.get('SourceFieldId') or c.get('SourceField')
                        by_target[tgt['$ref']].append(dict(kind=kind[:-11], source=st, sourceEvent=_evname(sev),
                                                           targetEvent=_evname(ev)))
        _conn_cache[key] = by_target
    return _conn_cache[key].get(obj['$i'], [])


EVN = {}
for _n in ['Start', 'Stop', 'Enable', 'Disable', 'Spawn', 'Unspawn', 'Destroy', 'Reset', 'Kill', 'Transform', 'Out',
           'OnDestroyed', 'Trigger', 'Show', 'Hide', 'Play', 'Pause', 'Resume', 'In', 'Activate', 'Deactivate',
           'BlueprintTransform', 'Enabled', 'OnStart', 'OnStop', 'OnEnter', 'OnLeave', 'OnTrue', 'OnFalse']:
    EVN[fnv(_n)] = _n


def _evname(spec):
    if isinstance(spec, dict):
        i = spec.get('Id')
        if i is not None and (i & 0xffffffff) in EVN:
            return EVN[i & 0xffffffff]
        return spec.get('Name') or (hex(i & 0xffffffff) if isinstance(i, int) else None)
    if isinstance(spec, int):
        return EVN.get(spec & 0xffffffff, hex(spec & 0xffffffff))
    return spec


rows, excl_vols, visiting = [], [], set()
embedded_bps = collections.Counter()


def add_effect(d, o, ctx, W, via):
    bp = o.get('Blueprint') or {}
    rows.append(dict(
        scope=ctx['scope'], status=ctx['status'], sublevel=ctx['sublevel'].split('/')[-1], layer=ctx['layer'],
        chain=list(ctx['chain']), via=via, partition=d['path'], objIndex=o['$i'], instanceGuid=o.get('$guid'),
        blueprint=bp.get('$ext'), bpFileGuid=bp.get('$fg'), bpClassGuid=bp.get('$cg'),
        autoStart=o.get('AutoStart'), excluded=o.get('Excluded'), excludedChain=ctx['excl'],
        effectParameters=o.get('EffectParameters') or [], localPlayerId=o.get('LocalPlayerId'),
        streamRealm=o.get('StreamRealm'), lightprobeVis=o.get('AffectedByLightprobeVisibility'),
        incoming=incoming(d, o), transform=[[round(x, 6) for x in r] for r in W]))


def walk_objects(d, objlist, ctx, parentT):
    for ref in objlist or []:
        dd, o = resolve_obj(d, ref)
        if o is None:
            continue
        t = o['$type']
        if t == 'WorldPartReferenceObjectData':
            wpath = ext(o.get('Blueprint'))
            wp = load(wpath) if wpath else None
            if wp is None:
                continue
            c2 = dict(ctx, layer=wpath.split('/')[-1], excl=ctx['excl'] or bool(o.get('Excluded')))
            walk_objects(wp, root(wp).get('Objects'), c2, compose(T(o['BlueprintTransform']), parentT))
        elif t == 'EffectReferenceObjectData':
            add_effect(dd, o, ctx, compose(T(o['BlueprintTransform']), parentT), 'layer' if not ctx['chain'] else 'prefab')
        elif t == 'EmitterExclusionVolumeData':
            excl_vols.append(dict(scope=ctx['scope'], sublevel=ctx['sublevel'].split('/')[-1], layer=ctx['layer'],
                                  chain=list(ctx['chain']), partition=dd['path'], objIndex=o['$i'],
                                  instanceGuid=o.get('$guid'), parentTransform=parentT,
                                  object={k: v for k, v in o.items() if not k.startswith('$')}))
        elif 'Blueprint' in o and 'BlueprintTransform' in o and isinstance(o.get('Blueprint'), dict):
            bp = ext(o.get('Blueprint'))
            bpt = o['Blueprint'].get('$etype')
            W = compose(T(o['BlueprintTransform']), parentT)
            if not bp:
                continue
            pd = load(bp)
            if pd is None or bp.lower() in visiting or len(ctx['chain']) >= 12:
                continue
            if bpt in ('SpatialPrefabBlueprint', 'PrefabBlueprint'):
                visiting.add(bp.lower())
                walk_objects(pd, root(pd).get('Objects'), dict(ctx, chain=ctx['chain'] + [bp],
                                                                excl=ctx['excl'] or bool(o.get('Excluded'))), W)
                visiting.discard(bp.lower())
            elif bpt == 'ObjectBlueprint' and t == 'ObjectReferenceObjectData':
                # a standalone prop runs its blueprint's extra objects (effects, lights, sounds)
                effs = [x for x in pd['objects'] if x['$type'] == 'EffectReferenceObjectData']
                if effs:
                    embedded_bps[bp] += 1
                    visiting.add(bp.lower())
                    walk_objects(pd, root(pd).get('Objects'), dict(ctx, chain=ctx['chain'] + [bp],
                                                                    excl=ctx['excl'] or bool(o.get('Excluded'))), W)
                    visiting.discard(bp.lower())


def main():
    out = sys.argv[1]
    for sl, (scope, status) in SUBLEVELS.items():
        d = load(sl)
        if d is None:
            print('missing sub-level dump', sl)
            continue
        walk_objects(d, root(d).get('Objects'), dict(sublevel=sl, scope=scope, status=status, layer='(root)',
                                                     chain=[], excl=False), IDENT)
    json.dump(dict(effects=rows, exclusionVolumes=excl_vols, embeddedBlueprints=embedded_bps), open(out, 'w'), indent=1)
    print('effects', len(rows), 'exclusion volumes', len(excl_vols))
    c = collections.Counter((r['scope'], r['autoStart'], bool(r['incoming']), bool(r['excluded'] or r['excludedChain']))
                            for r in rows)
    for k, v in sorted(c.items(), key=str):
        print('  scope=%s autoStart=%s triggered=%s excluded=%s : %d' % (k + (v,)))


if __name__ == '__main__':
    main()
