#!/usr/bin/env python3
"""Summarize local Build/Publish order diagnostics without pooling processes.

Examples:
  python summarize_order.py path/to/run00 path/to/run11 --output path/to/summary
  python summarize_order.py --root path/to/logs --output path/to/summary

Raw rows and metadata are archived unchanged. Warmup (round -1) is separate.
Pair durations are formed within each sample before computing statistics. p95
uses linear interpolation at (n-1)*0.95; small-sample p95 is descriptive only.
No empty-span subtraction, outlier removal, significance test, or CPU-time
conversion is performed. Thread cycles remain cycles, never nanoseconds.
"""
import argparse
import csv
import hashlib
import json
import math
from collections import Counter, defaultdict
from pathlib import Path
import shutil
import sys

FIELDS = ('case,case_order,round,order,mode,warmup,span_id,span,ticks,thread_cycles,count,'
          'frequency,positive_colliders,negative_colliders,build_positive_first,'
          'activate_positive_first,published,validated,cleaned').split(',')
INT_FIELDS = [name for name in FIELDS if name not in ('case', 'span')]
SPANS = ['buildtotal', 'objects+', 'objects-', 'colliders+', 'colliders-',
         'publishtotal', 'SetActive+', 'SetActive-', 'BuildSide+', 'BuildSide-',
         'reserved10', 'reserved11']
PAIRS = {'objects_pair': (1, 2), 'colliders_pair': (3, 4),
         'SetActive_pair': (6, 7), 'BuildSide_pair': (8, 9), 'BuildPublish': (0, 5)}
STAGES = {'objects': (1, 2, 'build'), 'colliders': (3, 4, 'build'),
          'BuildSide': (8, 9, 'build'), 'SetActive': (6, 7, 'activation')}
LATIN = ((0, 1, 3, 2), (1, 2, 0, 3), (2, 3, 1, 0), (3, 0, 2, 1))
COMMON = ['run', 'backend', 'source_fingerprint', 'case', 'phase', 'mode', 'probe_schema']


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')


def save_csv(path, rows, fields):
    with path.open('w', encoding='utf-8', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=fields, extrasaction='ignore')
        writer.writeheader()
        writer.writerows(rows)


def statistics(values):
    values = sorted(values)
    def quantile(fraction):
        at = (len(values) - 1) * fraction
        lo = int(math.floor(at))
        hi = int(math.ceil(at))
        return values[lo] + (values[hi] - values[lo]) * (at - lo)
    return {'n': len(values), 'median': quantile(0.5), 'p95': quantile(0.95),
            'min': values[0], 'max': values[-1]}


def metrics(rows, prefix=''):
    result = {}
    for field in ('elapsed_us', 'thread_cycles'):
        for key, value in statistics([row[field] for row in rows]).items():
            result[prefix + field + '_' + key] = value
    return result


def environment(path):
    if not path.exists():
        return {}
    return dict(line.split('=', 1) for line in path.read_text(encoding='utf-8-sig').splitlines()
                if '=' in line)


def sign(value):
    return (value > 0) - (value < 0)


