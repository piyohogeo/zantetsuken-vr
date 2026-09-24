"""Read captured pixels, retain strict failures, and archive sanitized regression evidence."""
import hashlib
import argparse
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET
import numpy as np
from PIL import Image

root = Path(__file__).resolve().parents[1]
out = root / 'docs/diagnostics/compact16uv-character-scene'
parser = argparse.ArgumentParser()
parser.add_argument('--xml', type=Path)
parser.add_argument('--expected-pid')
args = parser.parse_args()
def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()
def pixels(path):
    return np.asarray(Image.open(path).convert('RGBA'), dtype=np.int16)
run_ids = (4, 5, 6)
reports = [json.loads((out / f'run{i}/report.json').read_text()) for i in run_ids]
assert all(r == reports[0] for r in reports), 'Run reports differ'
report = reports[0]
assert report['graphics'] == 'Direct3D11' and report['vertexStride'] == 16
assert not report['failure'] and len(report['comparisons']) == 24
comparisons = report['comparisons']
failures = [r for r in comparisons if not r['passed']]
assert len(failures) == 1 and failures[0]['name'] == 'character-casual-pose1-skin-vs-bake'
assert failures[0]['silhouette'] == 1
images = sorted((out / 'run4').glob('*.png'))
assert len(images) == 48
for image in images:
    assert all(sha(image) == sha(out / f'run{i}' / image.name) for i in (5, 6)), image.name
a = pixels(out / 'run4/character-casual-pose1-skin.png')
b = pixels(out / 'run4/character-casual-pose1-bake.png')
ys, xs = np.where((a[:,:,3] > 127) != (b[:,:,3] > 127))
boundary = []
for y, x in zip(ys, xs):
    boundary.append(dict(x=int(x), y=int(y), skin=a[y,x].tolist(), bake=b[y,x].tolist(),
                         skin3x3=a[y-1:y+2,x-1:x+2].tolist(), bake3x3=b[y-1:y+2,x-1:x+2].tolist()))
common = (a[:,:,3] > 127) & (b[:,:,3] > 127)
delta = np.abs(a[:,:,:3] - b[:,:,:3])
caps = []
for family in ('casual', 'professional'):
    for pose in (0, 1):
        changes = []
        for stage in (1, 2):
            prefix = f'character-{family}-pose{pose}-stage{stage}'
            normal = pixels(out / 'run4' / (prefix + '-debug0-vp.png'))
            debug = pixels(out / 'run4' / (prefix + '-debug1-vp.png'))
            changes.append(int(np.any(normal[:,:,:3] != debug[:,:,:3], axis=2).sum()))
        assert sum(changes) > 0, 'No observed real cap colour change'
        caps.append(dict(family=family, pose=pose, changedPixelsByStage=changes))
summary = dict(schemaVersion=1, baseCommit='44576a19', independentProcesses=3,
    strictGatePassed=False, strictComparisonsPassedPerProcess=23, strictComparisonsPerProcess=24,
    vpComparisonsPassedPerProcess=20, skinBakeComparisonsPassedPerProcess=3,
    classification='F-SKIN-RASTER: repeatable 1-pixel skin/Bake silhouette mismatch; strict gate retained',
    exactImageReproducibility=True, imageCountPerProcess=len(images), runIds=run_ids,
    imageHashes={p.name:sha(p) for p in images},
    maximumRgbOnCommonCoveredPixels=int(delta[common].max()), boundaryMismatch=boundary, capChanges=caps,
    unity=report['unity'], graphics=report['graphics'], development=report['development'],
    intakeSha256=sha(root / 'Assets/Licensed/Compact16uvIntake/intake.json'),
    binarySha256=sha(root.parent / 'Compact16uvCharacterSceneCaps_20260924/GameAssembly.dll'),
    probeSourceSha256=sha(root / 'Assets/Zantetsu/Runtime/Sandbox/Compact16uvCharacterSceneProbe.cs'))
raw = args.xml or out / 'editmode.xml'
if raw.exists():
    xml = raw.read_text(encoding='utf-8-sig')
    original = ET.fromstring(xml)
    if args.xml:
        assert args.expected_pid, 'Require explicit test process identity for raw XML'
        pids = {x.attrib['value'] for x in original.findall('.//property[@name="_PID"]')}
        assert pids == {args.expected_pid}, pids
    for old in (os.environ['USERPROFILE'], os.environ['USERPROFILE'].replace('\\','/'), os.environ['COMPUTERNAME']):
        xml = xml.replace(old, '[REDACTED]')
    xml = '\n'.join(line.rstrip() for line in xml.splitlines()) + '\n'
    node = ET.fromstring(xml)
    assert node.attrib['result'] == 'Passed' and int(node.attrib['passed']) == 3622 and int(node.attrib['skipped']) == 0
    (out / 'editmode.xml').write_bytes(xml.encode('utf-8'))
    summary['editMode'] = dict(passed=3622, failed=0, skipped=0)
else:
    raise RuntimeError('A retained regression XML is required')
for i in run_ids:
    lines = (out / f'player{i}.log').read_text(encoding='utf-8-sig').splitlines()
    trace = '\n'.join(line for line in lines if line.startswith('CHARACTER ')) + '\n'
    assert 'CHARACTER SCENE END passed=False comparisons=24' in trace
    (out / f'run{i}/trace.txt').write_bytes(trace.encode('utf-8'))
(out / 'summary.json').write_bytes((json.dumps(summary, indent=2) + '\n').encode('utf-8'))
print(json.dumps({k:v for k,v in summary.items() if k not in ('boundaryMismatch', 'imageHashes')}, indent=2))
