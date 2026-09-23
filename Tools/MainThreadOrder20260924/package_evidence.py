"""Archive small, unchanged experiment records; verify the original working tree stayed intact.

Run after uninstalling diagnostic code and restoring Unity-generated settings.
All writes stay in this dedicated worktree. No source repository writes or deletions.
"""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]
LOGS = ROOT / 'Logs/MainThreadOrder20260924'
ORIGINAL = ROOT.parent / 'zantetsuken-vr'
DEST = ROOT / 'docs/diagnostics/phase41-main-thread-order-data-2026-09-24'
RUNS = ['editor-smoke1', 'editor-smoke2', 'il2cpp-build1', 'il2cpp-build2',
        'player00', 'player11', 'player10', 'player01']


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def git(root, *args):
    return subprocess.check_output(['git', '-c', 'safe.directory=' + root.as_posix(), *args], cwd=root)


def original_state():
    before = {r['path']: r['sha256'].lower() for r in
              json.loads((LOGS/'original-before.json').read_text(encoding='utf-8-sig'))}
    names = git(ORIGINAL, 'ls-files', '-z', '--cached', '--others', '--exclude-standard').decode().split('\0')
    after = {name: sha(ORIGINAL/name) for name in names if name and (ORIGINAL/name).is_file()}
    old_status = (LOGS/'original-status-before.txt').read_text(encoding='utf-8-sig').strip('\r\n')
    status = git(ORIGINAL, 'status', '--short').decode().strip('\r\n')
    head = git(ORIGINAL, 'rev-parse', 'HEAD').decode().strip()
    data = {'before_files': len(before), 'after_files': len(after),
            'added': sorted(after.keys()-before.keys()), 'removed': sorted(before.keys()-after.keys()),
            'changed': sorted(k for k in before.keys() & after.keys() if before[k] != after[k]),
            'status_equal': old_status == status, 'head': head,
            'expected_head': git(ROOT, 'rev-parse', 'b19ce67a').decode().strip(),
            'before_manifest_sha256': sha(LOGS/'original-before.json'),
            'scope': 'tracked and nonignored untracked working files; shared git worktree/branch metadata excluded'}
    data['unchanged'] = (before == after and old_status == status and head == data['expected_head'])
    save(LOGS/'original-verification.json', data)
    if not data['unchanged']:
        raise RuntimeError('Original changed; inspect original-verification.json without modifying it')
    return data


def copy_records(source, destination):
    destination.mkdir(parents=True, exist_ok=False)
    for path in sorted(source.iterdir()):
        if path.is_file() and (path.suffix in ('.csv', '.json', '.txt') or path.name == 'results.xml'):
            shutil.copyfile(path, destination/path.name)


def main():
    assert ROOT.name == 'zantetsuken-vr-main-thread-order-20260924' and (ROOT/'.git').is_file()
    assert not (LOGS/'installed').exists(), 'Restore diagnostic code before packaging'
    verification = original_state()
    regression = json.loads((LOGS/'regression2/editmode/command.json').read_text())['source_sha256']
    assert all(sha(ROOT/path) == digest for path, digest in regression.items()), 'Product source differs from regression'
    built = json.loads((LOGS/'il2cpp-build2/command.json').read_text())['source_sha256']
    binaries = json.loads((LOGS/'player00/binary.json').read_text())
    for name in ('player00', 'player11', 'player10', 'player01'):
        assert json.loads((LOGS/name/'command.json').read_text())['source_sha256'] == built
        assert json.loads((LOGS/name/'binary.json').read_text()) == binaries
    assert all(sha(Path(path)) == digest for path, digest in binaries.items())
    DEST.mkdir(parents=True, exist_ok=False)
    # Keep the source records' bytes and SHA-256 across Git checkout on Windows.
    (DEST/'.gitattributes').write_text('* -text whitespace=trailing-space,space-before-tab,cr-at-eol\n'
                                     '# NUnit log text includes original trailing spaces. Preserve it unchanged.\n'
                                     '*.xml -whitespace\n', encoding='ascii')
    for name in RUNS:
        copy_records(LOGS/name, DEST/'raw'/name)
    for round_name in ('regression1', 'regression2'):
        for name in ('editmode', 'playmode', 'termination1', 'termination2'):
            copy_records(LOGS/round_name/name, DEST/round_name/name)
    for kind in ('order', 'micro'):
        source = LOGS/('summary-'+kind+'-final')
        target = DEST/('summary-'+kind)
        target.mkdir()
        for path in source.iterdir():
            if path.is_file() and (path.name.startswith('run-') or path.name in
                ('summary.json', 'summary.md', 'method.json', 'validation.json')):
                shutil.copyfile(path, target/path.name)
    save(DEST/'original-verification.json', verification)
    input_root = Path(r'C:\log\zantetsuken-vr\Phase41ActPlayer\intake\m8')
    inputs = {name: sha(input_root/name) for name in ('m_8.hulls.json', 'm_8.render.json')}
    product_files = list(regression) + [str(p.relative_to(ROOT)).replace('\\', '/') for p in
        (ROOT/'Assets/Zantetsu/Tests/EditMode/PhysicsCut').glob('*')
        if p.name.startswith(('ProvisionalAxisBoxMassTests', 'PhysicsCutMeshSlotTests', 'PhysicsCutCookTests'))]
    save(DEST/'provenance.json', {
        'base': git(ROOT, 'rev-parse', 'b19ce67a').decode().strip(),
        'branch': git(ROOT, 'branch', '--show-current').decode().strip(),
        'product_sha256': {name: sha(ROOT/name) for name in product_files},
        'runtime_matches_regression2': True, 'four_players_match_build2_sources_and_binaries': True,
        'binary_sha256': binaries, 'input_sha256': inputs,
        'final_measurement_launch_order': ['player00', 'player11', 'player10', 'player01'],
        'early_runs': {'editor-smoke1': 'initial probe, no newRoot data, before numerical edge guards',
                       'editor-smoke2': 'newRoot probe, before inertia guard',
                       'il2cpp-build1': 'initial build before inertia guard',
                       'il2cpp-build2': 'final code, build auto-launch; independent measurements are playerXX'},
        'regression1': 'before inertia boundary guard; 311 EditMode + 41 PlayMode',
        'regression2': 'final product code; 314 EditMode + 41 PlayMode',
        'excluded_from_git': 'Player binaries, Library, full logs remain under the dedicated worktree Logs'})
    log_records = {}
    folders = [LOGS/name for name in RUNS]
    folders += [LOGS/r/n for r in ('regression1', 'regression2') for n in
                ('editmode', 'playmode', 'termination1', 'termination2')]
    for folder in folders:
        for path in folder.glob('*.log'):
            lines = path.read_text(encoding='utf-8-sig', errors='replace').splitlines()
            log_records[str(path.relative_to(LOGS))] = {
                'sha256': sha(path), 'bytes': path.stat().st_size,
                'native_leak_messages': [line for line in lines if 'Leak Detected' in line],
                'compilation_errors': [line for line in lines if 'error CS' in line]}
    save(DEST/'log-checks.json', log_records)
    save(DEST/'sha256.json', {str(p.relative_to(DEST)).replace('\\', '/'): sha(p)
                            for p in sorted(DEST.rglob('*')) if p.is_file()})
    print(json.dumps({'output': str(DEST), 'original_unchanged': True,
                      'files': sum(p.is_file() for p in DEST.rglob('*'))}, indent=2))


if __name__ == '__main__':
    main()
