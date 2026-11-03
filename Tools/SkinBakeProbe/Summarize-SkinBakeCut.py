"""SKIN-GPU-FIRST-1: the cut bench runs of the skin bake probe summarised (DESIGN 4.5.15).

Usage: Summarize-SkinBakeCut.py <run folder>... [--out <file.md>]

A run folder (the probe's record directory) holds skin-bake-probe.json (the facts: build, the cut bench's settings and the
skin bake route the world ran), skin-bake-cut-requests.csv (one row a request: outcome, the main-thread time of the request,
the frames to its acceptance and its commit, the fallback or cancel reason, the switch's equality), skin-bake-cut-frames.csv
(one row a frame after the first request: the pending counts and the frame's whole times) and skin-bake-stats.csv (the
world's per-request records: a GPU-first request's hit, resume and worker times). Results are grouped by build and by
(route, bodies); a table never mixes a build or a body count. Per group, over the runs (the orders A and B):
  - the request frame's main-thread cost: the sum over the bodies of each request's own time (median over the runs), and the
    frame's whole Main at the request frame against the steady frames after the last acceptance (route - steady)
  - a GPU-first request's resume: the main-thread time of the completion (the storage write and the acceptance), the worker's
    time, the frames from the hit to the acceptance (median, max) and to the commit
  - the switch's equality (PASS counts), the fallbacks and cancellations by reason, the most requests pending at once
  - the frame's GPU (ftGpu on PC, xrAppGpu on Quest: a one-second value) at the request frame and the steady frames
No request's GPU execution time is measured here (the render-stage trace of SKIN-BAKE-PROBE-3 is the isolated value).
"""
import csv, json, os, sys
from collections import OrderedDict



ROUTE_ORDER = ['cpu', 'cpu-float', 'float', 'gpu']


def route_rank(route):
    # the routes in a fixed order; one this list does not know goes last (CPU-SKIN-FLOAT-1 added cpu-float)
    return ROUTE_ORDER.index(route) if route in ROUTE_ORDER else len(ROUTE_ORDER)

