"""The check's op records (tag "op" in the CheckHits recording, SandboxPropSlashPlayerCheck.HitLog.cs; formerly
multi-ops.csv / building-ops.csv): per recording, the ops' tally, the stages, and how the ops answer the recording's hits.
A recording is used only if its saved records are complete against its "[check hits] ended" line (check_writer_jsonl.py)
and its ops are complete against the summary's tally; otherwise it is reported and not analysed. A complete record is not
the check's verdict ("PROP SLASH: ok" / "FAILED" in the log).

The clocks and stages of an op record:
- eventFrame / seconds: the moment the record was made, at the run's summary (not any stage's moment).
- acceptedFrame, provisionalFrame, finalFrame, committedFrame: the frames the check OBSERVED each stage in (its LateUpdate):
  the hit (the hit's eventFrame; a held root's take-up), the Provisional publication, the operation leaving Admitted, the
  geometry Committed; -1 when not observed. Not the moments the product reached them.
- acceptedTime .. committedTime: the check's clock at those observations, Time.unscaledTimeAsDouble since the replay's
  start, in seconds; null when not observed. Not the Stopwatch of "seconds", not the hit's "at" (the replay input clock).
- ledgerState, positive, negative: the ledger's at the summary.

The ops' tally (the summary): opsApplicable (0 for a mode that writes no ops: MobPlan, a test's), opsPlanned (how many were to
be written, fixed from the accepted list before the writing; null when the summary never reached them), opsAttempted,
opsAccepted (by the logger); saved = the op records in the file. Applicable and complete: planned = attempted = accepted =
saved. "Not applicable" and "none accepted" (planned 0) are told apart.

Matching an op to the recording's hits, never across recordings:
- an op with an operation (not 0): the hits with the same operation, slashId and fragment = the op's source; its
  acceptedFrame should be the hit's eventFrame. A held root (name ending "-held"; its hit was Held, operation 0): the Held
  hits with the same slashId and fragment, observed at or before the op's acceptedFrame.
- an op with operation 0 (hull / fusion: a Pending answered without an operation of its own): the Pending hits with
  operation 0, the same slashId and fragment, observed in the op's acceptedFrame. Operation 0 is never joined as an id.
- by an operation id, one candidate: confirmed; by the held or the operation-0 rule, one candidate: a candidate (the
  slashId, source, frame and acceptance agree; that does not prove it the same acceptance); none: unmatched (a record that
  should be there is not); several: ambiguous (reported, not picked). Repeated op rows are kept: a hit answered by more than one op is reported.
- a hit with no op: by its acceptance -- EmptySide, NotAccepted, AnchorsRefused, Aborted, Stale, InvalidRequest: no cut by
  the rule (by specification); Held: not taken up (by specification); Published or Pending: missing (a record short).

  python analyze_ops.py <session.jsonl>:<log> [--recording R]
Other readers: ops_for_run(run_dir, check_dir[, log]) -> (ops, hits, matching) for the recording whose start names
check_dir, raising analyze_hits.NotComplete when the record or its ops are not complete.
"""
import collections, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyze_hits   # noqa: E402

NO_CUT = {'EmptySide', 'NotAccepted', 'AnchorsRefused', 'Aborted', 'Stale', 'InvalidRequest'}


def ops_status(rs):
    """(ok, text, ops): whether the recording's ops are complete against its summary's tally."""
    summary = next((d for t, d, _ in rs if t == 'summary'), None)
    ops = [d for t, d, _ in rs if t == 'op']
    if summary is None:
        return False, 'no summary', ops
    applicable = summary.get('opsApplicable')
    if applicable is None:
        return False, 'no ops tally in the summary (a record made before op records)', ops
    if applicable == 0:
        return (len(ops) == 0), ('ops not applicable' if not ops else 'ops not applicable, yet %d op records' % len(ops)), ops
    planned, attempted, accepted = summary.get('opsPlanned'), summary.get('opsAttempted'), summary.get('opsAccepted')
    if planned is None:
        return False, 'ops applicable, not written (the summary never reached them)', ops
    counts = 'planned %d, attempted %d, accepted %d, saved %d' % (planned, attempted, accepted, len(ops))
    return (planned == attempted == accepted == len(ops)), ('ops ' + counts), ops


def match(ops, hits):
    """For each op its candidate hits; for each hit the ops that took it; the classification."""
    by_op = []
    taken = collections.defaultdict(list)
    for i, o in enumerate(ops):
        if o['operation'] != 0:
            c = [j for j, h in enumerate(hits) if h['operation'] == o['operation'] and h['slashId'] == o['slash'] and h['fragment'] == o['source']]
            how = 'operation'
            if not c and str(o.get('name') or '').endswith('-held'):
                c = [j for j, h in enumerate(hits) if h['acceptance'] == 'Held' and h['slashId'] == o['slash'] and h['fragment'] == o['source']
                     and h['eventFrame'] <= o['acceptedFrame']]
                how = 'held'
        else:
            c = [j for j, h in enumerate(hits) if h['operation'] == 0 and h['acceptance'] == 'Pending' and h['slashId'] == o['slash']
                 and h['fragment'] == o['source'] and h['eventFrame'] == o['acceptedFrame']]
            how = 'operation 0'
        # Confirmed only through an operation id (with its slashId and source); one candidate by the held or the operation-0
        # rule is a candidate: the slashId, source, frame and acceptance agree, which does not prove the same acceptance.
        kind = ('confirmed' if how == 'operation' else 'candidate') if len(c) == 1 else 'unmatched' if not c else 'ambiguous'
        frame_ok = None
        if kind == 'confirmed':
            frame_ok = hits[c[0]]['eventFrame'] == o['acceptedFrame']
        by_op.append((kind, how, c, frame_ok))
        if kind in ('confirmed', 'candidate'):
            taken[c[0]].append((i, kind))
    unsure = {j for kind, _, c, _ in by_op if kind == 'ambiguous' for j in c}
    hit_kinds = collections.Counter()
    for j, h in enumerate(hits):
        if j in taken:
            ks = {k for _, k in taken[j]}
            hit_kinds['answered by %s op%s' % ('one' if len(taken[j]) == 1 else '%d (repeated op rows)' % len(taken[j]),
                                                 '' if ks == {'confirmed'} else ' (a candidate, not confirmed)')] += 1
        elif j in unsure:
            hit_kinds['a candidate of an ambiguous op (not picked)'] += 1
        elif h['acceptance'] in NO_CUT:
            hit_kinds['no op: ' + h['acceptance'] + ' (by specification: no cut)'] += 1
        elif h['acceptance'] == 'Held':
            hit_kinds['no op: Held, not taken up (by specification)'] += 1
        else:
            hit_kinds['no op: ' + h['acceptance'] + ' (a record short)'] += 1
    return by_op, hit_kinds


