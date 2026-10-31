"""The NPC skin benchmark's table (DESIGN 3.4, npc-skin-bench): every bench-result.json under a run root, one row a launch,
sorted by count, pose, shadows, route and repeat; then the paired comparison by condition (Skin24 minus SMR of the medians of
the launches that are valid AND whose comparison holds). Written as summary.csv and summary.md beside the runs.

Two validities stand apart (SKIN-BENCH-2 section 2): `valid` (the route and the resources: no fallback, no refusal, no growth)
and `comparison` (the eye within the tolerances, the rows in view, the frames asked measured) -- and, across the two routes of a
condition, the A/B start poses within the tolerances. A launch that fails either is listed and left out of the comparison,
with the reason. A Profiler diagnosis launch (profilerDiagnosis true: the Profiler's own load is in its frames) is listed
too and left out of every comparison (TL review, 2026-10-31).

Usage: Summarize-NpcSkinBench.py <run root> [--out <folder>]"""
import io, json, os, sys, glob, math, statistics

def load(root):
    rows = []
    for path in sorted(glob.glob(os.path.join(root, '*', 'run', 'bench-result.json'))) + sorted(glob.glob(os.path.join(root, '*', 'bench-result.json'))):
        try:
            with io.open(path, encoding='utf-8') as f:
                r = json.load(f)
        except Exception as e:  # noqa: BLE001 - a broken file is reported as a row, not swallowed
            rows.append({'name': os.path.basename(os.path.dirname(os.path.dirname(path))), 'error': str(e), 'path': path})
            continue
        r['name'] = os.path.basename(os.path.dirname(os.path.dirname(path))) if os.path.basename(os.path.dirname(path)) == 'run' else os.path.basename(os.path.dirname(path))
        r['path'] = path
        rows.append(r)
    return rows

def stat(r, key, field='median'):
    s = r.get(key)
    if not isinstance(s, dict) or s.get(field) is None:
        return None
    return float(s[field])

def fmt(v, digits=3):
    return '' if v is None else ('%.*f' % (digits, v))

COLUMNS = ['name', 'requestedRoute', 'actualRoute', 'valid', 'comparison', 'profilerDiagnosis', 'count', 'pose', 'shadows', 'individuals', 'models', 'vertices', 'bones', 'framesMeasured', 'steps',
           'gpu_med', 'gpu_p95', 'gpu_n', 'xrgpu_med', 'xrgpu_p95', 'xrgpu_n', 'main_med', 'main_p95', 'main_n', 'render_med', 'render_p95', 'render_n', 'present_med', 'present_n', 'dt_med',
           'poseEval_med', 'poseApply_med', 'skinFrame_med', 'skinPalette_med', 'skinGather_med', 'skinUpload_med', 'skinUploadPalette_med', 'paletteWrites_med',
           'gather', 'gatherActual', 'skinSlice', 'updateCount', 'skinPrepare_med', 'skinSchedule_med', 'skinCompleteWait_med', 'skinPublish_med', 'jobs', 'lateSchedules', 'lateRegathers', 'jobFailures',
           'posesApplied', 'palettesGathered', 'paletteWrites', 'paletteBytes', 'paletteBytesExtra', 'paletteJoinGap', 'skinBodies', 'skinCasters', 'skinMotion', 'bodies', 'casters', 'motion', 'selections', 'dispatches',
           'visibleSkin', 'visibleRenderers', 'eyeStart', 'eyeMaxMove', 'eyeMaxTurn', 'fixedView', 'rigMaxMove', 'rigMaxTurn', 'cascades', 'shadowDistance', 'shadowResolution', 'soft', 'longFrames', 'invalidities', 'comparisonIssues']

