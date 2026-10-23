"""2015 particle atlases that Battlefront II does not ship, imported with their 2015 pixels.

An AtlasTextureAsset's resource (type 0x957C32B1) is a small header -- flags u16, width u16, height u16, unknown u16,
border w/h floats, chunk guid -- whose chunk holds the texels of every mip (Frosty AtlasTexturePlugin). 2015 writes
version 2 (32 bytes); BFII writes version 3, the same 32 bytes followed by 15 u32 mip sizes, computed exactly like
AtlasTexture.SetData: per mip max(1,(w+3)/4)*4*h bytes until the chunk is consumed. The res meta is
[version, nameHash, 0, 0]; nameHash is name-derived and identical in both games (t_snow_01_d: 0x580B4132 in both),
so the assets keep their 2015 names (free in BFII) and 2015's hash. The chunk keeps its 2015 guid (free in BFII).

usage: fx_textures.py            -> writes tex_import/*.res (framed) + tex_import/atlas_*.json packages + steps json
"""
import json, os, struct, uuid, sys
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from canon import canonical  # noqa: E402
from clipscale import frame_scales  # noqa: E402
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'texcmp'))
from bc3 import bc3_decode  # noqa: E402

FX = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEX = {  # 2015 EBX name, file guid, res name, 2015 res nameHash (res meta word 1), frames, columns
    # absent from BFII
    'FX/Textures/Misc/T_TaunTaun_14x1_01_C': ('0fd264de-79a8-429a-a19c-64ad369aed60', 'fx/textures/misc/t_tauntaun_14x1_01_c', 0xCE9C8AB2, 14, 14),
    'FX/Textures/Smoke/T_billowSmokeHiLDB_8x8_01_N': ('4ff0bc82-d790-11e3-91a3-93dd906b377d', 'fx/textures/smoke/t_billowsmokehildb_8x8_01_n', 0x45A6A167, 64, 8),
    # BFII ships these file guids as DICE's rebuilt, doubled "Gnomon" atlases (T_*_Gnomon_*, LeftRightTiles, other
    # texels: left tiles vs 2015 alpha corr 0.94 / 0.82 / 0.74, RGB much brighter); the 2015 names are free in BFII,
    # so the 2015 texels come across under the 2015 names and hashes.
    'FX/Textures/Smoke/T_HighDetailLoopRUF_8x64_01_D': ('bab355bf-b5c7-11e4-9cdd-801aa11ce6fd', 'fx/textures/smoke/t_highdetailloopruf_8x64_01_d', 0x0F2D79F2, 64, 8),
    'FX/Textures/Smoke/T_billowSmokeHiRUF_8x8_01_D': ('6fc13d59-d790-11e3-91a3-93dd906b377d', 'fx/textures/smoke/t_billowsmokehiruf_8x8_01_d', 0xD5469866, 64, 8),
    'FX/Textures/Smoke/T_SmokeSpikesRUF_8x32_01_D': ('43ff3ec0-b758-11e4-b682-b3664bbbc419', 'fx/textures/smoke/t_smokespikesruf_8x32_01_d', 0x72549DBA, 32, 8),
}
ROOT_BUNDLE = 'win32/AddedContent/Hoth2015/Levels/Hoth2015_01/Hoth2015_01'


def mips(w, h, total):
    out = []
    left = total
    for _ in range(15):
        if left == 0:
            out.append(0); continue
        w, h = max(1, w), max(1, h)
        size = max(1, (w + 3) // 4) * 4 * h
        out.append(size)
        left -= size
        w >>= 1; h >>= 1
    if left != 0:
        raise ValueError('mip chain does not consume the chunk exactly (left %d)' % left)
    return out


def main():
    d = os.path.join(FX, 'tex_import')
    steps, mapping, clips = [], {}, {}
    for name, (fg, res, nh, frames, cols) in TEX.items():
        hdr = open(os.path.join(FX, 'texres15', fg + '.res'), 'rb').read()
        assert len(hdr) == 32
        flags, w, h, unk = struct.unpack('<HHHH', hdr[:8])
        chunk = str(uuid.UUID(bytes_le=hdr[16:32]))
        data = open(os.path.join(d, chunk + '.chunk'), 'rb').read()
        ms = mips(w, h, len(data))
        payload = hdr + struct.pack('<15I', *ms)
        meta = struct.pack('<iIII', 3, nh, 0, 0)
        resfile = os.path.join(d, res.replace('/', '__') + '.framedres')
        open(resfile, 'wb').write(meta + payload)
        pkg = {'Version': 1, 'Kind': 'ebx-graph', 'Game': 'swbf2', 'Asset': name, 'FileGuid': None,
               'RootType': 'AtlasTextureAsset', 'Sha256': None,
               'Source': {'converter': 'fx_textures.py', 'game': 'swbf1', 'file_guid': fg, 'chunk': chunk},
               'Objects': [{'type': 'AtlasTextureAsset', 'exported': True, 'fields': {
                   'AnimationColumnCount': cols, 'AnimationFrameCount': frames, 'Name': name,
                   # BFII-only: true only on DICE's doubled "Gnomon" atlases (left/right tile sets); a 2015 atlas has one set
                   'LeftRightTiles': False,
                   'Resource': {'resource': {'rid': '0x0', 'name': res}}}}]}
        pf = os.path.join(d, 'atlas_' + res.split('/')[-1] + '.json')
        json.dump(canonical(pkg), open(pf, 'w'), indent=1)
        steps += [
            {'op': 'chunk-import', 'file': os.path.join(d, chunk + '.chunk'), 'new': True, 'chunk_id': chunk},
            {'op': 'res-import', 'file': resfile, 'res': res, 'type': str(0x957C32B1), 'framed': True},
            {'op': 'ebx-graph-import', 'file': pf, 'as': name, 'bundle': ROOT_BUNDLE, 'strict': True, 'unsealed': True},
            {'op': 'bundle-add', 'res': res, 'bundle': ROOT_BUNDLE},
            {'op': 'bundle-add', 'chunk_id': chunk, 'bundle': ROOT_BUNDLE},
        ]
        mapping[fg] = name
        if flags == 0:   # colour atlases (BC3): the emitter's UpdateClipScaleData comes from these texels' alpha
            alpha = bc3_decode(data, w, h)[..., 3].astype(np.float32) / 255.0
            clips[fg] = frame_scales(alpha, frames, cols)
        print('%s: %dx%d flags %d, chunk %s %d bytes, mips %s' % (name, w, h, flags, chunk, len(data), [m for m in ms if m]))
    json.dump({'steps': steps, 'guid_to_asset': mapping, 'clip_by_guid': clips}, open(os.path.join(d, 'texture_steps.json'), 'w'), indent=1)


if __name__ == '__main__':
    main()
