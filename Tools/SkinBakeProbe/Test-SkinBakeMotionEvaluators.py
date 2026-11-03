# -*- coding: utf-8 -*-
"""Negative cases of the motion evaluators on a SAVED cut bench run (TL review 1, 2026-11-02): a copy of the run's folder is made
with one captured frame's raw altered, the cut evaluator is run on it, and the altered frame's rows must not be PASS (with a
shape mask) or EXPLAINED (without one):

  z-broken      z of every texel of a frame += 0.01 (x, y untouched)        -> FAIL / UNEXPLAINED
  nan-y         y of every 10th texel of a frame = NaN                      -> FAIL / UNEXPLAINED (non-finite counted)
  all-ground    every texel of a frame = the ground's expectation, alpha 1  -> FAIL (mask: body match 0); without a mask the
                                                                              row is EXPLAINED, which is why EXPLAINED is no PASS
  static        a hold frame with every texel = the ground                   -> never PASS nor PASS-PARTIAL (MATCH-ONLY at best)
  With a mask every moving negative must give FAIL on every row (PASS-PARTIAL and MATCH-ONLY are refused too).
  half-ground   the body texels of the upper half of each region replaced
                by the ground's expectation                                 -> FAIL with a mask (body match < 0.95)

The unaltered run is evaluated too, as the control. The frame altered is the first 'right' frame with decoded matrices.

Usage: Test-SkinBakeMotionEvaluators.py <run folder> <scratch folder>
"""
import importlib.util, os, shutil, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location('ev', os.path.join(HERE, 'Evaluate-SkinBakeMotionCut.py'))
ev = importlib.util.module_from_spec(spec); spec.loader.exec_module(ev)


def copy_run(src, dst):
    if os.path.exists(dst):
        shutil.rmtree(dst)
    os.makedirs(dst)
    for name in os.listdir(src):
        if name.endswith('.png'):
            continue
        shutil.copy2(os.path.join(src, name), os.path.join(dst, name))


def pick_frame(folder, phase='right', skip=0):
    issues, pictures, views, planes = ev.load(folder)
    for frame in sorted(issues):
        v = views.get(frame)
        if v and v['phase'] == phase and issues[frame].get('decoded'):
            if skip == 0:
                return frame
            skip -= 1
    raise SystemExit('no decoded %s frame' % phase)


def ground_fill(folder, frame, eye, which):
    """Writes the frame's raw for both copy points of the eye, altered as asked."""
    issues, pictures, views, planes = ev.load(folder)
    issue = issues[frame]; w, h = issue['width'], issue['height']
    cur, prev, how = ev.matrices_of(issues, frame, eye)
    modifier = issue.get('ndcModifier') or 1.0
    for copy_eye in (eye, eye + 10):
        p = os.path.join(folder, 'motion-f%d-e%d.bin' % (frame, copy_eye))
        data = np.fromfile(p, dtype=np.float16).reshape(h, w, 4).astype(np.float64)
        if which == 'z-broken':
            data[..., 2] += 0.01
        elif which == 'nan-y':
            flat = data.reshape(-1, 4); flat[::10, 1] = np.nan
        elif which in ('all-ground', 'half-ground'):
            ground = ev.expected_motion(cur, prev, modifier, np.array([0.0, 0.0, 0.0]), np.array([0.0, 1.0, 0.0]), (0, 0, w, h), w, h)
            if which == 'all-ground':
                data[..., :3] = ground; data[..., 3] = 1.0
            else:
                # the upper half of every piece's region: the body there replaced by the ground
                for body in [r for r in pictures if int(r['frame']) == frame]:
                    pieces, _ = ev.pieces_of(body)
                    for name, m, zr, side in pieces:
                        region = ev.region_of(cur, ev.box_corners(zr, m), w, h)
                        if region is None:
                            continue
                        x0, y0, x1, y1 = region; ym = (y0 + y1) // 2
                        data[y0:ym, x0:x1, :3] = ground[y0:ym, x0:x1]; data[y0:ym, x0:x1, 3] = 1.0
        data.astype(np.float16).tofile(p)