def row_of(r):
    if 'error' in r:
        return {'name': r['name'], 'invalidities': 'UNREADABLE: ' + r['error']}
    rend = r.get('rendering') or {}
    e = r.get('eyeStart') or {}
    return {
        'name': r['name'], 'requestedRoute': r.get('requestedRoute'), 'actualRoute': r.get('actualRoute'), 'valid': r.get('valid'), 'comparison': r.get('comparisonValid'),
        'profilerDiagnosis': r.get('profilerDiagnosis'),
        'count': r.get('count'), 'pose': r.get('pose'), 'shadows': r.get('shadows'), 'individuals': r.get('individuals'), 'models': r.get('models'),
        'vertices': r.get('vertices'), 'bones': r.get('bones'), 'framesMeasured': r.get('framesMeasured'), 'steps': '%s..%s' % (r.get('measureStepStart'), r.get('measureStepEnd')),
        'gpu_med': stat(r, 'ftGpuMs'), 'gpu_p95': stat(r, 'ftGpuMs', 'p95'), 'gpu_n': stat(r, 'ftGpuMs', 'count'),
        'xrgpu_med': stat(r, 'xrAppGpuMs'), 'xrgpu_p95': stat(r, 'xrAppGpuMs', 'p95'), 'xrgpu_n': stat(r, 'xrAppGpuMs', 'count'),
        'main_med': stat(r, 'ftMainMs'), 'main_p95': stat(r, 'ftMainMs', 'p95'), 'main_n': stat(r, 'ftMainMs', 'count'),
        'render_med': stat(r, 'ftRenderMs'), 'render_p95': stat(r, 'ftRenderMs', 'p95'), 'render_n': stat(r, 'ftRenderMs', 'count'),
        'present_med': stat(r, 'ftPresentWaitMs'), 'present_n': stat(r, 'ftPresentWaitMs', 'count'), 'dt_med': stat(r, 'unscaledDeltaMs'),
        'poseEval_med': stat(r, 'poseEvaluateMs'), 'poseApply_med': stat(r, 'poseApplyMs'), 'skinFrame_med': stat(r, 'skinFrameMs'), 'skinPalette_med': stat(r, 'skinPaletteMs'),
        'skinGather_med': stat(r, 'skinGatherMs'), 'skinUpload_med': stat(r, 'skinUploadMs'), 'skinUploadPalette_med': stat(r, 'skinUploadPaletteMs'), 'paletteWrites_med': stat(r, 'paletteWritesPerFrame'),
        'gather': r.get('skinGatherAsked'), 'gatherActual': r.get('skinGatherActual'), 'skinSlice': r.get('skinCasterSliceSelection'), 'updateCount': r.get('updateCount'),
        'skinPrepare_med': stat(r, 'skinPrepareMs'), 'skinSchedule_med': stat(r, 'skinScheduleMs'), 'skinCompleteWait_med': stat(r, 'skinCompleteWaitMs'), 'skinPublish_med': stat(r, 'skinPublishMs'),
        'jobs': r.get('jobsScheduledInMeasurement'), 'lateSchedules': r.get('lateSchedulesInMeasurement'), 'lateRegathers': r.get('lateRegathersInMeasurement'), 'jobFailures': r.get('jobFailuresInMeasurement'),
        'posesApplied': r.get('posesAppliedInMeasurement'), 'palettesGathered': r.get('palettesGatheredInMeasurement'), 'paletteWrites': r.get('paletteWritesInMeasurement'),
        'paletteBytes': r.get('paletteBytesInMeasurement'), 'paletteBytesExtra': r.get('paletteBytesExtraInMeasurement'), 'paletteJoinGap': r.get('paletteJoinGap'),
        'skinBodies': r.get('skinBodiesIssuedInMeasurement'), 'skinCasters': r.get('skinCastersIssuedInMeasurement'), 'skinMotion': r.get('skinMotionIssuedInMeasurement'),
        'bodies': r.get('bodiesIssuedInMeasurement'), 'casters': r.get('castersIssuedInMeasurement'), 'motion': r.get('motionIssuedInMeasurement'),
        'selections': r.get('selectionsIssuedInMeasurement'), 'dispatches': r.get('boundsDispatchesInMeasurement'),
        'visibleSkin': r.get('visibleBySkinSelectionAtEnd'), 'visibleRenderers': r.get('visibleRenderersAtEnd'),
        'eyeStart': ('(%s %s %s) yaw %s pitch %s' % (tuple(fmt(e.get(k), 3) for k in ('x', 'y', 'z')) + tuple(fmt(e.get(k), 2) for k in ('yaw', 'pitch')))) if e else '',
        'eyeMaxMove': r.get('eyeMaxMoveMetres'), 'eyeMaxTurn': r.get('eyeMaxTurnDegrees'), 'fixedView': r.get('fixedView'), 'rigMaxMove': r.get('rigMaxMoveMetres'), 'rigMaxTurn': r.get('rigMaxTurnDegrees'),
        'cascades': rend.get('m_ShadowCascadeCount'), 'shadowDistance': rend.get('m_ShadowDistance'), 'shadowResolution': rend.get('m_MainLightShadowmapResolution'), 'soft': rend.get('m_SoftShadowsSupported'),
        'longFrames': len(r.get('longFramesOver100ms') or []), 'invalidities': '; '.join(r.get('invalidities') or []), 'comparisonIssues': '; '.join(r.get('comparisonIssues') or []),
        '_eye': e, '_tolM': r.get('toleranceMetres'), '_tolD': r.get('toleranceDegrees'),
    }

def cell(v):
    if v is None:
        return ''
    if isinstance(v, float):
        return '%.3f' % v
    return str(v)

