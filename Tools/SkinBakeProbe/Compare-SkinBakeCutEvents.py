# -*- coding: utf-8 -*-
"""PLANE-1: the cut bench's per-frame pictures compared between two runs BY EVENT, not by the k number alone.

Each run's skin-bake-cut-pictures.csv says, for every picture and body, which events had been observed at the bench's step of
the pictured frame (accepted, provisional physics, ledger published, CPU ready, committed: frames, -1 = not yet), the display
states and the physics placements (positions, rotations as Euler degrees). From it the pictures of the boundaries are picked:
  pre          the last warmup frame (k = -1)
  hit          the request frame (k = 0)
  waiting      the last pictured frame before every body's CPU ready (the gpu route; synchronous routes have none)
  ready        the first pictured frame at or after every body's CPU ready
  committed    the first pictured frame at or after every body's commit
  committed+2  two frames after that
The two runs' pictures of the same boundary are compared (pixels beyond the threshold, max channel difference, bounding box,
darker/lighter), their physics placements are compared (the largest position and rotation difference over the bodies and sides),
and runs whose cut planes differ are not compared. Usage:
  Compare-SkinBakeCutEvents.py <runs root> <out folder> --pair <runA> <runB> [--pair ...] [--threshold 8] [--box x0,y0,x1,y1]
"""
import csv, os, sys
import numpy as np
from PIL import Image


def rows(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding='utf-8', newline='') as h:
        return list(csv.DictReader(h))


def vec(text):
    text = (text or '').strip().strip('()')
    if not text:
        return None
    try:
        return np.array([float(x) for x in text.split(',')], dtype=float)
    except ValueError:
        return None


def load_run(root, run):
    folder = os.path.join(root, run, 'run')
    pictures = rows(os.path.join(folder, 'skin-bake-cut-pictures.csv'))
    requests = rows(os.path.join(folder, 'skin-bake-cut-requests.csv'))
    plane = (requests[0].get('cutPlane') or 'legacy') if requests else '?'
    route = requests[0].get('route') if requests else '?'
    by_k = {}
    for p in pictures:
        if p['round'] != '0':
            continue
        by_k.setdefault(int(p['k']), []).append(p)
    return {'name': run, 'folder': folder, 'plane': plane, 'route': route, 'pictures': by_k, 'requests': requests}


def event_frames(run, column):
    """The frame of the event for every body, from the requests CSV (-1 where it never happened)."""
    return [int(r[column]) for r in run['requests'] if r['round'] == '0']


def boundaries(run):
    """k of each boundary's picture (None where it is not pictured / not applicable)."""
    ks = sorted(run['pictures'])
    frame_of = {k: int(run['pictures'][k][0]['frame']) for k in ks}
    out = {'pre': -1 if -1 in frame_of else None, 'hit': 0 if 0 in frame_of else None}
    ready = event_frames(run, 'readyFrame'); committed = event_frames(run, 'committedFrame'); requested = event_frames(run, 'requestFrame')
    gpu = any(r['route'] == 'gpu' for r in run['requests'])
    if gpu and ready and min(ready) >= 0:
        last_ready = max(ready)
        before = [k for k in ks if k >= 0 and frame_of[k] < last_ready]
        out['waiting'] = max(before) if before else None
        after = [k for k in ks if frame_of[k] >= last_ready]
        out['ready'] = min(after) if after else None
    else:
        out['waiting'] = None
        out['ready'] = None
    if committed and min(committed) >= 0:
        last_commit = max(committed)
        after = [k for k in ks if frame_of[k] >= last_commit]
        out['committed'] = min(after) if after else None
        out['committed+2'] = out['committed'] + 2 if out['committed'] is not None and out['committed'] + 2 in frame_of else None
    else:
        out['committed'] = None; out['committed+2'] = None
    out['_frames'] = frame_of
    return out


def load_picture(run, k):
    name = 'picture-cut-r0-pre.png' if k == -1 else 'picture-cut-r0-k%d.png' % k
    p = os.path.join(run['folder'], name)
    return (np.asarray(Image.open(p).convert('RGB')).astype(np.int32), name) if os.path.exists(p) else (None, name)


def luminance(a):
    return (a[..., 0] * 299 + a[..., 1] * 587 + a[..., 2] * 114) // 1000


def compare_pictures(a, b, threshold, box):
    if box:
        x0, y0, x1, y1 = box
        a = a[y0:y1, x0:x1]; b = b[y0:y1, x0:x1]
    d = np.abs(a - b).max(axis=2)
    mask = d > threshold
    n = int(mask.sum())
    la, lb = luminance(a), luminance(b)
    darker = int((mask & (lb < la)).sum()); lighter = int((mask & (lb > la)).sum())
    bbox = '-'
    if n:
        ys, xs = np.nonzero(mask)
        bbox = '(%d,%d)-(%d,%d)' % (xs.min(), ys.min(), xs.max(), ys.max())
    return n, n / mask.size, int(d.max()), darker, lighter, bbox, d, mask


def quaternion(euler):
    """Unity's Euler (degrees, applied Z then X then Y) as a quaternion (x, y, z, w)."""
    import math
    x, y, z = (math.radians(v) for v in euler)
    cx, sx, cy, sy, cz, sz = math.cos(x / 2), math.sin(x / 2), math.cos(y / 2), math.sin(y / 2), math.cos(z / 2), math.sin(z / 2)
    # q = qy * qx * qz
    qx = (sx, 0.0, 0.0, cx); qy = (0.0, sy, 0.0, cy); qz = (0.0, 0.0, sz, cz)
    def mul(a, b):
        ax, ay, az, aw = a; bx, by, bz, bw = b
        return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx, aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)
    return mul(mul(qy, qx), qz)


