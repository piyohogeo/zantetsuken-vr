# -*- coding: utf-8 -*-
"""MOTION-CAPTURE-1 step 6: the AppSW motion output of a cut bench run (Quest, -MotionCapture -MotionView) evaluated at each
body's pieces, from what the motion pass actually used, with the body's coverage required from its SHAPE (TL review 1,
2026-11-02).

A run folder holds motion-f<frame>-e<eye>.bin (the motion target read back: eyes 0/1 copied inside the motion pass after the
plugin's draws, eyes 10/11 at the end of the frame), motion-check-issues.json (per captured frame: the pass's viewport, target
size/format, its own stereo VP (passVP), both eyes' previous/current VP decoded from the constant block bound at the issue,
_SpaceWarpNDCModifier), skin-bake-cut-pictures.csv (per frame and body: the request/accepted/physics/published/ready/committed
frames, the display state of the source and the two children, and the DISPLAY's placements at onBeforeRender -- the
geometry-local -> world matrix answered for the source drawn whole (before the request: the uncut renderer's localToWorld),
for its two provisional sides and for the two children, each written Kind(16 row-major numbers)), motion-view-frames.csv
(per frame: the view sequence's index/phase/offset), skin-bake-cut-requests.csv (per body: the cut plane in the geometry's
frame) and, when the probe wrote them, body-r<round>-b<body>-mesh.bin: the body's shape at the hit from Unity's own skinning
(SkinnedMeshRenderer.BakeMesh in the renderer's local frame = the geometry's frame; int32 vertex count, int32 index count,
float3 positions, int32 indices).

Pieces. For a body in a frame the shown pieces come from the picture row's display states: children shown (displayPositive /
displayNegative not NotShown/Retired/empty) -> each child with its own matrix and its side of the plane; else the source split
provisionally (displaySource ProvisionalSplit, side matrices present) -> the two sides with the side matrices; else the source
whole with its matrix (before the request: the uncut renderer's). A Missing placement is refused (not comparable).

Region and expectation. A piece's region of interest is the inner box of the body in the geometry's frame (x +-0.14,
y +-0.08, z 0.30..1.50 whole / 0.30..0.80 below the plane / 0.95..1.50 above; the Z-up NPC mesh), projected through the piece's
matrix and the current VP, eroded 3 texels. The expectation of a texel is the camera-only motion of the world point where the
texel centre's ray meets a plane: for a BODY texel the plane through the box centre facing the eye; for a GROUND texel the
world plane y = 0 (the 16B route's motion moves the CURRENT world point through the current and the previous VP: no piece
motion is added): expected = currentNDC - previousNDC, y *= modifier, z = the NDC delta. Tolerance (pre-defined) on x, y
AND z: |err| <= 5e-4 + 15 % |expected| (the body's half thickness at 2.5 m is 6 %). A non-finite component (NaN/Inf) is an
outlier of its own kind.

Coverage from the shape. When the body's mesh file is present, its triangles are split by the cut plane (a triangle goes to
the side of its centroid), each piece's triangles are rasterised through the piece's matrix and the current VP into the
target's texels (the SHAPE mask), and the mask is eroded 1 texel. Inside the region: a texel of the mask must carry the body's
expectation (body match), a texel outside the mask may carry the body's or the ground's. Verdict: PASS when the alpha
coverage >= 0.9, the mask holds at least 50 texels in the region, the body-match rate over the mask >= 0.95, the unexplained
rate over the region <= 5 %, and the sign of the non-outlier mean x follows the phase (moving: the expected sign and |mean| >
5e-4; a hold: |expected| < 1e-6 and |mean| <= 5e-4); FAIL otherwise. The body's coverage is only told where the body's and
the ground's expectations DIFFER (a view that moves): when fewer than 90 % of the mask's texels have |body - ground| above
the tolerance (a static view: both expectations ~0, pre and hold frames), a region whose values pass is MATCH-ONLY, not PASS
-- the values match, the coverage is undecidable there (TL review 2). In between, when at least 50 of the mask's texels are
distinct and the body match over THOSE is >= 0.95, the verdict is PASS-PARTIAL: the coverage is confirmed on the distinct
share of the piece (written in the row), undecidable on the rest (the lower piece in front of near ground). Without a mesh
file nothing is PASS: the verdict is
EXPLAINED (every texel of the region carries the body's or the ground's expectation, unexplained <= 5 %, sign ok) or
UNEXPLAINED -- a statement about the values, never about the body's coverage.

Matrices. A frame whose constants were not decoded (no native issue: the uncut renderer's frame) but whose pass VP is written
is compared with previousVP := the previous captured frame's pass VP when that frame exists ("fallback"); the row says so.

Rows are written for every captured frame, eye, copy point, body and piece, with the boundary of the frame for the body (k
relative to its request; before the hit / hit not published / provisional / ready not committed / committed +n).

Usage: Evaluate-SkinBakeMotionCut.py <run folder> [--out <folder>] [--tolerance-abs 5e-4] [--tolerance-rel 0.15] [--no-mask]
"""
import csv, json, os, re, sys
import numpy as np

