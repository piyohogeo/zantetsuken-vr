# -*- coding: utf-8 -*-
"""MOTION-CAPTURE-1: the capture-method check evaluated from a run's raw captures and metadata.

A run folder holds motion-f<frame>-e<eye>.bin (the AppSW motion target read back: eyes 0/1 copied inside the motion pass after
the plugin's draw events, eyes 10/11 copied at the end of the frame's rendering), motion-check-issues.json (what the motion pass
used that frame: viewport, target, the pass's VP per eye, the constant block read back and decoded: previous and current stereo
VP, the SpaceWarp NDC modifier), motion-check-frames.csv (the frame's index and phase, the target offset, the eye's pose at
onBeforeRender, the quads' centres) and skin-bake-probe.json (the facts).

For every captured frame and eye the expectation is computed from the DECODED matrices (the values the vertex stage read), as
URP's CalcAswNdcMotionVectorFromCsPositions does:
    currentNDC = currentVP * world / w,  previousNDC = previousVP * world / w,  expected = currentNDC - previousNDC,
    expected.y *= ndcModifier
with no UV conversion (no halving). Each quad's region in the target is its four corners projected by the current VP into the
viewport (both row conventions are tried and the one whose alpha coverage is the quad's is kept, and reported); the region is
eroded by 2 texels so no mixed edge texel is read. Each interior texel's own world point is the eye ray through the texel centre
intersected with the quad's plane, so the expectation is per texel. Recorded per frame/eye/copy/quad: coverage (alpha 1 inside the
region), compared texels, max |error| (xy and z), outlier rate beyond the tolerance, and the sign of the mean x against the phase.
Tolerance (fixed beforehand): |error| <= 5e-4 + 5 % of |expected| per component (half precision at these magnitudes is ~1e-5; the
rest is the texel-centre position error). A quad whose region holds no texel, or a frame without decoded matrices, is
"not comparable", never a pass.

Usage: Evaluate-SkinBakeMotionCheck.py <run folder> [--out <folder>] [--tolerance-abs 5e-4] [--tolerance-rel 0.05]
"""
import csv, json, os, sys
import numpy as np

TOL_ABS, TOL_REL = 5e-4, 0.05


def load(folder):
    issues = {}
    p = os.path.join(folder, 'motion-check-issues.json')
    if os.path.exists(p):
        for issue in json.load(open(p, encoding='utf-8')):
            issues[issue['frame']] = issue
    frames = {}
    p = os.path.join(folder, 'motion-check-frames.csv')
    if os.path.exists(p):
        # The rows carry "(x, y, z)" vectors unquoted (the probe's first runs): the fields are re-joined by their parentheses.
        import re
        with open(p, encoding='utf-8') as h:
            lines = [l.rstrip('\r\n') for l in h if l.strip()]
        header = lines[0].split(',')
        for line in lines[1:]:
            fields = re.findall(r'\([^)]*\)|[^,]+', line)
            if len(fields) != len(header):
                continue
            row = dict(zip(header, fields))
            frames[int(row['frame'])] = row
    return issues, frames


def mat(values):
    return np.array(values, dtype=np.float64).reshape(4, 4)   # row-major as written (m[r, c])


def vec(text):
    return np.array([float(x) for x in text.strip('() ').split(',')], dtype=np.float64)


def project(vp, world):
    clip = vp @ np.array([world[0], world[1], world[2], 1.0])
    return clip[:3] / clip[3], clip[3]


def texel_of(ndc, w, h, rows_from_top):
    x = (ndc[0] * 0.5 + 0.5) * w
    y = ((ndc[1] * 0.5 + 0.5) if not rows_from_top else (1.0 - (ndc[1] * 0.5 + 0.5))) * h
    return x, y


def quad_corners(centre, half):
    return [centre + np.array([-half, -half, 0.0]), centre + np.array([half, -half, 0.0]), centre + np.array([half, half, 0.0]), centre + np.array([-half, half, 0.0])]


def inside_polygon(px, py, poly):
    inside = False
    n = len(poly)
    for i in range(n):
        x0, y0 = poly[i]; x1, y1 = poly[(i + 1) % n]
        if (y0 > py) != (y1 > py):
            xi = x0 + (py - y0) * (x1 - x0) / (y1 - y0)
            if px < xi:
                inside = not inside
    return inside


def eroded_polygon(poly, texels):
    cx = sum(p[0] for p in poly) / len(poly); cy = sum(p[1] for p in poly) / len(poly)
    out = []
    for x, y in poly:
        dx, dy = x - cx, y - cy; d = (dx * dx + dy * dy) ** 0.5
        if d < 1e-6:
            out.append((x, y)); continue
        out.append((x - dx / d * texels, y - dy / d * texels))
    return out


