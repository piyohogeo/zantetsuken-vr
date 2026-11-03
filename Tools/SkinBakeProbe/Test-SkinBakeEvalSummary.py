# -*- coding: utf-8 -*-
"""SKIN-GPU-FIRST-EVAL-1 (TL 8): the evaluation summary checked on synthetic runs -- negatives first.

  1. a frame with two FTM rows (a repeated result) is counted ONCE in the window total, by the nearest row;
  2. a frame whose FTM start is far from any frame-start mark is UNDETERMINED: not counted, the total marked PARTIAL;
  3. a gpu request whose collect ended as Failed has no CPU ready (readyNotCompleted 1), apart from a completed one;
  4. an unobserved clip state is '-' (never a claim that nothing was displayed); a repeated event row is not counted twice;
  5. round 0 is kept apart from the steady rounds; the pooled values take only the steady rounds;
  6. a comparison whose difference is inside the spread says so and gives no verdict;
  7. (review 1) a round after a reset that did not return to the initial state is INVALID: left out of the steady pooling, listed;
  8. (review 1) a frame's interval is the one that begins at its start: the interval before the hit is not in the window, the last frame's is;
  9. (review 1) two builds of one platform are never pooled or compared together;
 10. (review 1/2) the request's local Main total = TryCut + the collect's Main (the gpu route's readyMainMs, over frames), bodies summed; it is compared as 'local', apart from the frame metrics.
Usage: Test-SkinBakeEvalSummary.py   (exit 0 = all checks pass)
"""
import json, os, shutil, sys, tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import importlib
S = importlib.import_module('Summarize-SkinBakeEval')

FREQ = 10000000.0   # Stopwatch ticks a second in the synthetic data


