"""Save the colour channels of a picture without its alpha (packed game textures keep data in alpha).
python rgb_view.py <in.png> <out.png> [scale]"""
import sys

import numpy as np
from PIL import Image

im = np.asarray(Image.open(sys.argv[1]).convert('RGBA'))
k = float(sys.argv[3]) if len(sys.argv) > 3 else 1.0
out = Image.fromarray(im[..., :3])
if k != 1.0:
    out = out.resize((int(out.width * k), int(out.height * k)), Image.NEAREST)
out.save(sys.argv[2])
print(im.shape, 'r', im[..., 0].min(), im[..., 0].max(), 'g', im[..., 1].min(), im[..., 1].max(), 'b', im[..., 2].min(), im[..., 2].max(), 'a', im[..., 3].min(), im[..., 3].max())
