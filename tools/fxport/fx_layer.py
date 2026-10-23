"""Build the FX_Hoth2015 LayerData package: one EffectReferenceObjectData per selected 2015 placement, plus 2015's
VFX schematic (levels/arctic/arctic_01/streamedlevel/layer8_streamedlevel_vfx_schematic) with its area shape and every
connection between them, so the outdoor ambient FX stop while the camera is inside Echo Base and restart (staggered)
when it leaves, and the camera-proximity snow starts on spawn -- exactly as 2015 wires it.

Effect reference fields come from the 2015 reference object itself; the transform is its WORLD transform (prefab
placements flattened); the blueprint is the ported asset. Logic objects keep their 2015 fields; BFII-only fields get
the SDK defaults (ReceiveEventEntityData.EventId -> EventIds [id]; listed in REPORT.md).

usage: fx_layer.py <placements.json> <out package>
"""
import json, sys, os, uuid, hashlib
sys.path.insert(0, r'I:/HothPort/inventory/placements/_work')
from flatlib import load  # noqa: E402
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from canon import canonical  # noqa: E402

NS = 'AddedContent/Hoth2015/FX/'
LAYER = 'AddedContent/Hoth2015/Levels/Hoth2015_01/FX_Hoth2015'
L8 = 'levels/arctic/arctic_01/streamedlevel/layer8_streamedlevel_vfx_schematic'
L3 = 'levels/arctic/arctic_01/streamedlevel/layer3_streamedlevel_fx'
SL = 'Levels/Arctic/Arctic_01/StreamedLevel'


def vec(x, y, z):
    return {'type': 'Vec3', 'fields': {'x': float(x), 'y': float(y), 'z': float(z)}}


def lt(t):
    r, u, f, p = t
    big = 3.4028234663852886e+38
    return {'type': 'LinearTransform', 'fields': {
        'Rotation': vec(big, 0, 0), 'Scale': vec(big, 0, 0), 'Translate': vec(big, 0, 0),
        'forward': vec(*f), 'right': vec(*r), 'trans': vec(*p), 'up': vec(*u)}}


def flat_to_pkg(v):
    """FlatDump value -> graph-package value (structs become {type, fields})."""
    if isinstance(v, list):
        return [flat_to_pkg(x) for x in v]
    if isinstance(v, dict):
        if '$ref' in v or '$ext' in v:
            raise ValueError('reference inside a logic object is not expected: %s' % v)
        if '$type' in v:
            return {'type': v['$type'], 'fields': {k: flat_to_pkg(x) for k, x in v.items() if not k.startswith('$')}}
    return v


def logic_object(o):
    f = {k: flat_to_pkg(v) for k, v in o.items() if not k.startswith('$') and k not in ('__Id', '__InstanceGuid')}
    t = o['$type']
    if t == 'ReceiveEventEntityData':
        eid = int(f.pop('EventId'))
        if eid >= 0x80000000:
            eid -= 0x100000000            # EventIds is List<Int32>
        f['EventIds'] = [eid]
        f['PlayerTarget'] = {'type': 'QueryEntityResult', 'fields': {'QueryEntityResultUniquID': 0, 'Unused': 0}}
        f.update({'Dummy': False, 'FilterOnPlayer': False, 'RemoteProperties': [], 'TypeAssetId': 0})
    if t == 'VolumeVectorShapeData':
        f.setdefault('AllowYawPitch', False)
    if t == 'PlayersQueryEntityData':
        f.setdefault('TriggerOnChange', False)
    return {'type': t, 'exported': True, 'fields': f}


