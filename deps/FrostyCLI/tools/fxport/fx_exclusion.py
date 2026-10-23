"""Build EmitterExclusionVolumesAsset for the level from 2015's EmitterExclusionVolumeData (7 in WA: 6 in
PF_Arctic_01_Base_02_CombinedforE3, 1 in PF_Arctic_01_RebelLastBase_02) -- the boxes inside which emitters with
ExclusionVolumeCullEnable (2015's camera-proximity snowfall, dust motes) draw nothing, i.e. no snow inside Echo Base.

BFII bakes the volumes into this asset (the level's EmitterSystemComponent.ExclusionVolumes). The layout is the one the
Frostbite ExclusionVolumeDatabase builds and DICE's Levels/MP/Hoth_01/EmitterExlusionVolumes carries (checked against
it: 19 volumes -> 20 entries, 5 sphere groups):
  volume  = Left/Up/Forward: unit axes of the world transform, w = the inverse transform's translation component;
            HalfExtents = axis lengths * 0.5, w = tag mask (0); Id 0; list padded to a multiple of 4 with zero volumes
  spheres = SoA groups of 4: position = transform translation, RadiusSqr = |HalfExtents.xyz|^2; unused slots
            (0x7F7F7F7F, 0, 0, 0)
A 2015 volume's box is its transform's axes (scaled), centred on its translation (FIFA ExclusionVolumeDatabase:
halfExtents = getScale(transform) * 0.5).

usage: fx_exclusion.py <out package>
"""
import json, math, struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from canon import canonical  # noqa: E402

NAME = 'AddedContent/Hoth2015/Levels/Hoth2015_01/EmitterExclusionVolumes_Hoth2015'
PAD = struct.unpack('<f', b'\x7f\x7f\x7f\x7f')[0]


def T(lt):
    return [[lt[a]['x'], lt[a]['y'], lt[a]['z']] for a in ('right', 'up', 'forward', 'trans')]


def compose(local, parent):
    b = parent[:3]
    mul = lambda v: [v[0] * b[0][j] + v[1] * b[1][j] + v[2] * b[2][j] for j in range(3)]
    t = mul(local[3])
    return [mul(local[0]), mul(local[1]), mul(local[2]), [t[j] + parent[3][j] for j in range(3)]]


def v4(x, y, z, w):
    return {'type': 'Vec4', 'fields': {'w': float(w), 'x': float(x), 'y': float(y), 'z': float(z)}}


def main():
    inv = json.load(open('inv/fx_inventory_raw.json'))
    vols = [v for v in inv['exclusionVolumes'] if v['scope'] == 'WA' and v['object'].get('Enabled', True)]
    world = [compose(T(v['object']['Transform']), v['parentTransform']) for v in vols]
    volumes, spheres_src = [], []
    for w in world:
        axes = w[:3]
        lens = [math.sqrt(sum(c * c for c in a)) for a in axes]
        unit = [[c / l for c in a] for a, l in zip(axes, lens)]
        t = w[3]
        inv_t = [-sum(t[j] * u[j] for j in range(3)) for u in unit]
        half = [l * 0.5 for l in lens]
        volumes.append({'type': 'EmitterExclusionVolume', 'fields': {
            'Forward': v4(*unit[2], inv_t[2]), 'HalfExtents': v4(*half, 0.0), 'Id': 0,
            'Left': v4(*unit[0], inv_t[0]), 'Up': v4(*unit[1], inv_t[1])}})
        spheres_src.append((t, sum(h * h for h in half)))
    count = len(volumes)
    while len(volumes) % 4:
        volumes.append({'type': 'EmitterExclusionVolume', 'fields': {
            'Forward': v4(0, 0, 0, 0), 'HalfExtents': v4(0, 0, 0, 0), 'Id': 0, 'Left': v4(0, 0, 0, 0), 'Up': v4(0, 0, 0, 0)}})
    spheres = []
    for g in range(0, len(volumes), 4):
        slots = []
        for i in range(g, g + 4):
            slots.append((spheres_src[i][0], spheres_src[i][1]) if i < count else ([PAD, 0.0, 0.0], 0.0))
        comp = lambda k: {'type': 'Vec4', 'fields': dict(zip(('x', 'y', 'z', 'w'), [float(s[0][k]) for s in slots]))}
        spheres.append({'type': 'EmitterExclusionVolumeBoundingSphereSoA', 'fields': {
            'PosX': comp(0), 'PosY': comp(1), 'PosZ': comp(2),
            'RadiusSqr': {'type': 'Vec4', 'fields': dict(zip(('x', 'y', 'z', 'w'), [float(s[1]) for s in slots]))}}})
    pkg = {'Version': 1, 'Kind': 'ebx-graph', 'Game': 'swbf2', 'Asset': NAME, 'FileGuid': None,
           'RootType': 'EmitterExclusionVolumesAsset', 'Sha256': None,
           'Source': {'converter': 'fx_exclusion.py', 'from': [' > '.join(v['chain']) + ' #%d' % v['objIndex'] for v in vols]},
           'Objects': [{'type': 'EmitterExclusionVolumesAsset', 'exported': True, 'fields': {
               'ExclusionVolumeBoundingSpheres': spheres, 'ExclusionVolumes': volumes,
               'ExclusionVolumesCount': count, 'Name': NAME}}]}
    json.dump(canonical(pkg), open(sys.argv[1], 'w'), indent=1)
    for w, v in zip(world, vols):
        print('  volume at (%.1f, %.1f, %.1f) extents %s  %s' % (w[3][0], w[3][1], w[3][2],
              [round(math.sqrt(sum(c * c for c in a)), 1) for a in w[:3]], v['chain'][-1].split('/')[-1]))
    print('%d volumes (+%d padding), %d sphere groups -> %s' % (count, len(volumes) - count, len(spheres), sys.argv[1]))


if __name__ == '__main__':
    main()
