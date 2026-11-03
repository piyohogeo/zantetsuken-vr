# -*- coding: utf-8 -*-
"""SKIN-LINES-1: the cut bench's per-frame pictures compared across routes and shadow conditions (DESIGN 4.5.15).

The runs are made at the fixed view with the same pose table phases, so two runs' pictures of the same frame key (pre = the last
warmup frame, k0 = the request frame, k1.. = the frames after) show the same bodies at the same pixels; what differs is what the
route or the shadow condition draws differently. For every pair asked, per frame key: the pixels differing beyond the threshold,
their fraction, the largest channel difference, the bounding box, and -- the lines being thin and dark -- how many of the differing
pixels are DARKER in the second picture than in the first (and how many lighter). The difference picture (the absolute difference
amplified x4, the differing pixels beyond the threshold marked red) and a crop of each picture around the bodies are written to the
output folder, so that the lines can be looked at side by side.

Usage: Compare-SkinBakeCutPictures.py <runs root> <out folder> [--threshold 8] [--keys pre,k0,k1,k2,k3,k4]
       [--pair <runA> <runB>]...   (default pairs: the SKIN-LINES-1 set, see PAIRS)
A run is <runs root>\\<run name>\\run (the probe's record directory with picture-cut-r0-<key>.png).
"""
import os, sys
import numpy as np
from PIL import Image

# (first, second, what the pair asks): the second is the one suspected of the lines.
PAIRS = [
    ('lines1-float1-both', 'lines1-gpu1-both', 'float sync vs gpu, 1 body, the scene shadows: the lines of the gpu route alone?'),
    ('lines1-float4-both', 'lines1-gpu4-both', 'float sync vs gpu, 4 bodies, the scene shadows'),
    ('lines1-cpu1-both', 'lines1-float1-both', 'cpu (24-byte route) vs float sync, 1 body: the float kernel geometry against the product route'),
    ('lines1-cpu4-both', 'lines1-float4-both', 'cpu vs float sync, 4 bodies'),
    ('lines1-cpu4-both', 'lines1-gpu4-both', 'cpu vs gpu, 4 bodies'),
    ('lines1-gpu4-both', 'lines1-gpu4-nocast', 'gpu: the scene shadows vs the cut display casting none (self-shadow / caster side)'),
    ('lines1-gpu4-both', 'lines1-gpu4-noreceive', 'gpu: the scene shadows vs shadow strength 0 (receiver side)'),
    ('lines1-gpu4-both', 'lines1-gpu4-none', 'gpu: the scene shadows vs no shadow map'),
    ('lines1-float4-both', 'lines1-float4-nocast', 'float: the scene shadows vs the cut display casting none'),
    ('lines1-float4-both', 'lines1-float4-noreceive', 'float: the scene shadows vs shadow strength 0'),
    ('lines1-float4-both', 'lines1-float4-none', 'float: the scene shadows vs no shadow map'),
    ('lines1-float4-nocast', 'lines1-gpu4-nocast', 'float vs gpu with the cut display casting none: lines left without the casters?'),
    ('lines1-float4-noreceive', 'lines1-gpu4-noreceive', 'float vs gpu with shadow strength 0'),
    ('lines1-float4-none', 'lines1-gpu4-none', 'float vs gpu with no shadow map'),
]
DEFAULT_KEYS = ['pre', 'k0', 'k1', 'k2', 'k3', 'k4']


def load(path):
    return np.asarray(Image.open(path).convert('RGB')).astype(np.int32)   # int32: the luminance products overflow int16


def luminance(a):
    return (a[..., 0] * 299 + a[..., 1] * 587 + a[..., 2] * 114) // 1000


def body_box(a, margin=24):
    """The bounding box of what is not the background (the median colour of the picture), with a margin; the whole picture when nothing stands out."""
    g = luminance(a)
    med = np.median(g)
    mask = np.abs(g - med) > 40
    if not mask.any():
        return 0, 0, a.shape[1], a.shape[0]
    ys, xs = np.nonzero(mask)
    x0, y0 = max(0, xs.min() - margin), max(0, ys.min() - margin)
    x1, y1 = min(a.shape[1], xs.max() + margin + 1), min(a.shape[0], ys.max() + margin + 1)
    return x0, y0, x1, y1


def save(a, path):
    Image.fromarray(np.clip(a, 0, 255).astype(np.uint8)).save(path)


