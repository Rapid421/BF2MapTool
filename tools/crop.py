"""Cut a rectangle out of a picture and enlarge it. python crop.py <in.png> <out.png> x0 y0 x1 y1 [scale]"""
import sys
from PIL import Image

im = Image.open(sys.argv[1])
x0, y0, x1, y1 = [int(v) for v in sys.argv[3:7]]
k = float(sys.argv[7]) if len(sys.argv) > 7 else 3.0
c = im.crop((x0, y0, x1, y1))
c.resize((int(c.width * k), int(c.height * k)), Image.LANCZOS).save(sys.argv[2])
print(c.size, '->', (int(c.width * k), int(c.height * k)))
