"""radar_crop.py <shot.png>... : crop the HUD radar (bottom-left octagon) from 1600x900 game-window shots, 3x upscaled,
and a contact sheet of them (shots/<first stem>_radar_sheet.png). Radar box measured on tk16_predeploy.png:
x 60..255, y 680..875 of 1600x900."""
import os
import sys
from PIL import Image, ImageDraw

BOX = (60, 680, 255, 875)


def crop(path, scale=3):
    im = Image.open(path).convert('RGB')
    sx, sy = im.width / 1600.0, im.height / 900.0
    b = tuple(int(round(v * (sx if i % 2 == 0 else sy))) for i, v in enumerate(BOX))
    c = im.crop(b)
    return c.resize((c.width * scale, c.height * scale), Image.LANCZOS)


if __name__ == '__main__':
    paths = sys.argv[1:]
    tiles = []
    for p in paths:
        c = crop(p)
        out = os.path.splitext(p)[0] + '_radar.png'
        c.save(out)
        tiles.append((os.path.basename(os.path.splitext(p)[0]), c))
    if tiles:
        w, h = tiles[0][1].size
        cols = min(4, len(tiles))
        rows = (len(tiles) + cols - 1) // cols
        sheet = Image.new('RGB', (cols * w, rows * (h + 24)), (20, 20, 20))
        d = ImageDraw.Draw(sheet)
        for i, (name, c) in enumerate(tiles):
            x, y = (i % cols) * w, (i // cols) * (h + 24)
            sheet.paste(c, (x, y + 24))
            d.text((x + 6, y + 5), name, fill=(255, 255, 255))
        out = os.path.join(os.path.dirname(paths[0]), os.path.splitext(os.path.basename(paths[0]))[0] + '_radar_sheet.png')
        sheet.save(out)
        print(out)
