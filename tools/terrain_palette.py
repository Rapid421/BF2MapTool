"""terrain_palette.py <export dir> [out.png]: which layer is which, as one picture.

Battlefront II terrain layers have no names -- a layer is its index into the host terrain's layer list -- so choosing
"sand here, rock there" starts with finding out what each index looks like. Run `frostycli terrain export-layers
<terrain> --out <dir>` on the terrain you build on (your `terrain create --host`), then this: one tile per painted layer,
the map's colour where that layer is painted and grey where it is not, with its index and how much of the map it covers.
Pick the indices you want, paint a mask for each (Blender vertex groups `bfii:<index>`, or PNGs in `layers.json`), and
`terrain create --layers layers.json` paints your terrain with them. `terrain retexture` then swaps a layer's texture
for your own.
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

# measured in game with labelled test textures (README, terrain retexture)
KNOWN = {
    'levels/mp/tatooine_01/terrain/terrain': {
        0: 'base: T_MosEisley_DirtGravelGreyScale_01 + T_DesertBase_MudSmooth_01',
        1: 'town ground: T_MosEisley_SandPacked_01',
        2: 'paving: T_MosEisley_DirtGravel_01',
        3: 'rocky overlay: T_DesertBase_Mudrocks_01',
    },
}


def font(size):
    for f in ('arial.ttf', 'segoeui.ttf', 'DejaVuSans.ttf'):
        try:
            return ImageFont.truetype(f, size)
        except OSError:
            pass
    return ImageFont.load_default()


def main(d, out):
    meta = json.load(open(os.path.join(d, 'layers.json')))
    source = meta.get('source', '').replace('res ', '').replace('.streamingtree_win32', '')
    known = KNOWN.get(source.lower(), {})
    colour_path = os.path.join(d, 'colour.png')
    tile = 256
    base = Image.open(colour_path).convert('RGB').resize((tile, tile)) if os.path.exists(colour_path) else Image.new('RGB', (tile, tile), (150, 140, 120))
    base_a = np.asarray(base, np.float32)
    grey = base_a.mean(axis=2, keepdims=True).repeat(3, axis=2) * 0.35 + 30
    layers = meta['layers']
    cols = 4
    rows = (len(layers) + cols - 1) // cols
    pad, head = 12, 58
    sheet = Image.new('RGB', (cols * (tile + pad) + pad, rows * (tile + head + pad) + pad + 40), (24, 24, 28))
    draw = ImageDraw.Draw(sheet)
    draw.text((pad, 10), 'Terrain layers of %s  (index = what layers.json / Blender bfii:<index> paints)' % source, fill=(230, 230, 230), font=font(16))
    for n, lay in enumerate(layers):
        mask_file = lay.get('mask')
        if mask_file and os.path.exists(os.path.join(d, mask_file)):
            m = np.asarray(Image.open(os.path.join(d, mask_file)).convert('L').resize((tile, tile)), np.float32)[..., None] / 255.0
        else:          # the base layer: everywhere nothing above it is opaque
            m = np.ones((tile, tile, 1), np.float32)
        img = (base_a * m + grey * (1 - m))
        if lay.get('coveragePercent', 100) < 20:       # small layers: mark every painted texel so it can be found
            hot = (m[..., 0] > 0.08)
            img[hot] = img[hot] * 0.4 + np.array([255, 60, 200], np.float32) * 0.6
        img = Image.fromarray(img.clip(0, 255).astype(np.uint8))
        x = pad + (n % cols) * (tile + pad)
        y = 40 + pad + (n // cols) * (tile + head + pad)
        sheet.paste(img, (x, y + head))
        if mask_file and lay.get('coveragePercent', 100) < 20:
            full = np.asarray(Image.open(os.path.join(d, mask_file)).convert('L'), np.float32) / 255.0
            ys_, xs_ = np.nonzero(full > 0.3)
            if len(xs_):                                   # zoomed inset: where the layer is painted, in colour
                cx_, cy_ = int(np.median(xs_)), int(np.median(ys_))
                half = max(24, int(max(np.percentile(xs_, 90) - np.percentile(xs_, 10), np.percentile(ys_, 90) - np.percentile(ys_, 10)) / 2) + 8)
                col_full = Image.open(colour_path).convert('RGB').resize(full.shape[::-1]) if os.path.exists(colour_path) else Image.new('RGB', full.shape[::-1], (150, 140, 120))
                box = (max(0, cx_ - half), max(0, cy_ - half), min(full.shape[1], cx_ + half), min(full.shape[0], cy_ + half))
                ca = np.asarray(col_full.crop(box), np.float32)
                ma = full[box[1]:box[3], box[0]:box[2]][..., None]
                inset = (ca * (0.35 + 0.65 * ma) + np.array([255, 60, 200], np.float32) * 0.35 * ma).clip(0, 255).astype(np.uint8)
                inset = Image.fromarray(inset).resize((tile // 2, tile // 2), Image.NEAREST)
                sheet.paste(inset, (x + tile // 2 - 4, y + head + tile // 2 - 4))
                ImageDraw.Draw(sheet).rectangle([x + tile // 2 - 5, y + head + tile // 2 - 5, x + tile - 4, y + head + tile - 4], outline=(255, 220, 120))
        idx = lay.get('hostLayer', n)
        draw.text((x, y + 4), 'layer %d   %.1f%% of the map' % (idx, lay.get('coveragePercent', 0)), fill=(255, 220, 120), font=font(15))
        if idx in known:
            draw.text((x, y + 26), known[idx][:40], fill=(200, 200, 200), font=font(12))
            if len(known[idx]) > 40:
                draw.text((x, y + 40), known[idx][40:80], fill=(200, 200, 200), font=font(12))
    sheet.save(out)
    print('wrote', out, '-', len(layers), 'layers of', source)


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else os.path.join(sys.argv[1], 'palette.png'))
