"""UpdateClipScaleData.Lookup from an atlas texture's alpha -- the Frostbite EmitterBuildClipScaleLookupPipeline
algorithm (FIFA-branch EmitterPipeline.cpp): per frame, shrink the quad symmetrically to the rows/columns whose alpha
exceeds 0.0001, then max each frame with the NEXT one (frame blending), store clamp(v,0,1)*32767 as s16 x4."""
import numpy as np


def frame_scales(alpha, frames, cols, border_w=0, border_h=0, per_frame_border=False, thr=0.0001):
    th, tw = alpha.shape
    rows = frames // cols
    width = (tw - (0 if per_frame_border else border_w * 2)) // cols
    height = (th - (0 if per_frame_border else border_h * 2)) // rows
    wb = tw // cols - border_w * 2
    hb = th // rows - border_h * 2
    fw = wb if per_frame_border else width
    fh = hb if per_frame_border else height
    out = []
    for f in range(frames):
        ox = width * (f % cols) + border_w
        oy = height * (f // cols) + border_h
        a = alpha[oy:oy + fh, ox:ox + fw] > thr
        rows_any = a.any(axis=1)
        cols_any = a.any(axis=0)
        # top: first row with alpha (scan limited to half the height)
        half_h, half_w = fh // 2, fw // 2
        top = next((y for y in range(0, half_h) if rows_any[y]), half_h)
        bottom = next((fh - 1 - y for y in range(fh - 1, fh - 1 - half_h, -1) if rows_any[y]), half_h)
        left = next((x for x in range(0, half_w) if cols_any[x]), half_w)
        right = next((fw - 1 - x for x in range(fw - 1, fw - 1 - half_w, -1) if cols_any[x]), half_w)
        out.append([(fh - top * 2) / fh, (fh - bottom * 2) / fh, (fw - left * 2) / fw, (fw - right * 2) / fw])
    res = [list(v) for v in out]
    nxt = out[0]
    for f in range(frames - 1, -1, -1):
        cur = out[f]
        res[f] = [max(a, b) for a, b in zip(cur, nxt)]
        nxt = cur
    lookup = []
    for v in res:
        lookup += [int(min(1.0, max(0.0, x)) * 32767.0) for x in v]
    return lookup
