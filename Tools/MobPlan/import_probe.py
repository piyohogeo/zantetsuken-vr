"""Import a pinned C0 run, retaining only audited rig paths. Output must be private.

No retargeting or C0 solve is performed. Sample-major values are copied as bytes;
root, timing and contact data remain byte-identical. Uses only Python's stdlib.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import shutil
import struct


def integer(f):
    return struct.unpack('<i', f.read(4))[0]


def string(f):
    n, shift = 0, 0
    while True:
        b = f.read(1)[0]
        n |= (b & 127) << shift
        if b < 128:
            break
        shift += 7
    return f.read(n).decode('utf-8')


def array(f, stride):
    n = integer(f)
    data = f.read(n * stride)
    if n < 0 or len(data) != n * stride:
        raise ValueError('truncated array')
    return data


def put_array(f, data, stride):
    f.write(struct.pack('<i', len(data) // stride))
    f.write(data)


def put_string(f, text):
    data = text.encode('utf-8')
    n = len(data)
    while n >= 128:
        f.write(bytes([(n & 127) | 128]))
        n >>= 7
    f.write(bytes([n]))
    f.write(data)


def compact(data, paths):
    f = io.BytesIO(data)
    if f.read(8) not in (b'ZPTAB002', b'ZPTAB003'):
        raise ValueError('not a pose table')
    for _ in range(7):
        string(f)
    f.read(30)  # duration/rate/regular, two bools, four floats
    header = data[:f.tell()]
    scales, parents, human, times = (array(f, size) for size in (12, 4, 4, 4))
    names = [string(f) for _ in range(integer(f))]
    positions, rotations = array(f, 12), array(f, 16)
    tail = f.read()
    indices = [names.index(p) for p in paths]
    old_to_new = {old: new for new, old in enumerate(indices)}
    old_parents = struct.unpack('<' + 'i' * (len(parents) // 4), parents)
    # A removed ancestor would change FK; never silently flatten it.
    for i in indices:
        if old_parents[i] >= 0 and old_parents[i] not in old_to_new:
            raise ValueError('required ancestor omitted: ' + names[old_parents[i]])
    samples = len(times) // 4
    if len(positions) != samples * len(names) * 12 or len(rotations) != samples * len(names) * 16:
        raise ValueError('inconsistent sample-major arrays')
    out = io.BytesIO()
    out.write(header)
    put_array(out, b''.join(scales[i*12:i*12+12] for i in indices), 12)
    put_array(out, b''.join(struct.pack('<i', old_to_new.get(old_parents[i], -1)) for i in indices), 4)
    # Humanoid mapping is indexed by human bone, with values referring to table indices.
    human_indices = struct.unpack('<' + 'i' * (len(human) // 4), human)
    put_array(out, b''.join(struct.pack('<i', old_to_new.get(i, -1)) for i in human_indices), 4)
    put_array(out, times, 4)
    out.write(struct.pack('<i', len(paths)))
    for path in paths:
        put_string(out, path)
    for values, stride in ((positions, 12), (rotations, 16)):
        selected = b''.join(values[(s*len(names)+i)*stride:(s*len(names)+i+1)*stride] for s in range(samples) for i in indices)
        put_array(out, selected, stride)
    out.write(tail)
    result = out.getvalue()
    # Parse the reduced file again: compaction to the same paths must be idempotent.
    return result, samples


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--probe', type=Path, required=True)
    parser.add_argument('--run', required=True)
    parser.add_argument('--paths', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    paths = args.paths.read_text(encoding='utf-8-sig').splitlines()
    if len(paths) != 66 or len(set(paths)) != 66:
        raise ValueError('expected 66 unique audited paths')
    source = args.probe / 'LocalData/OptimizedPoseTables' / args.run
    clips = json.loads((source / 'clips.json').read_text(encoding='utf-8-sig'))['clips']
    dest = args.output
    (dest / 'Tables').mkdir(parents=True, exist_ok=True)
    entries, before, after, samples = [], 0, 0, 0
    for clip in clips:
        raw = (source / 'Tables' / clip['tableFile']).read_bytes()
        reduced, count = compact(raw, paths)
        assert compact(reduced, paths)[0] == reduced
        name = Path(clip['tableFile']).stem + '.bytes'
        (dest / 'Tables' / name).write_bytes(reduced)
        entries.append(dict(clipId=clip['clipId'], file='Tables/' + name, sourceSha256=digest(raw), sha256=digest(reduced)))
        before += len(raw)
        after += len(reduced)
        samples += count
    for src, name in ((args.probe / 'Generated/LocomotionSearch/dataset-INERTIAL.bin', 'dataset.bytes'),
                      (args.probe / 'Generated/LocomotionSearch/SceneMaps/tiled-seed2/polygons.bin', 'polygons.bytes'),
                      (source / 'manifest.json', 'source-c0-manifest.json')):
        shutil.copyfile(src, dest / name)
    manifest = dict(sourceRun=args.run, boneCount=66, clips=entries, sourceBytes=before, compactBytes=after,
                    sampleCount=samples, requiredPaths=paths, datasetSha256=digest((dest/'dataset.bytes').read_bytes()),
                    mapSha256=digest((dest/'polygons.bytes').read_bytes()))
    (dest / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(clips=len(entries), bones=66, samples=samples, before=before, after=after)))


if __name__ == '__main__':
    main()
