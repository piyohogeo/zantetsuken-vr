"""Validate runtime scale-input evidence without hiding strict raster failures."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import statistics
import xml.etree.ElementTree as ET
import numpy as np
from PIL import Image

root=Path(__file__).resolve().parents[1]
out=root/'docs/diagnostics/compact16uv-fixed-scale-input'
parser=argparse.ArgumentParser()
parser.add_argument('--focused-pid',required=True)
parser.add_argument('--editmode-pid',required=True)
args=parser.parse_args()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def archive(name,count,pid):
    raw=(out/(name+'-raw.xml')).read_text(encoding='utf-8-sig')
    node=ET.fromstring(raw)
    assert node.attrib['result']=='Passed' and int(node.attrib['passed'])==count
    assert int(node.attrib['failed'])==0 and int(node.attrib['skipped'])==0
    assert {p.attrib['value'] for p in node.findall('.//property[@name="_PID"]')}=={pid}
    for old in (os.environ['USERPROFILE'],os.environ['USERPROFILE'].replace('\\','/'),os.environ['COMPUTERNAME']):
        raw=raw.replace(old,'[REDACTED]')
    (out/(name+'.xml')).write_bytes(('\n'.join(line.rstrip() for line in raw.splitlines())+'\n').encode('utf-8'))
    return node


focused=archive('focused',40,args.focused_pid)
full=archive('editmode',3704,args.editmode_pid)
clips=[]
for test in full.findall('.//test-case'):
    if 'FixedScale_ImportedClip' in test.attrib['name']:
        m=re.search(r'IMPORTED_CLIP family=(\S+) clip=(\S+) length=(\S+) curves=(\d+) varyingCurves=(\d+) samples=120 maxWorldError=(\S+) maxWorldNormalError=(\S+) maxWorldMotion=(\S+)',test.findtext('output',''))
        assert m, 'Missing imported clip evidence'
        family,clip,length,curves,varying,error,normal,motion=m.groups()
        clips.append(dict(family=family,clip=clip,length=float(length),curves=int(curves),varyingCurves=int(varying),samples=120,
            maxWorldPositionError=float(error),maxWorldNormalError=float(normal),maxWorldMotion=float(motion)))
assert len(clips)==2
dynamic_clips_validated=all(c['varyingCurves']>0 and c['maxWorldMotion']>1e-5 for c in clips)
cases=focused.findall('.//test-case')
assert len([c for c in cases if 'FixedScale_Compound19_CommonPlane' in c.attrib['name']])==18
assert len([c for c in cases if 'FixedScale_RuntimeParentScale' in c.attrib['name']])==12
for c in cases:
    if 'FixedScale_Compound19' in c.attrib['name'] or 'FixedScale_RuntimeParentScale' in c.attrib['name']:
        assert 'CutFixtureEnding state=ReleasedSafely' in c.findtext('output','')


def pixels(path):
    return np.asarray(Image.open(path).convert('RGBA'),dtype=np.int16)


reports=[]
for run in (1,2,3):
    report=json.loads((out/f'run{run}/report.json').read_text())
    assert not report['failure'] and report['fixedScalePrepared']
    assert report['graphics']=='Direct3D11' and report['vertexStride']==16 and report['development']
    assert len(report['comparisons'])==28 and report['preparedMeshBuilds']==1 and report['cachedMeshes']==2
    assert report['passed']==all(c['passed'] for c in report['comparisons'])
    for comparison in report['comparisons']:
        name=comparison['name']
        if name.endswith('-original-vs-prepared'):
            prefix=name[:-len('-original-vs-prepared')]
            a,b=prefix+'-original-skin.png',prefix+'-prepared-skin.png'
        elif name.endswith('-skin-vs-bake'):
            prefix=name[:-len('-skin-vs-bake')]
            a,b=prefix+'-skin.png',prefix+'-bake.png'
        else:
            a,b=name+'-mesh.png',name+'-vp.png'
        a,b=pixels(out/f'run{run}'/a),pixels(out/f'run{run}'/b)
        covered=(a[:,:,3]>127)|(b[:,:,3]>127)
        delta=np.abs(a[:,:,:3]-b[:,:,:3])
        assert comparison['silhouette']==int(((a[:,:,3]>127)!=(b[:,:,3]>127)).sum())
        assert comparison['covered']==int(covered.sum())
        assert comparison['maxRgb']==int(delta[covered].max())
        assert comparison['changed']==int((np.any(delta!=0,axis=2)&covered).sum())
        assert comparison['passed']==(comparison['covered']>100 and comparison['silhouette']==0 and comparison['maxRgb']<=3)
    for cost in report['preparation']:
        assert cost['preparedMeshBuilds']==(0 if cost['family']=='character-casual' else 1)
    reports.append(report)
    lines=(out/f'player{run}.log').read_text(encoding='utf-8-sig').splitlines()
    (out/f'run{run}/trace.txt').write_bytes(('\n'.join(s for s in lines if s.startswith('CHARACTER '))+'\n').encode('utf-8'))

images=sorted((out/'run1').glob('*.png'))
assert len(images)==56
reproducible=all(sha(p)==sha(out/f'run{run}'/p.name) for p in images for run in (2,3))
assert all(r['comparisons']==reports[0]['comparisons'] for r in reports), 'Raster outcomes changed between processes'
costs=[]
for family in ('character-casual','character-professional'):
    rows=[next(c for c in r['preparation'] if c['family']==family) for r in reports]
    values={field:dict(median=statistics.median(r[field] for r in rows),minimum=min(r[field] for r in rows),maximum=max(r[field] for r in rows))
        for field in ('coldPrepareUs','cachedPrepareMedianUs','originalBakeMedianUs','preparedBakeMedianUs','additionalPreparedMeshBytes')}
    costs.append(dict(family=family,processMedians=values))
caps=[]
for family in ('casual','professional'):
    for pose in (0,1):
        changes=[]
        for stage in (1,2):
            prefix=f'character-{family}-pose{pose}-stage{stage}'
            a=pixels(out/'run1'/(prefix+'-debug0-vp.png'))
            b=pixels(out/'run1'/(prefix+'-debug1-vp.png'))
            changes.append(int(np.any(a[:,:,:3]!=b[:,:,:3],axis=2).sum()))
        assert all(n>0 for n in changes), 'Cap must be visible in both stages'
        caps.append(dict(family=family,pose=pose,changedPixels=changes))
buildlog=(out/'build.log').read_text(encoding='utf-8-sig')
inventory=re.findall(r'FIXED SCALE CLIP INVENTORY family=(\S+) count=(\d+)',buildlog)
assert len(inventory)==2
manifest_path=root/'Assets/Licensed/Compact16uvConvexRepair/intake.json'
manifest=json.loads(manifest_path.read_text())
prior=json.loads((root/'docs/diagnostics/compact16uv-character-physics-repaired/export.json').read_text())
assert sha(manifest_path)==prior['manifestSha256'], 'Pinned intake changed'
for entry in manifest['assets']:
    assert sha(root/entry['assetPath'])==entry['sourceSha256']
    assert sha(root/entry['originalAssetPath'])==entry['originalSourceSha256']
    previous=next(a for a in prior['assets'] if a['family']==entry['family'])
    assert sha(root/'Assets/Licensed/Compact16uvConvexRepair/Resources/CharacterPhysicsMigration'/(entry['family']+'.json'))==previous['fixtureSha256']
summary=dict(schemaVersion=1,baseCommit='71b04d9a',unity=reports[0]['unity'],platform='Windows x64 IL2CPP Development D3D11 mono',
    runtimeInputImplemented=True,defaultGameplaySceneSwitched=False,independentProcesses=3,
    dynamicClipValidated=dynamic_clips_validated,
    focused=dict(passed=40,failed=0,skipped=0),fullEditMode=dict(passed=3704,failed=0,skipped=0),
    integratedCases=18,committedCuts=36,expectedRuntimeScaleRejections=12,
    strictPlayerPassed=all(r['passed'] for r in reports),
    comparisonsPerProcess=28,passedPerProcess=sum(c['passed'] for c in reports[0]['comparisons']),
    failures=[c for c in reports[0]['comparisons'] if not c['passed']],
    exactImageReproducibility=reproducible,imageCountPerProcess=len(images),imageHashes={p.name:sha(p) for p in images},
    importedClipInventory=[dict(family=f,count=int(n)) for f,n in inventory],
    sampledClips=reports[0]['poses'],clipGeometryComparison=clips,costs=costs,capChanges=caps,
    intakeSha256=sha(root/'Assets/Licensed/Compact16uvConvexRepair/intake.json'),
    binarySha256=sha(root.parent/'Compact16uvFixedScaleInput_20260924/GameAssembly.dll'),
    sourceSha256={p:sha(root/p) for p in [
        'Assets/Zantetsu/Runtime/Rendering/VpFixedScaleSkinInput.cs',
        'Assets/Zantetsu/Runtime/Sandbox/Compact16uvCharacterSceneProbe.cs',
        'Assets/Zantetsu/Editor/Sandbox/Compact16uvCharacterSceneBuild.cs',
        'Assets/Zantetsu/Tests/EditMode/Rendering/VpFixedScaleSkinInputTests.cs',
        'Assets/Zantetsu/Tests/EditMode/PhysicsCut/Compact16uvScaleNormalizationTests.cs',
        'Assets/Zantetsu/Tests/EditMode/PhysicsCut/CutGeometryConnectionTests.Character.cs']},
    limitations=['Player scene uses storage cuts, not physics/owner commits; those are EditMode evidence',
        'Imported CanonicalSource clips are static; animated production clip and Animator playback remain unvalidated',
        'API microtimings are not total main-thread cut cost, frame budget or worst-case preparation',
        'Mesh bytes exclude renderer/rig/temporary/driver/GPU residency and whole-scene peak',
        'No custom Burst skinning, XR, shadow, provisional Stencil, negative scale, shear or blendshape acceptance'])
(out/'summary.json').write_bytes((json.dumps(summary,indent=2)+'\n').encode('utf-8'))
print(json.dumps({k:v for k,v in summary.items() if k not in ('imageHashes','sourceSha256')},indent=2))
