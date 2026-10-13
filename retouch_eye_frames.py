# -*- coding: utf-8 -*-
"""
Retouch the 13 eye-opening frames (01 closed ... 05 fully open) so they animate cleanly.
  stage A  lock: everything that is not the moving eye is copied back from the fully-open original (05)
  stage D  hair: hair in front of the eye never changes, so hair pixels are copied back from 05
  stage E  smear: bluish smears left on the lid next to the hair (inner / outer eye corner) become lid skin
  stage B  lid skin: leftover blotches on the eyelid are pulled to the clean closed-eye lid tone
  stage C  specks: tiny bright ghost dots on the lid skin are removed
Only pixels inside the two eye regions can change; 05 itself is never touched.
usage: python retouch_eye_frames.py <src dir with 13 png> <out dir> [-v] [--stats]
"""
import os, sys, numpy as np
from collections import deque
from PIL import Image

ROI = (880, 300, 1280, 500)      # x0,y0,x1,y1 holding both eyes (1536x1024 image)
SPLIT_X = 1080                    # left eye | right eye
T_CORE, R_DIL, TOP_MARGIN = 20, 5, 3
SMEAR_ZONES = ((1008, 410, 1040, 434), (1216, 334, 1242, 362))   # left eye inner corner, right eye outer corner
PULL_TO_CLOSED = (1,)                                             # frame indexes whose lid is pulled to the closed frame's lid (8%)
FAR_PX = 18
SPECK_PEAK = 8
SPECK_SPOTS = ((1173, 368, 5), (1176, 375, 5), (983, 440, 5), (1201, 366, 7))   # x, y, radius
SPECK_LAST_FRAME = 2                                              # 0%, 8%, 17%
VERBOSE = "-v" in sys.argv
STRAND_SEED = (1016, 413)                                         # a pixel on the hair strand over the left eye's inner corner
BORROW = ((1, 2, (1226, 342, 1240, 358)),)                        # (frame index, borrow from frame index, box)


def load(p):
    return np.asarray(Image.open(p).convert("RGB")).astype(np.float32)


def save(a, p):
    Image.fromarray(np.clip(np.rint(a), 0, 255).astype(np.uint8), "RGB").save(p, optimize=True)


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


def grey_open(a, r):
    """greyscale opening per channel (min then max over a disc): removes bright details smaller than the disc"""
    offs = [(dy, dx) for dy in range(-r, r + 1) for dx in range(-r, r + 1) if dx * dx + dy * dy <= r * r + 0.5]

    def run(img, fn):
        pad = np.pad(img, ((r, r), (r, r), (0, 0)), mode="edge")
        out = img.copy()
        Hh, Ww = img.shape[:2]
        for dy, dx in offs:
            out = fn(out, pad[r + dy:r + dy + Hh, r + dx:r + dx + Ww])
        return out
    return run(run(a, np.minimum), np.maximum)


def components(m):
    m = m.astype(bool)
    H, W = m.shape
    lab = np.zeros((H, W), np.int32)
    sizes = [0]
    cur = 0
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
        sizes.append(n)
    return lab, np.array(sizes)


def fill_holes(m):
    m = m.astype(bool)
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


def smoothstep(x, a, b):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def main_parts(mask, split):
    """per eye: the biggest connected part, plus small parts touching it (lash tips)"""
    lab, sizes = components(mask)
    keep = np.zeros(mask.shape, bool)
    W = mask.shape[1]
    for xa, xb in ((0, split), (split, W)):
        sub = lab[:, xa:xb]
        ids = np.unique(sub[sub > 0])
        if len(ids):
            keep |= (lab == max(ids, key=lambda i: sizes[i]))
    near = dilate(keep, 3)
    for i in range(1, len(sizes)):
        cm = lab == i
        if not (cm & keep).any() and (cm & near).any():
            keep |= cm
    return keep


def big_parts(mask, min_size):
    lab, sizes = components(mask)
    return np.isin(lab, [k for k in range(1, len(sizes)) if sizes[k] >= min_size])


def top_of(mask, width):
    """per column: y of the top-most set pixel; columns without any take the value of the nearest column that has one"""
    has = mask.any(axis=0)
    top = np.where(has, mask.argmax(axis=0), -1)
    valid = np.nonzero(has)[0]
    out = np.full(width, -1, np.int64)
    if len(valid):
        for x in range(mask.shape[1]):
            out[x] = top[x] if has[x] else top[valid[np.argmin(np.abs(valid - x))]]
    return out


def luma(a):
    return 0.3 * a[..., 0] + 0.59 * a[..., 1] + 0.11 * a[..., 2]


def skin_colour(a):
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    Y = luma(a)
    return (Y > 105) & (Y < 220) & (r - g > 10) & (r - g < 42) & (b - g > 2) & (b - g < 30) & (r > b - 4)