def ray_plane_world(vp_inverse, ndc_x, ndc_y, plane_point, plane_normal):
    """The world point of the eye ray through NDC (x, y) on the quad's plane: two points of the ray from the inverse VP."""
    near = vp_inverse @ np.array([ndc_x, ndc_y, 0.0, 1.0]); near = near[:3] / near[3]
    far = vp_inverse @ np.array([ndc_x, ndc_y, 0.5, 1.0]); far = far[:3] / far[3]
    direction = far - near
    denominator = direction @ plane_normal
    if abs(denominator) < 1e-12:
        return None
    t = ((plane_point - near) @ plane_normal) / denominator
    return near + direction * t


def evaluate(folder, out, tol_abs, tol_rel):
    issues, frames = load(folder)
    rows = []
    print('# MOTION-CAPTURE-1 evaluation of', folder)
    print()
    print('| frame | idx | phase | eye | copy | quad | rows | coverage (alpha 1 in region) | texels | expected x (mean) | measured x (mean) | max err xy | max err z | outliers (xyz) | non-finite | sign ok | verdict |')
    print('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    for frame in sorted(issues):
        issue = issues[frame]
        row = frames.get(frame)
        phase = row['phase'] if row else '?'
        if not issue.get('decoded'):
            print('| %d | %s | %s | - | - | - | - | not comparable: constants %s | | | | | | | | |' % (frame, issue.get('index'), phase, issue.get('constantsFailure') or issue.get('decodeFailure') or 'not decoded'))
            continue
        w, h = issue['width'], issue['height']
        for eye in (0, 1):
            cur = mat(issue['currentVP'][eye]); prev = mat(issue['previousVP'][eye]); modifier = issue['ndcModifier']
            try:
                cur_inverse = np.linalg.inv(cur)
            except np.linalg.LinAlgError:
                print('| %d | %s | %s | %d | - | - | - | not comparable: the current VP is singular | | | | | | | | |' % (frame, issue.get('index'), phase, eye)); continue
            for copy_eye, copy_name in ((eye, 'in-pass'), (eye + 10, 'end-of-frame')):
                p = os.path.join(folder, 'motion-f%d-e%d.bin' % (frame, copy_eye))
                if not os.path.exists(p):
                    print('| %d | %s | %s | %d | %s | - | - | not comparable: no capture file | | | | | | | | |' % (frame, issue.get('index'), phase, eye, copy_name)); continue
                raw = np.fromfile(p, dtype=np.float16)
                if raw.size != w * h * 4:
                    print('| %d | %s | %s | %d | %s | - | - | not comparable: %d halves, expected %d (RGBA16F %dx%d) | | | | | | | | |' % (frame, issue.get('index'), phase, eye, copy_name, raw.size, w * h * 4, w, h)); continue
                tex = raw.reshape(h, w, 4).astype(np.float64)
                for quad_name, centre_key in (('unity', 'unityCentre'), ('native', 'nativeCentre')):
                    if not row:
                        print('| %d | %s | %s | %d | %s | %s | - | not comparable: no frame row | | | | | | | | |' % (frame, issue.get('index'), phase, eye, copy_name, quad_name)); continue
                    centre = vec(row[centre_key]); half = float(row['quadHalf'])
                    corners = quad_corners(centre, half)
                    projected = [project(cur, c)[0] for c in corners]
                    best = None
                    for rows_from_top in (True, False):
                        poly = [texel_of(n, w, h, rows_from_top) for n in projected]
                        region = eroded_polygon(poly, 2.0)
                        xs = [q[0] for q in region]; ys = [q[1] for q in region]
                        x0, x1 = max(0, int(min(xs))), min(w - 1, int(max(xs)) + 1); y0, y1 = max(0, int(min(ys))), min(h - 1, int(max(ys)) + 1)
                        texels = [(tx, ty) for ty in range(y0, y1 + 1) for tx in range(x0, x1 + 1) if inside_polygon(tx + 0.5, ty + 0.5, region)]
                        if not texels:
                            continue
                        coverage = sum(1 for tx, ty in texels if tex[ty, tx, 3] > 0.5) / len(texels)
                        if best is None or coverage > best[0]:
                            best = (coverage, rows_from_top, texels)
                    if best is None:
                        print('| %d | %s | %s | %d | %s | %s | - | not comparable: the quad projects to no texel | | | | | | | | |' % (frame, issue.get('index'), phase, eye, copy_name, quad_name)); continue
                    coverage, rows_from_top, texels = best
                    errs_xy, errs_z, expected_x, measured_x, outliers, nonfinite = [], [], [], [], 0, 0
                    plane_normal = np.array([0.0, 0.0, 1.0])
                    for tx, ty in texels:
                        ndc_x = (tx + 0.5) / w * 2.0 - 1.0
                        ndc_y = ((1.0 - (ty + 0.5) / h) if rows_from_top else (ty + 0.5) / h) * 2.0 - 1.0
                        world = ray_plane_world(cur_inverse, ndc_x, ndc_y, centre, plane_normal)
                        if world is None:
                            continue
                        c_ndc, _ = project(cur, world); p_ndc, _ = project(prev, world)
                        expected = c_ndc - p_ndc; expected[1] *= modifier
                        measured = tex[ty, tx, :3]
                        # [P2-2] a non-finite component (NaN/Inf) is an outlier of its own kind; x, y AND z are held to the tolerance
                        if not np.isfinite(measured).all():
                            outliers += 1; nonfinite += 1; continue
                        err = measured - expected
                        tol = tol_abs + tol_rel * np.abs(expected)
                        if (np.abs(err) > tol).any():
                            outliers += 1
                        errs_xy.append(float(np.abs(err[:2]).max())); errs_z.append(float(abs(err[2])))
                        expected_x.append(float(expected[0])); measured_x.append(float(measured[0]))
                    n = len(errs_xy) + nonfinite   # every evaluated texel, the non-finite ones included (a region of NaN is a FAIL, not 'not comparable')
                    if n == 0:
                        print('| %d | %s | %s | %d | %s | %s | %s | %.2f | 0 | not comparable | | | | | | |' % (frame, issue.get('index'), phase, eye, copy_name, quad_name, 'top' if rows_from_top else 'bottom', coverage)); continue
                    if not errs_xy:
                        # every texel non-finite (review 2, P3): a FAIL row with the count, not an exception on an empty error list
                        print('| %d | %s | %s | %d | %s | %s | %s | %.3f | %d | - | - | - | - | 1.000 | %d | False | FAIL |' % (frame, issue.get('index'), phase, eye, copy_name, quad_name, 'top' if rows_from_top else 'bottom', coverage, n, nonfinite))
                        rows.append(dict(frame=frame, index=issue.get('index'), phase=phase, eye=eye, copy=copy_name, quad=quad_name, rows='top' if rows_from_top else 'bottom', coverage=coverage, texels=n, expected_x=float('nan'), measured_x=float('nan'), max_err_xy=float('nan'), max_err_z=float('nan'), outlier_rate=1.0, nonfinite=nonfinite, sign_ok=False, verdict='FAIL'))
                        continue
                    ex, mx = float(np.mean(expected_x)), float(np.mean(measured_x))
                    moving = phase in ('right', 'left')
                    sign_ok = (abs(ex) < 1e-6 and abs(mx) <= tol_abs) if not moving else (np.sign(ex) == np.sign(mx) and abs(mx) > tol_abs)
                    rate = outliers / n
                    verdict = 'PASS' if coverage >= 0.9 and rate <= 0.01 and sign_ok else 'FAIL'
                    print('| %d | %s | %s | %d | %s | %s | %s | %.3f | %d | %.5f | %.5f | %.5f | %.5f | %.3f | %d | %s | %s |' % (frame, issue.get('index'), phase, eye, copy_name, quad_name, 'top' if rows_from_top else 'bottom', coverage, n, ex, mx, max(errs_xy), max(errs_z), rate, nonfinite, sign_ok, verdict))
                    rows.append(dict(frame=frame, index=issue.get('index'), phase=phase, eye=eye, copy=copy_name, quad=quad_name, rows='top' if rows_from_top else 'bottom', coverage=coverage, texels=n, expected_x=ex, measured_x=mx, max_err_xy=max(errs_xy), max_err_z=max(errs_z), outlier_rate=rate, nonfinite=nonfinite, sign_ok=bool(sign_ok), verdict=verdict))
    if out:
        os.makedirs(out, exist_ok=True)
        with open(os.path.join(out, 'motion-check-evaluation.csv'), 'w', encoding='utf-8', newline='') as h:
            if rows:
                writer = csv.DictWriter(h, fieldnames=list(rows[0].keys())); writer.writeheader(); writer.writerows(rows)
    print()
    passes = sum(1 for r in rows if r['verdict'] == 'PASS'); fails = sum(1 for r in rows if r['verdict'] == 'FAIL')
    print('Comparable rows %d: PASS %d, FAIL %d (frames with decoded matrices %d of %d issues). Tolerance |err| <= %g + %g*|expected| on x, y and z; a non-finite texel is an outlier; coverage >= 0.9; outliers <= 1 %%; the sign of the mean x follows the phase.' % (len(rows), passes, fails, sum(1 for i in issues.values() if i.get('decoded')), len(issues), tol_abs, tol_rel))


if __name__ == '__main__':
    args = sys.argv[1:]
    if not args:
        raise SystemExit(__doc__)
    out = args[args.index('--out') + 1] if '--out' in args else None
    tol_abs = float(args[args.index('--tolerance-abs') + 1]) if '--tolerance-abs' in args else TOL_ABS
    tol_rel = float(args[args.index('--tolerance-rel') + 1]) if '--tolerance-rel' in args else TOL_REL
    evaluate(args[0], out, tol_abs, tol_rel)