PLANE_Z_DEFAULT = 0.879
BOX_X, BOX_Y = 0.14, 0.08
WHOLE_Z = (0.30, 1.50); LOWER_Z = (0.30, 0.80); UPPER_Z = (0.95, 1.50)
ERODE = 3
NOT_SHOWN = ('', 'None', 'NotShown', 'Retired')
MIN_MASK_TEXELS = 50
BODY_MATCH_MIN = 0.95
UNEXPLAINED_MAX = 0.05
DISTINCT_MIN = 0.90   # share of the mask's texels whose body and ground expectations differ by more than the tolerance


def read_vectors_csv(path):
    """A CSV whose "(x, y, z)" fields may be unquoted (the probe's rows): split by the commas outside parentheses and quotes,
    so that an empty field (a missing placement) stays a field."""
    with open(path, encoding='utf-8') as h:
        lines = [l.rstrip('\r\n') for l in h if l.strip()]
    header = lines[0].split(',')
    rows = []
    for line in lines[1:]:
        fields, depth, quoted, cur = [], 0, False, ''
        for ch in line:
            if ch == '"':
                quoted = not quoted
            elif ch == '(':
                depth += 1
            elif ch == ')':
                depth -= 1
            if ch == ',' and depth == 0 and not quoted:
                fields.append(cur); cur = ''
            else:
                cur += ch
        fields.append(cur)
        if len(fields) != len(header):
            continue
        rows.append(dict(zip(header, [f.strip('"') for f in fields])))
    return rows


def vec(text):
    text = (text or '').strip().strip('"')
    if not text:
        return None
    return np.array([float(x) for x in text.strip('() ').split(',')], dtype=np.float64)


def placement(text):
    """Kind(16 row-major numbers) -> (kind, 4x4) or (None, None) when empty."""
    text = (text or '').strip().strip('"')
    if not text or '(' not in text:
        return None, None
    kind = text[:text.index('(')].strip()
    values = [float(x) for x in text[text.index('(') + 1:text.rindex(')')].split(',')]
    if len(values) != 16:
        return None, None
    return kind, np.array(values, dtype=np.float64).reshape(4, 4)


def mat(values):
    return np.array(values, dtype=np.float64).reshape(4, 4)   # row-major as written (m[r, c])


def project(vp, points):
    """NDC of world points (n, 3) through a VP (4x4): (n, 3) and w."""
    h = np.concatenate([points, np.ones((points.shape[0], 1))], axis=1)
    c = h @ vp.T
    return c[:, :3] / c[:, 3:4], c[:, 3]


def load(folder):
    issues = {}
    p = os.path.join(folder, 'motion-check-issues.json')
    if os.path.exists(p):
        for issue in json.load(open(p, encoding='utf-8')):
            issues[issue['frame']] = issue
    p = os.path.join(folder, 'skin-bake-cut-pictures.csv')
    pictures = read_vectors_csv(p) if os.path.exists(p) else []
    views = {}
    p = os.path.join(folder, 'motion-view-frames.csv')
    if os.path.exists(p):
        for row in read_vectors_csv(p):
            views[int(row['frame'])] = row
    planes = {}
    p = os.path.join(folder, 'skin-bake-cut-requests.csv')
    if os.path.exists(p):
        for row in read_vectors_csv(p):
            v = vec(row.get('planeLocal', ''))
            if v is not None and len(v) == 4:
                planes[(row['round'], row['body'])] = v
    return issues, pictures, views, planes


def read_motion(folder, frame, eye, width, height):
    p = os.path.join(folder, 'motion-f%d-e%d.bin' % (frame, eye))
    if not os.path.exists(p):
        return None
    data = np.fromfile(p, dtype=np.float16)
    if data.size != width * height * 4:
        return None
    return data.reshape(height, width, 4).astype(np.float64)


