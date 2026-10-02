"""The check's per-frame rows (writer CheckFrames, SandboxPropSlashPlayerCheck.FrameLog.cs; formerly multi.csv) of the
multi-NPC and MobPlan modes: a recording of their own per walk, apart from the walk's CheckHits recording (named by the
start's hitRecording), so that a gap in these rows leaves the hits and ops usable and the other way round. A recording is
used only if its saved records are complete against its "[check frames] ended" line (check_writer_jsonl.py, beside this
file) and its frame records number its summary's "frames"; otherwise it is reported and not analysed. A complete record is
not the check's verdict ("PROP SLASH: ok" / "FAILED" in the log).

A frame record:
- eventFrame: the frame the check ran MultiFrame in (its LateUpdate), the old CSV's "frame".
- seconds: the Stopwatch then, from the recording's start. The logger's own time and frame are the call's.
- real: the check's clock, Time.unscaledTimeAsDouble since the replay's start, in seconds (the op records' clock).
- waves .. committedThisFrame: the counts the check read in that frame.
- unsimulated: the step clock's unsimulated seconds (null without a clock); stepId: its step id (-1 without one).

  python analyze_frames.py <session.jsonl>:<log> [--recording R]
Other readers: frames_for_run(run_dir, check_dir[, log]) -> the frame records (dicts) of the recording whose start names
check_dir, raising analyze_hits.NotComplete when the record is not complete.
"""
import collections, datetime, glob, json, os, subprocess, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyze_hits   # noqa: E402  (NotComplete, the session window and the checker's location)

CHECK = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'check_writer_jsonl.py')
WRITER, LABEL = 'CheckFrames', 'check frames'


def records(jsonl):
    """The CheckFrames records of a session file, by recording: [(tag, fields, logger frame)] in file order."""
    out = collections.defaultdict(list)
    with open(jsonl, encoding='utf-8') as f:
        for line in f:
            if '"writer_id":"CheckFrames"' not in line:
                continue
            r = json.loads(line)
            v = r['value']
            d = dict(zip(v[0::2], v[1::2]))
            out[d['recording']].append((r['tag'], d, r['frame']))
    return out


def verdict(jsonl, log, recording):
    chk = subprocess.run([sys.executable, CHECK, WRITER, LABEL, jsonl, log, '--recording', str(recording)], capture_output=True, text=True)
    lines = chk.stdout.strip().splitlines()
    return chk.returncode, (lines[0] if lines else chk.stderr.strip())


def rows_status(rs):
    summary = next((d for t, d, _ in rs if t == 'summary'), None)
    rows = [d for t, d, _ in rs if t == 'frame']
    if summary is None:
        return False, 'no summary', rows
    ok = summary.get('frames') == len(rows)
    return ok, 'frames: summary %s, saved %d' % (summary.get('frames'), len(rows)), rows


def frames_for_run(run_dir, check_dir, log=None):
    """The frame records of the recording whose start names check_dir, from a session file written while the run's log
    was (run_dir/player.log unless given), checked against that log."""
    log = log or os.path.join(run_dir, 'player.log')
    first = datetime.datetime.utcfromtimestamp(os.path.getctime(log)) - datetime.timedelta(minutes=2)
    last = datetime.datetime.utcfromtimestamp(os.path.getmtime(log)) + datetime.timedelta(minutes=1)
    want = os.path.normcase(os.path.abspath(check_dir))
    found = []
    for path in sorted(glob.glob(os.path.join(analyze_hits.LOGGER, '*.jsonl'))):
        try:
            at = datetime.datetime.strptime(os.path.basename(path)[:15], '%Y%m%dT%H%M%S')
        except ValueError:
            continue
        if not (first <= at <= last):
            continue
        for rec, rs in records(path).items():
            start = next((d for t, d, _ in rs if t == 'start'), None)
            if start is not None and os.path.normcase(os.path.abspath(start['directory'])) == want:
                found.append((path, rec, rs))
    if len(found) != 1:
        raise analyze_hits.NotComplete('%d frame recordings name %s (one expected)' % (len(found), check_dir))
    path, rec, rs = found[0]
    code, text = verdict(path, log, rec)
    if code != 0:
        raise analyze_hits.NotComplete(text)
    ok, text, rows = rows_status(rs)
    if not ok:
        raise analyze_hits.NotComplete(text)
    return rows


def analyse(rs, hit_verdict):
    start = next((d for t, d, _ in rs if t == 'start'), {})
    ok, text, rows = rows_status(rs)
    print('   start: mode %s, iteration %s, directory %s, the walk\'s CheckHits recording %s (%s)' % (
        start.get('mode'), start.get('iteration'), start.get('directory'), start.get('hitRecording'), hit_verdict))
    print('   %s' % text)
    if not ok:
        print('   frames not analysed (the saved rows do not number the summary\'s)')
        return
    if not rows:
        return
    frames = [d['eventFrame'] for d in rows]
    reals = [d['real'] for d in rows]
    print('   rows %d, frames %d..%d (eventFrame = the frame the check ran MultiFrame in), frames going back %d, real going back %d' % (
        len(rows), frames[0], frames[-1], sum(1 for a, b in zip(frames, frames[1:]) if b <= a), sum(1 for a, b in zip(reals, reals[1:]) if b < a)))
    print('   real (the check clock, unscaledTime since the replay start): %.4f..%.4f s; unsimulated null %d; stepId -1 %d' % (
        reals[0], reals[-1], sum(1 for d in rows if d['unsimulated'] is None), sum(1 for d in rows if d['stepId'] == -1)))
    print('   livePieces max %d, liveConvexes max %d, acceptedOps at the end %d, pendingOps max %d, waves max %d' % (
        max(d['livePieces'] for d in rows), max(d['liveConvexes'] for d in rows), rows[-1]['acceptedOps'], max(d['pendingOps'] for d in rows),
        max(d['waves'] for d in rows)))
    lag = collections.Counter(f - d['eventFrame'] for t, d, f in rs if t == 'frame')
    print('   logger frame - eventFrame: %s' % dict(sorted(lag.items())))


if __name__ == '__main__':
    only = int(sys.argv[sys.argv.index('--recording') + 1]) if '--recording' in sys.argv else None
    for arg in [a for i, a in enumerate(sys.argv[1:], 1) if not a.startswith('--') and sys.argv[i - 1] != '--recording']:
        cut = arg.lower().rfind('.jsonl:')   # <session.jsonl>:<log>, absolute paths with drive letters included
        jsonl, log = (arg[:cut + 6], arg[cut + 7:]) if cut >= 0 else (arg, '')
        name = os.path.basename(jsonl)
        if not log:
            print('== %s: not analysed (no log given: completeness unconfirmed)' % name)
            continue
        recs = records(jsonl)
        if not recs:
            print('== %s: no CheckFrames records' % name)
        for rec in sorted(recs):
            if only is not None and rec != only:
                continue
            code, text = verdict(jsonl, log, rec)
            if code != 0:
                print('== %s recording %d: not analysed (%s)' % (name, rec, text))
                continue
            start = next((d for t, d, _ in recs[rec] if t == 'start'), {})
            hit = start.get('hitRecording')
            hit_verdict = 'none named' if hit is None else ('COMPLETE' if analyze_hits.verdict(jsonl, log, hit)[0] == 0 else 'not complete')
            print('== %s recording %d [COMPLETE]' % (name, rec))
            analyse(recs[rec], hit_verdict)
