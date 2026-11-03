# -*- coding: utf-8 -*-
"""SKIN-GPU-FIRST-EVAL-1: the cut bench runs of the skin bake probe evaluated per round -- the hit frame and the frames after it together.

A run folder holds skin-bake-probe.json (facts: build, cut bench settings, the route, the warm facts, the round resets),
skin-bake-cut-requests.csv (one row a request), skin-bake-cut-events.csv (the events -- the product's hooks -- and the bench's
observations, with Stopwatch ticks), skin-bake-frame-marks.csv (the frame-start marks: dtInitMs is the interval that ENDS at the frame's start; a frame's own interval is the next frame's dtInitMs),
skin-bake-ftm-raw.csv (FrameTimingManager raw: the main-thread time of the frame whose start is nearest a frame-start mark, with
the clock-base offset) and skin-bake-stats.csv (the world's per-request records: worker, kernel, collect times).

Per round (round 0 = the process's first after its warm, kept APART; the rounds after it = the steady trials) over a fixed
window of W frames from the request frame (the same W for every route; every Commit of the round should fall inside it, one
past it is flagged, never dropped):
  - the hit frame's Main (FTM, attributed by the frame-start mark; TryCut's own time beside it),
  - the frames after the hit: the Main sum and the largest frame (k and ms),
  - the window's Main total (one value a frame, non-overlapping; a frame without an attributed FTM result makes the total
    PARTIAL and is counted), the largest Main, the largest frame-start interval, frames over 35 ms,
  - per request: the frames and ms from the request to the acceptance, the provisional physics (the driver's hook), the
    ledger publication (hook), the clip state (observed at the step / at onBeforeRender -- a display state, not a pixel; '-' =
    not observed, never "not displayed"), the CPU ready (the collect hook: completed, apart from failed/dropped/...), the commit
    (hook), the budget wait and the geometry wait,
  - the worker times (CPU bake worker, cut kernel, collect Main) -- never added to Main.
Groups are (platform, build, route, bodies); each group's steady rounds are pooled over its launches (the orders A and B) and
the per-launch medians are kept apart as the run-to-run spread. The comparisons cpu->float, float->gpu and cpu->gpu give the
difference of the pooled medians and say "within the spread" when it is not larger than the launches' and the rounds' spread:
no verdict is written here; a lower or higher number is only that.

Usage: Summarize-SkinBakeEval.py <folder>... [--window 32] [--out <file.md>] [--json <file.json>]
A folder is a run (it holds skin-bake-probe.json) or a root searched three levels deep for runs.
"""
import csv, json, os, re, sys
from collections import OrderedDict


