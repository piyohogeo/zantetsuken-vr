"""The check's observed hits (writer CheckHits, SandboxPropSlashPlayerCheck.HitLog.cs), per recording: one recording per check
walk (each iteration of -zantetsuPropIterations). A recording is used only if its saved records are complete against its
"[check hits] ended" line in the log (check_writer_jsonl.py, beside this file); otherwise it is reported and not analysed.
A complete record is a statement about the record only: whether the check's scenario passed is its own verdict in the log
("PROP SLASH: ok" / "FAILED").

Fields of a hit record: eventFrame -- the frame the check observed the hit in (its LateUpdate reading the detector's new
hit list), the same value as the old multi-hits.csv / building-hits.csv "frame"; not the frame the hit was evaluated in
(not recorded). at -- the hit's input time on the replay's input clock (seconds), not the Stopwatch of "seconds". side --
+1 / -1 for a side of a Provisional pair, 0 for a published owner. update -- the recorder's replay index minus one at the
observation. child -- 1 when the fragment has an origin.

  python analyze_hits.py <session.jsonl>:<log> [--recording R]

Other readers: hits_for_run(run_dir, check_dir[, log]) finds the session file and recording whose start names check_dir (the
check's output directory), checks it against run_dir/player.log and returns its hit records (raises NotComplete otherwise).
"""
import collections, datetime, glob, json, os, subprocess, sys

CHECK = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'check_writer_jsonl.py')
LOGGER = r'C:\log\zantetsuken-vr\logger'
WRITER, LABEL = 'CheckHits', 'check hits'


class NotComplete(Exception):
    pass


def records(jsonl):
    """The CheckHits records of a session file, by recording: [(tag, fields, logger frame)] in file order."""
    out = collections.defaultdict(list)
    with open(jsonl, encoding='utf-8') as f:
        for line in f:
            if '"writer_id":"CheckHits"' not in line:
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


def hits_for_run(run_dir, check_dir, log=None):
    """The hit records of the recording whose start names check_dir, from a session file written while the run's log was
    (run_dir/player.log unless log is given), checked against that log."""
    log = log or os.path.join(run_dir, 'player.log')
    first = datetime.datetime.utcfromtimestamp(os.path.getctime(log)) - datetime.timedelta(minutes=2)
    last = datetime.datetime.utcfromtimestamp(os.path.getmtime(log)) + datetime.timedelta(minutes=1)
    want = os.path.normcase(os.path.abspath(check_dir))
    found = []
    for path in sorted(glob.glob(os.path.join(LOGGER, '*.jsonl'))):
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
        raise NotComplete('%d recordings name %s (one expected)' % (len(found), check_dir))
    path, rec, rs = found[0]
    code, text = verdict(path, log, rec)
    if code != 0:
        raise NotComplete(text)
    return [d for t, d, _ in rs if t == 'hit']


def analyse(rs):
    start = next((d for t, d, _ in rs if t == 'start'), {})
    hits = [(d, f) for t, d, f in rs if t == 'hit']
    summary = next((d for t, d, _ in rs if t == 'summary'), {})
    print('   start: mode %s, iteration %s, directory %s' % (start.get('mode'), start.get('iteration'), start.get('directory')))
    print('   hits %d (summary says %s); eventFrame is the frame the check observed each hit in (the old CSV\'s frame); update is the'
          ' replay index - 1 at that observation (not an input-side update id); at is on the replay input clock' % (len(hits), summary.get('hits')))
    kinds = collections.Counter((('child' if d['child'] else 'root'), d['acceptance']) for d, _ in hits)
    print('   by child/root and acceptance: %s' % dict(sorted(kinds.items())))
    slashes = collections.Counter(d['slashId'] for d, _ in hits)
    print('   Slashes %d, hits per Slash max %d' % (len(slashes), max(slashes.values()) if slashes else 0))
    per = collections.Counter(d['eventFrame'] for d, _ in hits)
    print('   observed frames with hits %d, hits per observed frame max %d' % (len(per), max(per.values()) if per else 0))
    lag = collections.Counter(f - d['eventFrame'] for d, f in hits)
    print('   logger frame - eventFrame: %s' % dict(sorted(lag.items())))
    if summary:
        calls = max(1, summary['attemptedBefore'])
        print('   record cost (write_log): %d calls, %.6f s in all; first call %.6f s; mean of one %.6f s, max of one %.6f s; %d frames with'
              ' records, max of one frame\'s sum %.6f s; allocated bytes %s (0 = not counted by the runtime)' % (
                  summary['attemptedBefore'], summary['writeSeconds'], summary.get('writeFirstSeconds', float('nan')), summary['writeSeconds'] / calls,
                  summary['writeMaxSeconds'], summary.get('writeFrames', -1), summary.get('writeFrameMaxSeconds', float('nan')), summary['writeAllocatedBytes']))
    print('   (a complete record is not the check\'s verdict: see "PROP SLASH" in the log)')


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
            print('== %s: no CheckHits records' % name)
        for rec in sorted(recs):
            if only is not None and rec != only:
                continue
            code, text = verdict(jsonl, log, rec)
            if code != 0:
                print('== %s recording %d: not analysed (%s)' % (name, rec, text))
                continue
            print('== %s recording %d [COMPLETE]' % (name, rec))
            analyse(recs[rec])