def compare(a, b, threshold):
    d = np.abs(a - b).max(axis=2)
    mask = d > threshold
    n = int(mask.sum())
    darker = lighter = 0
    bbox = '-'
    if n:
        la, lb = luminance(a), luminance(b)
        darker = int((mask & (lb < la)).sum())
        lighter = int((mask & (lb > la)).sum())
        ys, xs = np.nonzero(mask)
        bbox = '(%d,%d)-(%d,%d)' % (xs.min(), ys.min(), xs.max(), ys.max())
    return d, mask, n, darker, lighter, bbox


def plane_of(root, run):
    """PLANE-1: the run's cut plane condition from its requests CSV (legacy where the column is absent: the runs before it)."""
    import csv
    p = os.path.join(root, run, 'run', 'skin-bake-cut-requests.csv')
    if not os.path.exists(p):
        return '?'
    with open(p, encoding='utf-8', newline='') as h:
        for row in csv.DictReader(h):
            return row.get('cutPlane') or 'legacy'
    return 'legacy'


def main():
    args = sys.argv[1:]
    if len(args) < 2:
        raise SystemExit(__doc__)
    root, out = args[0], args[1]
    threshold = int(args[args.index('--threshold') + 1]) if '--threshold' in args else 8
    keys = args[args.index('--keys') + 1].split(',') if '--keys' in args else DEFAULT_KEYS
    pairs = []
    i = 0
    while i < len(args):
        if args[i] == '--pair':
            pairs.append((args[i + 1], args[i + 2], 'asked'))
            i += 3
        else:
            i += 1
    if not pairs:
        pairs = PAIRS
    os.makedirs(out, exist_ok=True)
    print('# SKIN-LINES-1 cut pictures compared: %s (threshold %d)' % (root, threshold))
    print()
    print('| pair | what | key | pixels differing | fraction | max channel diff | darker in 2nd | lighter in 2nd | bbox |')
    print('|---|---|---|---|---|---|---|---|---|')
    for first, second, what in pairs:
        pa_plane, pb_plane = plane_of(root, first), plane_of(root, second)
        if pa_plane != pb_plane:
            print('| %s vs %s | %s | - | NOT COMPARED: cut planes differ (%s vs %s) | | | | | |' % (first, second, what, pa_plane, pb_plane))
            continue
        for key in keys:
            pa = os.path.join(root, first, 'run', 'picture-cut-r0-%s.png' % key)
            pb = os.path.join(root, second, 'run', 'picture-cut-r0-%s.png' % key)
            if not os.path.exists(pa) or not os.path.exists(pb):
                print('| %s vs %s | %s | %s | missing (%s) | | | | | |' % (first, second, what, key, ('A' if not os.path.exists(pa) else '') + ('B' if not os.path.exists(pb) else '')))
                continue
            a, b = load(pa), load(pb)
            if a.shape != b.shape:
                print('| %s vs %s | %s | %s | sizes differ %s %s | | | | | |' % (first, second, what, key, a.shape, b.shape))
                continue
            d, mask, n, darker, lighter, bbox = compare(a, b, threshold)
            print('| %s vs %s | %s | %s | %d | %.5f | %d | %d | %d | %s |' % (first, second, what, key, n, n / mask.size, int(d.max()), darker, lighter, bbox))
            # The difference picture: the amplified absolute difference in grey, the pixels beyond the threshold in red.
            diff = np.repeat(np.clip(d * 4, 0, 255)[..., None], 3, axis=2).astype(np.int32)
            diff[mask] = [255, 0, 0]
            x0, y0, x1, y1 = body_box(a)
            tag = '%s__vs__%s__%s' % (first, second, key)
            save(diff[y0:y1, x0:x1], os.path.join(out, 'diff-%s.png' % tag))
            # The two crops side by side (2x), the first above the second, for the eye.
            ca, cb = a[y0:y1, x0:x1], b[y0:y1, x0:x1]
            stack = np.concatenate([ca, np.full((4, ca.shape[1], 3), 255, dtype=np.int32), cb], axis=0)
            im = Image.fromarray(np.clip(stack, 0, 255).astype(np.uint8))
            im = im.resize((im.width * 2, im.height * 2), Image.NEAREST)
            im.save(os.path.join(out, 'pair-%s.png' % tag))
    print()
    print('Difference pictures (diff-*.png: |A-B| x4, red = beyond the threshold) and the crops (pair-*.png: A above B, 2x) in', out)


if __name__ == '__main__':
    main()