def run(folder, out):
    import io, contextlib
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        ev.main(['x', folder, '--out', out])
    return buf.getvalue()


def verdicts(report, frame):
    vs = []
    for line in report.splitlines():
        if line.startswith('| %d |' % frame):
            cells = [c.strip() for c in line.strip('|').split('|')]
            if len(cells) >= 23:
                vs.append(cells[-1])   # the verdict is the last cell
    return vs


def main(argv):
    if len(argv) != 3:
        print(__doc__); return 2
    src, scratch = argv[1], argv[2]
    frame = pick_frame(src)
    os.makedirs(scratch, exist_ok=True)
    has_mask = any(n.startswith('body-r') and n.endswith('-mesh.bin') for n in os.listdir(src))
    print('run %s, altered frame %d, shape masks %s' % (os.path.basename(os.path.normpath(src)), frame, 'present' if has_mask else 'ABSENT (PASS impossible; the negatives must not be EXPLAINED where the values are wrong)'))
    control = run(src, os.path.join(scratch, 'control'))
    cv = verdicts(control, frame)
    print('control: frame %d verdicts %s' % (frame, sorted(set(cv))))
    failed = False
    for which in ('z-broken', 'nan-y', 'all-ground', 'half-ground'):
        dst = os.path.join(scratch, which)
        copy_run(src, dst)
        for eye in (0, 1):
            ground_fill(dst, frame, eye, which)
        report = run(dst, os.path.join(scratch, which + '-eval'))
        vs = verdicts(report, frame)
        counts = {v: vs.count(v) for v in sorted(set(vs))}
        if has_mask:
            bad = [v for v in vs if v != 'FAIL']   # PASS, PASS-PARTIAL and MATCH-ONLY are all refused for a moving negative (review 3)
            expect = 'every row FAIL'
        else:
            bad = [v for v in vs if v in ('PASS', 'EXPLAINED')] if which in ('z-broken', 'nan-y') else [v for v in vs if v == 'PASS']
            expect = 'no PASS/EXPLAINED' if which in ('z-broken', 'nan-y') else 'no PASS (EXPLAINED allowed: the ground IS an explanation without a mask)'
        ok = len(bad) == 0 and len(vs) > 0
        failed |= not ok
        print('%-12s frame %d rows %d verdicts %s -> %s (%s)' % (which, frame, len(vs), counts, 'ok' if ok else 'NOT CAUGHT', expect))
        with open(os.path.join(scratch, which + '-report.md'), 'w', encoding='utf-8') as h:
            h.write(report)
    # a STATIC frame (the second frame of the middle hold): the body's and the ground's expectations are both ~0 there, so even an
    # all-ground replacement matches -- the evaluator must not say PASS there (MATCH-ONLY at best), with or without the alteration
    try:
        static = pick_frame(src, 'hold2', 1)
    except SystemExit:
        static = None
    if static is not None:
        cs = verdicts(control, static)
        dst = os.path.join(scratch, 'static-all-ground')
        copy_run(src, dst)
        for eye in (0, 1):
            ground_fill(dst, static, eye, 'all-ground')
        report = run(dst, os.path.join(scratch, 'static-all-ground-eval'))
        vs = verdicts(report, static)
        accepted = ('PASS', 'PASS-PARTIAL')
        ok = len(vs) > 0 and not any(v in accepted for v in vs) and not any(v in accepted for v in cs)
        failed |= not ok
        print('%-12s frame %d (static) control %s altered %s -> %s (no PASS or PASS-PARTIAL in a static view: MATCH-ONLY/FAIL/EXPLAINED only)' % ('static', static, {v: cs.count(v) for v in sorted(set(cs))}, {v: vs.count(v) for v in sorted(set(vs))}, 'ok' if ok else 'NOT CAUGHT'))
        with open(os.path.join(scratch, 'static-all-ground-report.md'), 'w', encoding='utf-8') as h:
            h.write(report)
    print('negatives:', 'all caught' if not failed else 'SOME NOT CAUGHT')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
