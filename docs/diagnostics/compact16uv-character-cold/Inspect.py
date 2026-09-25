"""Read-only product D6 character cold-handle evidence; the test project is a separate candidate copy."""
import hashlib,json,re,sys
from pathlib import Path
import xml.etree.ElementTree as ET

folder=Path(__file__).resolve().parent
product=folder.parents[2]
project=product.parent/'zantetsuken-assets-private/Working/D6C-2ae6c9f/Project'
raw=product/'Logs/CharacterColdMigration'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
paths=['Runtime/PhysicsCut/CutWorldRoot.cs','Runtime/PhysicsCut/VpPreparedCharacterCut.cs',
       'Tests/EditMode/PhysicsCut/PreparedCharacterColdTests.cs',
       'Tests/PlayMode/ProvisionalMassFlagActivationPlayModeTests.cs','Tests/PlayMode/PreparedCharacterColdPlayModeTests.cs']
manifest=[]
for root in ['Runtime','Tests']:
    assert {p.relative_to(product/'Assets/Zantetsu'/root).as_posix() for p in (product/'Assets/Zantetsu'/root).rglob('*') if p.is_file()} == {
        p.relative_to(project/'Assets/Zantetsu'/root).as_posix() for p in (project/'Assets/Zantetsu'/root).rglob('*') if p.is_file()},root
    for p in sorted((product/'Assets/Zantetsu'/root).rglob('*')):
        if not p.is_file():continue
        rel=p.relative_to(product).as_posix()
        assert sha(p)==sha(project/rel),rel
        manifest.append(dict(file=rel,sha256=sha(p)))
sources=[dict(file=p,sha256=sha(product/'Assets/Zantetsu'/p)) for p in paths]
fixture_sets=[]
private=product.parent/'zantetsuken-assets-private'
for label,original,candidate in [
    ('scenes',product/'Assets/Scenes',project/'Assets/Scenes'),
    ('licensed-intake',product/'Assets/Licensed',project/'Assets/Licensed'),
    ('public-adopted',product/'Tools/Phase02',project/'Tools/Phase02'),
    ('licensed-adopted',private/'Working/Phase0.2/Adopted',project.parent/'zantetsuken-assets-private/Working/Phase0.2/Adopted')]:
    entries=[]
    for p in sorted(original.rglob('*')):
        if not p.is_file():continue
        rel=p.relative_to(original).as_posix()
        assert sha(p)==sha(candidate/rel),f'{label}/{rel}'
        entries.append(dict(file=rel,sha256=sha(p)))
    assert entries,label
    fixture_sets.append(dict(name=label,fileCount=len(entries),
        manifestDigest=hashlib.sha256(json.dumps(entries,sort_keys=True,separators=(',',':')).encode()).hexdigest()))
runs=[]
for directory in sorted(raw.glob('*-*')):
    log=directory/'editor.log'
    if not log.exists():continue
    text=log.read_text(encoding='utf-8-sig',errors='replace')
    item=dict(name=directory.name,logSha256=sha(log),
        sources=[dict(file=p,sha256=sha(directory/'source'/Path(p).name)) for p in paths],
        compileErrors=sorted(set(re.findall(r'.*error CS\d+.*',text))))
    xmlpath=directory/'results.xml'
    if not xmlpath.exists():item['result']='NoXml';runs.append(item);continue
    xml=ET.parse(xmlpath).getroot();cases=xml.findall('.//test-case')
    item.update(result=xml.attrib['result'],xmlSha256=sha(xmlpath),
        pids=sorted({p.attrib['value'] for p in xml.findall('.//property[@name="_PID"]')}))
    item.update({k:int(xml.attrib[k]) for k in ['total','passed','failed','skipped','inconclusive']})
    item['d6Cases']=sum('D6H_' in c.attrib['name'] for c in cases)
    item['failures']=[dict(name=c.attrib['fullname'],result=c.attrib['result'],
                           message=c.findtext('failure/message',c.findtext('reason/message','')))
                      for c in cases if c.attrib['result']!='Passed']
    item['matchesCurrentSources']=item['sources']==sources
    if item['result']=='Passed':
        assert item['failed']==item['skipped']==item['inconclusive']==0
    runs.append(item)
summary=dict(baseProductCommit='2ae6c9f26886559b7025b0bc0e78367a8ccc7318',
    candidateFileCount=len(manifest),candidateManifestDigest=hashlib.sha256(json.dumps(manifest,sort_keys=True,separators=(',',':')).encode()).hexdigest(),
    sources=sources,runs=runs,regressionFixtureSets=fixture_sets,
    licensedAssetsUsed=True,newD6TestsSyntheticOnly=True,gameplayConnected=False,
    publicColdPreparationApiAdded=True,hotAdapterAdded=False,performanceMeasured=False,playerVerified=False,productUnityLaunched=False)
if '--check' in sys.argv:
    for prefix,total,d6 in [('focused-',14,14),('full-',3855,14),('play-',2,2),('related-',7,2)]:
        assert any(r['name'].startswith(prefix) and r['result']=='Passed' and r['matchesCurrentSources']
                   and not r['compileErrors'] and r['passed']==total and r['d6Cases']==d6 for r in runs),prefix
    assert summary==json.loads((folder/'summary.json').read_text(encoding='utf-8'))
    print('D6 cold handle: current candidate, fixtures and evidence match; focused 14, full 3855, PlayMode 2 and related 7 passed.')
else:print(json.dumps(summary,indent=2))