def angle_apart(a, b):
    """The smaller angle between two headings in degrees (359.9 and 0.1 are 0.2 apart)."""
    x = abs(float(a) - float(b)) % 360.0
    return 360.0 - x if x > 180.0 else x

def pose_apart(a, b, tol_m, tol_d):
    """The distance and the largest per-axis angle between two start poses; None when either is missing. Each axis is
    wrapped on its own before the largest is taken (TL review, 2026-10-31: wrapping the largest raw difference let
    (359.9, 0.9) against (0.1, 359.1) pass as 0.2 degrees while the pitch is 1.8 apart)."""
    if not a or not b:
        return None, None, None
    d = math.sqrt(sum((float(a.get(k, 0)) - float(b.get(k, 0))) ** 2 for k in ('x', 'y', 'z')))
    ang = max(angle_apart(a.get(k, 0), b.get(k, 0)) for k in ('yaw', 'pitch', 'roll'))
    return d, ang, (d <= (tol_m or 0.01) and ang <= (tol_d or 1.0))

def main():
    args = sys.argv[1:]
    if not args:
        raise SystemExit(__doc__)
    root = args[0]
    out = args[args.index('--out') + 1] if '--out' in args else root
    rows = [row_of(r) for r in load(root)]
    def key(x):
        return (x.get('count') or 0, x.get('pose') or '', x.get('shadows') or '', x.get('requestedRoute') or '', x.get('name') or '')
    rows.sort(key=key)
    with io.open(os.path.join(out, 'summary.csv'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(','.join(COLUMNS) + '\n')
        for x in rows:
            f.write(','.join('"' + cell(x.get(c)).replace('"', "'") + '"' if c in ('invalidities', 'comparisonIssues', 'name', 'eyeStart', 'steps') else cell(x.get(c)) for c in COLUMNS) + '\n')
    lines = ['# NPC skin bench summary', '', 'root: `' + root + '`', '',
             'Medians / p95 in ms over the measured frames (valid samples only; n = valid sample count). GPU = FrameTimingManager gpuFrameTime (whole frame); XR GPU = the XR display app GPU time (another definition; the Quest\'s measure). '
             '`valid` = the route and the resources held; `comparison` = the eye within the tolerances, the rows in view, the frames asked measured. A launch failing either is left out of the comparison.', '',
             '| launch | route asked -> actual | valid | comparison | n | pose | shadows (cascades) | frames / steps | GPU med / p95 (n) | XR GPU med / p95 (n) | Main med / p95 | Render med / p95 | dt med | pose eval / apply | skin frame / palette / gather / upload / upload-palette | palette writes (med/frame, total, extra B, gap) | poses | palettes | skin b/c/m | b/c/m | visible skin / renderers | eye start | eye max move / turn | rig max move / turn | long | issues |',
             '| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |']
    for x in rows:
        lines.append('| %s | %s -> %s | %s | %s | %s | %s | %s (%s) | %s / %s | %s / %s (%s) | %s / %s (%s) | %s / %s | %s / %s | %s | %s / %s | %s / %s / %s / %s / %s | %s / %s / %s / %s | %s | %s | %s/%s/%s | %s/%s/%s | %s / %s | %s | %s / %s | %s / %s | %s | %s |' % (
            x.get('name'), x.get('requestedRoute'), x.get('actualRoute'), ('%s (profiler diagnosis)' % x.get('valid')) if x.get('profilerDiagnosis') is True else x.get('valid'), x.get('comparison'), cell(x.get('count')), x.get('pose'), x.get('shadows'), cell(x.get('cascades')),
            cell(x.get('framesMeasured')), x.get('steps'), fmt(x.get('gpu_med')), fmt(x.get('gpu_p95')), cell(x.get('gpu_n')), fmt(x.get('xrgpu_med')), fmt(x.get('xrgpu_p95')), cell(x.get('xrgpu_n')),
            fmt(x.get('main_med')), fmt(x.get('main_p95')), fmt(x.get('render_med')), fmt(x.get('render_p95')), fmt(x.get('dt_med')),
            fmt(x.get('poseEval_med')), fmt(x.get('poseApply_med')), fmt(x.get('skinFrame_med')), fmt(x.get('skinPalette_med')), fmt(x.get('skinGather_med')), fmt(x.get('skinUpload_med')), fmt(x.get('skinUploadPalette_med')),
            fmt(x.get('paletteWrites_med'), 1), cell(x.get('paletteWrites')), cell(x.get('paletteBytesExtra')), cell(x.get('paletteJoinGap')),
            cell(x.get('posesApplied')), cell(x.get('palettesGathered')), cell(x.get('skinBodies')), cell(x.get('skinCasters')), cell(x.get('skinMotion')), cell(x.get('bodies')), cell(x.get('casters')), cell(x.get('motion')),
            cell(x.get('visibleSkin')), cell(x.get('visibleRenderers')), x.get('eyeStart'), fmt(x.get('eyeMaxMove')), fmt(x.get('eyeMaxTurn'), 2), fmt(x.get('rigMaxMove')), fmt(x.get('rigMaxTurn'), 2),
            cell(x.get('longFrames')), ('; '.join(s for s in (x.get('invalidities') or '', x.get('comparisonIssues') or '') if s)).replace('|', '/')))
    # The paired comparison by condition: the valid launches whose comparison holds, and whose A/B start poses agree.
    lines += ['', '## Skin24 minus SMR by condition (medians of the adopted launches\' medians, ms; adopted = valid and comparison holds and the A/B start poses within the tolerances)', '',
              '| count | pose | shadows | SMR adopted / all | Skin24 adopted / all | A/B start apart (m, deg) | GPU SMR | GPU Skin24 | GPU diff | XR GPU SMR | XR GPU Skin24 | XR GPU diff | Main SMR | Main Skin24 | Main diff | Render SMR | Render Skin24 | Render diff | excluded |',
              '| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |']
    conditions = sorted({(x.get('count'), x.get('pose'), x.get('shadows')) for x in rows if x.get('count') is not None})
    for count, pose, shadows in conditions:
        of = lambda route: [x for x in rows if x.get('count') == count and x.get('pose') == pose and x.get('shadows') == shadows and x.get('requestedRoute') == route]
        smr, skin = of('SMR'), of('SKIN24')
        excluded = []
        def ok(x):
            if x.get('profilerDiagnosis') is True:
                excluded.append(x['name'] + ': profiler diagnosis (the Profiler\'s load is in its frames: not a performance sample)'); return False
            if x.get('valid') is not True:
                excluded.append(x['name'] + ': route invalid'); return False
            if x.get('comparison') is not True:
                excluded.append(x['name'] + ': comparison (' + (x.get('comparisonIssues') or '?') + ')'); return False
            return True
        smr_ok = [x for x in smr if ok(x)]
        skin_ok = [x for x in skin if ok(x)]
        # The A/B start poses: every adopted launch against the first adopted SMR launch.
        apart = ''
        if smr_ok and skin_ok:
            ref = smr_ok[0]
            worst_d = worst_a = 0.0
            for x in smr_ok[1:] + skin_ok:
                d, a, within = pose_apart(ref.get('_eye'), x.get('_eye'), ref.get('_tolM'), ref.get('_tolD'))
                if d is None:
                    continue
                worst_d, worst_a = max(worst_d, d), max(worst_a, a)
                if not within:
                    excluded.append(x['name'] + ': start pose %.3f m / %.2f deg from %s' % (d, a, ref['name']))
            apart = '%.3f, %.2f' % (worst_d, worst_a)
            excluded_names = {e.split(':')[0] for e in excluded}
            smr_ok = [x for x in smr_ok if x['name'] not in excluded_names]
            skin_ok = [x for x in skin_ok if x['name'] not in excluded_names]
        def med(xs, key):
            vals = [x[key] for x in xs if x.get(key) is not None]
            return statistics.median(vals) if vals else None
        def diff(a, b):
            return None if a is None or b is None else b - a
        g0, g1 = med(smr_ok, 'gpu_med'), med(skin_ok, 'gpu_med')
        x0, x1 = med(smr_ok, 'xrgpu_med'), med(skin_ok, 'xrgpu_med')
        m0, m1 = med(smr_ok, 'main_med'), med(skin_ok, 'main_med')
        r0, r1 = med(smr_ok, 'render_med'), med(skin_ok, 'render_med')
        lines.append('| %s | %s | %s | %d / %d | %d / %d | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |' % (
            count, pose, shadows, len(smr_ok), len(smr), len(skin_ok), len(skin), apart, fmt(g0), fmt(g1), fmt(diff(g0, g1)), fmt(x0), fmt(x1), fmt(diff(x0, x1)), fmt(m0), fmt(m1), fmt(diff(m0, m1)), fmt(r0), fmt(r1), fmt(diff(r0, r1)),
            '; '.join(excluded).replace('|', '/')))
    with io.open(os.path.join(out, 'summary.md'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    print('summary: %d launches -> %s' % (len(rows), os.path.join(out, 'summary.md')))
    for line in lines[lines.index('') + 1:]:
        if line.startswith('| --- '):
            continue
        if line.startswith('## '):
            print(line)
        elif line.startswith('| ') and 'launch' not in line and 'count |' not in line:
            print(line[:220])

if __name__ == '__main__':
    main()
