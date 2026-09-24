"""Archive component results without converting known convex rejection into product acceptance."""
import hashlib
import json
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
out = root / 'docs/diagnostics/compact16uv-character-physics'
def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()
def archive(source, target, passed, failed, pid):
    raw = (out / source).read_text(encoding='utf-8-sig')
    node = ET.fromstring(raw)
    assert int(node.attrib['passed']) == passed and int(node.attrib['failed']) == failed and int(node.attrib['skipped']) == 0
    assert {x.attrib['value'] for x in node.findall('.//property[@name="_PID"]')} == {str(pid)}
    for original in (os.environ['USERPROFILE'], os.environ['USERPROFILE'].replace('\\','/'), os.environ['COMPUTERNAME']):
        raw = raw.replace(original, '[REDACTED]')
    (out / target).write_bytes(('\n'.join(line.rstrip() for line in raw.splitlines()) + '\n').encode('utf-8'))
    return node
archive('double-raw.xml', 'exact-coordinate-probe.xml', 0, 8, 110448)
focused = archive('frame-raw.xml', 'focused.xml', 8, 0, 76176)
full = archive('editmode-raw.xml', 'editmode.xml', 3630, 0, 32700)
report = json.loads((out / 'export.json').read_text())
assert report['scriptSha256'] == sha(root / 'Tools/Export-Character-Physics.py')
assert report['manifestSha256'] == sha(root / 'Assets/Licensed/Compact16uvIntake/intake.json')
accepted, rejected = [], []
for asset in report['assets']:
    fixture = root / 'Assets/Licensed/Compact16uvIntake/Resources/CharacterPhysicsMigration' / asset['fixtureFile']
    assert asset['fixtureSha256'] == sha(fixture)
    for hull in asset['hulls']:
        (accepted if hull['convexAccepted'] else rejected).append(dict(family=asset['family'], **hull))
assert len(accepted) == 35 and len(rejected) == 3
localization = json.loads((out / 'failure-localization.json').read_text())
assert len(localization) == 3 and all(x['alternateDiagonalHypothesis']['passed'] and not x['alternateDiagonalHypothesis']['sourceModified'] for x in localization)
cases = []
for case in focused.findall('.//test-case'):
    output = case.findtext('output', '')
    match = re.search(r'family=(\S+) pose=(\d+) hulls=19 mappedVertices=608 maxWorldError=(\S+) maxRendererLocalError=(\S+) wrongBoneError=(\S+) accepted=(\d+) rejected=(\d+) cuts=(\d+)', output)
    assert match, case.attrib['name']
    family, pose, world, local, wrong, valid, invalid, cuts = match.groups()
    cases.append(dict(family=family, pose=int(pose), maxWorldError=float(world), maxRendererLocalError=float(local),
                      wrongBoneError=float(wrong), accepted=int(valid), rejected=int(invalid), cuts=int(cuts)))
assert len(cases) == 8 and sum(c['cuts'] for c in cases) == 280
summary = dict(schemaVersion=1, baseCommit='941844ee', unity='6000.3.22f1', platform='Windows Editor EditMode',
    physicsRegistrationReady=False, classification='F-PHYS-CONVEX: 3 authored UCX fail existing convexity audit; do not omit them from product owner',
    coordinateCasesPassed=8, distinctHullsMapped=38, uniqueHullVertices=1216, convexAccepted=35, convexRejected=3,
    acceptedHullCutSteps=280, fullEditMode=dict(passed=3630, failed=0, skipped=0),
    maximumWorldPositionError=max(c['maxWorldError'] for c in cases), cases=cases, rejected=rejected,
    testSourceSha256=sha(root / 'Assets/Zantetsu/Tests/EditMode/PhysicsCut/Compact16uvCharacterPhysicsTests.cs'),
    exportSha256=sha(out / 'export.json'),
    failureLocalizationSha256=sha(out / 'failure-localization.json'), alternateDiagonalHypothesesPassed=3, alternateDiagonalAppliedToSource=False,
    limitations=['No owner/scene registration', 'Rejected hulls not cooked or cut', 'No source repair or tolerance relaxation for convexity', 'No IL2CPP or performance measurement'])
(out / 'summary.json').write_bytes((json.dumps(summary, indent=2) + '\n').encode('utf-8'))
print(json.dumps({k: v for k, v in summary.items() if k not in ('cases', 'rejected')}, indent=2))
