"""Per diagnosis run: the decisions recorded inside the target case (between its begin and end marks), counted by reason,
with the clock, budget and estimate they read; and the state at the target's begin.
Reads the development logger's session file (records of writer CutPhysicsStep, 2026-10-02), each run given as
<session.jsonl>:<editor.log>. A run is analysed only if its saved records are complete against the counts in its Editor's
log (check_writer_jsonl.py, beside this file); a run whose records are missing or cannot be checked is reported and not analysed."""
import collections, json, os, statistics, subprocess, sys
CHECK = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'check_writer_jsonl.py')
TARGET = 'TheStep_IsDecidedOncePerFrame'
for arg in sys.argv[1:]:
    cut = arg.lower().rfind('.jsonl:')   # <session.jsonl>:<log>, absolute paths with drive letters included
    jsonl, log = (arg[:cut + 6], arg[cut + 7:]) if cut >= 0 else (arg, '')
    run = os.path.basename(jsonl)
    if not log:
        print('== %s: not analysed (no Editor log given: completeness unconfirmed)' % run); continue
    chk = subprocess.run([sys.executable, CHECK, 'CutPhysicsStep', 'step diagnosis', jsonl, log], capture_output=True, text=True)
    verdict = chk.stdout.strip().splitlines()[-1] if chk.stdout.strip() else chk.stderr.strip()
    if chk.returncode != 0:
        print('== %s: not analysed (%s)' % (run, verdict)); continue
    inside = False; rows = []; begin_state = end_mark = None
    for line in open(jsonl, encoding='utf-8'):
        rec = json.loads(line)
        if rec['writer_id'] != 'CutPhysicsStep':
            continue
        v = rec['value']; d = dict(zip(v[0::2], v[1::2]))
        if rec['tag'] == 'mark':
            if d['what'] == 'begin' and TARGET in d['name']:
                inside = True; begin_state = d['state']
            elif d['what'] == 'end' and TARGET in d['name']:
                inside = False; end_mark = 'end %s -> %s' % (d['name'], d['outcome'])
        elif rec['tag'] == 'decision' and inside:
            rows.append(d)
    c = collections.Counter(r['reason'] for r in rows)
    f = lambda k: [r[k] for r in rows]
    print('== %s: %s [%s]' % (run, end_mark, verdict))
    print('   decisions in the case %d: %s' % (len(rows), dict(c)))
    if rows:
        rem, exp, owed = f('remaining'), f('expected'), f('owed')
        print('   remaining s: min %.5f median %.5f max %.5f | expected s: min %.6f median %.6f max %.6f | owed s: min %.5f max %.5f | step s %s'
              % (min(rem), statistics.median(rem), max(rem), min(exp), statistics.median(exp), max(exp), min(owed), max(owed), rows[0]['stepSeconds']))
        print('   frames %s..%s, seconds %.3f..%.3f (%.1f ms a frame), cost samples %s..%s, skipped in a row max %s'
              % (rows[0]['eventFrame'], rows[-1]['eventFrame'], rows[0]['seconds'], rows[-1]['seconds'],
                 1000 * (rows[-1]['seconds'] - rows[0]['seconds']) / max(1, len(rows) - 1), rows[0]['costSamples'], rows[-1]['costSamples'],
                 max(r['skippedInARow'] for r in rows)))
        print('   first 6 decisions: ' + ' | '.join('%s %s rem %.4f exp %.5f owed %.4f' % (r['eventFrame'], r['reason'], r['remaining'], r['expected'], r['owed']) for r in rows[:6]))
    print('   state at begin: ' + (begin_state or '')[:330])