def median(v):
    s = sorted(v); n = len(s)
    return float('nan') if n == 0 else (s[n // 2] if n % 2 else 0.5 * (s[n // 2 - 1] + s[n // 2]))


def p95(v):
    s = sorted(v); n = len(s)
    return float('nan') if n == 0 else s[min(n - 1, max(0, int(-(-0.95 * n // 1)) - 1))]


def f(x, d=3):
    return '-' if x is None or x != x else ('%.' + str(d) + 'f') % x


def rows(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding='utf-8-sig') as h:
        return list(csv.DictReader(h))


def num(r, key):
    v = r.get(key, '')
    try:
        return float(v) if v not in ('', None) else None
    except ValueError:
        return None


def load(folder):
    record = json.load(open(os.path.join(folder, 'skin-bake-probe.json'), encoding='utf-8'))
    facts = record.get('facts', {})
    cut = facts.get('cut bench', '')
    route = 'cpu'
    for token in cut.split(';'):
        token = token.strip()
        if token.startswith('skin bake route '):
            route = token[len('skin bake route '):].split(' ')[0]
    bodies = 0
    for token in cut.split(';'):
        token = token.strip()
        if token.startswith('bodies '):
            bodies = int(token.split(' ')[1])
    build = facts.get('build', '?')
    requests = rows(os.path.join(folder, 'skin-bake-cut-requests.csv'))
    # PLANE-1: the plane condition (the requests' column; the runs before it are legacy) and the diagnosis hold (no performance figure).
    plane = (requests[0].get('cutPlane') or 'legacy') if requests else 'legacy'
    hold = 'CPU bake hold' in facts.get('diagnosis', '') and 'hold none' not in facts.get('diagnosis', '')
    return {
        'folder': folder, 'name': os.path.basename(os.path.dirname(folder.rstrip('/\\'))) or folder, 'build': build, 'build_key': build.split('; unity')[0] + ('; plane ' + plane) + ('; DIAGNOSIS HOLD (no performance)' if hold else ''),
        'facts': facts, 'route': route, 'bodies': bodies, 'plane': plane, 'hold': hold,
        'requests': requests,
        'frames': rows(os.path.join(folder, 'skin-bake-cut-frames.csv')),
        'stats': rows(os.path.join(folder, 'skin-bake-stats.csv')),
    }


def tail_of(rr, ff, by_described):
    """Review 4 (P2-2): every frame after the round's first request, not only the next five: the longest frame dt and the
    longest main-thread frame with their offsets, and every frame over 1.5 x the round's steady dt (the frames from +30 on)."""
    base = min([int(r['requestFrame']) for r in rr] or [0])
    rows = []
    for fr in ff:
        k = int(fr['frame']) - base
        if k < 0:
            continue
        m = by_described.get(int(fr['frame']))
        rows.append((k, num(fr, 'frameDtMs'), num(m, 'ftMainMs') if m else None))
    if not rows:
        return None
    steady = [r[1] for r in rows if r[0] >= 30 and r[1] is not None]
    period = median(steady) if steady else None
    thr = 1.5 * period if period else None
    dts = [r for r in rows if r[1] is not None]; mains = [r for r in rows if r[2] is not None]
    return {
        'frames': len(rows), 'period': period, 'threshold': thr,
        'max_dt': max(dts, key=lambda r: r[1]) if dts else None, 'max_main': max(mains, key=lambda r: r[2]) if mains else None,
        'over_dt': [(r[0], r[1]) for r in dts if thr and r[1] > thr], 'over_main': [(r[0], r[2]) for r in mains if thr and r[2] > thr],
    }


def analyse(run):
    out = {}
    req = run['requests']
    out['outcomes'] = OrderedDict()
    for r in req:
        out['outcomes'][r['outcome']] = out['outcomes'].get(r['outcome'], 0) + 1
    out['request_main_sum'] = sum(num(r, 'requestMainMs') or 0.0 for r in req)
    out['request_main_each'] = [num(r, 'requestMainMs') for r in req if num(r, 'requestMainMs') is not None]
    out['frames_to_accept'] = [num(r, 'framesToAccept') for r in req if num(r, 'framesToAccept') is not None and num(r, 'framesToAccept') >= 0]
    out['frames_to_commit'] = [num(r, 'framesToCommit') for r in req if num(r, 'framesToCommit') is not None and num(r, 'framesToCommit') >= 0]
    out['never_accepted'] = sum(1 for r in req if (num(r, 'framesToAccept') or -1) < 0)
    out['switch_pass'] = sum(1 for r in req if 'PASS' in (r.get('switchCompare') or ''))
    out['switch_fail'] = sum(1 for r in req if 'FAIL' in (r.get('switchCompare') or '') or 'ERROR' in (r.get('switchCompare') or ''))
    out['switch_none'] = sum(1 for r in req if not any(k in (r.get('switchCompare') or '') for k in ('PASS', 'FAIL', 'ERROR')))
    out['fallbacks'] = [r['fallbackReason'] for r in req if r.get('fallbackReason')]
    # SKIN-GPU-FIRST-2: the basis outcome of a gpu-first request ("ready" is the ordinary one); the older runs' cancelReason where present
    out['cancels'] = [r['basisOutcome'] for r in req if r.get('basisOutcome') and r['basisOutcome'] not in ('ready', '')] + [r['cancelReason'] for r in req if r.get('cancelReason')]
    for key, col in (('frames_to_physics', 'framesToPhysics'), ('frames_to_publish', 'framesToPublish'), ('frames_to_clip', 'framesToClip'), ('frames_to_ready', 'framesToReady')):
        out[key] = [num(r, col) for r in req if num(r, col) is not None and num(r, col) >= 0]
    out['cut_kernel'] = [num(r, 'cutKernelMs') for r in req if num(r, 'cutKernelMs') is not None]
    # The frames: k = sinceRequest. The request frame is k = 0; the steady frames begin 30 after the last acceptance.
    frames = run['frames']
    by_frame = {}
    for fr in frames:
        by_frame[int(fr['frame'])] = fr
    # The timing result that describes a frame (the ftFrame column, where the run has it): the row whose ftFrame is that frame.
    by_described = {}
    for fr in frames:
        if fr.get('ftFrame'):
            by_described[int(fr['ftFrame'])] = fr
    out['has_ft_frame'] = len(by_described) > 0
    offsets = [float(fr['ftStartOffsetMs']) for fr in frames if fr.get('ftStartOffsetMs')]
    out['ft_offset_min'] = min(offsets) if offsets else None
    out['ft_offset_max'] = max(offsets) if offsets else None
    last_accept = max([int(r['acceptedFrame']) for r in req if int(r['acceptedFrame']) >= 0] or [0])
    first_request = min([int(r['requestFrame']) for r in req] or [0])
    steady = [fr for fr in frames if int(fr['frame']) >= last_accept + 30]
    out['steady_n'] = len(steady)
    for col in ('ftMainMs', 'ftGpuMs', 'xrAppGpuMs'):
        vals = [num(fr, col) for fr in steady if num(fr, col) is not None]
        out['steady_' + col] = median(vals) if vals else None
        out['steady_' + col + '_p95'] = p95(vals) if vals else None
        # the request frame: the result describing it where the run attributes results (ftFrame), else the row read in it
        rf = by_described.get(first_request) if by_described else by_frame.get(first_request)
        out['request_' + col] = num(rf, col) if rf else None
        rf1 = by_described.get(first_request + 1) if by_described else by_frame.get(first_request + 1)
        out['request1_' + col] = num(rf1, col) if rf1 else None
        src = by_described if by_described else by_frame
        acc = [src.get(int(r['acceptedFrame'])) for r in req if int(r['acceptedFrame']) > int(r['requestFrame'])]
        accv = [num(a, col) for a in acc if a is not None and num(a, col) is not None]
        out['accept_' + col] = median(accv) if accv else None
    # the request frame's duration: the next row's frame dt (unscaledDeltaTime at frame k+1 = the time from frame k's start to k+1's)
    nxt = by_frame.get(first_request + 1)
    out['request_dt'] = num(nxt, 'frameDtMs') if nxt else None
    after = [num(by_frame.get(first_request + 1 + i), 'frameDtMs') for i in range(5) if by_frame.get(first_request + 1 + i)]
    out['request_dt_max5'] = max([v for v in after if v is not None] or [float('nan')])
    pend = [int(fr.get('pendingBakes') or fr.get('pendingDeferred') or 0) for fr in frames]
    out['max_pending'] = max(pend) if pend else 0
    # the world's records of the gpu route
    gpu = [s for s in run['stats'] if s.get('route') == 'gpu']
    out['ready_main'] = [num(s, 'readyMainMs') for s in gpu if num(s, 'readyMainMs') is not None]
    out['worker'] = [num(s, 'workerMs') for s in gpu if num(s, 'workerMs') is not None]
    out['kernel'] = [num(s, 'kernelMs') for s in gpu if num(s, 'kernelMs') is not None]
    out['hit_main_records'] = [num(s, 'hitMainMs') for s in gpu if num(s, 'hitMainMs') is not None]
    out['dispatches'] = [num(s, 'dispatches') for s in gpu if num(s, 'dispatches') is not None]
    # By round: the first round carries the process's first-time costs (the first cut's shader, allocations, caches); the
    # later rounds are the steady cost. A gpu record belongs to the round whose requests were made in its hit frame.
    rounds = sorted({int(r['round']) for r in req})
    out['rounds'] = rounds
    out['by_round'] = {}
    for rd in rounds:
        rr = [r for r in req if int(r['round']) == rd]
        frames_of_round = {int(r['requestFrame']) for r in rr}
        gr = [g for g in gpu if num(g, 'hitFrame') is not None and int(num(g, 'hitFrame')) in frames_of_round]
        out['by_round'][rd] = {
            'request_main_sum': sum(num(r, 'requestMainMs') or 0.0 for r in rr),
            'resume_main_sum': sum(num(g, 'readyMainMs') or 0.0 for g in gr) if gr else None,
            'worker': [num(g, 'workerMs') for g in gr if num(g, 'workerMs') is not None],
            'frames_to_accept': [num(r, 'framesToAccept') for r in rr if num(r, 'framesToAccept') is not None and num(r, 'framesToAccept') >= 0],
            'frames_to_commit': [num(r, 'framesToCommit') for r in rr if num(r, 'framesToCommit') is not None and num(r, 'framesToCommit') >= 0],
            'frames_to_physics': [num(r, 'framesToPhysics') for r in rr if num(r, 'framesToPhysics') is not None and num(r, 'framesToPhysics') >= 0],
            'frames_to_publish': [num(r, 'framesToPublish') for r in rr if num(r, 'framesToPublish') is not None and num(r, 'framesToPublish') >= 0],
            'tail': tail_of(rr, [fr for fr in frames if fr.get('round') not in (None, '') and int(fr['round']) == rd], by_described),
            'frames_to_clip': [num(r, 'framesToClip') for r in rr if num(r, 'framesToClip') is not None and num(r, 'framesToClip') >= 0],
            'frames_to_ready': [num(r, 'framesToReady') for r in rr if num(r, 'framesToReady') is not None and num(r, 'framesToReady') >= 0],
            'cut_kernel': [num(r, 'cutKernelMs') for r in rr if num(r, 'cutKernelMs') is not None],
        }
    return out


def main(argv):
    out = None; folders = []
    i = 0
    while i < len(argv):
        if argv[i] == '--out':
            out = argv[i + 1]; i += 2
        else:
            folders.append(argv[i]); i += 1
    runs = [load(x) for x in folders]
    for r in runs:
        r['a'] = analyse(r)
    lines = ['# Skin bake cut bench summary', '', 'The request frame\'s cost is each request\'s own main-thread time (TryCut, the acceptance included, in every route) summed over the bodies. SKIN-GPU-FIRST-2: a GPU-first request is accepted at the hit as the CPU route\'s is; its bake\'s collect (the storage completion, the region\'s return, the DAG registration) is a later frame\'s main-thread time ("collect main"); frames to physics (the provisional pair in the scene: the frame of the transaction) / publish (the ledger: the children issued) / clip / ready / commit are counted from the request frame; the clip is seen at the step of the bench in the frame (a collection later in a frame is seen the frame after). Frame rows are not independent GPU samples where the XR value repeats (a one-second value). No bake\'s GPU execution time is measured here.', '']
    builds = OrderedDict()
    for r in runs:
        builds.setdefault(r['build_key'], []).append(r)
    for build_key, group in builds.items():
        lines += ['## Build: %s' % build_key, '']
        for r in group:
            a = r['a']
            lines.append('- run %s: route %s, bodies %d, plane %s%s; %s' % (r['name'], r['route'], r['bodies'], r['plane'], ' (DIAGNOSIS HOLD: no performance figure)' if r['hold'] else '', r['facts'].get('cut bench', '?')[:300]))
            lines.append('  - bodies: %s' % r['facts'].get('cut bench bodies', '?')[:300])
            lines.append('  - outcomes %s; request main sum %s ms (each %s); frames to accept %s; to physics %s; to publish (ledger) %s; to clip %s; to ready %s; to commit %s; never accepted %d; switch PASS %d FAIL %d none %d; fallbacks %s; basis not ready %s; max pending %d; cut kernel ms %s'
                         % (dict(a['outcomes']), f(a['request_main_sum']), ', '.join(f(x) for x in a['request_main_each']), a['frames_to_accept'], a['frames_to_physics'], a['frames_to_publish'], a['frames_to_clip'], a['frames_to_ready'], a['frames_to_commit'], a['never_accepted'], a['switch_pass'], a['switch_fail'], a['switch_none'], a['fallbacks'], a['cancels'], a['max_pending'], ', '.join(f(x, 4) for x in a['cut_kernel'])))
            if a['ready_main']:
                lines.append('  - gpu-first records: collect main %s ms (each), bake worker %s ms, bake kernel %s ms, dispatches %s' % (', '.join(f(x, 4) for x in a['ready_main']), ', '.join(f(x) for x in a['worker']), ', '.join(f(x, 4) for x in a['kernel']), [int(x) for x in a['dispatches']]))
            lines.append('  - skin bake stats: %s' % r['facts'].get('skin bake stats', '?')[:400])
            lines.append('  - display gpu regions: %s; storage deferred: %s' % (r['facts'].get('display gpu regions', '?'), r['facts'].get('storage deferred', '?')))
        # By round: the first round (the first cut of the process) apart from the later ones.
        all_rounds = sorted({rd for r in group for rd in r['a']['rounds']})
        if len(all_rounds) > 1:
            lines += ['', '### By route, bodies and round (medians over the runs; round 0 is the process\'s first cut: first-time costs)', '',
                      '| route | bodies | round | runs | request main sum ms | collect main sum ms (gpu) | bake worker ms (gpu, median) | cut kernel ms (median) | frames to accept (median / max) | to physics | to publish (ledger) | to clip | to ready | frames to commit (median / max) |',
                      '|---|---|---|---|---|---|---|---|---|---|---|---|---|---|']
            groups_r = OrderedDict()
            for r in group:
                groups_r.setdefault((r['route'], r['bodies']), []).append(r)
            for (route, bodies), rs in sorted(groups_r.items(), key=lambda kv: (kv[0][1], route_rank(kv[0][0]) if kv[0][0] in ('cpu', 'float', 'gpu') else 9)):
                for rd in all_rounds:
                    br = [x['a']['by_round'][rd] for x in rs if rd in x['a']['by_round']]
                    if not br:
                        continue
                    req_sum = [b['request_main_sum'] for b in br]
                    res_sum = [b['resume_main_sum'] for b in br if b['resume_main_sum'] is not None]
                    worker = [w for b in br for w in b['worker']]
                    fa = [v for b in br for v in b['frames_to_accept']]
                    fc = [v for b in br for v in b['frames_to_commit']]
                    fp = [v for b in br for v in b['frames_to_publish']]
                    fph = [v for b in br for v in b['frames_to_physics']]
                    fl = [v for b in br for v in b['frames_to_clip']]
                    fr_ = [v for b in br for v in b['frames_to_ready']]
                    ck = [v for b in br for v in b['cut_kernel']]
                    def mm(vals):
                        return ('%s / %s' % (f(median(vals), 0), f(max(vals), 0))) if vals else '-'
                    lines.append('| %s | %d | %d | %d | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |' % (
                        route, bodies, rd, len(br), f(median(req_sum)), f(median(res_sum), 4) if res_sum else '-', f(median(worker)) if worker else '-', f(median(ck), 4) if ck else '-',
                        mm(fa), mm(fph), mm(fp), mm(fl), mm(fr_), mm(fc)))
            # Review 4 (P2-2): the whole window after each request, one row a run and round
            lines += ['', '### The frames after each request (the whole observed window; max with its offset from the request frame; over = frames longer than 1.5 x the steady dt)', '',
                      '| run | route | bodies | round | frames | max dt ms @+n | max Main ms @+n | frames over (offset: dt) | frames over (offset: Main) | steady dt |',
                      '|---|---|---|---|---|---|---|---|---|---|']
            for r in group:
                for rd in sorted(r['a']['by_round']):
                    tl = r['a']['by_round'][rd].get('tail')
                    if not tl:
                        continue
                    lines.append('| %s | %s | %d | %d | %d | %s | %s | %s | %s | %s |' % (
                        r['name'], r['route'], r['bodies'], rd, tl['frames'],
                        ('%s @+%d' % (f(tl['max_dt'][1]), tl['max_dt'][0])) if tl['max_dt'] else '-', ('%s @+%d' % (f(tl['max_main'][2]), tl['max_main'][0])) if tl['max_main'] else '-',
                        ', '.join('+%d: %s' % (k, f(v)) for k, v in tl['over_dt']) or '-', ', '.join('+%d: %s' % (k, f(v)) for k, v in tl['over_main']) or '-', f(tl['period'])))
        lines += ['', '### By route and bodies (medians over the runs; all rounds together)', '',
                  '| route | bodies | runs | request main sum ms | collect main sum ms (gpu) | bake worker ms (gpu, median) | frames to accept (median / max) | frames to commit (median / max) | switch PASS / FAIL / none | fallbacks | basis not ready | max pending |',
                  '|---|---|---|---|---|---|---|---|---|---|---|---|']
        groups = OrderedDict()
        for r in group:
            groups.setdefault((r['route'], r['bodies']), []).append(r)
        for (route, bodies), rs in sorted(groups.items(), key=lambda kv: (kv[0][1], route_rank(kv[0][0]) if kv[0][0] in ('cpu', 'float', 'gpu') else 9)):
            req_sum = [x['a']['request_main_sum'] for x in rs]
            resume_sum = [sum(x['a']['ready_main']) for x in rs if x['a']['ready_main']]
            worker = [w for x in rs for w in x['a']['worker']]
            fa = [v for x in rs for v in x['a']['frames_to_accept']]
            fc = [v for x in rs for v in x['a']['frames_to_commit']]
            lines.append('| %s | %d | %d | %s | %s | %s | %s / %s | %s / %s | %d / %d / %d | %d | %d | %d |' % (
                route, bodies, len(rs), f(median(req_sum)), f(median(resume_sum), 4) if resume_sum else '-', f(median(worker)) if worker else '-',
                f(median(fa), 0) if fa else '-', f(max(fa), 0) if fa else '-', f(median(fc), 0) if fc else '-', f(max(fc), 0) if fc else '-',
                sum(x['a']['switch_pass'] for x in rs), sum(x['a']['switch_fail'] for x in rs), sum(x['a']['switch_none'] for x in rs),
                sum(len(x['a']['fallbacks']) for x in rs), sum(len(x['a']['cancels']) for x in rs), max(x['a']['max_pending'] for x in rs)))
        lines += ['', '### The frame\'s whole times (medians over the runs; steady = frames from 30 after the last acceptance; request = the request frame, request+1 the next; accept = the acceptance frames of deferred requests)', '',
                  '| route | bodies | steady ftMain | request ftMain | request+1 ftMain | accept ftMain | steady ftGpu | request ftGpu | steady xrAppGpu | request xrAppGpu | accept xrAppGpu | request frame dt | max dt of the 5 after | steady frames | attributed |',
                  '|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|']
        for (route, bodies), rs in sorted(groups.items(), key=lambda kv: (kv[0][1], route_rank(kv[0][0]) if kv[0][0] in ('cpu', 'float', 'gpu') else 9)):
            def med(key):
                v = [x['a'][key] for x in rs if x['a'].get(key) is not None]
                return median(v) if v else None
            lines.append('| %s | %d | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |' % (
                route, bodies, f(med('steady_ftMainMs')), f(med('request_ftMainMs')), f(med('request1_ftMainMs')), f(med('accept_ftMainMs')),
                f(med('steady_ftGpuMs')), f(med('request_ftGpuMs')), f(med('steady_xrAppGpuMs')), f(med('request_xrAppGpuMs')), f(med('accept_xrAppGpuMs')),
                f(med('request_dt')), f(med('request_dt_max5')), f(med('steady_n'), 0),
                ('ftFrame (start offset %s..%s ms)' % (f(min(x['a']['ft_offset_min'] for x in rs if x['a'].get('ft_offset_min') is not None)), f(max(x['a']['ft_offset_max'] for x in rs if x['a'].get('ft_offset_max') is not None)))
                 if all(x['a'].get('has_ft_frame') and x['a'].get('ft_offset_min') is not None for x in rs) else 'ftFrame (at-or-before match: may name the frame before)' if all(x['a'].get('has_ft_frame') for x in rs) else 'read-in frame')))
        lines.append('')
    text = '\n'.join(lines) + '\n'
    if out:
        open(out, 'w', encoding='utf-8', newline='\n').write(text)
    sys.stdout.write(text)


if __name__ == '__main__':
    main(sys.argv[1:])