def write_run(folder, route, rounds, bodies=1, failed_collect_round=None, duplicate_ftm_frame=None, undetermined_frame=None, main_ms=5.0, hit_main=12.0, build='buildGUID abc12345; graphics Direct3D12',
              reset_failed_round=None, long_dt_offsets=()):
    os.makedirs(folder)
    facts = {'build': 'WindowsPlayer Release; ' + build, 'cut bench': 'bodies %d; stagger 0 frames; rounds %d; 60 frames observed after the last request, 60 warmup; load 0 NPCs; skin bake route %s (asked)' % (bodies, rounds, route),
             'warm before the bench': 'none (cold)', 'cut bench reaction events': 'buffers: physics 1'}
    for r in range(rounds - 1):
        facts['cut bench round %d reset' % r] = ('NOT SETTLED after 600 frames (pending bakes 1, storage pending 0, DAG pending bases 1); pieces retired 1, refused 1 (last: owner busy); live cut fragments 2 -> 1, owners 2 -> 1'
                                                if reset_failed_round == r else 'settled after 0 frames (pending bakes 0, storage pending 0, DAG pending bases 0); pieces retired 2, refused 0; live cut fragments 2 -> 0, owners 2 -> 0')
    json.dump({'title': 't', 'passed': 1, 'failed': 0, 'facts': facts, 'rows': []}, open(os.path.join(folder, 'skin-bake-probe.json'), 'w', encoding='utf-8'))
    req = ['round,body,route,requestFrame,outcome,acceptance,requestMainMs,acceptedFrame,physicsFrame,publishedFrame,clipFrame,readyFrame,committedFrame,framesToAccept,framesToPhysics,framesToPublish,framesToClip,framesToReady,framesToCommit,cutKernelMs,readyMainMs,bakeWorkerMs,fallbackReason,basisOutcome,switchCompare,cutPlane,planeLocal,planeWorld,anchor,budgetWait,geometryWait,physicsObservedFrame,ledgerEventFrame,clipBeforeRenderFrame,readyEventFrame,readyOutcome,committedEventFrame,framesToLedgerEvent,framesToClipBeforeRender,framesToReadyEvent,framesToCommitEvent']
    ev = ['round,body,event,frame,tick,msAfterRequestBegin,note']
    marks = ['round,frame,k,initTick,initMs,dtInitMs,stepMs,hitBeginMs,hitEndMs,hitMs,lateMs,beforeRenderMs,beginRenderMs,endRenderMs,bakeMarks,bakeMs,motionIssuedTotal']
    ftm = ['readFrame,frameStartTimestamp,firstSubmitTimestamp,cpuTimePresentCalled,cpuTimeFrameComplete,cpuMainThreadFrameTimeMs,cpuRenderThreadFrameTimeMs,gpuFrameTimeMs,cpuMainThreadPresentWaitTimeMs,frameStartAsStopwatchTick,nearestInitFrame,offsetToNearestInitMs']
    stats = ['requestId,route,vertexCount,boneCount,hitFrame,readyFrame,hitMainMs,readyMainMs,workerMs,kernelMs,dispatches,acceptance,basisOutcome']
    frame = 1000; tick = 100000000
    for r in range(rounds):
        rq = frame + 60
        for k in range(-60, 40):
            fr = rq + k
            t = tick + (fr - 1000) * int(0.0139 * FREQ)
            marks.append('%d,%d,%s,%d,%.3f,%.3f,,%s,%s,%s,,,,,0,0,0' % (r, fr, k if k >= 0 else '', t, (fr - 1000) * 13.9, 40.0 if k in long_dt_offsets else 13.9, '1.0' if k == 0 else '', '3.0' if k == 0 else '', '2.0' if k == 0 else ''))
            main = hit_main if k == 0 else main_ms
            off = 0.3
            if undetermined_frame is not None and k == undetermined_frame:
                off = 7.0
            ftm.append('%d,%d,0,0,0,%.3f,0,0,0,%d,%d,%.3f' % (fr + 4, t, main, t, fr, off))
            if duplicate_ftm_frame is not None and k == duplicate_ftm_frame:
                ftm.append('%d,%d,0,0,0,%.3f,0,0,0,%d,%d,%.3f' % (fr + 5, t + 2000, main * 10, t + 2000, fr, 0.5))   # a repeated result, farther from the mark, ten times the Main
        for b in range(bodies):
            t0 = tick + (rq - 1000) * int(0.0139 * FREQ) + 10000
            gpu = route == 'gpu'
            failed = gpu and failed_collect_round == r
            ready_frame = rq + 1 if gpu and not failed else (rq if not gpu else -1)
            ended_frame = rq + 1 if failed else -1
            commit_frame = -1 if failed else rq + 2
            req.append('%d,%d,%s,%d,Requested,Published,%.4f,%d,%d,%d,%d,%d,%d,0,0,1,1,%d,%d,0.5,%s,0.6,,%s,,horizontal,p,p,a,none,%s,%d,%d,%d,%d,%s,%d,1,%s,%s,%s' % (
                r, b, route, rq, 4.0, rq, rq, rq + 1, rq + 1, ready_frame, commit_frame, ready_frame - rq if ready_frame >= 0 else -1, commit_frame - rq if commit_frame >= 0 else -1,
                '0.4' if gpu and not failed else '', 'failed: x' if failed else ('ready' if gpu else ''), 'gpu route' if gpu else 'none', rq, rq + 1, -1, ready_frame if gpu else -1, 'failed' if failed else ('completed' if gpu else 'synchronous'), commit_frame,
                '-1', (ready_frame - rq) if gpu and not failed else '-1', (commit_frame - rq) if commit_frame >= 0 else '-1'))
            def E(name, fr, ms, note=''):
                ev.append('%d,%d,%s,%d,%s,%s,%s' % (r, b, name, fr, (t0 + int(ms * FREQ / 1000.0)) if fr >= 0 else '', ('%.3f' % ms) if fr >= 0 else '', note))
            E('request-begin', rq, 0.0); E('request-end', rq, 4.0); E('accepted', rq, 4.0); E('physics-published', rq, 2.5); E('physics-published', rq, 2.5)   # the repeated row
            E('ledger-published', rq + 1, 15.0); E('clip-observed-step', rq + 1, 16.0); E('clip-observed-beforeRender', -1, 0.0)
            if gpu and not failed:
                E('cpu-ready', rq + 1, 17.0)
            E('cpu-ready-ended', ended_frame, 17.5, 'Failed' if failed else '')
            E('committed', commit_frame, 30.0)
            if gpu:
                stats.append('%d,gpu,4728,63,%d,%d,4.0,0.4,0.66,0.58,1,Published,%s' % (r * 10 + b + 1, rq, ready_frame, 'failed: x' if failed else 'ready'))
        frame = rq + 40 + 5
    for name, rows in [('skin-bake-cut-requests.csv', req), ('skin-bake-cut-events.csv', ev), ('skin-bake-frame-marks.csv', marks), ('skin-bake-ftm-raw.csv', ftm), ('skin-bake-stats.csv', stats)]:
        open(os.path.join(folder, name), 'w', encoding='utf-8', newline='\n').write('\n'.join(rows) + '\n')


