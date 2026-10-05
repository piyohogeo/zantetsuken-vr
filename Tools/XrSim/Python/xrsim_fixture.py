"""Quality checks and verification for Meta XR Simulator VRS fixtures (Tools/XrSim).

Subcommands (each prints a JSON report and exits 0 when the check passes, 1 when it fails, 2 on bad input):

  precheck      Input delivery and camera follow before a recording: the head must move and come back after the
                nudge keys, and the Unity camera must follow it (camera local pose == XR head pose).
  qc-recording  Quality of a generated recording, from the hold run the tools recorded it in: VRS length, three
                distinct stationary head poses with enough travel, camera follow in every recorded frame, eye
                matrices consistent with the camera, Simulator version, Player conditions. Writes fixture.json
                (status "candidate" or "rejected") and targets.json (the pose targets for replay runs).
  verify        Verification of a candidate from replay runs (official session_capture replay) and no-VRS runs:
                pose-ordered captures during the replay, camera follow, recording vs replay vs no-VRS head poses,
                same-path noise and VP3 vs VP3C per eye (differing pixels, largest connected region, brightened /
                darkened regions), shadow atlas differences and VP3C selection counts. Writes verification.json and
                verification.md; status "verified" only when every pose has enough valid VP3 and VP3C captures and
                the no-VRS runs do not show the recorded non-default poses. Image differences are reported, not gated.
"""
import argparse
import csv
import datetime
import hashlib
import itertools
import json
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

FOLLOW_M = 0.002
FOLLOW_DEG = 0.2
PIXEL_THRESHOLD = 32
LUMA_DELTA = 24
DEFAULT_HEAD = np.array([0.0, 1.7, 0.0])


