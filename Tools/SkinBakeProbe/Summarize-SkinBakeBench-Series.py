"""SKIN-BAKE-PROBE-3: the bench series runs of the skin bake probe summarised (DESIGN 4.5.14).

Usage: Summarize-SkinBakeBench-Series.py <run folder>... [--out <file.md>]

A run folder holds skin-bake-probe.json (the facts: build, bench, render settings, equality cases), skin-bake-bench.csv
(the measured frames) and skin-bake-bench-summary.csv (n, median, p95, mean, min, max a condition and column). Results are
grouped by build (platform, build kind, buildGUID) and by condition (route, bodies, repeat): a table never mixes a request
count or a build. For each group: the local sections (kernel, bounds, upload, indices, issue, total), the frame's whole
times (ftMain, ftRender, ftGpu, ftPresentWait, xrAppGpu) with their n, the equality verdicts, the Burst entry, the owner
writes. Then, within a build: each route's difference from 'none' of the same bodies in ftMain / ftGpu / xrAppGpu medians,
and the GPU time's slope over the gpu repeat conditions (ftGpu or xrAppGpu median vs. dispatches a frame). The GPU
execution time of one bake is never read from the issue time; where no repeat conditions exist it is reported unmeasured.
"""
import csv, json, os, sys
from collections import OrderedDict