def f(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def i(v, default=-1):
    try:
        return int(v)
    except (TypeError, ValueError):
        return default


def median(v):
    s = sorted(x for x in v if x is not None)
    n = len(s)
    if n == 0:
        return None
    return s[n // 2] if n % 2 else 0.5 * (s[n // 2 - 1] + s[n // 2])


def quartiles(v):
    s = sorted(x for x in v if x is not None)
    if len(s) < 2:
        return (s[0], s[0]) if s else (None, None)
    def at(q):
        p = q * (len(s) - 1); lo = int(p); hi = min(lo + 1, len(s) - 1)
        return s[lo] + (s[hi] - s[lo]) * (p - lo)
    return at(0.25), at(0.75)


def fmt(v, d=2):
    return '-' if v is None else ('%.*f' % (d, v))


def read_csv(folder, name):
    p = os.path.join(folder, name)
    if not os.path.exists(p):
        return []
    with open(p, encoding='utf-8-sig', newline='') as h:
        return list(csv.DictReader(h))


def find_runs(folder, depth=3):
    if os.path.exists(os.path.join(folder, 'skin-bake-probe.json')):
        return [folder]
    if depth == 0:
        return []
    out = []
    for name in sorted(os.listdir(folder)):
        p = os.path.join(folder, name)
        if os.path.isdir(p):
            out.extend(find_runs(p, depth - 1))
    return out


ATTRIBUTION_MS = 1.5   # |FTM frame start - nearest frame-start mark| beyond this = the result is not that frame's (undetermined)
LONG_FRAME_MS = 35.0


class Run(object):
    def __init__(self, folder, name, window):
        self.folder = folder; self.name = name; self.window = window
        with open(os.path.join(folder, 'skin-bake-probe.json'), encoding='utf-8-sig') as h:
            j = json.load(h)
        self.facts = j.get('facts', {}) or {}
        self.passed = j.get('passed'); self.failed = j.get('failed')
        self.requests = read_csv(folder, 'skin-bake-cut-requests.csv')
        self.events = read_csv(folder, 'skin-bake-cut-events.csv')
        self.marks = read_csv(folder, 'skin-bake-frame-marks.csv')
        self.ftm = read_csv(folder, 'skin-bake-ftm-raw.csv')
        self.stats = read_csv(folder, 'skin-bake-stats.csv')
        self.frames = read_csv(folder, 'skin-bake-cut-frames.csv')
        build = self.facts.get('build', '')
        self.platform = 'Quest' if 'Vulkan' in build or 'Android' in build else ('PC' if 'Direct3D12' in build or 'Windows' in build else 'unknown')
        self.build_guid = build.split('buildGUID ')[1].split(';')[0] if 'buildGUID ' in build else ''
        bench = self.facts.get('cut bench', '')
        self.route = (self.requests[0]['route'] if self.requests else bench.split('skin bake route ')[1].split(' ')[0] if 'skin bake route ' in bench else '?')
        self.bodies = i(bench.split('bodies ')[1].split(';')[0]) if 'bodies ' in bench else (max(i(r['body']) for r in self.requests) + 1 if self.requests else 0)
        self.rounds_asked = i(bench.split('rounds ')[1].split(';')[0]) if 'rounds ' in bench else 0
        self.order = 'B' if '-B' in name else ('A' if '-A' in name else '?')
        self.warm_draw = self.facts.get('warm before the bench (draw)', '')
        self.warm_skin = self.facts.get('warm before the bench (skin)', '')
        self.warm_none = self.facts.get('warm before the bench', '')
        self.resets = OrderedDict((k, v) for k, v in self.facts.items() if k.startswith('cut bench round ') and k.endswith(' reset'))
        self.reaction = self.facts.get('cut bench reaction events', '')
        self._index()
        self.invalid_rounds = self._invalid_rounds()
        self.rounds = [self._round(r) for r in sorted(set(i(x['round']) for x in self.requests))]

    def _invalid_rounds(self):
        # TL review P2-3: a round after a reset that did not return to the initial state (not settled, a retirement refused, live cut
        # fragments or owners left) is INVALID -- it and every later round are left out of the steady pooling and listed. The runner's
        # roundValid column is the record when the run has it; an older run is read from its reset facts.
        if self.requests and 'roundValid' in self.requests[0]:
            invalid = OrderedDict()
            for r in sorted(self.requests, key=lambda x: i(x['round'])):
                v = r.get('roundValid', '')
                if v not in ('', 'valid') and i(r['round']) not in invalid:
                    invalid[i(r['round'])] = v
            return invalid
        invalid = OrderedDict(); failed = None
        for rnd in sorted(set(i(x['round']) for x in self.requests)):
            if failed is not None:
                invalid[rnd] = failed
                continue
            fact = self.facts.get('cut bench round %d reset' % rnd)
            if fact is None:
                continue   # the last round makes no reset
            why = []
            if not fact.startswith('settled'):
                why.append('not settled')
            m = re.search(r'refused (\d+)', fact)
            if m and int(m.group(1)) > 0:
                why.append('retirement refused %s' % m.group(1))
            m = re.search(r'live cut fragments (\d+) -> (\d+), owners (\d+) -> (\d+)', fact)
            if m and (m.group(2) != '0' or m.group(4) != '0'):
                why.append('residue: live cut fragments %s, owners %s' % (m.group(2), m.group(4)))
            if why:
                failed = 'invalid: the reset of round %d did not return to the initial state (%s)' % (rnd, ', '.join(why))
        return invalid

    def warm_state(self):
        parts = []
        if self.warm_draw:
            parts.append('draw: ' + ('completed' if 'completed' in self.warm_draw and 'FAILED' not in self.warm_draw and 'NOT warmed' not in self.warm_draw else 'NOT complete'))
        if self.warm_skin:
            parts.append('skin: ' + ('completed' if 'completed' in self.warm_skin and 'FAILED' not in self.warm_skin and 'NOT' not in self.warm_skin.split('completed')[0][-12:] else 'NOT complete'))
        if not parts:
            parts.append('none (cold first round)' if self.warm_none else 'no warm fact')
        return '; '.join(parts)

    def _index(self):
        # the FTM result of a frame: the one whose start is nearest its frame-start mark and inside the attribution bound; one a frame
        self.main_of = {}; self.undetermined_frames = set(); self.ftm_rows_dropped = 0
        best = {}
        for r in self.ftm:
            fr = i(r.get('nearestInitFrame'))
            off = f(r.get('offsetToNearestInitMs'))
            main = f(r.get('cpuMainThreadFrameTimeMs'))
            if fr < 0 or off is None or main is None:
                continue
            if abs(off) > ATTRIBUTION_MS:
                self.undetermined_frames.add(fr); continue
            if fr in best:
                self.ftm_rows_dropped += 1
                if abs(off) >= abs(best[fr][0]):
                    continue
            best[fr] = (off, main, f(r.get('cpuRenderThreadFrameTimeMs')), f(r.get('gpuFrameTimeMs')), f(r.get('cpuMainThreadPresentWaitTimeMs')))
        for fr, (off, main, render, gpu, present) in best.items():
            self.main_of[fr] = {'main': main, 'offset': off, 'render': render, 'gpu': gpu, 'present': present}
            self.undetermined_frames.discard(fr)
        self.mark_of = {}
        for r in self.marks:
            fr = i(r.get('frame'))
            if fr >= 0:
                self.mark_of[fr] = r
        self.events_of = {}
        for e in self.events:
            key = (i(e['round']), i(e['body']))
            self.events_of.setdefault(key, {})
            if e['event'] not in self.events_of[key]:   # the first row of an event is the record; a repeated row is not counted again
                self.events_of[key][e['event']] = (i(e['frame']), f(e.get('msAfterRequestBegin')), e.get('note', ''))
        self.xr_gpu_of = {}
        for r in self.frames:
            fr = i(r.get('frame'))
            if fr >= 0:
                self.xr_gpu_of[fr] = (f(r.get('xrAppGpuMs')), f(r.get('frameDtMs')))
        self.stats_of = {}
        for r in self.stats:
            self.stats_of[(i(r.get('hitFrame')), r.get('route'))] = r

    def _round(self, rnd):
        reqs = [r for r in self.requests if i(r['round']) == rnd]
        rq = min(i(r['requestFrame']) for r in reqs)
        W = self.window
        frames = list(range(rq, rq + W))
        per = []
        for k, fr in enumerate(frames):
            m = self.main_of.get(fr)
            mk = self.mark_of.get(fr)
            nxt = self.mark_of.get(fr + 1)   # TL review P2-2: the frame's own interval begins at its start and ends at the next frame's start
            per.append({'k': k, 'frame': fr, 'main': m['main'] if m else None, 'render': m['render'] if m else None, 'gpu': m['gpu'] if m else None,
                        'dt': f(nxt.get('dtInitMs')) if nxt else None, 'attributed': m is not None, 'in_marks': mk is not None,
                        'hitMs': f(mk.get('hitMs')) if mk else None, 'xrGpu': self.xr_gpu_of.get(fr, (None, None))[0]})
        mains = [p['main'] for p in per]
        attributed = [p for p in per if p['attributed']]
        total = sum(p['main'] for p in attributed) if attributed else None
        later = [p for p in per[1:] if p['attributed']]
        later_max = max(later, key=lambda p: p['main']) if later else None
        dts = [p['dt'] for p in per if p['dt'] is not None]
        long_frames = [(p['k'], p['dt']) for p in per if p['dt'] is not None and p['dt'] > LONG_FRAME_MS]
        # the whole observation of the round (every marks row of the round at or after the request), for long frames past the window
        all_long = []
        for r in self.marks:
            if i(r['round']) != rnd:
                continue
            fr = i(r['frame']); dt = f(r.get('dtInitMs'))
            if fr > rq and dt is not None and dt > LONG_FRAME_MS:
                all_long.append((fr - rq - 1, dt))   # the interval that ends at fr belongs to the frame before it
        bodies = []
        for r in sorted(reqs, key=lambda x: i(x['body'])):
            b = i(r['body']); ev = self.events_of.get((rnd, b), {})
            def frame_delta(name):
                e = ev.get(name)
                return None if e is None or e[0] < 0 else e[0] - i(r['requestFrame'])
            def ms(name):
                e = ev.get(name)
                return None if e is None or e[0] < 0 else e[1]
            st = self.stats_of.get((i(r['requestFrame']), r['route']))
            bodies.append({
                'body': b, 'outcome': r.get('outcome'), 'acceptance': r.get('acceptance'), 'requestMainMs': f(r.get('requestMainMs')),
                'accepted': frame_delta('accepted'), 'physics': frame_delta('physics-published'), 'physicsMs': ms('physics-published'),
                'physicsObserved': frame_delta('physics-published-observed'),
                'ledger': frame_delta('ledger-published'), 'ledgerMs': ms('ledger-published'), 'ledgerObserved': frame_delta('ledger-published-observed'),
                'clipStep': frame_delta('clip-observed-step') if 'clip-observed-step' in ev else frame_delta('clip-observed'),
                'clipBeforeRender': frame_delta('clip-observed-beforeRender'), 'clipBeforeRenderMs': ms('clip-observed-beforeRender'),
                'ready': frame_delta('cpu-ready'), 'readyMs': ms('cpu-ready'), 'readyEnded': frame_delta('cpu-ready-ended'), 'readyOutcome': r.get('readyOutcome', ''),
                'readyObserved': frame_delta('cpu-ready-observed'),
                'commit': frame_delta('committed'), 'commitMs': ms('committed'), 'commitObserved': frame_delta('committed-observed'),
                'cutKernelMs': f(r.get('cutKernelMs')), 'readyMainMs': f(r.get('readyMainMs')), 'bakeWorkerMs': f(r.get('bakeWorkerMs')),
                'fallback': r.get('fallbackReason', ''), 'basis': r.get('basisOutcome', ''), 'budgetWait': r.get('budgetWait', ''), 'geometryWait': r.get('geometryWait', ''),
                'statsWorkerMs': f(st.get('workerMs')) if st else None, 'statsKernelMs': f(st.get('kernelMs')) if st else None,
            })
        commits = [b['commit'] for b in bodies if b['commit'] is not None]
        commit_ms = [b['commitMs'] for b in bodies if b['commitMs'] is not None]
        return {
            'round': rnd, 'requestFrame': rq, 'first': rnd == 0, 'frames': per,
            'hitMain': mains[0], 'hitAttributed': per[0]['attributed'], 'hitTryCutMs': sum(b['requestMainMs'] or 0 for b in bodies), 'hitDt': per[0]['dt'],
            # TL review 1 P2-1 / review 2 P3: the request's LOCAL MAIN TOTAL = TryCut's Main + the collect's Main of the same request (the gpu route's readyMainMs, which may fall in a later
            # frame; a synchronous route has none), bodies summed -- the cut's own Main over the frames it touches, never "the hit frame's cost"
            'localMainMs': sum((b['requestMainMs'] or 0) + (b['readyMainMs'] or 0) for b in bodies), 'invalid': self.invalid_rounds.get(rnd),
            'laterSum': sum(p['main'] for p in later) if later else None, 'laterMax': later_max['main'] if later_max else None, 'laterMaxK': later_max['k'] if later_max else None,
            'windowTotal': total, 'windowAttributed': len(attributed), 'windowPartial': len(attributed) < W, 'windowMax': max(p['main'] for p in attributed) if attributed else None,
            'maxDt': max(dts) if dts else None, 'longFrames': long_frames, 'longFramesAll': all_long,
            'commitMaxFrames': max(commits) if commits else None, 'commitMaxMs': max(commit_ms) if commit_ms else None,
            'commitPastWindow': any(c >= W for c in commits), 'commitsMissing': len([b for b in bodies if b['commit'] is None]),
            'bodies': bodies,
            'pendingAcceptance': len([b for b in bodies if b['acceptance'] == 'Pending']), 'fallbacks': len([b for b in bodies if b['fallback']]),
            'readyNotCompleted': len([b for b in bodies if self.route == 'gpu' and b['ready'] is None]),
        }


METRICS = [('windowTotal', 'window Main total (ms)'), ('hitMain', 'hit frame Main (ms)'), ('laterMax', 'largest later frame Main (ms)'), ('windowMax', 'largest Main in the window (ms)'),
           ('maxDt', 'largest frame-start interval (ms)'), ('commitMaxMs', 'last Commit (ms after the request)'), ('hitTryCutMs', 'TryCut Main at the hit, bodies summed (ms)'),
           ('localMainMs', 'local Main total per request: TryCut + collect over frames, bodies summed (ms)')]
FRAME_METRICS = ('windowTotal', 'hitMain', 'laterMax', 'windowMax', 'maxDt', 'commitMaxMs')   # the frame-level metrics, apart from the local cost
COMPARED_METRICS = [m for m in METRICS if m[0] != 'hitTryCutMs']


def group_key(run):
    return (run.platform, run.build_guid, run.route, run.bodies)


def steady_rounds(run):
    return [r for r in run.rounds if not r['first'] and not r['invalid']]


def pooled(runs, metric):
    values = []
    per_run = []
    for run in runs:
        # a PARTIAL window total (a frame without an attributed result) is not a total of the window: left out of the pooled totals (counted apart)
        v = [r[metric] for r in steady_rounds(run) if r[metric] is not None and not (metric == 'windowTotal' and r['windowPartial'])]
        values.extend(v)
        per_run.append(median(v))
    return values, per_run


def compare(groups, platform, build, bodies, a, b, metric):
    ga = groups.get((platform, build, a, bodies)); gb = groups.get((platform, build, b, bodies))
    if not ga or not gb:
        return None
    va, ra = pooled(ga, metric); vb, rb = pooled(gb, metric)
    if not va or not vb:
        return None
    ma, mb = median(va), median(vb)
    qa = quartiles(va); qb = quartiles(vb)
    spread = max([abs(x - y) for x in ra for y in ra if x is not None and y is not None] + [abs(x - y) for x in rb for y in rb if x is not None and y is not None]
                 + [qa[1] - qa[0] if None not in qa else 0, qb[1] - qb[0] if None not in qb else 0])
    diff = mb - ma
    return {'from': a, 'to': b, 'metric': metric, 'medianFrom': ma, 'medianTo': mb, 'diff': diff, 'spread': spread, 'nFrom': len(va), 'nTo': len(vb),
            'verdict': 'within the spread (no verdict)' if abs(diff) <= spread else ('lower' if diff < 0 else 'higher')}


def main(argv):
    args = [a for a in argv[1:]]
    window = 32; out = None; js = None
    folders = []
    k = 0
    while k < len(args):
        a = args[k]
        if a == '--window':
            window = int(args[k + 1]); k += 2; continue
        if a == '--out':
            out = args[k + 1]; k += 2; continue
        if a == '--json':
            js = args[k + 1]; k += 2; continue
        folders.append(a); k += 1
    if not folders:
        print(__doc__); return 2
    runs = []
    for root in folders:
        for folder in find_runs(root):
            # the run's name: the launch folder -- <root>/<name>/run (PC) or <root>/<name>/<timestamp>-... (Quest); a root that is the launch folder itself names the run
            parts = os.path.relpath(folder, root).split(os.sep)
            name = parts[0] if len(parts) >= 2 else os.path.basename(os.path.abspath(root))
            try:
                runs.append(Run(folder, name, window))
            except Exception as e:   # a run that cannot be read is listed, not skipped silently
                print('UNREADABLE', folder, e)
    lines = []
    P = lines.append
    P('# SKIN-GPU-FIRST-EVAL-1 summary (window %d frames from the request frame; round 0 = the process first, apart)' % window)
    P('')
    P('## Runs')
    P('')
    P('| run | platform | build | route | bodies | order | rounds | warm | resets settled | invalid rounds | FTM undetermined frames | FTM duplicate rows | reaction buffers |')
    P('|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    groups = OrderedDict()
    for run in runs:
        settled = sum(1 for v in run.resets.values() if v.startswith('settled')); total_resets = len(run.resets)
        P('| %s | %s | %s | %s | %d | %s | %d (%d first + %d steady%s) | %s | %d/%d | %s | %d | %d | %s |' % (
            run.name, run.platform, run.build_guid[:8], run.route, run.bodies, run.order, len(run.rounds), 1 if run.rounds else 0, len(steady_rounds(run)),
            ' + %d INVALID' % len(run.invalid_rounds) if run.invalid_rounds else '', run.warm_state(), settled, total_resets,
            ('%s (%s)' % (', '.join(str(r) for r in run.invalid_rounds), list(run.invalid_rounds.values())[0])) if run.invalid_rounds else 'none',
            len(run.undetermined_frames), run.ftm_rows_dropped, run.reaction.replace('|', '/')[:120]))
        groups.setdefault(group_key(run), []).append(run)
    P('')
    for key, members in groups.items():
        platform, build, route, bodies = key
        P('## %s (build %s) %s %d body/bodies: %s' % (platform, build[:8] or '?', route, bodies, ', '.join(r.name for r in members)))
        P('')
        P('### First round of each process (apart from the steady trials)')
        P('')
        P('| run | hit Main | hit TryCut | k0 dt | later max (k) | window total | window max | max dt | long frames (k: ms) | Commit +frames / ms | pending acc. | fallbacks | ready not completed |')
        P('|---|---|---|---|---|---|---|---|---|---|---|---|---|')
        for run in members:
            for r in run.rounds:
                if not r['first']:
                    continue
                P('| %s | %s%s | %s | %s | %s (%s) | %s%s | %s | %s | %s | %s / %s%s | %d | %d | %d |' % (
                    run.name, fmt(r['hitMain']), '' if r['hitAttributed'] else ' (undetermined)', fmt(r['hitTryCutMs']), fmt(r['hitDt']), fmt(r['laterMax']), r['laterMaxK'] if r['laterMaxK'] is not None else '-',
                    fmt(r['windowTotal']), ' PARTIAL %d/%d' % (r['windowAttributed'], window) if r['windowPartial'] else '', fmt(r['windowMax']), fmt(r['maxDt']),
                    ' '.join('%d: %.1f' % lf for lf in r['longFramesAll']) or 'none', r['commitMaxFrames'] if r['commitMaxFrames'] is not None else '-', fmt(r['commitMaxMs'], 1),
                    ' PAST WINDOW' if r['commitPastWindow'] else (' MISSING %d' % r['commitsMissing'] if r['commitsMissing'] else ''), r['pendingAcceptance'], r['fallbacks'], r['readyNotCompleted']))
        P('')
        P('### Steady rounds pooled over the launches (median [min .. max], n rounds; per-launch medians apart)')
        P('')
        P('| metric | pooled median | min .. max | p25 .. p75 | n | per-launch medians |')
        P('|---|---|---|---|---|---|')
        for metric, label in METRICS:
            values, per_run = pooled(members, metric)
            if not values:
                P('| %s | - | - | - | 0 | - |' % label); continue
            q = quartiles(values)
            P('| %s | %s | %s .. %s | %s .. %s | %d | %s |' % (label, fmt(median(values)), fmt(min(values)), fmt(max(values)), fmt(q[0]), fmt(q[1]), len(values), ' / '.join(fmt(m) for m in per_run)))
        partial = sum(1 for run in members for r in steady_rounds(run) if r['windowPartial'])
        past = sum(1 for run in members for r in steady_rounds(run) if r['commitPastWindow'])
        missing = sum(r['commitsMissing'] for run in members for r in steady_rounds(run))
        longs = [(run.name, r['round'], lf) for run in members for r in steady_rounds(run) for lf in r['longFramesAll']]
        P('')
        P('- steady rounds with a PARTIAL window total (a frame without an attributed FTM result): %d; Commits past the window: %d; Commits not reported: %d; frames over %.0f ms: %s' % (
            partial, past, missing, LONG_FRAME_MS, ' '.join('%s r%d k%d %.1f' % (n, rd, lf[0], lf[1]) for n, rd, lf in longs) or 'none'))
        P('- acceptance Pending (steady): %d; fallbacks: %d; gpu ready not completed: %d' % (
            sum(r['pendingAcceptance'] for run in members for r in steady_rounds(run)), sum(r['fallbacks'] for run in members for r in steady_rounds(run)), sum(r['readyNotCompleted'] for run in members for r in steady_rounds(run))))
        P('')
        P('### Per-request reaction (steady rounds, all bodies; frames after the request, median [min .. max]; ms = median of the hook time after request-begin)')
        P('')
        P('| event | frames | ms | n |')
        P('|---|---|---|---|')
        for ev, label in [('accepted', 'accepted (TryCut returned Requested)'), ('physics', 'provisional physics published (driver hook)'), ('ledger', 'ledger published (hook)'),
                          ('clipStep', 'clip state observed at the step'), ('clipBeforeRender', 'clip state observed at onBeforeRender'), ('ready', 'CPU ready = collect completed (hook)'),
                          ('readyEnded', 'collect ended otherwise (failed/dropped/...)'), ('commit', 'committed (DAG hook)')]:
            fr = [b[ev] for run in members for r in steady_rounds(run) for b in r['bodies'] if b[ev] is not None]
            msk = ev + 'Ms'
            msv = [b[msk] for run in members for r in steady_rounds(run) for b in r['bodies'] if msk in b and b[msk] is not None]
            P('| %s | %s | %s | %d |' % (label, ('%s [%s .. %s]' % (fmt(median(fr), 0), min(fr), max(fr))) if fr else '- (not reported/observed)', fmt(median(msv), 1) if msv else '-', len(fr)))
        worker = [b['bakeWorkerMs'] for run in members for r in steady_rounds(run) for b in r['bodies'] if b['bakeWorkerMs'] is not None]
        kernel = [b['cutKernelMs'] for run in members for r in steady_rounds(run) for b in r['bodies'] if b['cutKernelMs'] is not None]
        collect = [b['readyMainMs'] for run in members for r in steady_rounds(run) for b in r['bodies'] if b['readyMainMs'] is not None]
        P('')
        P('- worker times (not Main): CPU bake worker %s ms (n %d), cut kernel %s ms (n %d); collect Main %s ms (n %d) -- medians' % (fmt(median(worker), 3), len(worker), fmt(median(kernel), 3), len(kernel), fmt(median(collect), 3), len(collect)))
        waits = set(b['budgetWait'] for run in members for r in steady_rounds(run) for b in r['bodies']); gw = set(b['geometryWait'] for run in members for r in steady_rounds(run) for b in r['bodies'])
        P('- budget waits: %s; geometry waits: %s' % ('; '.join(sorted(waits)), '; '.join(sorted(gw))))
        P('')
    # comparisons
    P('## Comparisons (pooled steady medians; "within the spread" = |difference| <= the larger of the launches\' median gap and the rounds\' interquartile range; no verdict here)')
    P('')
    P('| platform | build | bodies | comparison | metric | kind | from | to | difference | spread | n | reading |')
    P('|---|---|---|---|---|---|---|---|---|---|---|---|')
    comparisons = []
    platform_builds = sorted(set((k[0], k[1]) for k in groups)); body_counts = sorted(set(k[3] for k in groups))
    for platform, build in platform_builds:
        for bodies in body_counts:
            for a, b in [('cpu', 'float'), ('float', 'gpu'), ('cpu', 'gpu')]:
                for metric, label in COMPARED_METRICS:
                    c = compare(groups, platform, build, bodies, a, b, metric)
                    if c is None:
                        continue
                    kind = 'frame' if metric in FRAME_METRICS else 'local'
                    comparisons.append(dict(c, platform=platform, build=build, bodies=bodies, kind=kind))
                    P('| %s | %s | %d | %s -> %s | %s | %s | %s | %s | %+.2f | %.2f | %d/%d | %s |' % (platform, build[:8] or '?', bodies, a, b, label, kind, fmt(c['medianFrom']), fmt(c['medianTo']), c['diff'], c['spread'], c['nFrom'], c['nTo'], c['verdict']))
    P('')
    P('Notes: Main values are FrameTimingManager main-thread times of the frame whose start is nearest a frame-start mark (|offset| <= %.1f ms); worker and GPU times are never added. ' % ATTRIBUTION_MS
      + 'A clip observation is a display state at the observing point, not a pixel; "-" means not observed and claims nothing about what was displayed. Round 0 is the process first (after the launch\'s warm, if any) and is never pooled with the steady rounds; a PARTIAL window total is left out of the pooled window totals. '
      + 'A round after a reset that did not return to the initial state is INVALID and left out of the steady pooling. The "local" metric is the request\'s local Main TOTAL (TryCut + the collect\'s Main, which may fall in a later frame; bodies summed) -- the cut\'s own Main over the frames it touches, not a cost of the hit frame; the "frame" metrics are the frame-level readings, read apart: a lower local total is not a frame-level improvement. '
      + 'A frame\'s interval is the one that begins at its start (the next frame\'s dtInitMs): the window holds the intervals of its own frames, none before the hit.')
    text = '\n'.join(lines) + '\n'
    if out:
        with open(out, 'w', encoding='utf-8', newline='\n') as h:
            h.write(text)
    else:
        sys.stdout.buffer.write(text.encode('utf-8'))
    if js:
        with open(js, 'w', encoding='utf-8') as h:
            json.dump({'window': window, 'runs': [{'name': r.name, 'platform': r.platform, 'build': r.build_guid, 'route': r.route, 'bodies': r.bodies, 'order': r.order, 'warm': r.warm_state(),
                                                     'undeterminedFrames': sorted(r.undetermined_frames), 'invalidRounds': dict((str(k), v) for k, v in r.invalid_rounds.items()), 'rounds': r.rounds} for r in runs], 'comparisons': comparisons}, h, indent=1)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
