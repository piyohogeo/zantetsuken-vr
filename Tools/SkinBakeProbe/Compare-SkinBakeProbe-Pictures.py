# -*- coding: utf-8 -*-
"""The GPU bake probe's pictures compared (DESIGN 4.5.14, SKIN-BAKE-PROBE-1/2): for each subject, the picture pairs that
should show the same shape at the same place, drawn by the same route:
  cpu-first-frame vs gpu-first-frame  -- the frame that showed each (the GPU one: the dispatch frame, no readback before it)
  cpu-alone vs gpu-alone              -- the windows' centres
  gpu-first-frame vs gpu-alone        -- the GPU one's first frame against its window centre (a late bake would differ here)
  cpu-first-frame vs cpu-alone        -- the CPU one's first frame against its window centre (the frame-to-frame noise)
  cpu-edge-right vs gpu-edge-right, cpu-edge-left vs gpu-edge-left -- each alone near an edge of the view
and, as indications only, the 'both' frame's halves mirrored and gpu-alone against the renderer's own picture. Prints, per
pair, the pixels that differ beyond a threshold, the largest channel difference and the bounding box of the differences;
identical geometry drawn by the same route differs only by what the renderer itself does differently between frames. A pair
is 'missing' where a picture is absent (an older run). Usage: Compare-SkinBakeProbe-Pictures.py <run directory> [--threshold 8]"""
import os, sys
import numpy as np
from PIL import Image

PAIRS = [('cpu-first-frame', 'gpu-first-frame'), ('cpu-alone', 'gpu-alone'), ('gpu-first-frame', 'gpu-alone'), ('cpu-first-frame', 'cpu-alone'),
         ('cpu-edge-right', 'gpu-edge-right'), ('cpu-edge-left', 'gpu-edge-left')]

def load(path):
    return np.asarray(Image.open(path).convert('RGB')).astype(np.int16)

def row(subject, label, a, b, threshold):
    if a.shape != b.shape:
        print('| %s | %s | %s vs %s | sizes differ | | | |' % (subject, label, a.shape, b.shape))
        return
    d = np.abs(a - b).max(axis=2)
    mask = d > threshold
    n = int(mask.sum())
    if n:
        ys, xs = np.nonzero(mask)
        bbox = '(%d,%d)-(%d,%d)' % (xs.min(), ys.min(), xs.max(), ys.max())
    else:
        bbox = '-'
    print('| %s | %s | %dx%d | %d | %.5f | %d | %s |' % (subject, label, a.shape[1], a.shape[0], n, n / mask.size, int(d.max()), bbox))

def main():
    args = sys.argv[1:]
    if not args:
        raise SystemExit(__doc__)
    root = args[0]
    threshold = int(args[args.index('--threshold') + 1]) if '--threshold' in args else 8
    names = sorted(f for f in os.listdir(root) if f.startswith('picture-') and f.endswith('.png'))
    subjects = sorted({n[len('picture-'):].rsplit('-', 2)[0] for n in names if n.endswith('-cpu-alone.png') or n.endswith('-gpu-alone.png')})
    print('# Skin bake probe pictures in', root)
    print()
    print('| subject | pair | size | pixels differing > %d | fraction | max channel diff | bbox of differences |' % threshold)
    print('|---|---|---|---|---|---|---|')
    for s in subjects:
        pictures = {}
        def get(name):
            if name not in pictures:
                p = os.path.join(root, 'picture-%s-%s.png' % (s, name))
                pictures[name] = load(p) if os.path.exists(p) else None
            return pictures[name]
        for a_name, b_name in PAIRS:
            a, b = get(a_name), get(b_name)
            if a is None or b is None:
                print('| %s | %s vs %s | missing | | | | |' % (s, a_name, b_name))
                continue
            row(s, '%s vs %s' % (a_name, b_name), a, b, threshold)
        # The 'both' picture: the two halves (the CPU bake left, the GPU bake right), mirrored about the centre column for a rough look;
        # the lighting is not mirror-symmetric, so this is an indication, not a test.
        both = get('both')
        if both is not None:
            h, w = both.shape[0], both.shape[1]
            left, right = both[:, : w // 2], both[:, w - w // 2:][:, ::-1]
            dd = np.abs(left - right).max(axis=2)
            m2 = dd > threshold
            print('| %s | both: left vs mirrored right (indication only) | %dx%d | %d | %.5f | %d | - |' % (s, w, h, int(m2.sum()), m2.sum() / m2.size, int(dd.max())))
        r, g = get('renderer'), get('gpu-alone')
        if r is not None and g is not None:
            row(s, 'gpu-alone vs renderer (different route and pose window: indication only)', g, r, threshold)

if __name__ == '__main__':
    main()
