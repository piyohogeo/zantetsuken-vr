"""Validate and anonymize the repaired + original control test evidence."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
out = root/'docs/diagnostics/compact16uv-character-physics-repaired'
private = root/'Assets/Licensed/Compact16uvConvexRepair'


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def archive(name, count, pid):
    raw = (out/(name+'-raw.xml')).read_text(encoding='utf-8-sig')
    node = ET.fromstring(raw)
    require(node.attrib['result'] == 'Passed' and int(node.attrib['passed']) == count
            and int(node.attrib['failed']) == int(node.attrib['skipped']) == 0, 'unexpected test totals')
    require({x.attrib['value'] for x in node.findall('.//property[@name="_PID"]')} == {str(pid)}, 'wrong process evidence')
    for original in (os.environ['USERPROFILE'], os.environ['USERPROFILE'].replace('\\','/'), os.environ['COMPUTERNAME']):
        raw = raw.replace(original, '[REDACTED]')
    (out/(name+'.xml')).write_bytes(('\n'.join(line.rstrip() for line in raw.splitlines())+'\n').encode('utf-8'))
    return node


parser = argparse.ArgumentParser()
parser.add_argument('--focused-pid', type=int, required=True)
parser.add_argument('--editmode-pid', type=int, required=True)
args = parser.parse_args()
focused = archive('focused', 16, args.focused_pid)
archive('editmode', 3638, args.editmode_pid)
export = json.loads((out/'export.json').read_text())
require(export['scriptSha256'] == sha(root/'Tools/Prepare-Character-Convex-Repair.py'), 'exporter changed')
require(export['manifestSha256'] == sha(private/'intake.json'), 'manifest changed')
require(export['originalManifestSha256'] == sha(root/'Assets/Licensed/Compact16uvIntake/intake.json'), 'original manifest changed')
manifest = json.loads((private/'intake.json').read_text())
for asset in export['assets']:
    entry, = [e for e in manifest['assets'] if e['family'] == asset['family']]
    require(sha(root/entry['assetPath']) == asset['sourceSha256'], 'repaired source changed')
    require(sha(root/entry['originalAssetPath']) == asset['originalSourceSha256'], 'original source changed')
    require(sha(private/'Resources/CharacterPhysicsMigration'/(asset['family']+'.json')) == asset['fixtureSha256'], 'fixture changed')
    require(sha(root/'Assets/Licensed/Compact16uvIntake/Resources/CharacterPhysicsMigration'/(asset['family']+'.json')) == asset['originalFixtureSha256'], 'original fixture changed')
    require(len(asset['hulls']) == 19 and asset['displayAndHullPositionsExactlyUnchanged'], 'invalid offline audit')
cases = []
for test in focused.findall('.//test-case'):
    output = test.findtext('output','')
    match = re.search(r'family=(\S+) pose=(\d+) hulls=19 mappedVertices=608 maxWorldError=(\S+) maxRendererLocalError=(\S+) wrongBoneError=(\S+) accepted=(\d+) rejected=(\d+) cuts=(\d+) repaired=(True|False)', output)
    require(match, 'missing case evidence')
    family, pose, world, local, wrong, accepted, rejected, cuts, repaired = match.groups()
    item = dict(family=family, pose=int(pose), repaired=repaired == 'True',
        maxWorldError=float(world), maxRendererLocalError=float(local), wrongBoneError=float(wrong),
        accepted=int(accepted), rejected=int(rejected), cuts=int(cuts))
    if item['repaired']:
        require('displayImportExactlyUnchanged=True' in output and item['accepted'] == 19 and item['rejected'] == 0, 'repaired input failed')
    cases.append(item)
repaired_cases = [c for c in cases if c['repaired']]
original_cases = [c for c in cases if not c['repaired']]
require(len(repaired_cases) == len(original_cases) == 8, 'missing comparison cases')
require(sum(c['cuts'] for c in repaired_cases) == 304 and sum(c['cuts'] for c in original_cases) == 280, 'missing cut steps')
summary = dict(schemaVersion=1, baseProductCommit='fe45914a', upstreamRepairCommit='5233b74',
    unity='6000.3.22f1', platform='Windows Editor EditMode', repairedHullCount=38, repairedRejectedCount=0,
    repairedCasesPassed=8, originalControlCasesPassed=8, repairedCutSteps=304, originalControlCutSteps=280,
    repairedCookCalls=760, fullEditMode=dict(passed=3638,failed=0,skipped=0),
    maximumRepairedWorldError=max(c['maxWorldError'] for c in repaired_cases),
    originalInputsPreserved=True, unityDisplayImportExactlyUnchanged=True, importedUcxOrientedTrianglesMatched=True,
    productionReferencesSwitched=False, productionOwnerSceneAcceptance=False,
    exportSha256=sha(out/'export.json'), testSourceSha256=sha(root/'Assets/Zantetsu/Tests/EditMode/PhysicsCut/Compact16uvCharacterPhysicsTests.cs'),
    cases=cases, limitations=['Per-hull component cuts, not a compound 19-hull owner/common-plane scene test',
    'Cook calls without errors, not PhysX cooked-topology readback or Rigidbody simulation',
    'No IL2CPP/performance/visual acceptance; prior Casual one-pixel difference remains independent'])
(out/'summary.json').write_bytes((json.dumps(summary,indent=2)+'\n').encode('utf-8'))
print(json.dumps({k:v for k,v in summary.items() if k != 'cases'},indent=2))