def read_mesh(folder, round_, body):
    p = os.path.join(folder, 'body-r%s-b%s-mesh.bin' % (round_, body))
    if not os.path.exists(p):
        return None
    raw = open(p, 'rb').read()
    nv, ni = np.frombuffer(raw[:8], dtype=np.int32)
    if len(raw) != 8 + nv * 12 + ni * 4 or nv <= 0 or ni <= 0:
        return None
    positions = np.frombuffer(raw[8:8 + nv * 12], dtype=np.float32).reshape(nv, 3).astype(np.float64)
    indices = np.frombuffer(raw[8 + nv * 12:], dtype=np.int32).reshape(ni // 3, 3)
    return positions, indices


def box_corners(z_range, local_to_world):
    z0, z1 = z_range
    corners = np.array([[x, y, z, 1.0] for x in (-BOX_X, BOX_X) for y in (-BOX_Y, BOX_Y) for z in (z0, z1)])
    world = corners @ local_to_world.T
    return world[:, :3] / world[:, 3:4]


def to_texels(vp, points, width, height, rows_from_top=True):
    ndc, w = project(vp, points)
    xs = (ndc[:, 0] * 0.5 + 0.5) * width
    ys = ((1.0 - (ndc[:, 1] * 0.5 + 0.5)) if rows_from_top else (ndc[:, 1] * 0.5 + 0.5)) * height
    return xs, ys, w


def region_of(vp, corners, width, height):
    xs, ys, w = to_texels(vp, corners, width, height)
    if np.any(w <= 0):
        return None
    x0, x1 = int(np.floor(xs.min())) + ERODE, int(np.ceil(xs.max())) - ERODE
    y0, y1 = int(np.floor(ys.min())) + ERODE, int(np.ceil(ys.max())) - ERODE
    x0, y0 = max(x0, 0), max(y0, 0); x1, y1 = min(x1, width), min(y1, height)
    if x1 <= x0 or y1 <= y0:
        return None
    return x0, y0, x1, y1


def expected_motion(cur_vp, prev_vp, modifier, plane_point, plane_normal, region, width, height):
    """Per texel of the region: the ray through the texel centre (the inverted current VP) meets the plane; the world point
    goes through both VPs."""
    x0, y0, x1, y1 = region
    inv = np.linalg.inv(cur_vp)
    xs = (np.arange(x0, x1) + 0.5) / width * 2.0 - 1.0
    ys = (1.0 - (np.arange(y0, y1) + 0.5) / height) * 2.0 - 1.0
    gx, gy = np.meshgrid(xs, ys)
    n = gx.size
    pts = []
    for z in (0.2, 0.8):
        clip = np.stack([gx.ravel(), gy.ravel(), np.full(n, z), np.ones(n)], axis=1)
        w = clip @ inv.T
        pts.append(w[:, :3] / w[:, 3:4])
    a, b = pts
    d = b - a
    denom = d @ plane_normal
    t = ((plane_point - a) @ plane_normal) / np.where(np.abs(denom) < 1e-12, 1e-12, denom)
    world = a + d * t[:, None]
    cur, _ = project(cur_vp, world)
    prev, _ = project(prev_vp, world)
    e = cur - prev
    e[:, 1] *= modifier
    return e.reshape(y1 - y0, x1 - x0, 3)


def rasterise(triangles_xy, width, height):
    """A boolean mask of the texels whose centre lies inside any triangle (pixel coordinates, (n, 3, 2))."""
    mask = np.zeros((height, width), dtype=bool)
    for tri in triangles_xy:
        x0 = max(int(np.floor(tri[:, 0].min())), 0); x1 = min(int(np.ceil(tri[:, 0].max())), width - 1)
        y0 = max(int(np.floor(tri[:, 1].min())), 0); y1 = min(int(np.ceil(tri[:, 1].max())), height - 1)
        if x1 < x0 or y1 < y0:
            continue
        px, py = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        (ax, ay), (bx, by), (cx, cy) = tri
        d = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay)
        if abs(d) < 1e-12:
            continue
        u = ((bx - px) * (cy - py) - (cx - px) * (by - py)) / d
        v = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) / d
        w = 1.0 - u - v
        inside = (u >= -1e-9) & (v >= -1e-9) & (w >= -1e-9)
        mask[y0:y1 + 1, x0:x1 + 1] |= inside
    return mask


