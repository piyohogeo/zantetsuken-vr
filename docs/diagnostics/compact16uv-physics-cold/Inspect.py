"""Read-only D5 evidence collector, including retained unsuccessful runs."""
import hashlib, json, subprocess, sys
from pathlib import Path
import xml.etree.ElementTree as ET

folder=Path(__file__).resolve().parent
product=folder.parents[2]
paths=[
 'Assets/Zantetsu/Runtime/PhysicsCut/ProvisionalSeparation.cs',
 'Assets/Zantetsu/Runtime/PhysicsCut/VpPreparedPhysicsInput.cs',
 'Assets/Zantetsu/Runtime/PhysicsCut/VpPhysicsColdPreparation.cs',
 'Assets/Zantetsu/Tests/EditMode/PhysicsCut/VpPreparedPhysicsInputTests.cs',
 'Assets/Zantetsu/Tests/EditMode/PhysicsCut/D5JointDefaultsTests.cs',
 'Assets/Zantetsu/Tests/PlayMode/ProvisionalMassFlagActivationPlayModeTests.cs']
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
runs=[]
for name,total,passed in [('focused-1',24,24),('playmode-1',1,0),('playmode-2',1,1),
                         ('provisional-playmode-1',14,13),('full-1',3821,3821),('palette-recheck-1',1,1)]:
    directory=product/'Logs/PhysicsColdMigration'/name
    xml=ET.parse(directory/'results.xml').getroot()
    counts={k:int(xml.attrib[k]) for k in ['total','passed','failed','skipped','inconclusive']}
    assert counts['total']==total and counts['skipped']==counts['inconclusive']==0
    if passed is not None: assert counts['passed']==passed and counts['failed']==total-passed
    cases=xml.findall('.//test-case'); assert len(cases)==total
    physics=[c for c in cases if c.attrib['fullname'].startswith('Zantetsu.PhysicsCut.PlayModeTests.')]
    if name=='provisional-playmode-1': assert len(physics)==13 and all(c.attrib['result']=='Passed' for c in physics)
    source_root=directory/'source' if name in ['focused-1','playmode-1'] else product
    runs.append(dict(name=name,result=xml.attrib['result'],**counts,
        d5Cases=sum('D5_' in c.attrib['name'] for c in cases),
        physicsPlayModePassed=sum(c.attrib['result']=='Passed' for c in physics),
        failures=[dict(name=c.attrib['fullname'],message=c.findtext('failure/message',''),stack=c.findtext('failure/stack-trace',''))
                  for c in cases if c.attrib['result']!='Passed'],
        pids=sorted({p.attrib['value'] for p in xml.findall('.//property[@name="_PID"]')}),
        xmlSha256=sha(directory/'results.xml'),logSha256=sha(directory/'editor.log'),
        sources=[dict(file=p,sha256=sha(source_root/p)) for p in paths]))
xr_blob=subprocess.check_output(['git','-c','safe.directory='+str(product),'-C',str(product),
    'hash-object','Assets/XR/Settings/OpenXR Package Settings.asset'],text=True).strip()
assert xr_blob=='3ecd4343a07b606469695cd872f833ee3fb67d40'
assert not (product/'ProjectSettings/SceneTemplateSettings.json').exists()
summary=dict(baselineCommit='64fc03d7fe8eaadc264cf23206530ceb5dd734c8',runs=runs,
    freshJointSettersOmitted=5,coldEntryGameplayConnected=False,performanceMeasured=False,
    standalonePlayerVerified=False,privateSnapshotsModified=False,
    paletteTestSourceSha256=sha(product/'Assets/Zantetsu/Tests/StandaloneRendering/PaletteAtlasPlayerTests.cs'),
    unityGeneratedSettingsRestored=True,
    unitySettingsBackup=[dict(file=name,sha256=sha(product/'Logs/PhysicsColdMigration/unity-generated-settings'/name))
        for name in ['OpenXR Package Settings.asset','SceneTemplateSettings.json']])
if '--check' in sys.argv:
    assert summary==json.loads((folder/'summary.json').read_text(encoding='utf-8'))
    print('D5 source/log/XML evidence and stored summary match, including retained failures.')
else: print(json.dumps(summary,indent=2))
