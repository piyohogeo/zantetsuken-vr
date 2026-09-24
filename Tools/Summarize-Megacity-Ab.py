"""Six-process actual-asset AB gate. Cross-layout shading need not be pixel-exact.
Position/index hashes, initial decoded attributes, debug cap coverage and within-
layout image repeats must match. Counters are not whole-product allocation proof.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import xml.etree.ElementTree as ET
import numpy as np
from PIL import Image


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def normalize(text):
    user = os.environ['USERPROFILE']
    return '\n'.join(line.rstrip() for line in text.replace(user, '[USERPROFILE]').replace(user.replace('\\', '/'), '[USERPROFILE]').replace(os.environ['COMPUTERNAME'], '[COMPUTER]').splitlines())+'\n'


def write(path, text):
    path.write_bytes(text.encode('utf-8'))


def picture(path):
    result = np.asarray(Image.open(path).convert('RGB'), dtype=np.int16)
    assert result.shape == (540, 960, 3), path
    return result


def compare(a, b):
    delta = np.abs(a-b)
    return dict(changedPixels=int(np.count_nonzero(np.any(delta != 0, axis=2))), maxRgb=int(delta.max()), meanAbsoluteRgb=float(delta.mean()))


def scope(samples):
    return [s['driverUpdateUs']+s['driverLateUs']+s['cameraSubmissionUs'] for s in samples]


def window(samples):
    values = scope(samples)
    return dict(frames=len(samples), productMedianUs=statistics.median(values), productP95Us=sorted(values)[math.ceil(.95*len(values))-1],
                productSumUs=sum(values), productPeakUs=max(values), inclusiveMainMedianMs=statistics.median(s['previousFrameMainMs'] for s in samples),
                unityAllocatedMedianBytes=statistics.median(s['unityAllocated'] for s in samples), workingSetMedianBytes=statistics.median(s['workingSet'] for s in samples))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--player16', type=Path, required=True)
    parser.add_argument('--player32', type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    root = repo/'docs/diagnostics/compact16uv-megacity-ab'
    rows, signatures, initial_attributes, images = [], [], [], []
    shots = ['04-after-the-child-cut', '04b-after-recut-debug', '05-after-the-ending']
    for stride in (16, 32):
        attribute_runs = []
        for number in (1, 2, 3):
            name = f'accepted{stride}-{number}'
            folder = root/name
            data = json.loads((folder/'scene-ab.json').read_text(encoding='utf-8-sig'))
            log = (root/(name+'.log')).read_text(encoding='utf-8-sig')
            assert data['passed'] and data['stride'] == stride and data['shaderLegacy'] == (stride == 32), name
            assert data['fixture'] == 'authored-megacity-static16-source', name
            assert (data['vertexCapacity'], data['indexCapacity']) == (65536, 262144), name
            for marker in ('hullVertices=24 hullFaces=44 displayVertices=5326', 'anchors=disabled', 'finished with code 0', 'IsReleased=True IsDrained=True displayDisposed=True', 'atlas bound after shutdown=False'):
                assert marker in log, (name, marker)
            assert not any(x in log for x in ('FAILED:', 'Exception:', 'broken=True', 'halted=True')), name
            assert 'geometryFaults=0' in log
            checkpoints = data['checkpoints']
            assert [c['vertices'] for c in checkpoints] == [5326, 6078, 6974], name
            signatures.append([(c['name'], c['vertices'], c['indices'], c['positionHash'], c['indexHash']) for c in checkpoints])
            initial_attributes.append(checkpoints[0]['decodedAttributeHash'])
            attribute_runs.append([c['decodedAttributeHash'] for c in checkpoints])
            samples = data['samples']
            before = [s for s in samples if s['phase'] == 0][-240:]
            after = [s for s in samples if s['phase'] == 4][-240:]
            for settled in (before, after):
                assert len(settled) == 240 and settled[-1]['drawnFrames']-settled[0]['drawnFrames'] == 239, name
            cut = [s for s in samples if s['phase'] in (1, 2)]
            recut = [s for s in samples if s['phase'] == 3]+[s for s in samples if s['phase'] == 4][:1]
            assert all(s['scopedManagedBytes'] == -1 for s in samples), 'unsupported allocation counter must not report zero'
            assert data['frameGcRecorderValid'], name
            rows.append(dict(name=name, stride=stride, frames=len(samples), before=window(before), after=window(after),
                             cut=window(cut), recut=window(recut), decodedAttributeHashes=attribute_runs[-1],
                             observedUnityAllocatedPeakBytes=max(s['unityAllocated'] for s in samples),
                             observedUnityReservedPeakBytes=max(s['unityReserved'] for s in samples),
                             lifetimeWorkingSetPeakBytes=max(s['lifetimePeakWorkingSet'] for s in samples),
                             wholeFrameGcBytes=sum(s['previousFrameGcAllocatedBytes'] for s in samples),
                             cpuVertexCapacityBytes=65536*stride, logicalGpuVertexCapacityBytes=65536*stride))
            write(folder/'trace.txt', normalize('\n'.join(line for line in log.splitlines() if line.startswith(('SANDBOX PLAYER:', 'SCENE AB:', 'AUTHORED MEGACITY:')))))
            for shot in shots:
                current = picture(folder/(shot+'.png'))
                if number > 1:
                    repeated = compare(picture(root/f'accepted{stride}-1'/(shot+'.png')), current)
                    assert repeated['changedPixels'] == 0, (name, shot, repeated)
        assert all(a == attribute_runs[0] for a in attribute_runs), 'within-layout attribute mismatch'
    assert all(s == signatures[0] for s in signatures), 'position/index geometry differs'
    assert len(set(initial_attributes)) == 1, 'initial decoded attributes differ'
    for number in (1, 2, 3):
        for shot in shots:
            a = picture(root/f'accepted16-{number}'/(shot+'.png'))
            b = picture(root/f'accepted32-{number}'/(shot+'.png'))
            result = compare(a, b)
            if 'debug' in shot:
                def green(p): return (p[:,:,1] > p[:,:,0]+30) & (p[:,:,1] > p[:,:,2]+30)
                cap_a, cap_b = green(a), green(b)
                result['greenCapPixels16'] = int(np.count_nonzero(cap_a))
                result['greenCapPixels32'] = int(np.count_nonzero(cap_b))
                result['greenCapMaskChangedPixels'] = int(np.count_nonzero(cap_a != cap_b))
                assert result['greenCapPixels16'] > 100 and result['greenCapMaskChangedPixels'] == 0, result
            images.append(dict(pair=number, shot=shot, **result))
    xml_path = root/'editmode.xml'
    write(xml_path, normalize(xml_path.read_text(encoding='utf-8-sig')))
    tests = ET.parse(xml_path).getroot().attrib
    assert tests['result'] == 'Passed' and all(int(tests[k]) == 0 for k in ('failed', 'skipped', 'inconclusive'))
    hashes = []
    for stride in (16, 32):
        player = getattr(args, f'player{stride}')
        log = (root/f'build{stride}.log').read_text(encoding='utf-8-sig')
        assert 'PLAYER BUILD RESULT: result=Succeeded' in log and 'bytes errors=0' in log
        write(root/f'build{stride}-trace.txt', normalize('\n'.join(line for line in log.splitlines() if line.startswith(('PLAYER BUILD ', 'Compact16uv scene:', 'SCENE AB BUILD:')))))
        for filename in ('CutWorldSandbox.exe', 'GameAssembly.dll', 'CutWorldSandbox_Data/globalgamemanagers'):
            hashes.append(dict(path=f'player{stride}/{filename}', sha256=sha(player/filename)))
    sources = ['Assets/Zantetsu/Editor/Sandbox/Compact16uvSandboxSceneBuild.cs', 'Assets/Zantetsu/Runtime/Sandbox/SandboxPlayerCheck.cs', 'Assets/Zantetsu/Runtime/Sandbox/SceneAbRecorder.cs',
               'Assets/Zantetsu/Runtime/Rendering/VpStatic16File.cs', 'Assets/Zantetsu/Runtime/Rendering/VpRenderVertex.cs', 'Assets/Zantetsu/Settings/CutWorldSandboxProfile.asset',
               'Tools/Summarize-Megacity-Ab.py', 'DESIGN.md', 'docs/diagnostics/compact16uv-megacity-ab/README.md']
    for filename in sources:
        hashes.append(dict(path=filename, sha256=sha(repo/filename)))
    for path in sorted(root.rglob('*')):
        if path.is_file() and path.suffix in ('.xml', '.json', '.png', '.txt') and path.name != 'summary.json':
            hashes.append(dict(path=path.relative_to(root).as_posix(), sha256=sha(path)))
    result = dict(baselineCommit='47e8442c', passed=True, rows=rows, images=images, geometrySignature=signatures[0],
                  initialDecodedAttributeHash=initial_attributes[0], editmodePassed=int(tests['passed']), hashes=hashes,
                  scope='Same Static16-decoded initial attributes, actual Megacity at ordinary scene capacities. No original-float source comparison, growth/retirement peak, GPU residency, XR, free-fall or character skin claim.')
    write(root/'summary.json', json.dumps(result, indent=2)+'\n')
    for row in rows:
        print(row['name'], 'before/after us', row['before']['productMedianUs'], row['after']['productMedianUs'],
              'cut/recut sum us', row['cut']['productSumUs'], row['recut']['productSumUs'], 'Unity peak MiB', row['observedUnityAllocatedPeakBytes']/2**20)
    print('cross-layout images:', images[:3])


if __name__ == '__main__': main()
