"""Archive fixed-scale preparation evidence separately from runtime-scale rejection."""
import argparse
from collections import defaultdict
import hashlib
import json
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
out = root/'docs/diagnostics/compact16uv-fixed-scale'


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def archive(source, destination, passed, pid):
    raw = (out/source).read_text(encoding='utf-8-sig')
    node = ET.fromstring(raw)
    require(int(node.attrib['passed']) == passed and int(node.attrib['failed']) == 0
        and int(node.attrib['skipped']) == 0 and int(node.attrib['total']) == passed,
        'unexpected test totals: '+source)
    require({p.attrib['value'] for p in node.findall('.//property[@name="_PID"]')} == {str(pid)}, 'PID mismatch')
    for original in (os.environ['USERPROFILE'],os.environ['USERPROFILE'].replace('\\','/'),os.environ['COMPUTERNAME']):
        raw = raw.replace(original,'[REDACTED]')
    (out/destination).write_bytes(('\n'.join(line.rstrip() for line in raw.splitlines())+'\n').encode('utf-8'))
    return node


parser = argparse.ArgumentParser()
parser.add_argument('--focused-pid',type=int,required=True)
parser.add_argument('--editmode-pid',type=int,required=True)
args = parser.parse_args()
focused = archive('focused-raw.xml','focused.xml',32,args.focused_pid)
archive('editmode-raw.xml','editmode.xml',3694,args.editmode_pid)
steps, rejected, comparisons = [], [], []
integration_cases = 0
for test in focused.findall('.//test-case'):
    text = test.findtext('output','')
    if 'FixedScale_LoadPreparation' in test.attrib['name']:
        m = re.search(r'SCALE_PREP family=(\S+) scale=(\S+) frames=300 maxWorldError=(\S+) maxWorldNormalVectorError=(\S+) unitRenderer=True rigAndUcxUntouched=True',text)
        require(m, 'missing preparation comparison')
        family,scale,position,normal = m.groups()
        require(float(position)<2e-5 and float(normal)<2e-5, 'comparison tolerance exceeded')
        comparisons.append(dict(family=family,scale=float(scale),frames=300,
            maxWorldPositionError=float(position),maxWorldNormalVectorError=float(normal)))
        continue
    require('CutFixtureEnding state=ReleasedSafely' in text, 'teardown not confirmed')
    if 'FixedScale_Compound19_CommonPlane' in test.attrib['name']:
        integration_cases += 1
        rows = re.findall(r'compound family=(\S+) pose=(\d+) fill=(-?\d+) stage=(\d+) input=(\d+) split=(\d+) inheritedPositive=(\d+) inheritedNegative=(\d+) positive=(\d+) negative=(\d+) geometryFirst=(True|False) volumeBefore=(\S+) volumeAfter=(\S+) commits=(\d+) fixedScalePrepared=True',text)
        require(len(rows) == 2, 'two prepared committed cuts required')
        for family,pose,fill,stage,inputs,split,ip,ineg,pos,neg,first,before,after,commits in rows:
            require(int(commits)==int(stage)+1, 'commit count mismatch')
            steps.append(dict(family=family,pose=int(pose),fill=int(fill),stage=int(stage),input=int(inputs),
                split=int(split),inheritedPositive=int(ip),inheritedNegative=int(ineg),positive=int(pos),negative=int(neg),
                geometryFirst=first=='True',volumeBefore=float(before),volumeAfter=float(after),commits=int(commits)))
    else:
        m = re.search(r'FRAME_REJECT family=(\S+) pose=(\d+) fill=(-?\d+) hulls=19 mappingDeterminant=(\S+) cuts=0 commits=0 uploads=0 reason=F-CHARACTER-LINEAGE-SCALE fixedScalePrepared=True',text)
        require(m, 'missing prepared frame rejection evidence')
        family,pose,fill,det = m.groups()
        rejected.append(dict(family=family,pose=int(pose),fill=int(fill),mappingDeterminant=float(det)))
require(integration_cases == 18 and len(steps) == 36 and len(rejected) == 12 and len(comparisons) == 2,
    'incorrect success/rejection/comparison split')
require({(r['family'],r['scale']) for r in comparisons} == {
    ('character-casual',1.0),('character-professional',0.01)}, 'unexpected input scales')
groups = defaultdict(list)
for step in steps:
    groups[(step['family'],step['pose'],step['stage'])].append(step)
for values in groups.values():
    require({v['fill'] for v in values} == {-1,0,205}, 'missing memory initialization condition')
    stripped = [{k:v for k,v in row.items() if k != 'fill'} for row in values]
    require(all(v == stripped[0] for v in stripped), 'memory initialization changed compound result')
manifest_path = root/'Assets/Licensed/Compact16uvConvexRepair/intake.json'
manifest = json.loads(manifest_path.read_text())
prior = json.loads((root/'docs/diagnostics/compact16uv-character-physics-repaired/export.json').read_text())
require(sha(manifest_path) == prior['manifestSha256'], 'intake changed')
for entry in manifest['assets']:
    require(sha(root/entry['assetPath']) == entry['sourceSha256'], 'repaired source changed')
    require(sha(root/entry['originalAssetPath']) == entry['originalSourceSha256'], 'original source changed')
    fixture = root/'Assets/Licensed/Compact16uvConvexRepair/Resources/CharacterPhysicsMigration'/(entry['family']+'.json')
    prior_asset, = [a for a in prior['assets'] if a['family'] == entry['family']]
    require(sha(fixture) == prior_asset['fixtureSha256'], 'fixture changed')
summary = dict(schemaVersion=1,baseCommit='a55bbf8',unity='6000.3.22f1',platform='Windows Editor EditMode',
    fixedUniformScalePreparationAccepted=True,runtimeNonuniformParentScaleSupported=False,
    distinctIntegratedFamilyPoses=6,integratedCases=18,expectedRuntimeScaleRejectionCases=12,
    committedCuts=36,memoryInitializationResultsExactlyEqual=True,
    focused=dict(passed=32,failed=0,skipped=0),fullEditMode=dict(passed=3694,failed=0,skipped=0),
    runtimeContractChanged=False,productionLoaderIntegrated=False,originalInputsPreserved=True,
    productionSceneReferencesChanged=False,originalRigAndUcxHierarchyPreserved=True,
    sourceSha256={name:sha(root/name) for name in [
        'Assets/Zantetsu/Tests/EditMode/PhysicsCut/Compact16uvScaleNormalizationTests.cs',
        'Assets/Zantetsu/Tests/EditMode/PhysicsCut/CutGeometryConnectionTests.cs',
        'Assets/Zantetsu/Tests/EditMode/PhysicsCut/CutGeometryConnectionTests.Character.cs',
        'Assets/Zantetsu/Runtime/MeshCut/VpMultiCutSnapshot.cs',
        'Assets/Zantetsu/Runtime/PhysicsCut/PhysicsOwnerBuild.cs']},
    comparisons=comparisons,steps=steps,rejected=rejected,limitations=[
        'Test-only in-memory preparation; no importer hook, persistent prepared asset or mesh cache',
        'Unity BakeMesh(true), not custom Burst skinning acceptance',
        '300 deterministic transform poses per family, not AnimationClip playback',
        'Fixed positive uniform input scale only; no negative scale, shear or blendshape support',
        'No Rigidbody simulation, player images, XR, performance or IL2CPP acceptance'])
(out/'summary.json').write_bytes((json.dumps(summary,indent=2)+'\n').encode('utf-8'))
print(json.dumps({k:v for k,v in summary.items() if k not in ('steps','rejected','sourceSha256')},indent=2))