# ---------------------------------------------------------------------------------------------------------------------
# small helpers
# ---------------------------------------------------------------------------------------------------------------------
def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def sha256(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as handle:
        for block in iter(lambda: handle.read(1 << 20), b''):
            digest.update(block)
    return digest.hexdigest().upper()


def load_json(path):
    with open(path, encoding='utf-8-sig') as handle:
        return json.load(handle)


def write_json(path, data):
    with open(path, 'w', encoding='utf-8', newline='\n') as handle:
        json.dump(data, handle, indent=2)


def read_frames(path):
    with open(path, newline='') as handle:
        return list(csv.DictReader(handle))


def pos(row, prefix):
    return np.array([float(row[prefix + 'px']), float(row[prefix + 'py']), float(row[prefix + 'pz'])])


def rot(row, prefix):
    return np.array([float(row[prefix + 'qx']), float(row[prefix + 'qy']), float(row[prefix + 'qz']), float(row[prefix + 'qw'])])


def angle(qa, qb):
    return math.degrees(2 * math.acos(min(1.0, abs(float(np.dot(np.asarray(qa, float), np.asarray(qb, float)))))))


def matrix(row, name):
    return np.array([[float(row['%s_m%d%d' % (name, r, c)]) for c in range(4)] for r in range(4)])


def eye_positions(row):
    return [np.linalg.inv(matrix(row, name))[:3, 3] for name in ('left_view', 'right_view')]


def report(data, ok):
    data['passed'] = bool(ok)
    print(json.dumps(data, indent=2))
    return 0 if ok else 1


# ---------------------------------------------------------------------------------------------------------------------
# precheck
# ---------------------------------------------------------------------------------------------------------------------
def cmd_precheck(args):
    rows = [r for r in read_frames(args.frames) if int(r['frame']) >= args.from_frame]
    result = {'check': 'precheck', 'rows': len(rows)}
    if len(rows) < 10:
        result['reasons'] = ['too few frames after the nudge started (%d)' % len(rows)]
        return report(result, False)
    start = pos(rows[0], 'head_')
    moves = [float(np.linalg.norm(pos(r, 'head_') - start)) for r in rows]
    follow_m = max(float(r['follow_error_m']) for r in rows)
    follow_deg = max(float(r['follow_error_deg']) for r in rows)
    result.update({'max_move_m': max(moves), 'final_offset_m': moves[-1], 'max_follow_error_m': follow_m, 'max_follow_error_deg': follow_deg})
    reasons = []
    if max(moves) < args.min_move:
        reasons.append('input not delivered to the Simulator: the head moved %.3f m (needs >= %.3f m); focus or input target lost' % (max(moves), args.min_move))
    if moves[-1] > args.max_return:
        reasons.append('head did not return after the nudge (offset %.3f m)' % moves[-1])
    if follow_m > FOLLOW_M or follow_deg > FOLLOW_DEG:
        reasons.append('camera not following the head: %.4f m / %.3f deg' % (follow_m, follow_deg))
    result['reasons'] = reasons
    return report(result, not reasons)


# ---------------------------------------------------------------------------------------------------------------------
# recording QC
# ---------------------------------------------------------------------------------------------------------------------
def plateaus(rows, min_seconds, still_m=0.001, still_deg=0.05):
    """Stationary head segments (every frame within still tolerances of the segment's first frame) of min_seconds."""
    segments = []
    start = 0
    for i in range(1, len(rows) + 1):
        moved = i == len(rows) or (np.linalg.norm(pos(rows[i], 'head_') - pos(rows[start], 'head_')) > still_m
                                   or angle(rot(rows[i], 'head_'), rot(rows[start], 'head_')) > still_deg)
        if moved:
            t0, t1 = float(rows[start]['realtime_s']), float(rows[i - 1]['realtime_s'])
            if t1 - t0 >= min_seconds:
                segments.append({'first': start, 'last': i - 1, 't0': t0, 't1': t1,
                                 'position': pos(rows[start], 'head_'), 'rotation': rot(rows[start], 'head_')})
            start = i
    merged = []
    for segment in segments:
        if merged and np.linalg.norm(segment['position'] - merged[-1]['position']) < 0.01 and angle(segment['rotation'], merged[-1]['rotation']) < 0.5:
            merged[-1]['last'], merged[-1]['t1'] = segment['last'], segment['t1']
        else:
            merged.append(segment)
    return merged


def cmd_qc_recording(args):
    run = Path(args.run_dir)
    context = load_json(args.context)
    reasons = []
    out = {'check': 'qc-recording', 'run_dir': str(run)}
    try:
        environment = load_json(run / 'environment.json')
        ready = load_json(run / 'ready.json')
        result = load_json(run / 'result.json')
        rows = read_frames(run / 'frames.csv')
    except (OSError, ValueError) as error:
        out['reasons'] = ['harness output missing or unreadable: %s' % error]
        return report(out, False)

    vrs = Path(args.vrs)
    if not vrs.exists() or vrs.stat().st_size == 0:
        reasons.append('VRS file missing or empty')
    if result.get('failed'):
        reasons.append('harness failed: %s' % '; '.join(result.get('failures', [])))
    if not str(environment.get('simulator_version', '')).startswith('205.'):
        reasons.append('Simulator version %r is not 205.x' % environment.get('simulator_version'))
    if not environment.get('single_pass_instanced') or environment.get('graphics_api') != 'Direct3D11':
        reasons.append('Player not in Single Pass Instanced / Direct3D11 (%s / %s)' % (environment.get('stereo_mode'), environment.get('graphics_api')))
    if not ready.get('camera_following'):
        reasons.append('camera not following the head at READY (%.4f m / %.3f deg)' % (ready.get('follow_error_m', -1), ready.get('follow_error_deg', -1)))
    for failure in context.get('failures', []):
        reasons.append(failure)
    if result.get('record_transitions') != 1:
        reasons.append('expected exactly one IDLE->RECORD transition in the Simulator log, saw %s' % result.get('record_transitions'))

    record = [r for r in rows if r['sim_state'] == 'RECORD']
    out['record_frames'] = len(record)
    if len(record) < 30:
        reasons.append('too few frames in RECORD state (%d)' % len(record))
        out['reasons'] = reasons
        write_fixture(args, context, environment, ready, out, None, 'rejected', reasons)
        return report(out, False)

    t0 = float(record[0]['realtime_s'])
    duration = float(record[-1]['realtime_s']) - t0
    out['record_seconds'] = duration
    if not args.min_seconds <= duration <= args.max_seconds:
        reasons.append('recording length %.2f s outside %.1f..%.1f s' % (duration, args.min_seconds, args.max_seconds))

    follow_m = max(float(r['follow_error_m']) for r in record)
    follow_deg = max(float(r['follow_error_deg']) for r in record)
    not_following = sum(1 for r in record if float(r['follow_error_m']) > FOLLOW_M or float(r['follow_error_deg']) > FOLLOW_DEG)
    out.update({'max_follow_error_m': follow_m, 'max_follow_error_deg': follow_deg, 'frames_not_following': not_following})
    if not_following:
        reasons.append('camera not following the head in %d of %d recorded frames (max %.4f m / %.3f deg)' % (not_following, len(record), follow_m, follow_deg))

    eye_center_error = 0.0
    separations = []
    projection_drift = 0.0
    first_projection = np.concatenate([matrix(record[0], 'left_proj').ravel(), matrix(record[0], 'right_proj').ravel()])
    for r in record:
        left, right = eye_positions(r)
        eye_center_error = max(eye_center_error, float(np.linalg.norm((left + right) / 2 - pos(r, 'camera_'))))
        separations.append(float(np.linalg.norm(left - right)))
        projection = np.concatenate([matrix(r, 'left_proj').ravel(), matrix(r, 'right_proj').ravel()])
        projection_drift = max(projection_drift, float(np.abs(projection - first_projection).max()))
    out.update({'max_eye_center_error_m': eye_center_error, 'eye_separation_m': [min(separations), max(separations)], 'max_projection_drift': projection_drift})
    if eye_center_error > 0.02:
        reasons.append('eye view matrices do not follow the camera (eye centre %.3f m from the camera)' % eye_center_error)
    if not 0.04 <= min(separations) <= max(separations) <= 0.09:
        reasons.append('eye separation outside 0.04..0.09 m (%.3f..%.3f)' % (min(separations), max(separations)))
    if projection_drift > 1e-3:
        reasons.append('eye projection matrices changed during the recording (max %.2e)' % projection_drift)

    stationary = plateaus(record, args.min_plateau_seconds)
    out['plateaus'] = [{'t0': s['t0'] - t0, 't1': s['t1'] - t0, 'position': s['position'].tolist(), 'rotation': s['rotation'].tolist()} for s in stationary]
    poses = None
    if len(stationary) < 3:
        reasons.append('%d stationary head poses of >= %.1f s recorded, need 3 (motion not delivered)' % (len(stationary), args.min_plateau_seconds))
    else:
        middle = max(stationary[1:-1], key=lambda s: min(np.linalg.norm(s['position'] - stationary[0]['position']), np.linalg.norm(s['position'] - stationary[-1]['position'])))
        poses = [stationary[0], middle, stationary[-1]]
        for (i, a), (j, b) in itertools.combinations(enumerate(poses), 2):
            d, g = float(np.linalg.norm(a['position'] - b['position'])), angle(a['rotation'], b['rotation'])
            out['P%d_P%d' % (i + 1, j + 1)] = {'distance_m': d, 'angle_deg': g}
            if d < args.min_step_m and g < args.min_step_deg:
                reasons.append('P%d and P%d are not distinct (%.3f m / %.1f deg)' % (i + 1, j + 1, d, g))
        travel = out['P1_P3']
        if travel['distance_m'] < args.min_travel_m or travel['angle_deg'] < args.min_travel_deg:
            reasons.append('P1->P3 travel %.3f m / %.1f deg below %.2f m and %.0f deg' % (travel['distance_m'], travel['angle_deg'], args.min_travel_m, args.min_travel_deg))
        default_far = max(float(np.linalg.norm(p['position'] - DEFAULT_HEAD)) for p in poses[1:])
        if default_far < args.min_travel_m:
            reasons.append('recorded poses stay near the Simulator default head pose (max %.3f m)' % default_far)

    out['reasons'] = reasons
    status = 'candidate' if not reasons else 'rejected'
    write_fixture(args, context, environment, ready, out, (record, t0, poses), status, reasons)
    return report(out, not reasons)


def write_fixture(args, context, environment, ready, qc, recording, status, reasons):
    vrs = Path(args.vrs)
    fixture = {
        'schema': 'zantetsu-xrsim-fixture/1',
        'id': context.get('id'),
        'status': status,
        'view': context.get('view'),
        'generated_at': context.get('generated_at'),
        'qc_at': now(),
        'recording': {
            'file': vrs.name,
            'sha256': sha256(vrs) if vrs.exists() else None,
            'bytes': vrs.stat().st_size if vrs.exists() else 0,
            'record_seconds': qc.get('record_seconds'),
        },
        'simulator': {
            'runtime_version': environment.get('simulator_version'),
            'exe_version': context.get('simulator_exe_version'),
            'device_profile': environment.get('simulator_device_profile'),
            'runtime_json': environment.get('xr_runtime_json'),
        },
        'player': {
            'unity_version': environment.get('unity_version'),
            'graphics_api': environment.get('graphics_api'),
            'stereo_mode': environment.get('stereo_mode'),
            'single_pass_instanced': environment.get('single_pass_instanced'),
            'refresh_rate': environment.get('refresh_rate'),
            'eye_texture': environment.get('eye_texture'),
            'manifest': context.get('player_manifest'),
        },
        'generation': {
            'method': 'Meta XR Simulator v205 non-public SessionCapture gRPC (record/stop) + Simulator keyboard input; not a public API',
            'attempt': context.get('attempt'),
            'max_attempts': context.get('max_attempts'),
            'motion_plan': context.get('motion_plan'),
            'adapter': context.get('adapter'),
            'foreground_checks': context.get('foreground_checks'),
            'precheck': context.get('precheck'),
        },
        'qc': qc,
        'reasons': reasons,
    }
    targets = None
    if recording and recording[2]:
        record, t0, poses = recording
        fixture['poses'] = [{'name': 'P%d' % (i + 1), 't0': p['t0'] - t0, 't1': p['t1'] - t0, 'position': p['position'].tolist(), 'rotation': p['rotation'].tolist()} for i, p in enumerate(poses)]
        step = max(1, len(record) // 120)
        fixture['trajectory'] = [[round(float(r['realtime_s']) - t0, 4)] + pos(r, 'head_').round(5).tolist() + rot(r, 'head_').round(6).tolist() for r in record[::step]]
        targets = {
            'poses': [{'name': 'P%d' % (i + 1), 'px': float(p['position'][0]), 'py': float(p['position'][1]), 'pz': float(p['position'][2]),
                       'qx': float(p['rotation'][0]), 'qy': float(p['rotation'][1]), 'qz': float(p['rotation'][2]), 'qw': float(p['rotation'][3])} for i, p in enumerate(poses)],
            'positionTolerance': args.target_position_tolerance,
            'angleToleranceDeg': args.target_angle_tolerance,
            'stationaryFrames': 8,
            'timeoutSeconds': 90.0,
        }
    out_dir = Path(args.out_dir)
    write_json(out_dir / 'fixture.json', fixture)
    if targets:
        write_json(out_dir / 'targets.json', targets)


# ---------------------------------------------------------------------------------------------------------------------
# verification
# ---------------------------------------------------------------------------------------------------------------------
def luma(image):
    return 0.299 * image[..., 0] + 0.587 * image[..., 1] + 0.114 * image[..., 2]


def largest(mask):
    if not mask.any():
        return 0
    labels, count = ndimage.label(mask, structure=np.ones((3, 3), bool))
    return int(np.bincount(labels.ravel())[1:].max()) if count else 0


def compare_images(a_path, b_path):
    a = np.asarray(Image.open(a_path).convert('RGB')).astype(np.int16)
    b = np.asarray(Image.open(b_path).convert('RGB')).astype(np.int16)
    if a.shape != b.shape:
        return {'error': 'size mismatch'}
    differing = np.abs(a - b).max(axis=2) > PIXEL_THRESHOLD
    delta = luma(b) - luma(a)
    return {'differing_px': int(differing.sum()), 'differing_percent': float(differing.mean() * 100), 'largest_region_px': largest(differing),
            'brighter_region_px': largest(delta > LUMA_DELTA), 'darker_region_px': largest(delta < -LUMA_DELTA)}


def compare_atlas(a_path, b_path):
    a = np.fromfile(a_path, dtype=np.float32)
    b = np.fromfile(b_path, dtype=np.float32)
    if a.shape != b.shape:
        return {'error': 'size mismatch'}
    far = 0.0 if (a == 0).mean() > 0.5 else 1.0
    diff = a != b
    return {'differing_texels': int(diff.sum()), 'only_first_wrote': int(((a != far) & (b == far)).sum()), 'only_second_wrote': int(((a == far) & (b != far)).sum()),
            'second_farther': int((diff & ((b < a) if far == 0.0 else (b > a))).sum()), 'second_nearer': int((diff & ((b > a) if far == 0.0 else (b < a))).sum()),
            'max_abs': float(np.abs(a - b).max()) if a.size else 0.0}


def run_label(run):
    return '%s %s run%s' % (run['result'].get('mode'), run['result'].get('path'), run['result'].get('run'))


def cmd_verify(args):
    fixture = load_json(args.fixture)
    fixture_dir = Path(args.fixture).parent
    vrs = fixture_dir / fixture['recording']['file']
    reasons = []
    if not vrs.exists() or sha256(vrs) != fixture['recording']['sha256']:
        reasons.append('recording file missing or SHA-256 differs from fixture.json')
    targets = {p['name']: p for p in fixture.get('poses', [])}
    if len(targets) != 3:
        return report({'check': 'verify', 'reasons': ['fixture has no three recorded poses']}, False)

    runs = []
    for run_dir in sorted(Path(d) for pattern in args.runs for d in Path(pattern).parent.glob(Path(pattern).name)):
        if not (run_dir / 'result.json').exists():
            runs.append({'dir': run_dir, 'result': {'mode': '?', 'path': '?', 'run': run_dir.name}, 'excluded': ['no result.json (Player did not finish)'], 'captures': {}})
            continue
        result = load_json(run_dir / 'result.json')
        run = {'dir': run_dir, 'result': result, 'excluded': [], 'captures': {c['name']: c for c in result.get('captures', [])}}
        if result.get('failed'):
            run['excluded'].append('harness failed: ' + '; '.join(result.get('failures', [])))
        run['excluded'].extend(result.get('run_reasons', []))
        runs.append(run)

    replay_runs = [r for r in runs if r['result'].get('mode') == 'replay']
    control_runs = [r for r in runs if r['result'].get('mode') == 'noreplay']
    out = {'check': 'verify', 'fixture': fixture.get('id'), 'verified_at': now(), 'runs': [], 'poses': {}, 'controls': []}

    valid = {name: {'VP3': [], 'VP3C': []} for name in targets}
    for run in replay_runs:
        entry = {'run': run_label(run), 'dir': run['dir'].name, 'excluded_run_reasons': list(run['excluded']), 'captures': {}}
        for name, target in targets.items():
            capture = run['captures'].get(name)
            if capture is None:
                entry['captures'][name] = {'valid': False, 'reasons': ['no capture record']}
                continue
            capture_reasons = list(capture.get('reasons', []))
            d = float(np.linalg.norm(np.array(capture['head_position']) - np.array(target['position'])))
            g = angle(capture['head_rotation'], target['rotation'])
            if capture.get('captured') and (d > args.pose_tolerance_m or g > args.pose_tolerance_deg):
                capture_reasons.append('head pose differs from the recording: %.4f m / %.3f deg' % (d, g))
            ok = capture.get('captured') and not capture_reasons and not run['excluded']
            entry['captures'][name] = {
                'valid': bool(ok), 'reasons': capture_reasons, 'frame': capture.get('frame'), 'replay_time_s': capture.get('replay_time_s'),
                'recorded_pose_t0_s': target['t0'], 'time_offset_s': (capture.get('replay_time_s') - target['t0']) if capture.get('replay_time_s', -1) >= 0 else None,
                'head_vs_recording_m': d, 'head_vs_recording_deg': g, 'follow_error_m': capture.get('follow_error_m'), 'follow_error_deg': capture.get('follow_error_deg'),
                'forward_selected': capture.get('forward_selected'), 'cascade_selected': capture.get('cascade_selected'),
            }
            if ok:
                valid[name][run['result']['path']].append((run, capture))
        entry['counted_as_success'] = all(c['valid'] for c in entry['captures'].values())
        out['runs'].append(entry)

    control_ok = bool(control_runs)
    for run in control_runs:
        entry = {'run': run_label(run), 'dir': run['dir'].name, 'excluded_run_reasons': list(run['excluded']), 'captures': {}}
        for capture in run['captures'].values():
            head = np.array(capture['head_position'])
            nearest = min(((float(np.linalg.norm(head - np.array(t['position']))), angle(capture['head_rotation'], t['rotation']), n) for n, t in targets.items()
                           if float(np.linalg.norm(np.array(t['position']) - DEFAULT_HEAD)) > args.pose_tolerance_m), default=None)
            reproduced = nearest is not None and nearest[0] <= args.pose_tolerance_m and nearest[1] <= args.pose_tolerance_deg
            entry['captures'][capture['name']] = {'head_position': capture['head_position'], 'nearest_recorded_non_default_pose': nearest[2] if nearest else None,
                                                  'distance_m': nearest[0] if nearest else None, 'angle_deg': nearest[1] if nearest else None, 'reproduces_recorded_pose': reproduced}
            if reproduced or not capture.get('captured'):
                control_ok = False
        if run['excluded']:
            control_ok = False
        out['controls'].append(entry)
    if not control_ok:
        reasons.append('no-VRS control missing, excluded, or showing a recorded non-default pose')

    for name in targets:
        pose_out = {'valid_captures': {path: len(items) for path, items in valid[name].items()}}
        for path in ('VP3', 'VP3C'):
            if len(valid[name][path]) < args.min_valid:
                reasons.append('%s: %d valid %s captures (needs %d)' % (name, len(valid[name][path]), path, args.min_valid))
        comparisons = []
        groups = [('noise VP3', list(itertools.combinations(valid[name]['VP3'], 2))), ('noise VP3C', list(itertools.combinations(valid[name]['VP3C'], 2))),
                  ('VP3 vs VP3C', list(itertools.product(valid[name]['VP3'], valid[name]['VP3C'])))]
        summary = {}
        for group, pairs in groups:
            for (run_a, cap_a), (run_b, cap_b) in pairs:
                item = {'group': group, 'a': run_label(run_a), 'b': run_label(run_b), 'eyes': {},
                        'eye_matrix_max_abs_diff': float(max(np.abs(np.array(cap_a[k]) - np.array(cap_b[k])).max() for k in ('left_view', 'right_view', 'left_projection', 'right_projection')))}
                for eye in ('left', 'right'):
                    metrics = compare_images(run_a['dir'] / 'captures' / ('%s_%s.png' % (name, eye)), run_b['dir'] / 'captures' / ('%s_%s.png' % (name, eye)))
                    item['eyes'][eye] = metrics
                    worst = summary.setdefault((group, eye), {'differing_percent': 0.0, 'largest_region_px': 0, 'brighter_region_px': 0, 'darker_region_px': 0})
                    for key in worst:
                        worst[key] = max(worst[key], metrics.get(key, 0))
                item['atlas'] = compare_atlas(run_a['dir'] / 'captures' / ('%s_atlas.bin' % name), run_b['dir'] / 'captures' / ('%s_atlas.bin' % name))
                comparisons.append(item)
        pose_out['comparisons'] = comparisons
        pose_out['summary'] = {'%s / %s' % key: value for key, value in summary.items()}
        for eye in ('left', 'right'):
            noise = max([summary.get(('noise VP3', eye), {}).get('brighter_region_px', 0), summary.get(('noise VP3C', eye), {}).get('brighter_region_px', 0)])
            candidate = summary.get(('VP3 vs VP3C', eye), {}).get('brighter_region_px')
            pose_out['filled_shadow_loss_%s' % eye] = None if candidate is None else {'vp3c_brighter_region_px': candidate, 'same_path_noise_px': noise,
                                                                                     'exceeds_noise': candidate > max(2 * noise, 50)}
        pose_out['vp3c_selection_counts'] = sorted({'%s %s' % (c['forward_selected'], c['cascade_selected']) for _, c in valid[name]['VP3C']})
        out['poses'][name] = pose_out

    successes = {path: sum(1 for r in out['runs'] if r['counted_as_success'] and r['run'].split()[1] == path) for path in ('VP3', 'VP3C')}
    attempted = {path: sum(1 for r in out['runs'] if r['run'].split()[1] == path) for path in ('VP3', 'VP3C')}
    not_following = sum(1 for r in replay_runs + control_runs for c in r['captures'].values() if any('not following' in x for x in c.get('reasons', [])))
    not_following += sum(1 for r in replay_runs + control_runs if any('not_following' in x for x in r['excluded']))
    out['run_counts'] = {'replay_attempted': attempted, 'replay_successful': successes, 'controls': len(control_runs),
                         'camera_not_following_events': not_following}
    out['status'] = 'verified' if not reasons else 'not_verified'
    out['reasons'] = reasons
    write_json(Path(args.out_dir) / 'verification.json', out)
    write_markdown(Path(args.out_dir) / 'verification.md', fixture, out)
    verified_fixture = dict(fixture)
    verified_fixture['status'] = out['status']
    verified_fixture['verification'] = {'verified_at': out['verified_at'], 'status': out['status'], 'reasons': reasons, 'run_counts': out['run_counts']}
    write_json(Path(args.out_dir) / 'fixture.verified.json', verified_fixture)
    return report({'check': 'verify', 'status': out['status'], 'reasons': reasons, 'run_counts': out['run_counts']}, not reasons)


def write_markdown(path, fixture, out):
    lines = ['# XrSim fixture verification: %s' % fixture.get('id'), '', '- status: **%s**' % out['status'], '- verified at: %s' % out['verified_at'],
             '- run counts: %s' % json.dumps(out['run_counts']), '']
    for reason in out['reasons']:
        lines.append('- reason: %s' % reason)
    lines += ['', '## Runs', '', '| run | success | excluded / capture reasons |', '| --- | --- | --- |']
    for run in out['runs']:
        notes = list(run['excluded_run_reasons']) + ['%s: %s' % (n, '; '.join(c['reasons'])) for n, c in run['captures'].items() if c['reasons']]
        lines.append('| %s | %s | %s |' % (run['run'], run['counted_as_success'], '<br>'.join(notes) or '-'))
    lines += ['', '## Poses', '']
    for name, pose in out['poses'].items():
        lines.append('### %s  (valid captures %s)' % (name, pose['valid_captures']))
        for key, value in pose['summary'].items():
            lines.append('- %s: differing %.3f%%, largest %d px, brighter %d px, darker %d px' % (key, value['differing_percent'], value['largest_region_px'], value['brighter_region_px'], value['darker_region_px']))
        lines.append('- filled shadow loss: left %s, right %s' % (pose.get('filled_shadow_loss_left'), pose.get('filled_shadow_loss_right')))
        lines.append('- VP3C selection counts: %s' % pose['vp3c_selection_counts'])
        lines.append('')
    Path(path).write_text('\n'.join(lines) + '\n', encoding='utf-8')


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest='command', required=True)

    p = sub.add_parser('precheck')
    p.add_argument('--frames', required=True)
    p.add_argument('--from-frame', type=int, required=True)
    p.add_argument('--min-move', type=float, default=0.05)
    p.add_argument('--max-return', type=float, default=0.03)

    q = sub.add_parser('qc-recording')
    q.add_argument('--run-dir', required=True)
    q.add_argument('--vrs', required=True)
    q.add_argument('--context', required=True)
    q.add_argument('--out-dir', required=True)
    q.add_argument('--min-seconds', type=float, default=8.0)
    q.add_argument('--max-seconds', type=float, default=12.0)
    q.add_argument('--min-plateau-seconds', type=float, default=0.5)
    q.add_argument('--min-step-m', type=float, default=0.10)
    q.add_argument('--min-step-deg', type=float, default=10.0)
    q.add_argument('--min-travel-m', type=float, default=0.25)
    q.add_argument('--min-travel-deg', type=float, default=20.0)
    q.add_argument('--target-position-tolerance', type=float, default=0.01)
    q.add_argument('--target-angle-tolerance', type=float, default=0.5)

    v = sub.add_parser('verify')
    v.add_argument('--fixture', required=True)
    v.add_argument('--runs', nargs='+', required=True, help='run directory glob patterns')
    v.add_argument('--out-dir', required=True)
    v.add_argument('--min-valid', type=int, default=2)
    v.add_argument('--pose-tolerance-m', type=float, default=0.01)
    v.add_argument('--pose-tolerance-deg', type=float, default=0.5)

    args = parser.parse_args()
    return {'precheck': cmd_precheck, 'qc-recording': cmd_qc_recording, 'verify': cmd_verify}[args.command](args)


if __name__ == '__main__':
    sys.exit(main())
