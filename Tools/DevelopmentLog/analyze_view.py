"""Read-only analysis of a view diagnosis (2026-10-02): per phase cut at the focus and pause records, how much each stage of
the head pose moved -- the Input System HMD, the legacy XR head, the driver's actions, the driver's current pose, the
camera's local pose -- the tracking states, and whether the camera holds the driver's pose at the input after-update.
Reads the development logger's session file (records of writer ViewDiagnosis, 2026-10-02), given as
<session.jsonl>:<log> (the Editor's or the Player's log, holding the "[view diagnosis] ended" lines). A recording is
analysed only if its saved records are complete against its end line (check_writer_jsonl.py, beside this file); one whose records are
missing, refused or cannot be checked is reported and not analysed. Also printed per recording: the stages' order within a
frame and the clock's order along seq, and the logger's frame against the record's own eventFrame, by stage."""
import collections, json, math, os, subprocess, sys

CHECK = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'check_writer_jsonl.py')
SAMPLE = ('inputAfterUpdate', 'update', 'beforeRender')


def q(r, p):
    return tuple(float(r[p + k]) for k in ('X', 'Y', 'Z', 'W')) if p == 'rotAct' else tuple(float(r[p + k]) for k in ('RotX', 'RotY', 'RotZ', 'RotW'))


def ang(a, b):
    dot = abs(sum(x * y for x, y in zip(a, b)))
    return math.degrees(2 * math.acos(min(1.0, dot))) if any(a) and any(b) else float('nan')


def analyse(recs):
    rows = [(t, d, f) for t, d, f in recs if t in SAMPLE]
    events = [(t, d) for t, d, _ in recs if t not in SAMPLE]
    found = next((d for t, d in events if t == 'found'), {})
    print('   found: %s camera %s (instance %s), hmd %s (device %s)' % (found.get('source'), found.get('camera'), found.get('cameraInstance'), found.get('hmd'), found.get('hmdDeviceId')))
    # the stages: their order within a frame, the clock along seq, the logger's frame against eventFrame
    order = collections.Counter(); per = collections.defaultdict(list)
    for t, d, _ in rows: per[d['eventFrame']].append(t)
    for ts in per.values(): order[' > '.join(ts)] += 1
    seqd = [d for _, d, _ in recs]
    back = sum(1 for a, b in zip(seqd, seqd[1:]) if b['seconds'] < a['seconds'] or b['eventFrame'] < a['eventFrame'])
    lag = collections.defaultdict(collections.Counter)
    for t, d, f in recs: lag[t][f - d['eventFrame']] += 1
    print('   stages within a frame: %s; seconds or eventFrame going back along seq: %d' % (dict(order.most_common(6)), back))
    print('   logger frame - eventFrame by tag: %s' % {t: dict(c) for t, c in lag.items()})
    cuts = [(d['seconds'], '%s %s' % (t, {k: d[k] for k in d if k in ('focus', 'pause', 'isFocused')})) for t, d in events if t in ('focus', 'pause')]
    bounds = [0.0] + [c[0] for c in cuts] + [1e18]
    print('   focus/pause records:', [('%.3f s' % c[0], c[1]) for c in cuts])
    for i in range(len(bounds) - 1):
        lo, hi = bounds[i], bounds[i + 1]
        ph = [(t, d) for t, d, _ in rows if lo <= d['seconds'] < hi]
        if not ph:
            continue
        print('\n   == phase %d: %.1f..%.1f s, samples %d, focused %s, frames %s..%s' % (i, lo, min(hi, ph[-1][1]['seconds']), len(ph), {d['focused'] for _, d in ph}, ph[0][1]['eventFrame'], ph[-1][1]['eventFrame']))
        for tag in SAMPLE:
            k = [d for t, d in ph if t == tag]
            if not k:
                continue
            span = lambda p: max((ang(q(k[0], p), q(r, p)) for r in k), default=float('nan'))
            steps = lambda p: sum(1 for a, b in zip(k, k[1:]) if q(a, p) != q(b, p))
            line = '     %s samples %d: max angle from the phase start / changes  hmd %.1f/%d  rotAct %.1f/%d  tpd %.1f/%d  cam %.1f/%d' % (
                tag, len(k), span('hmd'), steps('hmd'), span('rotAct'), steps('rotAct'), span('tpd'), steps('tpd'), span('cam'), steps('cam'))
            if tag == 'update':
                line += '  legacy %.1f/%d' % (span('legacy'), steps('legacy'))
            print(line)
            ts = lambda c: sorted({r[c] for r in k})
            print('        tracking: hmd %s tsAct %s tpd %s legacy %s | actions enabled rot %s ts %s | hmd enabled %s | lastUpdate %s..%s' % (
                ts('hmdTracking'), ts('tsActValue'), ts('tpdTracking'), ts('legacyTracking') if tag == 'update' else '-', ts('rotActEnabled'), ts('tsActEnabled'), ts('hmdEnabled'), k[0]['hmdLastUpdate'], k[-1]['hmdLastUpdate']))
            c2t = [r['camToTpdDeg'] for r in k if r['camToTpdDeg'] is not None]
            if c2t:
                print('        camera vs driver pose (deg): max %.3f, samples over 0.5 deg %d of %d' % (max(c2t), sum(1 for x in c2t if x > 0.5), len(c2t)))


for arg in sys.argv[1:]:
    cut = arg.lower().rfind('.jsonl:')   # <session.jsonl>:<log>, absolute paths with drive letters included
    jsonl, log = (arg[:cut + 6], arg[cut + 7:]) if cut >= 0 else (arg, '')
    name = os.path.basename(jsonl)
    if not log:
        print('== %s: not analysed (no log given: completeness unconfirmed)' % name); continue
    recs = collections.defaultdict(list)
    for line in open(jsonl, encoding='utf-8'):
        r = json.loads(line)
        if r['writer_id'] == 'ViewDiagnosis':
            v = r['value']; d = dict(zip(v[0::2], v[1::2]))
            recs[d['recording']].append((r['tag'], d, r['frame']))
    if not recs:
        print('== %s: no ViewDiagnosis records' % name)
    for rec in sorted(recs):
        chk = subprocess.run([sys.executable, CHECK, 'ViewDiagnosis', 'view diagnosis', jsonl, log, '--recording', str(rec)], capture_output=True, text=True)
        verdict = chk.stdout.strip().splitlines()[-1] if chk.stdout.strip() else chk.stderr.strip()
        if chk.returncode != 0:
            print('== %s recording %d: not analysed (%s)' % (name, rec, chk.stdout.strip().splitlines()[0] if chk.stdout.strip() else verdict)); continue
        print('== %s recording %d [%s]' % (name, rec, verdict))
        analyse(sorted(recs[rec], key=lambda x: x[1]['seq']))
