"""Read-only D4 evidence collector. Print JSON; --check compares the committed record."""
import hashlib, json, subprocess, sys
from pathlib import Path
import xml.etree.ElementTree as ET

folder = Path(__file__).resolve().parent
product = folder.parents[2]
base = '0d5313044ec1844e81a9cd5caf3d77201a546058'
sources = [
    'Assets/Zantetsu/Runtime/MeshCut/FixedSupportAnchors.cs',
    'Assets/Zantetsu/Runtime/MeshCut/LogicalCutLedger.cs',
    'Assets/Zantetsu/Runtime/MeshCut/CutDag.cs',
    'Assets/Zantetsu/Runtime/MeshCut/VpPreparedRootDisplay.cs',
    'Assets/Zantetsu/Runtime/PhysicsCut/PhysicsOwnerRegistry.cs',
    'Assets/Zantetsu/Tests/EditMode/MeshCut/D4ColdPreparationTests.cs',
    'Assets/Zantetsu/Tests/EditMode/MeshCut/CutDagTests.cs',
    'Assets/Zantetsu/Tests/EditMode/MeshCut/VpPreparedRootDisplayTests.cs',
    'Assets/Zantetsu/Tests/EditMode/PhysicsCut/ProvisionalPhysicsPublicationTests.cs',
]
digest = lambda data: hashlib.sha256(data).hexdigest()
records = []
for name, total in [('focused-1',20), ('focused-2',21), ('full-1',3797)]:
    directory = product/'Logs/ColdCapacityMigration'/name
    root = ET.parse(directory/'results.xml').getroot()
    assert root.attrib['result'] == 'Passed'
    assert int(root.attrib['total']) == int(root.attrib['passed']) == total
    assert all(int(root.attrib[k]) == 0 for k in ['failed','skipped','inconclusive'])
    cases = root.findall('.//test-case')
    assert len(cases) == total and all(t.attrib['result']=='Passed' for t in cases)
    manifest = []
    for path in sources:
        if name == 'focused-1':
            snapshot = directory/'source'/path
            data = snapshot.read_bytes() if snapshot.exists() else subprocess.check_output(
                ['git','-c',f'safe.directory={product.as_posix()}','-C',str(product),'show',base+':'+path])
        else:
            data = (product/path).read_bytes()
        manifest.append(dict(file=path,sha256=digest(data)))
    records.append(dict(name=name,result='Passed',total=total,passed=total,failed=0,skipped=0,inconclusive=0,
        pids=sorted({p.attrib['value'] for p in root.findall('.//property[@name="_PID"]')}),
        d4Cases=sum('D4_' in t.attrib['name'] for t in cases),sources=manifest,
        xmlSha256=digest((directory/'results.xml').read_bytes()), logSha256=digest((directory/'editor.log').read_bytes())))
summary = dict(baselineCommit=base, runs=records, gameplayCapacityConnected=False,
    emptyAnchorPathEnabled=True, playerVerified=False, performanceMeasured=False,
    licensedInputsAdded=False, privateSnapshotsModified=False)
if '--check' in sys.argv:
    assert summary == json.loads((folder/'summary.json').read_text(encoding='utf-8'))
    print('D4 sources and evidence match: focused 20/20 + 21/21; full 3797/3797, no skips.')
else:
    print(json.dumps(summary,indent=2))