def median(v):
    s = sorted(v); n = len(s)
    return float('nan') if n == 0 else (s[n // 2] if n % 2 else 0.5 * (s[n // 2 - 1] + s[n // 2]))


def load(folder):
    record = json.load(open(os.path.join(folder, 'skin-bake-probe.json'), encoding='utf-8'))
    facts = record.get('facts', {})
    summary = list(csv.DictReader(open(os.path.join(folder, 'skin-bake-bench-summary.csv'), encoding='utf-8')))
    build = facts.get('build', '?')
    build_key = build.split('; unity')[0]
    conditions = OrderedDict()
    for r in summary:
        key = (r['condition'], r['route'], int(r['bodies']), int(r['repeat']))
        conditions.setdefault(key, {})[r['column']] = r
    # the frame rows: how many distinct values a condition's GPU-time column holds, and the median run of equal consecutive values
    # (the XR display's app GPU time repeats over ~1 s, so n frames are not n independent GPU samples)
    distinct = {}
    frames_path = os.path.join(folder, 'skin-bake-bench.csv')
    if os.path.exists(frames_path):
        per = OrderedDict()
        for r in csv.DictReader(open(frames_path, encoding='utf-8-sig')):
            for col in ('ftGpuMs', 'xrAppGpuMs'):
                if r.get(col, '') != '':
                    per.setdefault((r['condition'], col), []).append(r[col])
        for (cond, col), vals in per.items():
            runs = []; prev = None; n = 0
            for v in vals:
                if v == prev:
                    n += 1
                else:
                    if prev is not None: runs.append(n)
                    prev = v; n = 1
            if prev is not None: runs.append(n)
            distinct[(cond, col)] = (len(set(vals)), len(vals), median(runs), max(runs))
    cases = {}
    for r in record.get('rows', []):
        if r.get('subject', '').startswith('bench '):
            cases.setdefault(r['subject'][6:], []).append((r['case'], r['pass'], r['kind']))
    return {'folder': folder, 'name': os.path.basename(folder.rstrip('/\\')), 'build': build, 'build_key': build_key, 'facts': facts, 'conditions': conditions, 'cases': cases, 'distinct': distinct}


def cell(stats, column, what='median'):
    r = stats.get(column)
    if not r or not r.get(what):
        return '-'
    return r[what]


def mp(stats, column):
    r = stats.get(column)
    if not r or not r.get('median'):
        return '-'
    return '%s / %s (n %s)' % (r['median'], r['p95'], r['n'])


def main(argv):
    out = None; folders = []
    i = 0
    while i < len(argv):
        if argv[i] == '--out':
            out = argv[i + 1]; i += 2
        else:
            folders.append(argv[i]); i += 1
    runs = [load(f) for f in folders]
    lines = ['# Skin bake bench series summary', '', 'The bake\'s own GPU execution time is not measured directly in any run; the gpu repeat conditions give its slope from the frame\'s GPU time. Local sections are the main thread\'s. A frame dt fixed to the XR period is not an improvement measure.', '']
    builds = OrderedDict()
    for r in runs:
        builds.setdefault(r['build_key'], []).append(r)
    for build_key, group in builds.items():
        lines += ['## Build: %s' % build_key, '']
        for r in group:
            lines.append('- run %s: bench %s' % (r['name'], r['facts'].get('bench', '?')))
            lines.append('  - render settings: %s' % r['facts'].get('render settings', '?'))
            lines.append('  - frame timing: %s' % r['facts'].get('bench frame timing', '?'))
            lines.append('  - owner writes: %s' % r['facts'].get('bench owner writes', '?'))
            if 'bench product preparation' in r['facts']:
                lines.append('  - product: %s' % r['facts']['bench product preparation'])
        lines += ['', '| run | condition | route | bodies | repeat | kernel med/p95 | bounds | upload | indices | issue | total | ftMain | ftRender | ftGpu | ftPresentWait | xrAppGpu | frame dt | equality | notes |',
                  '|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|']
        for r in group:
            for (cond, route, bodies, repeat), stats in r['conditions'].items():
                cases = r['cases'].get(cond, [])
                eq = ', '.join('%s %s' % (c, p) for c, p, k in cases if k == 'bake') or '-'
                notes = '; '.join(x for x in [r['facts'].get('bench %s burst ran' % cond, ''), r['facts'].get('bench %s product' % cond, '')] if x)
                lines.append('| %s | %s | %s | %d | %d | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |' % (
                    r['name'], cond, route, bodies, repeat, mp(stats, 'kernelMs'), mp(stats, 'boundsMs'), mp(stats, 'uploadMs'), mp(stats, 'indicesMs'), mp(stats, 'issueMs'), mp(stats, 'totalMs'),
                    mp(stats, 'ftMainMs'), mp(stats, 'ftRenderMs'), mp(stats, 'ftGpuMs'), mp(stats, 'ftPresentWaitMs'), mp(stats, 'xrAppGpuMs'), mp(stats, 'frameDtMs'), eq, notes))
        # the GPU-time columns' distinct values a condition (the frame rows are not independent GPU samples where values repeat)
        lines += ['', '### Distinct GPU-time values a condition (distinct / rows; median and longest run of equal consecutive values)', '']
        for r in group:
            parts = []
            for (cond, route, bodies, repeat), stats in r['conditions'].items():
                for col in ('ftGpuMs', 'xrAppGpuMs'):
                    d = r['distinct'].get((cond, col))
                    if d:
                        parts.append('%s %s %d/%d (run %.0f, max %d)' % (cond, col, d[0], d[1], d[2], d[3]))
            lines.append('- run %s: %s' % (r['name'], '; '.join(parts) if parts else 'no GPU-time column'))
        # the routes against none, by bodies, across the group's runs (medians of the run medians)
        lines += ['', '### Against none (same build, same bodies; median of the runs\' condition medians)', '', '| bodies | route | runs | total ms | ftMain ms (route - none) | ftGpu ms (route - none) | xrAppGpu ms (route - none) |', '|---|---|---|---|---|---|---|']
        by = {}
        for r in group:
            for (cond, route, bodies, repeat), stats in r['conditions'].items():
                if repeat != 1:
                    continue
                for col in ('totalMs', 'ftMainMs', 'ftGpuMs', 'xrAppGpuMs'):
                    v = cell(stats, col)
                    if v != '-':
                        by.setdefault((bodies, route, col), []).append(float(v))
        bodies_set = sorted({b for (b, _, _) in by})
        routes = ['none', 'product', 'burst', 'gpu']
        for b in bodies_set:
            for route in routes:
                if (b, route, 'ftMainMs') not in by and (b, route, 'totalMs') not in by:
                    continue
                def diff(col):
                    if (b, route, col) not in by:
                        return '-'
                    m = median(by[(b, route, col)])
                    if route == 'none' or (b, 'none', col) not in by:
                        return '%.3f' % m
                    return '%.3f (%+.3f)' % (m, m - median(by[(b, 'none', col)]))
                total = '%.4f' % median(by[(b, route, 'totalMs')]) if (b, route, 'totalMs') in by else '-'
                lines.append('| %d | %s | %d | %s | %s | %s | %s |' % (b, route, len(by.get((b, route, 'ftMainMs'), by.get((b, route, 'totalMs'), []))), total, diff('ftMainMs'), diff('ftGpuMs'), diff('xrAppGpuMs')))
        # the GPU slope over the gpu repeat conditions
        slope = {}
        for r in group:
            for (cond, route, bodies, repeat), stats in r['conditions'].items():
                if route != 'gpu':
                    continue
                for col in ('ftGpuMs', 'xrAppGpuMs'):
                    v = cell(stats, col)
                    if v != '-':
                        slope.setdefault((bodies, col), {}).setdefault(bodies * repeat, []).append(float(v))
        lines += ['', '### The GPU time\'s slope (gpu conditions: dispatches a frame vs. the frame\'s GPU median)', '']
        any_slope = False
        for (bodies, col), points in sorted(slope.items()):
            xs = sorted(points)
            if len(xs) < 2:
                continue
            any_slope = True
            ys = [median(points[x]) for x in xs]
            n = len(xs); sx = sum(xs); sy = sum(ys); sxx = sum(x * x for x in xs); sxy = sum(x * y for x, y in zip(xs, ys))
            den = n * sxx - sx * sx
            k = (n * sxy - sx * sy) / den if den else float('nan')
            lines.append('- %s, bodies %d: points %s -> slope %.4f ms a dispatch (least squares over the condition medians; the frame\'s other GPU work assumed unchanged)' % (col, bodies, ', '.join('%d: %.3f' % (x, y) for x, y in zip(xs, ys)), k))
        if not any_slope:
            lines.append('- no repeat conditions: the bake\'s GPU execution time is unmeasured here')
        lines.append('')
    text = '\n'.join(lines) + '\n'
    if out:
        open(out, 'w', encoding='utf-8', newline='\n').write(text)
    sys.stdout.write(text)


if __name__ == '__main__':
    main(sys.argv[1:])