def check(cond, what):
    print(('PASS ' if cond else 'FAIL ') + what)
    return cond


def main():
    root = tempfile.mkdtemp(prefix='skin-eval-test-')
    ok = True
    try:
        write_run(os.path.join(root, 'cutt-gpu1-A', 'run'), 'gpu', 3, duplicate_ftm_frame=3, undetermined_frame=5, failed_collect_round=2)
        write_run(os.path.join(root, 'cutt-gpu1-B', 'run'), 'gpu', 3, main_ms=5.2)
        write_run(os.path.join(root, 'cutt-cpu1-A', 'run'), 'cpu', 3, main_ms=5.1, hit_main=14.0)
        write_run(os.path.join(root, 'cutt-cpu1-B', 'run'), 'cpu', 3, main_ms=5.0, hit_main=13.0)
        runA = S.Run(os.path.join(root, 'cutt-gpu1-A', 'run'), 'cutt-gpu1-A', 32)
        r1 = runA.rounds[1]
        # 1. the duplicate row: counted once, by the nearest row (5.0, not 50.0)
        ok &= check(runA.ftm_rows_dropped == 3 and r1['frames'][3]['main'] == 5.0, '1. a repeated FTM row is dropped (%d dropped) and the frame keeps the nearest result (%s)' % (runA.ftm_rows_dropped, r1['frames'][3]['main']))
        # 2. the undetermined frame: not counted, PARTIAL
        ok &= check(r1['frames'][5]['attributed'] is False and r1['windowPartial'] and r1['windowAttributed'] == 31, '2. the far FTM result leaves its frame undetermined: window PARTIAL 31/32')
        expected_total = 12.0 + 5.0 * 30
        ok &= check(abs(r1['windowTotal'] - expected_total) < 1e-6, '3. the window total is the sum of the attributed frames once each (%.1f = 12 + 30 x 5)' % r1['windowTotal'])
        # 3. the failed collect
        r2 = runA.rounds[2]; b = r2['bodies'][0]
        ok &= check(b['ready'] is None and b['readyEnded'] == 1 and r2['readyNotCompleted'] == 1 and b['readyOutcome'] == 'failed' and r2['commitsMissing'] == 1, '4. a Failed collect is no CPU ready (ended +1, outcome failed, commit missing)')
        ok &= check(r1['bodies'][0]['ready'] == 1 and r1['readyNotCompleted'] == 0, '5. a completed collect is the CPU ready (+1)')
        # 4. clip unobserved and the repeated event row
        ok &= check(b['clipBeforeRender'] is None and b['clipStep'] == 1, '6. an unobserved clip state is None (printed as -), the step observation stands')
        ok &= check(len([e for e in runA.events if e['event'] == 'physics-published' and e['round'] == '1']) == 2 and r1['bodies'][0]['physics'] == 0 and r1['bodies'][0]['physicsMs'] == 2.5, '7. a repeated event row is read once (the first)')
        # 5. round 0 apart
        values, per_run = S.pooled([runA], 'hitMain')
        ok &= check(len(values) == 2 and runA.rounds[0]['first'] and all(not r['first'] for r in runA.rounds[1:]), '8. the pooled steady values leave round 0 out (n %d of 3 rounds)' % len(values))
        # 6. the comparison inside the spread
        groups = {}
        for name in ['cutt-gpu1-A', 'cutt-gpu1-B', 'cutt-cpu1-A', 'cutt-cpu1-B']:
            run = S.Run(os.path.join(root, name, 'run'), name, 32)
            groups.setdefault(S.group_key(run), []).append(run)
        c = S.compare(groups, 'PC', 'abc12345', 1, 'cpu', 'gpu', 'windowTotal')
        ok &= check(c is not None and c['verdict'].startswith('within the spread'), '9. cpu->gpu window total: difference %+.2f, spread %.2f -> %s' % (c['diff'], c['spread'], c['verdict']))
        c2 = S.compare(groups, 'PC', 'abc12345', 1, 'cpu', 'gpu', 'hitMain')
        ok &= check(c2 is not None and c2['verdict'] == 'lower', '10. cpu->gpu hit Main: difference %+.2f, spread %.2f -> %s (a number, not a verdict)' % (c2['diff'], c2['spread'], c2['verdict']))
        # the whole summary runs
        out = os.path.join(root, 'summary.md')
        rc = S.main(['x', os.path.join(root, 'cutt-gpu1-A'), os.path.join(root, 'cutt-gpu1-B'), os.path.join(root, 'cutt-cpu1-A'), os.path.join(root, 'cutt-cpu1-B'), '--out', out, '--window', '32'])
        text = open(out, encoding='utf-8').read()
        partial_pool, _ = S.pooled([runA], 'windowTotal')
        ok &= check(len(partial_pool) == 0, '12. PARTIAL window totals are left out of the pooled totals (n %d)' % len(partial_pool))
        ok &= check(rc == 0 and 'not displayed' not in text and 'PARTIAL 31/32' in text and 'within the spread' in text, '11. the summary writes, says PARTIAL and within the spread, never "not displayed"')
        # review 1 -- 13. a failed reset invalidates the rounds after it
        write_run(os.path.join(root, 'cutt-cpu1-C', 'run'), 'cpu', 4, reset_failed_round=1)
        runC = S.Run(os.path.join(root, 'cutt-cpu1-C', 'run'), 'cutt-cpu1-C', 32)
        valuesC, _ = S.pooled([runC], 'hitMain')
        ok &= check(list(runC.invalid_rounds.keys()) == [2, 3] and len(valuesC) == 1 and runC.rounds[2]['invalid'] and not runC.rounds[1]['invalid'],
                    '13. a reset that did not return to the initial state (round 1: not settled, refused, residue) makes rounds 2 and 3 INVALID; the pooled steady values keep round 1 only (n %d; invalid %s)' % (len(valuesC), list(runC.invalid_rounds.keys())))
        ok &= check('not settled' in list(runC.invalid_rounds.values())[0] and 'refused 1' in list(runC.invalid_rounds.values())[0] and 'residue' in list(runC.invalid_rounds.values())[0], '13b. the invalid reason names the settle, the refusal and the residue')
        # 14. the frame interval window: the interval ending at the request frame (before the hit) is not in the window; the last frame's own interval (ending at k0+32) is
        write_run(os.path.join(root, 'cutt-cpu1-D', 'run'), 'cpu', 2, long_dt_offsets=(0, 32))
        runD = S.Run(os.path.join(root, 'cutt-cpu1-D', 'run'), 'cutt-cpu1-D', 32)
        rD = runD.rounds[1]
        ok &= check(rD['longFrames'] == [(31, 40.0)] and abs(rD['maxDt'] - 40.0) < 1e-9 and abs(rD['hitDt'] - 13.9) < 1e-9 and rD['longFramesAll'] == [(31, 40.0)],
                    '14. the window holds its frames\' own intervals: the long interval before the hit is left out, the last frame\'s (k 31) is kept (long %s, hit dt %.1f)' % (rD['longFrames'], rD['hitDt']))
        # 15. another build of the same platform is a group of its own
        write_run(os.path.join(root, 'cutt-cpu1-Z', 'run'), 'cpu', 3, main_ms=9.0, hit_main=30.0, build='buildGUID zzz99999; graphics Direct3D12')
        groups2 = {}
        for name in ['cutt-gpu1-A', 'cutt-gpu1-B', 'cutt-cpu1-A', 'cutt-cpu1-B', 'cutt-cpu1-Z']:
            run = S.Run(os.path.join(root, name, 'run'), name, 32)
            groups2.setdefault(S.group_key(run), []).append(run)
        cz = S.compare(groups2, 'PC', 'abc12345', 1, 'cpu', 'gpu', 'hitMain')
        ok &= check(('PC', 'zzz99999', 'cpu', 1) in groups2 and len(groups2[('PC', 'abc12345', 'cpu', 1)]) == 2 and cz['nFrom'] == 4 and abs(cz['medianFrom'] - 13.5) < 1e-9,
                    '15. the other build is its own group; the comparison of build abc12345 takes its 2 cpu runs only (n %d, median %.1f, not 30)' % (cz['nFrom'], cz['medianFrom']))
        # 16. the request's local Main total: TryCut + the collect's Main (gpu), TryCut alone (cpu); compared as 'local', apart from the frame metrics; TryCut alone is not compared
        runB = S.Run(os.path.join(root, 'cutt-gpu1-B', 'run'), 'cutt-gpu1-B', 32)
        runCpu = S.Run(os.path.join(root, 'cutt-cpu1-A', 'run'), 'cutt-cpu1-A', 32)
        ok &= check(abs(runB.rounds[1]['localMainMs'] - 4.4) < 1e-9 and abs(runCpu.rounds[1]['localMainMs'] - 4.0) < 1e-9 and abs(runB.rounds[1]['hitTryCutMs'] - 4.0) < 1e-9,
                    '16. local Main total per request: gpu 4.0 + 0.4 = %.1f, cpu %.1f; TryCut alone 4.0' % (runB.rounds[1]['localMainMs'], runCpu.rounds[1]['localMainMs']))
        names = [m[0] for m in S.COMPARED_METRICS]
        ok &= check('localMainMs' in names and 'hitTryCutMs' not in names and 'localMainMs' not in S.FRAME_METRICS and 'hitMain' in S.FRAME_METRICS, '16b. the compared metrics hold the local Main total (kind local) and the frame metrics apart')
        rc2 = S.main(['x', os.path.join(root, 'cutt-gpu1-A'), os.path.join(root, 'cutt-gpu1-B'), os.path.join(root, 'cutt-cpu1-A'), os.path.join(root, 'cutt-cpu1-B'), os.path.join(root, 'cutt-cpu1-C'), os.path.join(root, 'cutt-cpu1-Z'), '--out', out, '--window', '32'])
        text2 = open(out, encoding='utf-8').read()
        ok &= check(rc2 == 0 and 'INVALID' in text2 and '| local |' in text2 and '(build zzz99999)' in text2 and '(build abc12345)' in text2, '17. the summary names the invalid rounds, the local kind and each build')
        # review 1 (CPU-SKIN-FLOAT-1): a bake time the route did not measure is blank -> None -> the metric is None and the comparison is skipped, never a 0
        ok &= check(runA.rounds[1]['bakeKernelMs'] is None and runA.rounds[1]['bakeAppendMs'] is None and runA.rounds[1]['showMs'] is None, '18. a record without bake-time columns gives None, not 0')
        ok &= check(S.compare(groups, 'PC', 'abc12345', 1, 'cpu', 'gpu', 'bakeKernelMs') is None, '18b. no comparison on an unmeasured bake time')
        ok &= check(S.f('NaN') is None and S.f('') is None and S.f('nan') is None and S.f('0.5') == 0.5, '18c. a NaN string is None, never a number (review 2)')
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print('ALL PASS' if ok else 'SOME FAILED')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