def erode(mask):
    m = mask.copy()
    m[1:, :] &= mask[:-1, :]; m[:-1, :] &= mask[1:, :]
    m[:, 1:] &= mask[:, :-1]; m[:, :-1] &= mask[:, 1:]
    return m


def shape_mask(mesh, plane, side, local_to_world, cur_vp, width, height):
    """The texels the piece's shape covers: the mesh's triangles on the plane's side (0: all), through the piece's matrix and the
    current VP, eroded one texel. None when nothing projects."""
    positions, indices = mesh
    if side != 0 and plane is not None:
        centroids = positions[indices].mean(axis=1)
        signed = centroids @ plane[:3] + plane[3]
        indices = indices[(signed >= 0) if side > 0 else (signed < 0)]
    if len(indices) == 0:
        return None
    world = (np.concatenate([positions, np.ones((len(positions), 1))], axis=1) @ local_to_world.T)[:, :3]
    xs, ys, w = to_texels(cur_vp, world, width, height)
    tri_ok = (w > 0)[indices].all(axis=1)
    tris = np.stack([xs[indices[tri_ok]], ys[indices[tri_ok]]], axis=2)
    if len(tris) == 0:
        return None
    return erode(rasterise(tris, width, height))


def boundary_of(body, frame):
    rq = int(body['requestFrame']) if body['requestFrame'] not in ('', '-1') else -1
    ready = int(body['readyFrame']) if body['readyFrame'] not in ('', '-1') else -1
    committed = int(body['committedFrame']) if body['committedFrame'] not in ('', '-1') else -1
    published = int(body['publishedFrame']) if body['publishedFrame'] not in ('', '-1') else -1
    if rq < 0 or frame < rq:
        return 'before the hit', None
    if committed >= 0 and frame >= committed:
        return 'committed (+%d)' % (frame - committed), frame - rq
    if ready >= 0 and frame >= ready:
        return 'ready, not committed', frame - rq
    if published >= 0 and frame >= published:
        return 'provisional (published)', frame - rq
    return 'hit, not published', frame - rq


def pieces_of(body):
    """(name, matrix, z range, side) of the shown pieces, and the refusals."""
    pieces, refused = [], []
    shown_p, shown_n, shown_s = body.get('displayPositive', ''), body.get('displayNegative', ''), body.get('displaySource', '')
    kind_p, m_p = placement(body.get('placementPositive', ''))
    kind_n, m_n = placement(body.get('placementNegative', ''))
    kind_s, m_s = placement(body.get('placementSource', ''))
    kind_sp, m_sp = placement(body.get('placementSourcePositiveSide', ''))
    kind_sn, m_sn = placement(body.get('placementSourceNegativeSide', ''))
    if shown_p not in NOT_SHOWN or shown_n not in NOT_SHOWN:
        for name, shown, kind, m, zr, side in (('positive(upper) ' + shown_p, shown_p, kind_p, m_p, UPPER_Z, 1), ('negative(lower) ' + shown_n, shown_n, kind_n, m_n, LOWER_Z, -1)):
            if shown in NOT_SHOWN:
                continue
            if m is None or kind == 'Missing':
                refused.append(name + ': placement ' + (kind or 'empty'))
            else:
                pieces.append((name + ' [' + kind + ']', m, zr, side))
    elif shown_s == 'ProvisionalSplit' and (m_sp is not None or m_sn is not None):
        for name, kind, m, zr, side in (('source +side(upper)', kind_sp, m_sp, UPPER_Z, 1), ('source -side(lower)', kind_sn, m_sn, LOWER_Z, -1)):
            if m is None or kind == 'Missing':
                refused.append(name + ': placement ' + (kind or 'empty'))
            else:
                pieces.append((name + ' [' + kind + ']', m, zr, side))
    elif m_s is not None and kind_s != 'Missing' and (shown_s not in NOT_SHOWN or kind_s == 'Renderer'):
        pieces.append(('source(whole) ' + (shown_s or 'uncut') + ' [' + kind_s + ']', m_s, WHOLE_Z, 0))
    return pieces, refused


