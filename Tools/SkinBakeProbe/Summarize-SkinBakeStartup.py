# -*- coding: utf-8 -*-
"""SKIN-GPU-FIRST-STARTUP-1: the frames around each request of a cut bench run, from the probe's clock-attributed marks.

Reads, in a run folder: skin-bake-frame-marks.csv (per frame: the Stopwatch marks as ms from the round's first frame start --
init, step, hit begin/end, LateUpdate, onBeforeRender, begin/endContextRendering -- the frame-start interval dtInitMs, the bake
marks' count and ms, the native motion issue counter), skin-bake-bake-marks.csv, skin-bake-ftm-raw.csv (FrameTimingManager
raw with the clock-base offset to the nearest frame-start mark), skin-bake-cut-events.csv (the cut's events with ticks) and
skin-bake-cut-frames.csv (the bench's own per-frame row: Time.unscaledDeltaTime and the sampler's FTM reading).

For each round it prints the frames k -3..k 8 (and any frame whose start interval exceeds 40 ms) with where the time went
inside the frame: the main-thread segments between the marks (init -> step, step -> late, late -> beforeRender -> beginRender,
beginRender -> endRender) and the gap from endRender to the next frame's init (the present/vsync wait and anything after the
render on the main thread). The FTM result whose frame start is nearest to the frame's init is shown with its offset: a small
steady offset (|offset| < 1 ms) says the two clocks share a base and the attribution holds; otherwise the FTM columns are marked
"undetermined". Nothing here infers a GPU cause from a short Main.

Usage: Summarize-SkinBakeStartup.py <run folder> [--all]
"""
import csv, os, sys


def read(folder, name):
    p = os.path.join(folder, name)
    if not os.path.exists(p):
        return []
    return list(csv.DictReader(open(p, encoding='utf-8')))


def f(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def main(argv):
    if len(argv) < 2 or not os.path.isdir(argv[1]):
        print(__doc__); return 2
    folder = argv[1]; show_all = '--all' in argv
    marks = read(folder, 'skin-bake-frame-marks.csv')
    ftm = read(folder, 'skin-bake-ftm-raw.csv')
    events = read(folder, 'skin-bake-cut-events.csv')
    frames = read(folder, 'skin-bake-cut-frames.csv')
    bake = read(folder, 'skin-bake-bake-marks.csv')
    if not marks:
        print('no skin-bake-frame-marks.csv in', folder); return 1
    dt_by_frame = {int(r['frame']): f(r['frameDtMs']) for r in frames}
    bake_by_frame = {}
    for b in bake:
        bake_by_frame.setdefault(int(b['frame']), []).append('%s %.2f' % (b['name'], f(b['ms']) or 0))
    # FTM by nearest init frame
    ftm_by_frame = {}
    offsets = []
    first_marked = min(int(r['frame']) for r in marks if r['initTick'] not in ('', '0')) if marks else None
    before_marks = 0
    for r in ftm:
        if r['nearestInitFrame']:
            o = f(r['offsetToNearestInitMs'])
            # a result whose frame started before the first mark (the first read carries up to 8 older results) cannot be attributed:
            # it is neither a clock mismatch nor a frame of the window
            if o is not None and int(r['nearestInitFrame']) == first_marked and o < -20.0:
                before_marks += 1
                continue
            ftm_by_frame.setdefault(int(r['nearestInitFrame']), []).append(r)
            if o is not None:
                offsets.append(o)
    if before_marks:
        print('FTM results before the first frame-start mark (not attributable, excluded): %d' % before_marks)
    if offsets:
        offsets_sorted = sorted(offsets)
        med = offsets_sorted[len(offsets_sorted) // 2]
        inliers = sum(1 for o in offsets if abs(o) < 1.0)
        # the base holds when the median is near 0 and nearly every result lands within 1 ms of a frame-start mark; a result whose
        # frame start precedes the marks (before the round began) is an outlier of the matching, not of the clock, and is marked per row
        base_ok = abs(med) < 1.0 and inliers >= 0.95 * len(offsets)
        print('FTM clock base: %d results, offset to the nearest frame-start mark median %.3f ms, within 1 ms %d of %d, min %.3f, max %.3f -> %s' % (len(offsets), med, inliers, len(offsets), offsets_sorted[0], offsets_sorted[-1], 'same base (attribution by frame start holds; rows beyond 1 ms are marked)' if base_ok else 'NOT the same base or not steady: FTM attribution undetermined'))
    else:
        base_ok = False
        print('FTM clock base: no results with a nearest frame-start mark (attribution undetermined)')
    rounds = sorted(set(int(r['round']) for r in marks))
    for rd in rounds:
        rows = [r for r in marks if int(r['round']) == rd]
        rows.sort(key=lambda r: int(r['frame']))
        print('')
        print('== round %d: %d frames' % (rd, len(rows)))
        ev = [e for e in events if int(e['round']) == rd]
        for e in ev:
            if e['frame'] not in ('', '-1'):
                print('   event %-26s frame %s  +%s ms after request-begin' % (e['event'], e['frame'], e['msAfterRequestBegin'] or '-'))
        print('   frame     k | dtInit |  init->step  step->late  late->bRender  bRender->begin  begin->end  end->nextInit | hit ms | bake marks | FTM main render gpu presentWait (offset)')
        for i, r in enumerate(rows):
            k = r['k']
            dt = f(r['dtInitMs'])
            interesting = (k != '' and -3 <= int(k) <= 8) or (dt is not None and dt > 40) or show_all
            if not interesting:
                continue
            init, step, late, br, bg, en = (f(r[c]) for c in ('initMs', 'stepMs', 'lateMs', 'beforeRenderMs', 'beginRenderMs', 'endRenderMs'))
            nxt = f(rows[i + 1]['initMs']) if i + 1 < len(rows) else None
            def seg(a, b): return ('%7.2f' % (b - a)) if a is not None and b is not None else '      -'
            frame = int(r['frame'])
            hit = r['hitMs']
            bakes = '; '.join(bake_by_frame.get(frame, [])) or '-'
            ftm_text = '-'
            if frame in ftm_by_frame:
                t = ftm_by_frame[frame][0]
                ftm_text = '%s %s %s %s (%s)' % (t['cpuMainThreadFrameTimeMs'], t['cpuRenderThreadFrameTimeMs'], t['gpuFrameTimeMs'], t['cpuMainThreadPresentWaitTimeMs'], t['offsetToNearestInitMs'])
                o = f(t['offsetToNearestInitMs'])
                if not base_ok or o is None or abs(o) >= 1.0:
                    ftm_text += ' undetermined'
            print('   %6d %5s | %6s | %s %s %s %s %s %s | %6s | %s | %s' % (frame, k, ('%.1f' % dt) if dt is not None else '-', seg(init, step), seg(step, late), seg(late, br), seg(br, bg), seg(bg, en), seg(en, nxt), hit or '-', bakes, ftm_text))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