def load_run(folder, archive):
    run_id = folder.name + '-' + hashlib.sha256(str(folder).encode()).hexdigest()[:8]
    target = archive / run_id
    target.mkdir(parents=True)
    manifest = {}
    paths = [folder / 'order.csv']
    paths += sorted(p for p in folder.iterdir() if p.is_file() and
                    (p.suffix in ('.json', '.txt') or p.name == 'results.xml'))
    for path in paths:
        shutil.copyfile(path, target / path.name)
        manifest[path.name] = {'sha256': digest(path), 'bytes': path.stat().st_size}
    save_json(target / 'archive-manifest.json', manifest)
    errors, warnings = [], []
    def require(test, message):
        if not test:
            errors.append(message)
    def read_json(name):
        path = folder / name
        if not path.exists():
            errors.append('Missing ' + name)
            return {}
        try:
            return json.loads(path.read_text(encoding='utf-8-sig'))
        except (ValueError, OSError) as error:
            errors.append(name + ': ' + str(error))
            return {}
    command = read_json('command.json')
    process = read_json('process.json')
    exit_info = read_json('exit.json')
    summary = read_json('summary.json')
    require(exit_info.get('exit_code') == 0, 'Process did not finish with exit code 0')
    require(summary.get('result') == 'Passed', 'Test result is not Passed')
    for field in ('failed', 'inconclusive', 'skipped'):
        require(str(summary.get(field, '0')) == '0', 'Test result has ' + field)
    require((folder / 'order-complete.txt').exists(), 'Missing order-complete.txt')
    require((folder / 'order-environment.txt').exists(), 'Missing order-environment.txt')
    env = environment(folder / 'order-environment.txt')
    mass_env = environment(folder / 'axis-mass-environment.txt')
    backend_text = mass_env.get('Backend', '') + ';' + env.get('Backend', '')
    backend = ('IL2CPP' if 'IL2CPP' in backend_text else
               'Editor Mono' if 'Editor' in backend_text else 'Unknown Player')
    if backend == 'Unknown Player':
        warnings.append('No explicit scripting-backend evidence; kept as Unknown Player')
    sources = command.get('source_sha256', {})
    require(bool(sources), 'Missing source_sha256 evidence')
    fingerprint = hashlib.sha256(json.dumps(sources, sort_keys=True).encode()).hexdigest()
    base = {'run': run_id, 'backend': backend, 'source_fingerprint': fingerprint}
    with (folder / 'order.csv').open(encoding='utf-8-sig', newline='') as stream:
        reader = csv.DictReader(stream)
        require(reader.fieldnames == FIELDS, 'Unexpected order.csv schema')
        raw = list(reader)
    rows = []
    for number, raw_row in enumerate(raw, 2):
        try:
            row = dict(raw_row)
            for field in INT_FIELDS:
                row[field] = int(row[field])
            rows.append(row)
        except (ValueError, TypeError, KeyError) as error:
            errors.append('CSV line %d: %s' % (number, error))
    new_root = {r['span'] for r in rows if r['span_id'] in (10, 11)} == {'newRoot+', 'newRoot-'}
    span_names = SPANS[:10] + ['newRoot+', 'newRoot-'] if new_root else SPANS
    base['probe_schema'] = 'newRoot' if new_root else 'reserved'
    require(len(rows) == 864, 'Expected 864 rows (72 samples x 12 spans), got %d' % len(rows))
    samples = defaultdict(list)
    for row in rows:
        key = tuple(row[k] for k in ('case', 'case_order', 'round', 'order', 'mode'))
        samples[key].append(row)
    require(len(samples) == 72, 'Expected 72 distinct samples, got %d' % len(samples))
    distributions = Counter()
    reverse = command.get('reverse')
    character_first = command.get('character_first')
    require(reverse in (0, 1) and character_first in (0, 1), 'Missing/invalid runner order flags')
    valid_samples = []
    for key, sample in sorted(samples.items()):
        case, case_order, round_id, order, mode = key
        label = repr(key)
        before = len(errors)
        require(case in ('box', 'character'), label + ': unknown case')
        require(mode in range(4) and order in range(4) and case_order in range(2), label + ': invalid order/mode')
        require(round_id in range(-1, 8), label + ': invalid round')
        require(sorted(r['span_id'] for r in sample) == list(range(12)), label + ': spans missing/duplicate')
        require(len({r['frequency'] for r in sample}) == 1 and sample[0]['frequency'] > 0,
                label + ': bad frequency')
        metadata = [name for name in INT_FIELDS if name not in ('span_id', 'ticks', 'thread_cycles', 'count')]
        require(all(all(r[name] == sample[0][name] for r in sample) for name in metadata),
                label + ': inconsistent sample metadata')
        for row in sample:
            span = row['span_id']
            require(0 <= span < 12 and row['span'] == span_names[span], label + ': span name/id mismatch')
            require(row['count'] == (1 if 0 <= span < (12 if new_root else 10) else 0), label + ': wrong span count ' + str(span))
            require(row['ticks'] >= 0 and row['thread_cycles'] >= 0, label + ': negative timing')
            require(row['published'] == row['validated'] == row['cleaned'] == 1, label + ': sample not published/validated/cleaned')
            require(row['warmup'] == int(round_id == -1), label + ': warmup mismatch')
            require(row['build_positive_first'] == int((mode & 1) == 0), label + ': build order mismatch')
            require(row['activate_positive_first'] == int((mode & 2) != 0), label + ': activation order mismatch')
            require((row['positive_colliders'], row['negative_colliders']) == ((15, 9) if case == 'character' else (1, 1)),
                    label + ': collider counts mismatch')
            if span >= 10 and not new_root:
                require(row['ticks'] == row['thread_cycles'] == 0, label + ': reserved span is not empty')
        if reverse in (0, 1) and mode in range(4) and order in range(4) and round_id in range(-1, 8):
            require(mode == LATIN[0 if round_id < 0 else round_id % 4][3 - order if reverse else order],
                    label + ': Latin order mismatch')
        if character_first in (0, 1):
            require((case == 'character') == ((case_order == 0) == bool(character_first)), label + ': case order mismatch')
        if len(errors) == before:
            by_span = {r['span_id']: r for r in sample}
            nested = [((8, 9), 0), ((1, 3), 8), ((2, 4), 9), ((6, 7), 5)]
            if new_root:
                nested += [((10,), 1), ((11,), 2)]
            for children, parent in nested:
                require(sum(by_span[i]['ticks'] for i in children) <= by_span[parent]['ticks'],
                        label + ': nested elapsed spans exceed parent')
            distributions[(case, mode, 'warmup' if round_id < 0 else 'measured')] += 1
            valid_samples.append((key, by_span))
    for case in ('box', 'character'):
        for mode in range(4):
            for phase, expected in (('warmup', 1), ('measured', 8)):
                require(distributions[(case, mode, phase)] == expected,
                        '%s mode%d %s: expected %d valid samples, got %d' %
                        (case, mode, phase, expected, distributions[(case, mode, phase)]))
    metadata = dict(base, input_directory=str(folder), process=process, command=command,
                    environment=env, backend_evidence=backend_text, archived_files=manifest,
                    valid=not errors, errors=sorted(set(errors)), warnings=warnings,
                    samples=len(samples), rows=len(raw), valid_sample_counts=[
                        {'case': key[0], 'mode': key[1], 'phase': key[2], 'n': value}
                        for key, value in sorted(distributions.items())])
    save_json(target / 'validation.json', metadata)
    return base, raw, valid_samples if not errors else [], metadata


