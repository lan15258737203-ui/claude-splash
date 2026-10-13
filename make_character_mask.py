# -*- coding: utf-8 -*-
"""
Character matte for the splash picture: white = the character (kept at full brightness),
black = everything else (the program darkens it), soft edge in between.
usage: python make_character_mask.py <picture.png> <mask.png> [preview.png]
"""
import sys
from collections import deque
import numpy as np
from PIL import Image

THRESHOLD = 16      # the starry background is almost black (luma ~5); anything clearly brighter belongs to the character
CLOSE_R = 9         # closes the gaps between hair strands (half-size pixels)
GROW_R = 3          # keeps the glowing rim of the hair inside the matte
SOFT_SIGMA = 7.0    # soft edge (half-size pixels)


def gauss(a, sigma):
    r = int(max(1, round(sigma * 3)))
    x = np.arange(-r, r + 1, dtype=np.float32)
    k = np.exp(-x * x / (2 * sigma * sigma))
    k /= k.sum()

    def conv(b, axis):
        pad = [(0, 0)] * b.ndim
        pad[axis] = (r, r)
        bp = np.pad(b, pad, mode="edge")
        out = np.zeros_like(b)
        for i, kv in enumerate(k):
            sl = [slice(None)] * b.ndim
            sl[axis] = slice(i, i + b.shape[axis])
            out += kv * bp[tuple(sl)]
        return out
    return conv(conv(a.astype(np.float32), 0), 1)


def dilate(m, r):
    m = m.astype(bool)
    out = m.copy()
    H, W = m.shape
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            if dx * dx + dy * dy > r * r + 0.5 or (dx == 0 and dy == 0):
                continue
            a0, a1 = max(0, dy), min(H, H + dy)
            b0, b1 = max(0, dx), min(W, W + dx)
            out[a0:a1, b0:b1] |= m[a0 - dy:a1 - dy, b0 - dx:b1 - dx]
    return out


def erode(m, r):
    return ~dilate(~m.astype(bool), r)


def biggest(m):
    """largest 8-connected part"""
    H, W = m.shape
    lab = np.zeros((H, W), np.int32)
    best, best_n, cur = 0, 0, 0
    for y0, x0 in zip(*np.nonzero(m)):
        if lab[y0, x0]:
            continue
        cur += 1
        lab[y0, x0] = cur
        q = deque([(y0, x0)])
        n = 0
        while q:
            y, x = q.popleft()
            n += 1
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    yy, xx = y + dy, x + dx
                    if 0 <= yy < H and 0 <= xx < W and m[yy, xx] and not lab[yy, xx]:
                        lab[yy, xx] = cur
                        q.append((yy, xx))
        if n > best_n:
            best, best_n = cur, n
    return lab == best


def fill_holes(m):
    H, W = m.shape
    pad = np.zeros((H + 2, W + 2), bool)
    pad[1:-1, 1:-1] = m
    bg = np.zeros_like(pad)
    q = deque([(0, 0)])
    bg[0, 0] = True
    while q:
        y, x = q.popleft()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            yy, xx = y + dy, x + dx
            if 0 <= yy < H + 2 and 0 <= xx < W + 2 and not pad[yy, xx] and not bg[yy, xx]:
                bg[yy, xx] = True
                q.append((yy, xx))
    return ~bg[1:-1, 1:-1]


def main(src, dst, preview=None):
    im = Image.open(src).convert("RGB")
    W0, H0 = im.size
    small = im.resize((W0 // 2, H0 // 2), Image.LANCZOS)
    b = np.asarray(small).astype(np.float32)
    Y = gauss(0.3 * b[..., 0] + 0.59 * b[..., 1] + 0.11 * b[..., 2], 1.2)
    m = Y > THRESHOLD
    m = dilate(erode(m, 1), 1)            # stars and other specks go
    m = biggest(m)
    H, W = m.shape
    # the character runs off the bottom and the right edge of the picture: treat those stretches of the border as solid,
    # so the black dress and dark hair that touch the border count as "inside"
    solid = dilate(m, CLOSE_R)
    xs = np.nonzero(solid[H - 1])[0]
    ys = np.nonzero(solid[:, W - 1])[0]
    if len(xs):
        solid[H - 1, xs.min():xs.max() + 1] = True
    if len(ys):
        solid[ys.min():ys.max() + 1, W - 1] = True
    solid = fill_holes(solid)
    solid = erode(np.pad(solid, CLOSE_R, mode="edge"), CLOSE_R)[CLOSE_R:-CLOSE_R, CLOSE_R:-CLOSE_R] | m
    solid = dilate(solid, GROW_R)
    soft = np.clip(gauss(solid.astype(np.float32), SOFT_SIGMA) * 1.25, 0, 1)
    soft = np.maximum(soft, solid.astype(np.float32) * 0.999)      # fully white inside, the blur only softens outwards
    mask = Image.fromarray(np.rint(soft * 255).astype(np.uint8), "L").resize((W0, H0), Image.BICUBIC)
    mask.save(dst, optimize=True)
    print("matte covers %.1f%% of the picture" % (100 * (np.asarray(mask) > 127).mean()))
    if preview:
        a = np.asarray(im).astype(np.float32)
        k = np.asarray(mask).astype(np.float32)[..., None] / 255
        dark = a * (1 - 0.70 * (1 - k))
        both = np.concatenate([a, dark], axis=1)
        Image.fromarray(np.clip(both, 0, 255).astype(np.uint8)).resize((W0, H0 // 2), Image.LANCZOS).save(preview)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