def ops_for_run(run_dir, check_dir, log=None):
    log = log or os.path.join(run_dir, 'player.log')
    hits = analyze_hits.hits_for_run(run_dir, check_dir, log)   # raises NotComplete unless the recording is complete
    path, rec = analyze_hits.recording_of(run_dir, check_dir, log)
    rs = analyze_hits.records(path)[rec]
    ok, text, ops = ops_status(rs)
    if not ok:
        raise analyze_hits.NotComplete(text)
    by_op, hit_kinds = match(ops, hits)
    return ops, hits, (by_op, hit_kinds)


def analyse(rs):
    ok, text, ops = ops_status(rs)
    start = next((d for t, d, _ in rs if t == 'start'), {})
    print('   start: mode %s, iteration %s, directory %s' % (start.get('mode'), start.get('iteration'), start.get('directory')))
    print('   %s' % text)
    if not ok:
        print('   ops not analysed (not complete against the tally)')
        return
    if not ops:
        return
    hits = [d for t, d, _ in rs if t == 'hit']
    print('   ops %d (Pending %d, operation 0 %d, held roots %d, repeated operation rows %d)' % (
        len(ops), sum(1 for o in ops if o['acceptance'] == 'Pending'), sum(1 for o in ops if o['operation'] == 0),
        sum(1 for o in ops if str(o.get('name') or '').endswith('-held')),
        sum(c - 1 for c in collections.Counter(o['operation'] for o in ops if o['operation'] != 0).values() if c > 1)))
    print('   stages (the frames the check observed them in; times on the check clock, unscaledTime since the replay start; -1 / null = not observed):')
    for stage in ('provisional', 'final', 'committed'):
        frames = [o[stage + 'Frame'] - o['acceptedFrame'] for o in ops if o[stage + 'Frame'] >= 0 and o['acceptedFrame'] >= 0]
        times = [1000 * (o[stage + 'Time'] - o['acceptedTime']) for o in ops if o[stage + 'Time'] is not None and o['acceptedTime'] is not None]
        print('     accepted -> %s: observed %d of %d; frames %s; ms %s' % (
            stage, len(frames), len(ops), (min(frames), max(frames)) if frames else '-', ('%.3f..%.3f' % (min(times), max(times))) if times else '-'))
    print('   ledger at the summary: %s; pending ended without publication: %d' % (
        dict(collections.Counter(o['ledgerState'] for o in ops)), sum(1 for o in ops if o['pendingEnd'])))
    by_op, hit_kinds = match(ops, hits)
    kinds = collections.Counter((k, how) for k, how, _, _ in by_op)
    print('   ops against the recording\'s hits: %s' % {('%s by %s' % (k, how)): n for (k, how), n in sorted(kinds.items())})
    bad_frames = sum(1 for k, how, _, f in by_op if f is False)
    print('   confirmed by operation id with acceptedFrame != the hit\'s eventFrame: %d' % bad_frames)
    for (k, how, c, _), o in zip(by_op, ops):
        if k not in ('confirmed', 'candidate'):
            print('     %s op: operation %s slash %s source %s acceptedFrame %s name %s (candidates %d)' % (k, o['operation'], o['slash'], o['source'], o['acceptedFrame'], o.get('name'), len(c)))
    print('   hits: %s' % dict(sorted(hit_kinds.items())))


if __name__ == '__main__':
    only = int(sys.argv[sys.argv.index('--recording') + 1]) if '--recording' in sys.argv else None
    for arg in [a for i, a in enumerate(sys.argv[1:], 1) if not a.startswith('--') and sys.argv[i - 1] != '--recording']:
        cut = arg.lower().rfind('.jsonl:')   # <session.jsonl>:<log>, absolute paths with drive letters included
        jsonl, log = (arg[:cut + 6], arg[cut + 7:]) if cut >= 0 else (arg, '')
        name = os.path.basename(jsonl)
        if not log:
            print('== %s: not analysed (no log given: completeness unconfirmed)' % name)
            continue
        recs = analyze_hits.records(jsonl)
        for rec in sorted(recs):
            if only is not None and rec != only:
                continue
            code, text = analyze_hits.verdict(jsonl, log, rec)
            if code != 0:
                print('== %s recording %d: not analysed (%s)' % (name, rec, text))
                continue
            print('== %s recording %d [COMPLETE]' % (name, rec))
            analyse(recs[rec])
