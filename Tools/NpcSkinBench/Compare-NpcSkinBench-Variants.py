# -*- coding: utf-8 -*-
"""SKIN-BENCH-3: the A/B of the skin draw's variants within one route -- the palette gathering (sync / worker) and the skin casters'
slice selection (off / on) -- from the launches' bench-result.json under one or more roots (PC: <root>/<launch>/run/, Quest:
<root>/<launch>/). A launch is adopted when it is valid and its comparison holds. Launches are grouped by condition (count, pose,
shadows, update count, route) and, within it, by variant (gather actual, skin caster slice selection); the table gives each variant's
medians (over its launches' medians) of Main, GPU, XR app GPU and the skin spans, and the difference of every variant against the
first (the baseline, named by -Baseline: 'sync/False' by default). Usage: Compare-NpcSkinBench-Variants.py [-Baseline sync/False] <root>...
"""
import glob, io, json, os, statistics, sys

def med(xs):
    xs = [x for x in xs if x is not None]
    return statistics.median(xs) if xs else None

def stat(r, key, field='median'):
    v = r.get(key)
    return v.get(field) if isinstance(v, dict) else None

def f(v, p=3):
    return '-' if v is None else '%.*f' % (p, v)

def d(a, b, p=3):
    return '-' if a is None or b is None else '%+.*f' % (p, b - a)

def main():
    args = sys.argv[1:]
    baseline = 'sync/False'
    if args and args[0] == '-Baseline':
        baseline = args[1]; args = args[2:]
    runs = []
    for root in args:
        for path in glob.glob(os.path.join(root, '*', 'run', 'bench-result.json')) + glob.glob(os.path.join(root, '*', 'bench-result.json')):
            try:
                r = json.load(io.open(path, encoding='utf-8'))
            except Exception as e:
                print('unreadable:', path, e); continue
            r['_name'] = os.path.relpath(path, root).split(os.sep)[0]
            r['_root'] = root
            runs.append(r)
    groups = {}
    excluded = []
    for r in runs:
        if r.get('valid') is not True or r.get('comparisonValid') is not True:
            excluded.append('%s: valid %s, comparison %s [%s]' % (r['_name'], r.get('valid'), r.get('comparisonValid'), '; '.join((r.get('invalidities') or []) + (r.get('comparisonIssues') or [])))); continue
        cond = (r.get('individuals'), r.get('pose'), r.get('shadows'), r.get('updateCount'), r.get('requestedRoute'))
        variant = '%s/%s' % (r.get('skinGatherActual') or '-', r.get('skinCasterSliceSelection'))
        if r.get('requestedRoute') == 'SMR': variant = 'smr'
        groups.setdefault(cond, {}).setdefault(variant, []).append(r)
    cols = [('Main', 'ftMainMs'), ('GPU', 'ftGpuMs'), ('XR GPU', 'xrAppGpuMs'), ('skin frame', 'skinFrameMs'), ('gather', 'skinGatherMs'), ('prepare', 'skinPrepareMs'),
            ('schedule', 'skinScheduleMs'), ('complete wait', 'skinCompleteWaitMs'), ('publish', 'skinPublishMs'), ('upload', 'skinUploadMs')]
    print('# Variants by condition (medians of the adopted launches\' medians, ms). Baseline: ' + baseline)
    print()
    print('| count | pose | shadows | update count | route | variant (gather/skin slice) | launches | ' + ' | '.join(c[0] for c in cols) + ' | jobs | late regathers | job failures | GPU clock | Main diff vs baseline | GPU diff | XR GPU diff |')
    print('|' + '---|' * (6 + 1 + len(cols) + 4 + 3))
    for cond in sorted(groups, key=lambda c: tuple(str(x) for x in c)):
        variants = groups[cond]
        base = variants.get(baseline) or (variants.get('smr') if 'smr' in variants else None)
        bm = {k: med([stat(r, k) for r in base]) for _, k in cols} if base else {}
        for variant in sorted(variants, key=lambda v: (v != baseline, v)):
            rs = variants[variant]
            m = {k: med([stat(r, k) for r in rs]) for _, k in cols}
            jobs = med([r.get('jobsScheduledInMeasurement') for r in rs]); late = med([r.get('lateRegathersInMeasurement') for r in rs]); fails = med([r.get('jobFailuresInMeasurement') for r in rs])
            clocks = sorted({str(r.get('gpuClockMHz')) for r in rs if r.get('gpuClockMHz') is not None}) or ['-']
            print('| %s | %s | %s | %s | %s | %s | %d | %s | %s | %s | %s | %s | %s | %s | %s |' % (
                cond[0], cond[1], cond[2], cond[3], cond[4], variant, len(rs), ' | '.join(f(m[k]) for _, k in cols), f(jobs, 0), f(late, 0), f(fails, 0), '/'.join(clocks),
                d(bm.get('ftMainMs'), m['ftMainMs']) if base and variant != baseline else '-', d(bm.get('ftGpuMs'), m['ftGpuMs']) if base and variant != baseline else '-',
                d(bm.get('xrAppGpuMs'), m['xrAppGpuMs']) if base and variant != baseline else '-'))
    print()
    print('## Per launch')
    print()
    print('| launch | route | variant | Main | GPU | XR GPU | gather | complete wait | publish | upload | jobs | late | fails | update count |')
    print('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    for r in sorted(runs, key=lambda r: r['_name']):
        variant = 'smr' if r.get('requestedRoute') == 'SMR' else '%s/%s' % (r.get('skinGatherActual') or '-', r.get('skinCasterSliceSelection'))
        print('| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |' % (
            r['_name'], r.get('requestedRoute'), variant, f(stat(r, 'ftMainMs')), f(stat(r, 'ftGpuMs')), f(stat(r, 'xrAppGpuMs')), f(stat(r, 'skinGatherMs')), f(stat(r, 'skinCompleteWaitMs')),
            f(stat(r, 'skinPublishMs')), f(stat(r, 'skinUploadMs')), r.get('jobsScheduledInMeasurement'), r.get('lateRegathersInMeasurement'), r.get('jobFailuresInMeasurement'), r.get('updateCount')))
    if excluded:
        print()
        print('## Excluded')
        print()
        for e in excluded: print('- ' + e)

if __name__ == '__main__':
    main()