def summarize(base, samples):
    sample_rows, differences, summaries, comparisons, tracking = [], [], [], [], []
    new_root = base['probe_schema'] == 'newRoot'
    span_names = SPANS[:10] + ['newRoot+', 'newRoot-'] if new_root else SPANS[:10]
    pairs = dict(PAIRS)
    stages = dict(STAGES)
    if new_root:
        pairs['newRoot_pair'] = (10, 11)
        stages['newRoot'] = (10, 11, 'build')
    for key, spans in samples:
        case, case_order, round_id, order, mode = key
        common = dict(base, case=case, case_order=case_order, round=round_id, order=order,
                      mode=mode, phase='warmup' if round_id < 0 else 'measured')
        groups = {name: (i,) for i, name in enumerate(span_names)}
        groups.update(pairs)
        for name, ids in groups.items():
            sample_rows.append(dict(common, span=name,
                elapsed_us=sum(spans[i]['ticks'] for i in ids) * 1e6 / spans[0]['frequency'],
                thread_cycles=sum(spans[i]['thread_cycles'] for i in ids)))
        for stage, (positive, negative, kind) in stages.items():
            positive_first = spans[0]['build_positive_first' if kind == 'build' else 'activate_positive_first']
            for metric, scale in (('elapsed_us', 1e6 / spans[0]['frequency']), ('thread_cycles', 1)):
                field = 'ticks' if metric == 'elapsed_us' else 'thread_cycles'
                delta = (spans[positive][field] - spans[negative][field]) * scale
                differences.append(dict(common, stage=stage, order_kind=kind, metric=metric,
                    positive_ordinal=1 if positive_first else 2, negative_ordinal=2 if positive_first else 1,
                    positive_build_ordinal=1 if spans[0]['build_positive_first'] else 2,
                    negative_build_ordinal=2 if spans[0]['build_positive_first'] else 1,
                    positive_activation_ordinal=1 if spans[0]['activate_positive_first'] else 2,
                    negative_activation_ordinal=2 if spans[0]['activate_positive_first'] else 1,
                    positive_colliders=spans[0]['positive_colliders'], negative_colliders=spans[0]['negative_colliders'],
                    positive_minus_negative=delta, first_minus_second=delta if positive_first else -delta))
    buckets = defaultdict(list)
    for row in sample_rows:
        buckets[tuple(row[k] for k in COMMON) + (row['span'],)].append(row)
    for key, rows in sorted(buckets.items()):
        summaries.append(dict(zip(COMMON + ['span'], key), **metrics(rows)))
    for key, rows in sorted(buckets.items()):
        values = dict(zip(COMMON + ['span'], key))
        baseline_key = tuple(0 if i == 5 else part for i, part in enumerate(key))
        baseline_rows = buckets[baseline_key]
        baseline_by_round = {r['round']: r for r in baseline_rows}
        deltas = [{'elapsed_us': row['elapsed_us'] - baseline_by_round[row['round']]['elapsed_us'],
                   'thread_cycles': row['thread_cycles'] - baseline_by_round[row['round']]['thread_cycles']}
                  for row in rows]
        comparison = dict(values, **metrics(deltas, 'paired_round_delta_'))
        for field in ('elapsed_us', 'thread_cycles'):
            current = statistics([r[field] for r in rows])['median']
            baseline = statistics([r[field] for r in baseline_rows])['median']
            comparison[field + '_delta_of_medians'] = current - baseline
            comparison[field + '_delta_of_medians_percent'] = (current / baseline - 1) * 100 if baseline else ''
        comparisons.append(comparison)
    difference_groups = defaultdict(list)
    for row in differences:
        difference_groups[tuple(row[k] for k in COMMON) + (row['stage'], row['metric'])].append(row)
    difference_summary = []
    for key, rows in sorted(difference_groups.items()):
        row = dict(zip(COMMON + ['stage', 'metric'], key))
        for field in ('positive_minus_negative', 'first_minus_second'):
            row.update({field + '_' + name: value for name, value in statistics([r[field] for r in rows]).items()})
        row.update({field: rows[0][field] for field in ('order_kind', 'positive_ordinal', 'negative_ordinal', 'positive_colliders', 'negative_colliders')})
        difference_summary.append(row)
    index = {(r['case'], r['phase'], r['mode'], r['stage'], r['metric']): r for r in difference_summary}
    for case in ('box', 'character'):
        for phase in ('warmup', 'measured'):
            for stage, (_, _, kind) in stages.items():
                pairs = ((0, 1), (2, 3)) if kind == 'build' else ((0, 2), (1, 3))
                for before, after in pairs:
                    for metric in ('elapsed_us', 'thread_cycles'):
                        a = index[(case, phase, before, stage, metric)]
                        b = index[(case, phase, after, stage, metric)]
                        pa, pb = a['positive_minus_negative_median'], b['positive_minus_negative_median']
                        fa, fb = a['first_minus_second_median'], b['first_minus_second_median']
                        tracking.append(dict(base, case=case, phase=phase, stage=stage, metric=metric,
                            order_kind=kind, mode_before=before, mode_after=after,
                            positive_minus_negative_before=pa, positive_minus_negative_after=pb,
                            first_minus_second_before=fa, first_minus_second_after=fb,
                            side_difference_sign_reverses=sign(pa) * sign(pb) < 0,
                            ordinal_difference_sign_agrees=sign(fa) == sign(fb) and sign(fa) != 0))
    return sample_rows, summaries, differences, difference_summary, comparisons, tracking


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('runs', nargs='*', type=Path)
    parser.add_argument('--root', type=Path, help='Recursively find directories containing order.csv')
    parser.add_argument('--output', required=True, type=Path, help='New output directory; never overwrite evidence')
    args = parser.parse_args()
    if bool(args.runs) == bool(args.root):
        parser.error('Specify run directories OR --root')
    output = args.output.resolve()
    runs = sorted({p.resolve() for p in (args.runs if args.runs else
                   [path.parent for path in args.root.resolve().rglob('order.csv')
                    if not (path.parent / 'archive-manifest.json').exists()])})
    if not runs:
        parser.error('No runs found')
    if any(not (run / 'order.csv').is_file() for run in runs):
        parser.error('Each specified run must contain order.csv')
    if any(output == run or run in output.parents for run in runs):
        parser.error('Output must not be inside an input run')
    output.mkdir(parents=True, exist_ok=False)
    raw_all, validation = [], []
    collections = [[] for _ in range(6)]
    for run in runs:
        base, raw, samples, meta = load_run(run, output / 'raw')
        validation.append(meta)
        raw_all.extend(dict(base, **row) for row in raw)
        if samples:
            for combined, rows in zip(collections, summarize(base, samples)):
                combined.extend(rows)
    save_csv(output / 'raw-all.csv', raw_all, ['run', 'backend', 'source_fingerprint', 'probe_schema'] + FIELDS)
    names = ['sample-spans', 'run-summary', 'sample-side-differences', 'run-side-differences',
             'run-mode-comparisons', 'run-order-tracking']
    for name, rows in zip(names, collections):
        save_csv(output / (name + '.csv'), rows, list(rows[0]) if rows else ['run', 'backend'])
    save_json(output / 'validation.json', validation)
    save_json(output / 'method.json', {
        'script_sha256': digest(Path(__file__)), 'input_runs': [str(run) for run in runs],
        'process_pooling': False, 'warmup_separate': True, 'outlier_removal': False,
        'p95': 'linear interpolation at (n-1)*0.95', 'cycles_unit': 'thread cycles, not nanoseconds',
        'sample_sums': dict({name: list(ids) for name, ids in PAIRS.items()}, newRoot_pair=[10, 11]),
        'probe_schemas': 'reserved: initial wiring smoke, no newRoot observations; newRoot: span10/11 measured inside objects',
        'mode_comparison': 'same-run same-case same-round difference from mode 0; execution times differ',
        'order_tracking': 'descriptive sign checks, not a significance or causality claim',
        'scope': 'local TryBuild/TryPublish; excludes setup, full Request, worker, rendering and Final handoff',
        'limitations': ['Character has 15 positive versus 9 negative colliders.',
                       'Warmup has one sample per mode/case/process.',
                       'Eight measured rounds per mode/case/process; no cross-process raw pooling.',
                       'Elapsed includes interruptions; cycles include kernel/user thread activity.',
                       'Mode activation reversal changes which connected joint body is active first.',
                       'Source hashes are runner snapshots; Player binary provenance needs build evidence.']})
    invalid = [meta['run'] for meta in validation if not meta['valid']]
    print(json.dumps({'output': str(output), 'runs': len(runs), 'invalid_runs': invalid}, indent=2))
    return 2 if invalid else 0


if __name__ == '__main__':
    sys.exit(main())