def matrices_of(issues, frame, eye):
    """(currentVP, previousVP, how) for an eye of a frame: decoded constants, or the pass VP with the previous captured frame's
    pass VP as the previous (fallback), or None."""
    issue = issues[frame]
    if issue.get('decoded'):
        return mat(issue['currentVP'][eye]), mat(issue['previousVP'][eye]), 'decoded'
    if issue.get('passVP') and (frame - 1) in issues and issues[frame - 1].get('passVP'):
        return mat(issue['passVP'][eye]), mat(issues[frame - 1]['passVP'][eye]), 'fallback: pass VP, previous = frame %d pass VP' % (frame - 1)
    return None, None, issue.get('constantsFailure') or issue.get('decodeFailure') or 'not decoded, no pass VP of the previous frame'


def evaluate_region(motion, region, exp_body, exp_ground, mask, moving, tol_abs, tol_rel):
    """The numbers of one region. mask: the shape mask of the whole target (bool) or None."""
    x0, y0, x1, y1 = region
    reg = motion[y0:y1, x0:x1]
    alpha = reg[..., 3] > 0.5
    meas = reg[..., :3]
    finite = np.isfinite(meas).all(axis=2)
    err_b = np.abs(np.where(finite[..., None], meas, 0.0) - exp_body); tol_b = tol_abs + tol_rel * np.abs(exp_body)
    body_ok = finite & (err_b <= tol_b).all(axis=2)
    err_g = np.abs(np.where(finite[..., None], meas, 0.0) - exp_ground); tol_g = tol_abs + tol_rel * np.abs(exp_ground)
    ground_ok = finite & (err_g <= tol_g).all(axis=2)
    n = alpha.size
    r = dict(coverage=float(alpha.mean()), texels=n, nonfinite=int((~finite).sum()),
             body_rate=float(body_ok.mean()), ground_rate=float((ground_ok & ~body_ok).mean()), unexplained_rate=float((~body_ok & ~ground_ok).mean()),
             expected_x=float(exp_body[..., 0].mean()), max_err_body=float(err_b[body_ok].max()) if body_ok.any() else float('nan'))
    r['measured_x'] = float(meas[body_ok, 0].mean()) if body_ok.any() else (float(meas[finite, 0].mean()) if finite.any() else float('nan'))
    ex, mx = r['expected_x'], r['measured_x']
    r['sign_ok'] = bool((abs(ex) < 1e-6 and abs(mx) <= tol_abs) if not moving else (np.sign(ex) == np.sign(mx) and abs(mx) > tol_abs)) if np.isfinite(mx) else False
    # where the body's and the ground's expectations differ by more than the tolerance, the measured value tells them apart
    distinct = (np.abs(exp_body - exp_ground) > tol_b).any(axis=2)
    if mask is not None:
        m = mask[y0:y1, x0:x1]
        r['mask_texels'] = int(m.sum())
        r['mask_body_match'] = float(body_ok[m].mean()) if m.any() else float('nan')
        r['ground_outside_mask'] = float((ground_ok & ~body_ok & ~m).sum() / max(1, (~m).sum()))
        r['ground_inside_mask'] = float((ground_ok & ~body_ok & m).sum() / max(1, m.sum()))
        r['distinct'] = float(distinct[m].mean()) if m.any() else 0.0
        dm = m & distinct
        r['distinct_texels'] = int(dm.sum())
        r['mask_body_match_distinct'] = float(body_ok[dm].mean()) if dm.any() else float('nan')
        ok = r['coverage'] >= 0.9 and r['mask_texels'] >= MIN_MASK_TEXELS and r['mask_body_match'] >= BODY_MATCH_MIN and r['unexplained_rate'] <= UNEXPLAINED_MAX and r['sign_ok']
        if not ok:
            r['verdict'] = 'FAIL'
        elif r['distinct'] >= DISTINCT_MIN:
            r['verdict'] = 'PASS'
        elif r['distinct_texels'] >= MIN_MASK_TEXELS and r['mask_body_match_distinct'] >= BODY_MATCH_MIN:
            r['verdict'] = 'PASS-PARTIAL'   # the coverage confirmed on the distinct share of the mask only
        else:
            r['verdict'] = 'MATCH-ONLY'   # the values match, but body and ground would match alike: no coverage statement
    else:
        r['mask_texels'] = 0; r['mask_body_match'] = float('nan'); r['ground_outside_mask'] = float('nan'); r['ground_inside_mask'] = float('nan')
        r['distinct'] = float(distinct.mean()); r['distinct_texels'] = 0; r['mask_body_match_distinct'] = float('nan')
        ok = r['coverage'] >= 0.9 and r['unexplained_rate'] <= UNEXPLAINED_MAX and r['sign_ok']
        r['verdict'] = 'EXPLAINED' if ok else 'UNEXPLAINED'
    return r


