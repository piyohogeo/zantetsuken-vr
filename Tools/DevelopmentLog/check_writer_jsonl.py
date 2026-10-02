# The saved records of one development-logger writer checked against the end lines its run wrote to the log (2026-10-02).
# usage: check_writer_jsonl.py <writer_id> <log label> <session.jsonl> <editor or player .log> [--recording R]
#   e.g. check_writer_jsonl.py CutPhysicsStep "step diagnosis" <jsonl> <log>
# A writer's record is one or more recordings (field "recording"; one recording when the field is absent), each with
# seq 1..N and an explicit end: the summary is seq N, its own counts of seq 1..N-1 (attemptedBefore, acceptedBefore), and
# the log line "[<label>] ended: [recording R, ]attempted N ... accepted A, refused: queue full q, unavailable u,
# invalid value i, disabled d" counts seq 1..N.
# Per recording -- COMPLETE: seq 1..N each exactly once, saved = accepted = N, the summary at N with N-1 / A-1, nothing
# refused, nothing after it. PARTIAL: that up to the summary, but records after it. NOT CONFIRMED: anything else (no end
# line, a refusal, a missing or repeated seq, the summary not saved, a tail cut, records of a recording with no end line).
# The run: COMPLETE (exit 0) only if every recording is; PARTIAL (exit 2) if the worst is PARTIAL; else NOT CONFIRMED (exit 1).
import json, re, sys, collections

args = [a for a in sys.argv[1:] if not a.startswith('--')]
writer, label, jsonl, log = args[:4]
only = int(sys.argv[sys.argv.index('--recording') + 1]) if '--recording' in sys.argv else None
streams = collections.defaultdict(list); bad = 0
with open(jsonl, encoding='utf-8') as f:
    for line in f:
        if not line.strip():
            continue
        try:
            r = json.loads(line)
        except ValueError:
            bad += 1; continue
        if r.get('writer_id') != writer:
            continue
        v = r['value']; d = dict(zip(v[0::2], v[1::2]))
        streams[d.get('recording')].append((r, d))
ends = {}
pat = re.compile(r'\[' + re.escape(label) + r'\] ended: (?:recording (\d+), )?attempted (\d+) .*?accepted (\d+), refused: queue full (\d+), unavailable (\d+), invalid value (\d+), disabled (\d+)')
dup_end = []
with open(log, encoding='utf-8', errors='replace') as f:
    for line in f:
        m = pat.search(line)
        if m:
            key = int(m.group(1)) if m.group(1) else None
            if key in ends: dup_end.append(key)
            ends[key] = tuple(int(x) for x in m.groups()[1:])
keys = sorted(set(streams) | set(ends), key=lambda k: -1 if k is None else k)
if only is not None: keys = [k for k in keys if k == only]
rank = {'COMPLETE': 0, 'PARTIAL': 1, 'NOT CONFIRMED': 2}; worst = 'COMPLETE' if keys else 'NOT CONFIRMED'
if bad: print('unreadable lines %d' % bad); worst = 'NOT CONFIRMED'
if not keys: print('no records and no end line of %s' % writer)
for k in keys:
    recs = streams.get(k, []); seqs = [d['seq'] for _, d in recs]; counts = collections.Counter(seqs); problems = []
    if k in dup_end: problems.append('more than one end line')
    after = 0
    if k not in ends:
        problems.append('no end line in the log: counts unknown')
    else:
        att, acc, full, una, inv, dis = ends[k]
        if full or una or inv or dis: problems.append('refused: queue full %d, unavailable %d, invalid %d, disabled %d' % (full, una, inv, dis))
        within = [s for s in seqs if s <= att]
        missing = sorted(set(range(1, att + 1)) - set(within))
        dup = sorted(s for s in set(within) if counts[s] > 1)
        if missing: problems.append('missing seq %d: %s' % (len(missing), missing[:10]))
        if dup: problems.append('repeated seq %s' % dup[:10])
        if len(within) != acc: problems.append('saved up to seq %d: %d, accepted %d' % (att, len(within), acc))
        summ = [d for r, d in recs if r['tag'] == 'summary' and d['seq'] == att]
        if not summ: problems.append('summary (seq %d) not saved' % att)
        elif summ[0]['attemptedBefore'] != att - 1 or summ[0]['acceptedBefore'] != acc - 1:
            problems.append('summary counts %d/%d do not precede seq %d/accepted %d' % (summ[0]['attemptedBefore'], summ[0]['acceptedBefore'], att, acc))
        after = sum(1 for s in seqs if s > att)
    fd = collections.Counter(r['frame'] - d['eventFrame'] for r, d in recs)
    verdict = 'NOT CONFIRMED: ' + '; '.join(problems) if problems else ('PARTIAL: %d records after the summary' % after if after else 'COMPLETE')
    print('recording %s: records %d, end %s, tags %s, logger frame - eventFrame %s -> %s' % (k, len(recs), ends.get(k), dict(collections.Counter(r['tag'] for r, _ in recs)), dict(sorted(fd.items())), verdict))
    v = verdict.split(':')[0]
    if rank[v] > rank[worst]: worst = v
print(worst)
sys.exit({'COMPLETE': 0, 'PARTIAL': 2, 'NOT CONFIRMED': 1}[worst])