def plain_skin(a):
    """flat skin pixels: skin coloured, no line through them"""
    Y = luma(a)
    col = skin_colour(a)
    Yp = np.pad(Y, 1, mode="edge")
    gx = Yp[1:-1, 2:] - Yp[1:-1, :-2]
    gy = Yp[2:, 1:-1] - Yp[:-2, 1:-1]
    flat = np.hypot(gx, gy) < 11
    return erode(col & flat, 1)


def run(src, out):
    os.makedirs(out, exist_ok=True)
    ns = sorted(f for f in os.listdir(src) if f.lower().endswith(".png"))
    assert len(ns) == 13, ns
    ref_full = load(os.path.join(src, ns[-1]))
    x0, y0, x1, y1 = ROI
    split = SPLIT_X - x0
    ref = ref_full[y0:y1, x0:x1]
    full = [load(os.path.join(src, n)) for n in ns[:-1]]
    for n, a in zip(ns, full):
        d = np.abs(a - ref_full).max(axis=2)
        d[y0:y1, x0:x1] = 0
        assert d.max() == 0, "frame differs from the original outside the eye region: " + n
    fr = [a[y0:y1, x0:x1] for a in full]
    H, W = ref.shape[:2]
    yy = np.arange(H)[:, None]

    # ---------- stage A : lock ----------
    cores = [main_parts(np.abs(a - ref).max(axis=2) >= T_CORE, split) for a in fr]
    late = np.zeros((H, W), bool)
    for c in cores[7:]:                      # 58%..92% : clean frames, they agree on the lid's top edge
        late |= c
    limit = np.zeros(W, np.int32)
    for xa, xb in ((0, split), (split, W)):
        seg = np.array([(np.nonzero(late[:, x])[0].min() if late[:, x].any() else -1) for x in range(xa, xb)])
        valid = np.nonzero(seg >= 0)[0]
        seg = np.array([seg[x] if seg[x] >= 0 else seg[valid[np.argmin(np.abs(valid - x))]] for x in range(len(seg))])
        limit[xa:xb] = np.array([seg[max(0, x - 4):x + 5].min() for x in range(len(seg))]) - TOP_MARGIN
    allowed = yy >= limit[None, :]
    A, alphas = [], []
    for a, c in zip(fr, cores):
        core = fill_holes(main_parts(c & allowed, split)) & allowed
        m = dilate(core, R_DIL) & (yy >= (limit[None, :] - R_DIL))
        al = np.maximum(np.clip(gauss(m.astype(np.float32), 2.0) * 1.6 - 0.3, 0, 1), core.astype(np.float32))
        A.append(ref + al[..., None] * (a - ref))
        alphas.append(al)

    # clean lid tone from the closed frame, lines filled through (used by stages E and B)
    P0 = plain_skin(A[0]).astype(np.float32)
    den = gauss(P0, 4.0)
    S = gauss(A[0] * P0[..., None], 4.0) / np.maximum(den, 1e-3)[..., None]
    S_ok = smoothstep(den, 0.10, 0.30)

    # ---------- stage D : hair ----------
    br_ref = ref[..., 2] - ref[..., 0]
    lid01 = ((A[0][..., 2] - A[0][..., 0]) < -4) & (luma(A[0]) > 90)     # skin when the eye is closed
    # bluish, mid-dark pixels of the open frame are hair - unless the closed frame shows lid skin there (then it is the eyeball)
    hair_ref = (br_ref >= 6) & (luma(ref) >= 45) & (luma(ref) <= 140) & ~lid01
    d01 = np.abs(A[0] - ref).max(axis=2)
    same01 = 1 - smoothstep(d01, 10, 20)                 # closed frame and open frame agree here
    w_d1 = np.clip(gauss((hair_ref & (d01 <= 12)).astype(np.float32), 0.6) * 1.6, 0, 1) * same01
    # the strand that hangs over the inner corner of the left eye: nothing ever passes in front of it
    strand = np.zeros((H, W), bool)
    zx0, zy0, zx1, zy1 = SMEAR_ZONES[0]
    strand[zy0 - y0:zy1 - y0, zx0 - x0:zx1 - x0] = True
    lab_h, _ = components(hair_ref)
    assert lab_h[STRAND_SEED[1] - y0, STRAND_SEED[0] - x0] > 0, "strand seed is not on hair"
    strand &= lab_h == lab_h[STRAND_SEED[1] - y0, STRAND_SEED[0] - x0]     # only the strand itself, not the eyeball rim next to it
    nhair = []
    for i in range(1, 12):
        a = A[i]
        hair_i = ((a[..., 2] - a[..., 0]) >= 6) & (luma(a) >= 45)
        w = np.maximum(w_d1, ((hair_ref & hair_i) | strand).astype(np.float32))
        nhair.append(int(((w > 0.5) & (np.abs(a - ref).max(axis=2) > 4)).sum()))
        A[i] = a + w[..., None] * (ref - a)

    # ---------- stage E : smear ----------
    zone = np.zeros((H, W), bool)
    for zx0, zy0, zx1, zy1 in SMEAR_ZONES:
        zone[zy0 - y0:zy1 - y0, zx0 - x0:zx1 - x0] = True
    skin01 = ((A[0][..., 2] - A[0][..., 0]) < -8) & (luma(A[0]) > 95)   # lid skin when the eye is closed
    hair_keep = dilate(hair_ref, 1)
    nsmear = [0]
    for i in range(1, 12):
        a = A[i]
        bri = a[..., 2] - a[..., 0]
        lab_e, sizes_e = components((bri > 15) & (luma(a) > 120))
        eye_open = dilate(np.isin(lab_e, [k for k in range(1, len(sizes_e)) if sizes_e[k] >= 40]), 2)   # the visible eyeball of this frame (big, bright, blue)
        cand = zone & (bri > -4) & (luma(a) > 50) & (luma(a) < 175) & skin01 & ~hair_keep & (alphas[i] > 0.3)
        lab, sizes = components(cand)
        fix = np.zeros((H, W), bool)
        for k in range(1, len(sizes)):
            cm = lab == k
            # a smear is a blob at least 3 px thick that is not the rim of the eyeball
            if sizes[k] >= 10 and erode(cm, 1).any() and not (cm & eye_open).any():
                fix |= cm
        nsmear.append(int(fix.sum()))
        # the closed frame is clean here (lid skin + the hair strand), so the smear and the strand edge next to it are taken from it
        w = np.clip(gauss((dilate(fix, 2) & zone).astype(np.float32), 1.0) * 1.6, 0, 1)
        A[i] = a + w[..., None] * (A[0] - a)

    # ---------- stage B0 : 8% frame ----------
    # at 8% the lid is still the closed lid: where both frames show skin and differ only a little, use the closed frame's pixels
    for i in PULL_TO_CLOSED:
        a = A[i]
        # left eye: the source frame has a doubled hair strand and a bright patch high on the lid. Farther than
        # FAR_PX from this frame's lashes / eyeball the lid cannot differ from the closed lid at all -> closed frame's pixels
        bri = a[..., 2] - a[..., 0]
        lab_e, sizes_e = components((bri > 15) & (luma(a) > 120))
        moved = np.abs(a - ref).max(axis=2) > 20                       # static dark hair does not count as structure
        structure = big_parts((luma(a) < 60) & moved, 30) | np.isin(lab_e, [k for k in range(1, len(sizes_e)) if sizes_e[k] >= 40])
        far = ~dilate(structure, FAR_PX)
        far[:, split:] = False
        far &= yy < top_of(structure[:, :split], W)[None, :]            # only the lid ABOVE the eye
        w = gauss(far.astype(np.float32), 2.5)      # (above the lid's top edge the closed frame is the original anyway)
        a = a + w[..., None] * (A[0] - a)
        both = skin_colour(a) & skin_colour(A[0])
        dlt = a - A[0]
        mag = gauss(np.abs(dlt).max(axis=2), 0.8)
        lab_e, sizes_e = components((a[..., 2] - a[..., 0] > 15) & (luma(a) > 120))
        structure = big_parts((luma(a) < 60) & moved, 30) | np.isin(lab_e, [k for k in range(1, len(sizes_e)) if sizes_e[k] >= 40])
        tops = np.concatenate([top_of(structure[:, :split], split), top_of(structure[:, split:], W - split)])
        above = (yy < tops[None, :]).astype(np.float32)        # both eyes this time, still only above the eye
        w = (1 - smoothstep(mag, 14, 28)) * np.clip(gauss(both.astype(np.float32), 0.7) * 1.4, 0, 1) * both * smoothstep(alphas[i], 0.0, 0.6) * above
        A[i] = a - w[..., None] * dlt

    # ---------- stage B : lid skin (two rounds) ----------
    B = [A[0]]
    lid_stats = []
    for i in range(1, 12):
        a = A[i]
        n_even = 0
        for _ in range(2):
            P = plain_skin(a).astype(np.float32)
            dn = gauss(P, 2.0)
            low = gauss((a - S) * P[..., None], 2.0) / np.maximum(dn, 1e-3)[..., None]
            mag = np.abs(low).max(axis=2)
            w = ((1 - smoothstep(mag, 10, 24))
                 * np.clip(gauss(erode(P > 0, 1).astype(np.float32), 1.0) * 1.5, 0, 1)
                 * S_ok * smoothstep(alphas[i], 0.0, 0.6) * smoothstep(dn, 0.25, 0.5))
            n_even = max(n_even, int(((w > 0.5) & (mag > 2)).sum()))
            a = a - w[..., None] * low
        B.append(a)
        lid_stats.append(n_even)

    # ---------- stage C : specks ----------
    # only at the checked spots: faint white ghost dots left on the lid skin of the first frames (they fade out by 33%).
    # The white sparkle dots of the original drawing (e.g. under the left eye) and the glints on the lashes are not touched.
    spots = np.zeros((H, W), bool)
    gy, gx = np.mgrid[0:H, 0:W]
    for sx, sy, sr in SPECK_SPOTS:
        spots |= (gx - (sx - x0)) ** 2 + (gy - (sy - y0)) ** 2 <= sr * sr
    C, nspeck = [], []
    for i, a in enumerate(B):
        if i > SPECK_LAST_FRAME:
            C.append(a)
            nspeck.append(0)
            continue
        opened = grey_open(a, 2)                 # the picture without anything bright that is smaller than ~4 px
        over = luma(a) - luma(opened)
        lab, sizes = components((over > 5) & spots & (luma(a) > 120))
        fix = np.zeros((H, W), bool)
        cnt = 0
        for k in range(1, len(sizes)):
            cm = lab == k
            ring = dilate(cm, 4) & ~dilate(cm, 1)
            # faint, small, and sitting on lid skin (once the lid has lifted, the same spot shows the eye - leave that alone)
            if sizes[k] <= 20 and SPECK_PEAK <= over[cm].max() <= 45 and skin_colour(a)[ring].mean() >= 0.6:
                fix |= dilate(cm, 1)
                cnt += 1
                if VERBOSE:
                    ys, xs = np.nonzero(cm)
                    print("   speck frame %d at (%d,%d) size %d peak +%d" % (i, xs.mean() + x0, ys.mean() + y0, sizes[k], over[cm].max()))
        wsp = np.clip(gauss(fix.astype(np.float32), 0.8) * 2.2, 0, 1)
        C.append(a + wsp[..., None] * (opened - a))
        nspeck.append(cnt)

    # ---------- stage F : borrow ----------
    # 8% frame, outer corner of the right eye: the lashes against the hair came out as ragged blocks.
    # The corner hardly moves between 8% and 17%, so that small patch is taken from the (clean) 17% frame.
    for fi, src_i, (bx0, by0, bx1, by1) in BORROW:
        m = np.zeros((H, W), np.float32)
        m[by0 - y0:by1 - y0, bx0 - x0:bx1 - x0] = 1
        m = gauss(m, 1.5)
        C[fi] = C[fi] + m[..., None] * (C[src_i] - C[fi])

    stats = {}
    for i, n in enumerate(ns[:-1]):
        o = ref_full.copy()
        o[y0:y1, x0:x1] = C[i]
        save(o, os.path.join(out, n))
        o8 = np.rint(np.clip(C[i], 0, 255))
        d_ref = np.abs(o8 - ref).max(axis=2)
        d_src = np.abs(o8 - fr[i]).max(axis=2)
        d_in = np.abs(fr[i] - ref).max(axis=2)
        stats[n] = {
            "differs_from_original_before": int((d_in > 0).sum()), "differs_from_original_after": int((d_ref > 0).sum()),
            "changed": int((d_src > 0).sum()), "changed_clearly": int((d_src >= 8).sum()), "max_change": int(d_src.max()),
            "locked_back_to_original": int(((d_in > 0) & (d_ref == 0)).sum()),
            "hair_restored": nhair[i - 1] if i else 0, "smear": nsmear[i], "lid_evened": lid_stats[i - 1] if i else 0, "specks": nspeck[i],
        }
        print("%-4s differs from original: %5d -> %5d px | changed %5d px | locked back %5d | hair %4d | smear %4d | lid evened %5d | specks %d" % (
            n[:3].strip("_"), stats[n]["differs_from_original_before"], stats[n]["differs_from_original_after"], stats[n]["changed"],
            stats[n]["locked_back_to_original"], stats[n]["hair_restored"], stats[n]["smear"], stats[n]["lid_evened"], stats[n]["specks"]))
    with open(os.path.join(src, ns[-1]), "rb") as f:        # 05: byte-for-byte copy
        data = f.read()
    with open(os.path.join(out, ns[-1]), "wb") as f:
        f.write(data)
    return stats


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("-")]
    result = run(args[0], args[1])
    if "--stats" in sys.argv:           # also write the per-frame numbers next to this script's caller
        import json
        with open(os.path.join(args[1], "_stats.json"), "w", encoding="utf-8") as f:
            json.dump(result, f, ensure_ascii=False, indent=1)