def main():
    rows = json.load(open(sys.argv[1]))
    out = sys.argv[2]
    rows.sort(key=lambda r: (r['blueprint'], r['partition'], r['objIndex'], r['chain']))
    objects = [None]
    refs = []
    by2015 = {}
    for r in rows:
        d = load(r['partition'])
        src = d['objects'][r['objIndex']]
        assert src['$type'] == 'EffectReferenceObjectData'
        assert src.get('EffectParameters') == [] and src.get('ObjectVariation') is None
        ro = src.get('RenderingOverrides') or {}
        for k in ('ShadowEnable', 'DynamicReflectionEnable', 'StaticReflectionEnable'):
            assert ro.get(k) == 'RenderingOverride_Inherit', (r['blueprint'], ro)
        f = {
            'AffectedByLightprobeVisibility': bool(src['AffectedByLightprobeVisibility']),
            'AutoStart': bool(src['AutoStart']),
            'Blueprint': {'external': {'file_guid': None, 'class_guid': None, 'asset': NS + r['blueprint'], 'type': 'EffectBlueprint'}},
            'BlueprintTransform': lt(r['transform']),
            'CreateIndestructibleEntity': False, 'EffectParameters': [], 'Excluded': False,
            'Flags': int(src['Flags']) & 0xffffffff,
            'LightmapResolutionScale': int(src['LightmapResolutionScale']), 'LightmapScaleWithSize': True,
            'LocalPlayerId': src['LocalPlayerId'], 'ObjectVariation': {'null': True},
            'RadiosityTypeOverride': src['RadiosityTypeOverride'],
            'RenderingOverrides': {'type': 'RenderingOverrides', 'fields': {
                'DistantShadowCacheEnable': 'BoolOverride_Inherit', 'DynamicDistantShadowCacheEnable': 'BoolOverride_Inherit',
                'DynamicReflectionEnable': 'BoolOverride_Inherit', 'HologramEnable': 'BoolOverride_Inherit',
                'HologramProjectorIndex': 1, 'LocalShadowCacheEnable': 'BoolOverride_Inherit',
                'LocalShadowEnable': 'BoolOverride_Inherit', 'PlanarShadowEnable': 'BoolOverride_Inherit',
                'RootShaderEffect': 'BoolOverride_Inherit', 'ShadowEnable': 'BoolOverride_Inherit',
                'StaticReflectionEnable': 'BoolOverride_Inherit', 'SunShadowEnable': 'BoolOverride_Inherit'}},
            'StreamRealm': src['StreamRealm'],
        }
        idx = len(objects)
        refs.append({'ref': idx})
        objects.append({'type': 'EffectReferenceObjectData', 'exported': True, 'fields': f,
                        '_src': '%s#%d %s' % (r['partition'], r['objIndex'], ' > '.join(r['chain']))})
        if r['partition'].lower() == L3 and src['$guid'] and not src['$guid'].startswith('internal'):
            by2015[(L3, src['$guid'].lower())] = idx
    # ---- 2015 VFX schematic: its 12 logic entities and the area shape (an object of layer3 no Objects list names)
    l8 = load(L8)
    for o in l8['objects'][1:]:
        by2015[(L8, o['$guid'].lower())] = len(objects)
        refs.append({'ref': len(objects)})
        lo = logic_object(o)
        lo['_src'] = '%s#%d' % (L8, o['$i'])
        objects.append(lo)
    l3 = load(L3)
    shape = [o for o in l3['objects'] if o['$type'] == 'VolumeVectorShapeData']
    assert len(shape) == 1
    by2015[(L3, shape[0]['$guid'].lower())] = len(objects)
    so = logic_object(shape[0])
    so['_src'] = '%s#%d' % (L3, shape[0]['$i'])
    objects.append(so)
    # ---- connections: all of them live on the StreamedLevel root in 2015; keep those whose both ends are ported
    sl = load(SL)
    root = sl['objects'][sl['root']]

    def end(x):
        if not (isinstance(x, dict) and '$ext' in x):
            return None
        return by2015.get((x['$ext'].lower(), (x.get('$cg') or '').lower()))
    conns = {'EventConnections': [], 'PropertyConnections': [], 'LinkConnections': []}
    dropped = []
    for kind in conns:
        for c in root.get(kind) or []:
            s, t = end(c.get('Source')), end(c.get('Target'))
            touches = any(isinstance(x, dict) and (x.get('$ext') or '').lower() == L8 for x in (c.get('Source'), c.get('Target')))
            if s is None or t is None:
                if touches:
                    dropped.append('%s %s -> %s' % (kind, (c.get('Source') or {}).get('$cg'), (c.get('Target') or {}).get('$cg')))
                continue
            f = {k: flat_to_pkg(v) for k, v in c.items() if not k.startswith('$') and k not in ('Source', 'Target')}
            f['Source'] = {'ref': s}
            f['Target'] = {'ref': t}
            conns[kind].append({'type': kind[:-1], 'fields': dict(sorted(f.items()))})
    hack = str(uuid.UUID(bytes=hashlib.md5(b'FX_Hoth2015 HackToSolveRealTimeTweakingIssue').digest()))
    objects[0] = {'type': 'LayerData', 'exported': True, 'fields': {
        'Enabled': True, 'EventConnections': conns['EventConnections'], 'Flags': 0,
        'HackToSolveRealTimeTweakingIssue': hack, 'Interface': {'null': True},
        'LinkConnections': conns['LinkConnections'], 'Name': LAYER, 'Objects': refs,
        'PropertyConnections': conns['PropertyConnections'], 'Schematics': {'null': True},
        'TimeDeltaType': 'TimeDeltaType_None'}}
    prov = [o.pop('_src', None) for o in objects]
    pkg = {'Version': 1, 'Kind': 'ebx-graph', 'Game': 'swbf2', 'Asset': LAYER, 'FileGuid': None, 'RootType': 'LayerData',
           'Sha256': None, 'Source': {'converter': 'fx_layer.py', 'placements': len(rows), 'from': prov[1:],
                                      'dropped_connections': dropped}, 'Objects': objects}
    json.dump(canonical(pkg), open(out, 'w'), indent=1)
    print('%d effect references + 13 schematic objects, connections %s, %d schematic connections dropped (ends not ported) -> %s'
          % (len(rows), {k: len(v) for k, v in conns.items()}, len(dropped), out))


if __name__ == '__main__':
    main()
