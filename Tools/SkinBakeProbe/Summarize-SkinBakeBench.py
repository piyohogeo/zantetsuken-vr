"""SKIN-BAKE-PROBE-2: the bench runs of the skin bake probe summarised (DESIGN 4.5.14).

Usage: Summarize-SkinBakeBench.py <run folder>... [--out <file.md>]

A run folder holds skin-bake-probe.json (the facts, the equality cases) and skin-bake-bench.csv (the measured frames: the
sums over a frame's bodies, milliseconds). For each run: the route, the bodies, the build, the device, n, the median and the
p95 (nearest rank) of kernel / bounds / upload / issue / total / frame dt, the equality verdicts, the Burst entry. Then the
routes side by side in launch order (A/B then B/A), the medians of the totals and their difference. The GPU execution time
is not in any column: it was not measured. Nothing here estimates it from the issue time.
"""
import csv, json, os, sys


def nearest_rank(values, q):
    if not values:
        return float('nan')
    s = sorted(values)
    k = max(1, min(len(s), int(-(-q * len(s) // 1))))
    return s[k - 1]


def median(values):
    if not values:
        return float('nan')
    s = sorted(values)
    n = len(s)
    return s[n // 2] if n % 2 else 0.5 * (s[n // 2 - 1] + s[n // 2])


def load(folder):
    record = json.load(open(os.path.join(folder, 'skin-bake-probe.json'), encoding='utf-8'))
    rows = list(csv.DictReader(open(os.path.join(folder, 'skin-bake-bench.csv'), encoding='utf-8')))
    facts = record.get('facts', {})
    bench = facts.get('bench', '')
    route = bench.split(';')[0].replace('route', '').strip() if bench else '?'
    bodies = rows[0]['bodies'] if rows else '?'
    cols = {}
    for c in ('kernelMs', 'boundsMs', 'uploadMs', 'issueMs', 'totalMs', 'frameDtMs'):
        v = [float(r[c]) for r in rows]
        cols[c] = (len(v), median(v), nearest_rank(v, 0.95), (sum(v) / len(v)) if v else float('nan'), min(v) if v else float('nan'), max(v) if v else float('nan'))
    equality = [(r['case'], r['pass']) for r in record.get('rows', []) if r.get('subject') == 'bench' and r.get('kind') == 'bake']
    burst_ran = all(r['burstRan'] == 'true' for r in rows) if rows and route == 'burst' else None
    return {'folder': folder, 'route': route, 'bodies': bodies, 'platform': facts.get('platform', ''), 'device': facts.get('device', ''), 'n': len(rows),
            'cols': cols, 'equality': equality, 'burst_ran': burst_ran, 'title': record.get('title', '')}


def main(argv):
    out = None
    folders = []
    i = 0
    while i < len(argv):
        if argv[i] == '--out':
            out = argv[i + 1]; i += 2
        else:
            folders.append(argv[i]); i += 1
    runs = [load(f) for f in folders]
    lines = ['# Skin bake bench summary', '', 'GPU execution time: NOT MEASURED in any run (no column; the issue time is the main thread\'s, not an estimate of it).', '',
             '| run | route | bodies | build | n | kernel med/p95 | bounds med/p95 | upload med/p95 | issue med/p95 | total med/p95 | frame dt med/p95 | equality | Burst entry |',
             '|---|---|---|---|---|---|---|---|---|---|---|---|---|']
    def mp(c):
        n, med, p95, mean, lo, hi = c
        return '-' if n == 0 or med != med else '%.4f / %.4f' % (med, p95)
    for r in runs:
        eq = ', '.join('%s %s' % (c, p) for c, p in r['equality']) or 'none'
        lines.append('| %s | %s | %s | %s | %d | %s | %s | %s | %s | %s | %s | %s | %s |' % (
            os.path.basename(r['folder'].rstrip('/\\')), r['route'], r['bodies'], r['platform'], r['n'], mp(r['cols']['kernelMs']), mp(r['cols']['boundsMs']), mp(r['cols']['uploadMs']),
            mp(r['cols']['issueMs']), mp(r['cols']['totalMs']), mp(r['cols']['frameDtMs']), eq, 'ran' if r['burst_ran'] else ('-' if r['burst_ran'] is None else 'MANAGED FALLBACK')))
    lines += ['', '## The routes side by side (launch order)', '', '| launch | route | total median ms | total p95 ms | n |', '|---|---|---|---|---|']
    for k, r in enumerate(runs):
        c = r['cols']['totalMs']
        lines.append('| %d | %s | %.4f | %.4f | %d |' % (k + 1, r['route'], c[1], c[2], c[0]))
    gpu = [r['cols']['totalMs'][1] for r in runs if r['route'] == 'gpu']
    burst = [r['cols']['totalMs'][1] for r in runs if r['route'] == 'burst']
    if gpu and burst:
        lines += ['', 'median of the run medians: gpu issue %.4f ms (%d runs), burst kernel+upload %.4f ms (%d runs); burst - gpu = %.4f ms (main thread only; the GPU execution of the gpu route is not in this number)'
                  % (median(gpu), len(gpu), median(burst), len(burst), median(burst) - median(gpu))]
    text = '\n'.join(lines) + '\n'
    if out:
        open(out, 'w', encoding='utf-8', newline='\n').write(text)
    sys.stdout.write(text)


if __name__ == '__main__':
    main(sys.argv[1:])