def angle_between(ea, eb):
    """The rotation angle (deg) between two Unity Euler rotations, by their quaternions (no Euler wrap artefacts near gimbal lock)."""
    import math
    a, b = quaternion(ea), quaternion(eb)
    dot = abs(sum(u * v for u, v in zip(a, b)))
    return math.degrees(2.0 * math.acos(min(1.0, dot)))


def compare_physics(pa, pb):
    """The largest position (m) and rotation (deg, the angle between the two rotations) difference over the bodies and the sides
    pictured, with how many placement pairs were compared and how many were missing on either side (TL review 2026-11-02: a
    pair with nothing to compare is N/A, never a difference of 0)."""
    worst_pos, worst_rot, detail, compared, missing = 0.0, 0.0, [], 0, 0
    for ra in pa:
        rb = next((r for r in pb if r['body'] == ra['body']), None)
        if rb is None:
            missing += 3
            continue
        for side in ('source', 'positive', 'negative'):
            va, vb = vec(ra[side + 'Pos']), vec(rb[side + 'Pos'])
            if va is None or vb is None:
                if va is not None or vb is not None:
                    missing += 1   # one side has the placement, the other has not: not comparable
                continue
            dp = float(np.linalg.norm(va - vb))
            qa, qb = vec(ra[side + 'Rot']), vec(rb[side + 'Rot'])
            dr = angle_between(qa, qb) if qa is not None and qb is not None else float('nan')
            worst_pos = max(worst_pos, dp); worst_rot = max(worst_rot, dr) if dr == dr else worst_rot
            compared += 1
            detail.append('b%s %s dp %.4f m dr %s' % (ra['body'], side, dp, ('%.2f deg' % dr) if dr == dr else 'n/a'))
    return worst_pos, worst_rot, compared, missing, '; '.join(detail)


def states(p):
    return ' '.join('b%s[acc %s phys %s led %s rdy %s cmt %s | %s/%s/%s]' % (r['body'], r['acceptedFrame'], r['physicsFrame'], r['publishedFrame'], r['readyFrame'], r['committedFrame'], r['displaySource'], r['displayPositive'], r['displayNegative']) for r in p)


def main():
    args = sys.argv[1:]
    if len(args) < 2:
        raise SystemExit(__doc__)
    root, out = args[0], args[1]
    threshold = int(args[args.index('--threshold') + 1]) if '--threshold' in args else 8
    box = tuple(int(v) for v in args[args.index('--box') + 1].split(',')) if '--box' in args else None
    pairs = []
    i = 0
    while i < len(args):
        if args[i] == '--pair':
            pairs.append((args[i + 1], args[i + 2])); i += 3
        else:
            i += 1
    os.makedirs(out, exist_ok=True)
    print('# PLANE-1 cut pictures compared by event: %s (threshold %d%s)' % (root, threshold, ', box %s' % (box,) if box else ''))
    print()
    for a_name, b_name in pairs:
        A, B = load_run(root, a_name), load_run(root, b_name)
        print('## %s (%s, plane %s) vs %s (%s, plane %s)' % (a_name, A['route'], A['plane'], b_name, B['route'], B['plane']))
        print()
        if A['plane'] != B['plane']:
            print('NOT COMPARED: the cut planes differ.'); print(); continue
        ba, bb = boundaries(A), boundaries(B)
        print('| boundary | A picture (k, frame) | B picture (k, frame) | pixels differing | fraction | max channel | darker in B | lighter in B | bbox | physics: max position diff m | max rotation diff deg | placements compared / missing |')
        print('|---|---|---|---|---|---|---|---|---|---|---|---|')
        for boundary in ('pre', 'hit', 'waiting', 'ready', 'committed', 'committed+2'):
            ka, kb = ba.get(boundary), bb.get(boundary)
            if ka is None or kb is None:
                print('| %s | %s | %s | not pictured in %s | | | | | | | | |' % (boundary, ka, kb, 'A' if ka is None else 'B'))
                continue
            ia, na = load_picture(A, ka); ib, nb = load_picture(B, kb)
            if ia is None or ib is None:
                print('| %s | k%s | k%s | picture missing | | | | | | | | |' % (boundary, ka, kb)); continue
            n, frac, mx, darker, lighter, bbox, d, mask = compare_pictures(ia, ib, threshold, box)
            pa, pb = A['pictures'][ka], B['pictures'][kb]
            wp, wr, compared, missing, detail = compare_physics(pa, pb)
            physics = ('%.4f | %.2f' % (wp, wr)) if compared else 'N/A | N/A'
            print('| %s | k%d, %d | k%d, %d | %d | %.5f | %d | %d | %d | %s | %s | %d / %d |' % (boundary, ka, ba['_frames'][ka], kb, bb['_frames'][kb], n, frac, mx, darker, lighter, bbox, physics, compared, missing))
            diff = np.repeat(np.clip(d * 4, 0, 255)[..., None], 3, axis=2).astype(np.int32); diff[mask] = [255, 0, 0]
            Image.fromarray(np.clip(diff, 0, 255).astype(np.uint8)).save(os.path.join(out, 'diff-%s__vs__%s__%s.png' % (a_name, b_name, boundary)))
            with open(os.path.join(out, 'events-%s__vs__%s.md' % (a_name, b_name)), 'a', encoding='utf-8') as h:
                h.write('- %s: A k%d (%s) %s\n  B k%d (%s) %s\n  physics: %s\n' % (boundary, ka, na, states(pa), kb, nb, states(pb), detail))
        print()
        print('A boundaries: %s; B boundaries: %s' % ({k: v for k, v in ba.items() if k != '_frames'}, {k: v for k, v in bb.items() if k != '_frames'}))
        print()
    print('Difference pictures and the per-boundary event states in', out)


if __name__ == '__main__':
    main()
