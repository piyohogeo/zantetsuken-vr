#!/usr/bin/env python3
"""Summarize axis-mass and native-bookkeeping ABBA/BAAB batches per process.

  python summarize_micro.py RUN1 RUN2 --output NEW_DIRECTORY
  python summarize_micro.py --root LOG_ROOT --output NEW_DIRECTORY

All timings are elapsed microseconds per call (batch ticks / frequency / iterations).
Processes, cases, and benchmarks are never pooled. Empty batches are reported,
never subtracted. Warmup is separate if recorded; current harnesses omit it.
"""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import re
import shutil
import statistics
import sys

BENCHMARKS = ('axis-mass', 'native-bookkeeping')
AXIS_FIELDS = 'run,case,round,order,version,ticks,freq,iterations'.split(',')
NATIVE_FIELDS = ('run,case,mesh_slots,round,order,region,version,ticks,freq,iterations,'
                 'native_arrays,payload_bytes,cleared_payload_bytes').split(',')


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')


def stat(values):
    ordered = sorted(values)
    if not ordered:
        return {'n': 0}
    at = (len(ordered) - 1) * 0.95
    lo, hi = int(math.floor(at)), int(math.ceil(at))
    return {'n': len(ordered), 'median': statistics.median(ordered),
            'mean': statistics.mean(ordered),
            'p95': ordered[lo] + (ordered[hi] - ordered[lo]) * (at - lo),
            'min': ordered[0], 'max': ordered[-1]}


def parse_env(path):
    text = path.read_text(encoding='utf-8-sig') if path.exists() else ''
    fields = dict(line.split('=', 1) for line in text.splitlines() if '=' in line)
    return text, fields


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig')) if path.exists() else {}


def flag(text, name):
    match = re.search(r'(?:^|[;\n])\s*' + re.escape(name) + r'=(0|1|True|False)(?:;|\s|$)', text)
    return None if match is None else int(match[1] in ('1', 'True'))