def main(argv):
    if len(argv) < 2 or not os.path.isdir(argv[1]):
        print(__doc__); return 2
    folder = argv[1]
    args = argv[2:]
    out = args[args.index('--out') + 1] if '--out' in args else None
    tol_abs = float(args[args.index('--tolerance-abs') + 1]) if '--tolerance-abs' in args else 5e-4
    tol_rel = float(args[args.index('--tolerance-rel') + 1]) if '--tolerance-rel' in args else 0.15
    use_mask = '--no-mask' not in args
    issues, pictures, views, planes = load(folder)
    by_frame = {}
    for row in pictures:
        by_frame.setdefault(int(row['frame']), []).append(row)
    meshes = {}
    print('# MOTION-CAPTURE-1 step 6 evaluation of %s' % os.path.basename(os.path.normpath(folder)))
    print('')
    print('| frame | k | phase | eye | copy | body | piece | boundary | matrices | region | coverage | texels | mask texels | mask body match | distinct | body | ground | unexplained | non-finite | expected x | measured x (body) | max err (body) | sign ok | verdict |')
    print('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    rows = []
    counts = {'PASS': 0, 'PASS-PARTIAL': 0, 'FAIL': 0, 'MATCH-ONLY': 0, 'EXPLAINED': 0, 'UNEXPLAINED': 0}
    masks_used = 0
    for frame in sorted(issues):
        view = views.get(frame)
        phase = view['phase'] if view else '?'
        k_view = view['k'] if view else '?'
        moving = phase in ('right', 'left')
        issue = issues[frame]
        width, height = issue['width'], issue['height']
        modifier = issue.get('ndcModifier')
        if modifier is None:
            modifier = 1.0
        bodies = by_frame.get(frame, [])
        if not bodies:
            print('| %d | %s | %s | - | - | - | - | - | - | not comparable: no picture-state row for the frame | | | | | | | | | | | | | |' % (frame, k_view, phase))
            continue
        for eye in (0, 1):
            cur_vp, prev_vp, how = matrices_of(issues, frame, eye)
            if cur_vp is None:
                print('| %d | %s | %s | %d | - | - | - | - | not comparable: %s | | | | | | | | | | | | | | |' % (frame, k_view, phase, eye, how))
                continue
            for copy_eye, copy_name in ((eye, 'in-pass'), (eye + 10, 'end-of-frame')):
                motion = read_motion(folder, frame, copy_eye, width, height)
                if motion is None:
                    print('| %d | %s | %s | %d | %s | - | - | - | %s | not comparable: no readback file for eye %d | | | | | | | | | | | | | |' % (frame, k_view, phase, eye, copy_name, how, copy_eye))
                    continue
                for body in bodies:
                    boundary, k_body = boundary_of(body, frame)
                    pieces, refused = pieces_of(body)
                    for r in refused:
                        print('| %d | %s | %s | %d | %s | %s | %s | %s | %s | not comparable: %s | | | | | | | | | | | | | |' % (frame, k_view, phase, eye, copy_name, body['body'], r.split(':')[0], boundary, how, r))
                    if not pieces:
                        if not refused:
                            print('| %d | %s | %s | %d | %s | %s | - | %s | %s | not comparable: no shown piece with a placement (source %s, positive %s, negative %s) | | | | | | | | | | | | | |' % (frame, k_view, phase, eye, copy_name, body['body'], boundary, how, body.get('displaySource') or '-', body.get('displayPositive') or '-', body.get('displayNegative') or '-'))
                        continue
                    mesh = None
                    if use_mask:
                        key = (body['round'], body['body'])
                        if key not in meshes:
                            meshes[key] = read_mesh(folder, body['round'], body['body'])
                        mesh = meshes[key]
                    plane = planes.get((body['round'], body['body']))
                    for piece_name, local_to_world, zr, side in pieces:
                        corners = box_corners(zr, local_to_world)
                        region = region_of(cur_vp, corners, width, height)
                        if region is None:
                            print('| %d | %s | %s | %d | %s | %s | %s | %s | %s | not comparable: the box projects outside the target | | | | | | | | | | | | | |' % (frame, k_view, phase, eye, copy_name, body['body'], piece_name, boundary, how))
                            continue
                        x0, y0, x1, y1 = region
                        eye_pos = vec(body.get('eyePos', ''))
                        centre = corners.mean(axis=0)
                        normal = (centre - eye_pos) if eye_pos is not None else np.array([0.0, 0.0, -1.0])
                        normal = normal / np.linalg.norm(normal)
                        exp_body = expected_motion(cur_vp, prev_vp, modifier, centre, normal, region, width, height)
                        exp_ground = expected_motion(cur_vp, prev_vp, modifier, np.array([0.0, 0.0, 0.0]), np.array([0.0, 1.0, 0.0]), region, width, height)
                        mask = None
                        if mesh is not None:
                            mask = shape_mask(mesh, plane if plane is not None else np.array([0.0, 0.0, 1.0, -PLANE_Z_DEFAULT]), side, local_to_world, cur_vp, width, height)
                            if mask is not None:
                                masks_used += 1
                        r = evaluate_region(motion, region, exp_body, exp_ground, mask, moving, tol_abs, tol_rel)
                        counts[r['verdict']] += 1
                        print('| %d | %s | %s | %d | %s | %s | %s | %s | %s | (%d,%d)-(%d,%d) | %.3f | %d | %d | %s | %.3f | %.3f | %.3f | %.3f | %d | %.5f | %.5f | %.5f | %s | %s |' % (
                            frame, k_view, phase, eye, copy_name, body['body'], piece_name, boundary, how, x0, y0, x1, y1, r['coverage'], r['texels'], r['mask_texels'],
                            ('%.3f' % r['mask_body_match']) if np.isfinite(r['mask_body_match']) else '-', r['distinct'], r['body_rate'], r['ground_rate'], r['unexplained_rate'], r['nonfinite'], r['expected_x'], r['measured_x'], r['max_err_body'], r['sign_ok'], r['verdict']))
                        rows.append(dict(frame=frame, k=k_view, phase=phase, eye=eye, copy=copy_name, body=body['body'], piece=piece_name, boundary=boundary, k_body=k_body, matrices=how, x0=x0, y0=y0, x1=x1, y1=y1, **r))
    print('')
    print('Rows %d: PASS %d, PASS-PARTIAL %d, FAIL %d, MATCH-ONLY %d, EXPLAINED %d, UNEXPLAINED %d (frames with decoded matrices %d of %d issues; picture-state frames %d; shape masks used %d). Tolerance |err| <= %g + %g*|expected| on x, y and z; a non-finite texel is an outlier; alpha coverage >= 0.9; with a shape mask: mask >= %d texels, body match over the mask >= %.2f, unexplained <= %.0f %%, sign of the non-outlier mean x by the phase -> PASS/FAIL, and PASS only where at least %.0f %% of the mask\'s texels have body and ground expectations apart by more than the tolerance (PASS-PARTIAL when at least %d distinct texels match the body at >= %.2f: the coverage confirmed on the distinct share only; otherwise MATCH-ONLY: the values match, the coverage is undecidable in a static view); without a mask: EXPLAINED/UNEXPLAINED only (no statement about the body\'s coverage). Box x +-%.2f, y +-%.2f, z %s whole / %s lower / %s upper, eroded %d texels; expectation = camera-only motion of the plane through the box centre (body) or of the ground y = 0; no piece motion added.' % (
        len(rows), counts['PASS'], counts['PASS-PARTIAL'], counts['FAIL'], counts['MATCH-ONLY'], counts['EXPLAINED'], counts['UNEXPLAINED'], sum(1 for i in issues.values() if i.get('decoded')), len(issues), len(by_frame), masks_used, tol_abs, tol_rel, MIN_MASK_TEXELS, BODY_MATCH_MIN, UNEXPLAINED_MAX * 100, DISTINCT_MIN * 100, MIN_MASK_TEXELS, BODY_MATCH_MIN, BOX_X, BOX_Y, WHOLE_Z, LOWER_Z, UPPER_Z, ERODE))
    if out:
        os.makedirs(out, exist_ok=True)
        with open(os.path.join(out, 'motion-cut-evaluation.csv'), 'w', encoding='utf-8', newline='') as h:
            if rows:
                writer = csv.DictWriter(h, fieldnames=list(rows[0].keys())); writer.writeheader(); writer.writerows(rows)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