def load(folder, archive):
    run_id = folder.name + '-' + hashlib.sha256(str(folder).encode()).hexdigest()[:8]
    target = archive / run_id
    target.mkdir(parents=True)
    files = sorted(p for p in folder.iterdir() if p.is_file() and
                   (p.name in [b + '.csv' for b in BENCHMARKS] or p.suffix in ('.json', '.txt') or p.name == 'results.xml'))
    hashes = {}
    for source in files:
        shutil.copyfile(source, target / source.name)
        hashes[source.name] = {'sha256': sha(source), 'bytes': source.stat().st_size}
    write_json(target / 'archive-manifest.json', hashes)
    command, process = read_json(folder / 'command.json'), read_json(folder / 'process.json')
    exit_info, test_summary = read_json(folder / 'exit.json'), read_json(folder / 'summary.json')
    errors = []
    def require(condition, message):
        if not condition:
            errors.append(message)
    require(exit_info.get('exit_code') == 0, 'Process exit code is not zero or missing')
    require(test_summary.get('result') == 'Passed', 'Test summary is not Passed or missing')
    require(all(str(test_summary.get(k, '0')) == '0' for k in ('failed', 'skipped', 'inconclusive')),
            'Test summary contains failed/skipped/inconclusive tests')
    source_hashes = command.get('source_sha256', {})
    require(bool(source_hashes), 'Missing source hashes')
    _, mass_env = parse_env(folder / 'axis-mass-environment.txt')
    backend_text = mass_env.get('Backend', '')
    backend = ('IL2CPP' if 'IL2CPP' in backend_text else
               'Editor Mono' if 'Editor' in backend_text else 'Unknown')
    result = {'run': run_id, 'input_directory': str(folder), 'backend': backend,
              'process': process, 'command': command, 'source_sha256': source_hashes,
              'source_fingerprint': hashlib.sha256(json.dumps(source_hashes, sort_keys=True).encode()).hexdigest(),
              'archived_files': hashes, 'benchmarks': {}, 'errors': errors}
    for benchmark in BENCHMARKS:
        csv_path = folder / (benchmark + '.csv')
        if not csv_path.exists():
            errors.append('Missing ' + csv_path.name)
            continue
        env_text, env = parse_env(folder / (benchmark + '-environment.txt'))
        reverse, character_first = command.get('reverse'), command.get('character_first')
        env_reverse, env_case = flag(env_text, 'ORDER_REVERSE'), flag(env_text, 'ORDER_CHARACTER_FIRST')
        if reverse is None:
            reverse = env_reverse
        if character_first is None:
            character_first = env_case
        require(reverse in (0, 1), benchmark + ': missing reverse flag')
        require(character_first in (0, 1), benchmark + ': missing case-order flag')
        require(env_reverse is None or reverse == env_reverse, benchmark + ': reverse metadata conflict')
        require(env_case is None or character_first == env_case, benchmark + ': case-order metadata conflict')
        require(bool(env_text), benchmark + ': missing environment')
        with csv_path.open(encoding='utf-8-sig', newline='') as stream:
            reader = csv.DictReader(stream)
            require(reader.fieldnames == (AXIS_FIELDS if benchmark == 'axis-mass' else NATIVE_FIELDS),
                    benchmark + ': unexpected CSV schema')
            raw = list(reader)
        rows = []
        for number, original in enumerate(raw, 2):
            try:
                row = dict(original)
                for key in set(row) - {'run', 'case', 'region', 'version'}:
                    row[key] = int(row[key])
                require(row['freq'] > 0 and row['iterations'] == 1024 and row['ticks'] >= 0,
                        benchmark + ': invalid frequency/iterations/ticks at line ' + str(number))
                if row['freq'] <= 0 or row['iterations'] <= 0:
                    continue
                row['elapsed_us_per_call'] = row['ticks'] * 1e6 / row['freq'] / row['iterations']
                rows.append(row)
            except (ValueError, TypeError, KeyError) as error:
                errors.append('%s line %d: %s' % (benchmark, number, error))
        observed_case_order = list(dict.fromkeys(r['case'] for r in rows))
        expected_case_order = ['character', 'box'] if character_first else ['box', 'character']
        require(observed_case_order == expected_case_order, benchmark + ': CSV case order differs from metadata')
        require(len({r['run'] for r in rows}) == 1, benchmark + ': multiple CSV run labels')
        require(all(r['case'] in ('box', 'character') for r in rows), benchmark + ': unknown case')
        warmup = [r for r in rows if r['round'] < 0]
        measured = [r for r in rows if r['round'] >= 0]
        require(len(measured) == (64 if benchmark == 'axis-mass' else 80), benchmark + ': wrong measured row count')
        record = {'raw_csv_sha256': sha(csv_path), 'environment': env,
                  'reverse': reverse, 'character_first': character_first,
                  'batch_order': 'BAAB' if reverse else 'ABBA', 'case_order': observed_case_order,
                  'warmup': {'rows': warmup, 'recorded': bool(warmup), 'description': env.get('Warmup', '')},
                  'raw_row_count': len(raw), 'cases': {}}
        result['benchmarks'][benchmark] = record
        for case in ('box', 'character'):
            case_rows = [r for r in measured if r['case'] == case]
            main = [r for r in case_rows if r['version'] in ('baseline', 'candidate')]
            empty = [r for r in case_rows if r['version'] == 'empty']
            require(len(main) == 32, benchmark + '/' + case + ': expected 32 measured batches')
            require({r['round'] for r in main} == set(range(8)), benchmark + '/' + case + ': rounds must be 0..7')
            require(len(empty) == (8 if benchmark == 'native-bookkeeping' else 0), benchmark + '/' + case + ': empty batch count')
            require(all(r['version'] in ('baseline', 'candidate', 'empty') for r in case_rows), benchmark + '/' + case + ': unknown version')
            if benchmark == 'native-bookkeeping':
                require({r['round'] for r in empty} == set(range(8)), benchmark + '/' + case + ': empty rounds')
                require(all(r['order'] == -1 and r['region'] == 'empty_batch' for r in empty), benchmark + '/' + case + ': malformed empty batch')
                require(all(r['region'] == 'allocation_release' for r in main), benchmark + '/' + case + ': wrong measured region')
                require(all(r['mesh_slots'] == (2 if case == 'box' else 10) for r in case_rows), benchmark + '/' + case + ': slot count')
                require(all(r['native_arrays'] == (4 if r['version'] == 'baseline' else 1) for r in main), benchmark + '/' + case + ': allocation count')
            rounds = []
            for round_id in range(8):
                group = sorted([r for r in main if r['round'] == round_id], key=lambda r: r['order'])
                expected = ['candidate', 'baseline', 'baseline', 'candidate'] if reverse else ['baseline', 'candidate', 'candidate', 'baseline']
                require([r['order'] for r in group] == list(range(4)) and [r['version'] for r in group] == expected,
                        '%s/%s round%d: not expected ABBA/BAAB' % (benchmark, case, round_id))
                require(len({r['freq'] for r in group}) == 1 and len({r['iterations'] for r in group}) == 1,
                        '%s/%s round%d: batch settings differ' % (benchmark, case, round_id))
                if len(group) != 4 or [r['version'] for r in group] != expected:
                    continue
                baseline_mean = statistics.mean(r['elapsed_us_per_call'] for r in group if r['version'] == 'baseline')
                candidate_mean = statistics.mean(r['elapsed_us_per_call'] for r in group if r['version'] == 'candidate')
                rounds.append({'round': round_id, 'baseline_mean_us': baseline_mean,
                               'candidate_mean_us': candidate_mean,
                               'saving_us': baseline_mean - candidate_mean,
                               'saving_percent': (1 - candidate_mean / baseline_mean) * 100 if baseline_mean else None})
            baseline = stat([r['elapsed_us_per_call'] for r in main if r['version'] == 'baseline'])
            candidate = stat([r['elapsed_us_per_call'] for r in main if r['version'] == 'candidate'])
            med_base, med_candidate = baseline.get('median'), candidate.get('median')
            case_result = {'baseline_us_per_call': baseline, 'candidate_us_per_call': candidate,
                           'median_saving_us': med_base - med_candidate if med_base is not None and med_candidate is not None else None,
                           'median_saving_percent': (1 - med_candidate / med_base) * 100 if med_base and med_candidate is not None else None,
                           'rounds': rounds, 'paired_round_saving_us': stat([r['saving_us'] for r in rounds]),
                           'paired_round_saving_percent': stat([r['saving_percent'] for r in rounds if r['saving_percent'] is not None]),
                           'empty_batch_us_per_call': stat([r['elapsed_us_per_call'] for r in empty])}
            if benchmark == 'native-bookkeeping':
                case_result['allocation_metadata'] = {version: {key: sorted({r[key] for r in main if r['version'] == version})
                    for key in ('mesh_slots', 'native_arrays', 'payload_bytes', 'cleared_payload_bytes')}
                    for version in ('baseline', 'candidate')}
            record['cases'][case] = case_result
    result['valid'] = not errors
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('runs', nargs='*', type=Path)
    parser.add_argument('--root', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if bool(args.runs) == bool(args.root):
        parser.error('Specify run directories OR --root')
    runs = sorted({p.resolve() for p in (args.runs if args.runs else
        [p.parent for p in args.root.resolve().rglob('axis-mass.csv') if not (p.parent / 'archive-manifest.json').exists()])})
    if not runs:
        parser.error('No runs found')
    output = args.output.resolve()
    if any(output == run or run in output.parents for run in runs):
        parser.error('Output must not be inside an input run')
    output.mkdir(parents=True, exist_ok=False)
    results = [load(run, output / 'raw') for run in runs]
    report = {'method': {'script_sha256': sha(Path(__file__)), 'units': 'elapsed microseconds per call',
        'median': 'median of all 16 batch means per version/case/process',
        'median_saving_percent': '100 * (1 - candidate median / baseline median)',
        'paired_round': 'mean of two baseline batches minus mean of two candidate batches in the same round; 8 differences',
        'p95': 'linear interpolation at (n-1)*0.95', 'process_pooling': False,
        'outlier_removal': False, 'empty_batch_subtraction': False,
        'warmup': 'not recorded by current harnesses; negative-round rows, if present, stored separately',
        'scope': 'local mass arithmetic or native allocation/release, not full cutting cost or CPU time',
        'inference': 'descriptive measurements only; no significance or causality claim'}, 'runs': results}
    write_json(output / 'summary.json', report)
    markdown = ['Per-process local microbenchmarks. All values below are elapsed us per call.', '',
        'Each version uses 16 retained batch means; paired savings use 8 same-round differences. '
        'Positive savings indicate a lower candidate time. Processes and warmup are separate; empty batches are not subtracted.', '',
        '| Run | Backend | Probe | Case | Order / cases | Baseline median | Candidate median | Saving % | Paired round median saving |',
        '|---|---|---|---|---|---:|---:|---:|---:|']
    for result in results:
        if not result['valid']:
            continue
        for name, bench in result['benchmarks'].items():
            for case, values in bench['cases'].items():
                markdown.append('| %s | %s | %s | %s | %s / %s | %.6f | %.6f | %.2f | %.6f |' %
                    (result['run'], result['backend'], name, case, bench['batch_order'], ','.join(bench['case_order']),
                     values['baseline_us_per_call']['median'], values['candidate_us_per_call']['median'],
                     values['median_saving_percent'], values['paired_round_saving_us']['median']))
    invalid = [r['run'] for r in results if not r['valid']]
    markdown += ['', 'Invalid runs (retained as evidence, excluded from this table): ' + (', '.join(invalid) or 'none') + '.', '',
        'Raw CSVs, environment, process/command records and hashes are preserved under raw/. '
        'See summary.json for every round, calibration, validation and source fingerprint. '
        'Warmup batches were not saved by the current harnesses. These are descriptive results, not full Request/Final handoff measurements.', '']
    (output / 'summary.md').write_text('\n'.join(markdown), encoding='utf-8')
    print(json.dumps({'output': str(output), 'runs': len(results), 'invalid_runs': invalid}, indent=2))
    return 2 if invalid else 0


if __name__ == '__main__':
    sys.exit(main())
