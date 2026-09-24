"""Pinned Megacity physics intake. Run with Blender --background --disable-autoexec.

No source save, geometry repair, fitted basis, positional weld or hull generation.
The explicit Blender-owner-local -> imported Mesh-local basis is (-x,y,z).
Every display position and oriented triangle must agree through the existing
authoring topology map before any private runtime fixture is emitted.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import struct
import sys


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def cross(a, b):
    return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])


def dot(a, b):
    return sum(x*y for x, y in zip(a, b))


def cycle(ids):
    return min(tuple(ids[i:] + ids[:i]) for i in range(len(ids)))


def audit_hull(vertices, faces):
    """Reject invalid data; retain authored vertices and face IDs unchanged."""
    if len(vertices) < 4 or any(not math.isfinite(c) for v in vertices for c in v):
        raise ValueError('invalid hull vertices')
    extent = max(max(v[d] for v in vertices)-min(v[d] for v in vertices) for d in range(3))
    tolerance = max(1e-6, extent * 1e-6)
    edges = defaultdict(list)
    volume = 0.0
    max_outside = 0.0
    used = set()
    for fi, face in enumerate(faces):
        if len(face) < 3 or len(set(face)) != len(face) or any(i < 0 or i >= len(vertices) for i in face):
            raise ValueError('invalid face indices')
        used.update(face)
        a, b, c = [vertices[i] for i in face[:3]]
        n = cross(sub(b, a), sub(c, a))
        length = math.sqrt(dot(n, n))
        if length <= tolerance*tolerance:
            raise ValueError('degenerate face')
        distances = [dot(n, sub(v, a))/length for v in vertices]
        if any(abs(distances[i]) > tolerance for i in face):
            raise ValueError('nonplanar face')
        max_outside = max(max_outside, max(distances))
        if max(distances) > tolerance:
            raise ValueError('nonconvex or inward face')
        for k in range(1, len(face)-1):
            volume += dot(a, cross(vertices[face[k]], vertices[face[k+1]]))/6
        for k, vi in enumerate(face):
            vj = face[(k+1) % len(face)]
            edges[min(vi, vj), max(vi, vj)].append((fi, vi, vj))
    if used != set(range(len(vertices))):
        raise ValueError('unused hull vertex')
    adjacency = defaultdict(set)
    for owners in edges.values():
        if len(owners) != 2 or owners[0][1:] != owners[1][1:][::-1]:
            raise ValueError('open, nonmanifold or inconsistently oriented edge')
        a, b = owners[0][0], owners[1][0]
        adjacency[a].add(b)
        adjacency[b].add(a)
    seen, pending = set(), [0]
    while pending:
        f = pending.pop()
        if f not in seen:
            seen.add(f)
            pending.extend(adjacency[f] - seen)
    if len(seen) != len(faces) or len(vertices)-len(edges)+len(faces) != 2 or volume <= tolerance**3:
        raise ValueError('disconnected/invalid volume or Euler characteristic')
    return dict(vertices=len(vertices), faces=len(faces), edges=len(edges),
                signedVolume=volume, maximumConvexityError=max_outside, tolerance=tolerance)


def main():
    import bpy
    from mathutils import Matrix
    parser = argparse.ArgumentParser()
    parser.add_argument('--product-root', required=True)
    parser.add_argument('--upstream-blend', required=True)
    parser.add_argument('--out', required=True, help='Fresh ignored private directory')
    parser.add_argument('--report', required=True, help='Fresh aggregate report, no raw licensed geometry')
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    root, output, report_path = Path(args.product_root), Path(args.out), Path(args.report)
    if output.exists() or report_path.exists():
        raise ValueError('refusing to overwrite an existing fixture/report')
    manifest_path = root/'Assets/Licensed/Compact16uvIntake/intake.json'
    entry, = [x for x in json.loads(manifest_path.read_text(encoding='utf-8'))['assets'] if x['family']=='static-megacity']
    source = root/entry['assetPath']
    source_sha = sha(source)
    if Path(bpy.data.filepath).resolve() != source.resolve() or source_sha != entry['sourceSha256'] or sha(args.upstream_blend) != source_sha:
        raise ValueError('loaded/pinned/upstream source identity mismatch')
    owner = bpy.data.objects[entry['objectName']]
    if owner.type != 'MESH' or owner.modifiers or owner.data.shape_keys:
        raise ValueError('only the pinned static unmodified mesh is supported')
    # Explicit imported Mesh-local frame, not the prefab/world frame. Do not infer
    # this from bounds or choose a best-fit matrix. Bind it by all mapped vertices.
    basis = Matrix.Diagonal((-1.0, 1.0, 1.0, 1.0))
    packed_path = root/'Assets/Licensed/Compact16uvIntake/Resources/Static16Migration/Megacity.bytes'
    packed = packed_path.read_bytes()
    magic, version, stride, vc, ic, tc, sc = struct.unpack_from('<7i', packed)
    if (magic, version, stride) != (0x36315056, 1, 16) or len(packed) != 28+20*vc+4*ic+12*sc:
        raise ValueError('Static16 header/length mismatch')
    positions = [struct.unpack_from('<3f', packed, 28+16*i) for i in range(vc)]
    indices = list(struct.unpack_from('<%di' % ic, packed, 28+16*vc))
    topology = list(struct.unpack_from('<%di' % vc, packed, 28+16*vc+4*ic))
    if topology != entry['topologyMap'] or tc != len(owner.data.vertices):
        raise ValueError('authoring topology map mismatch')
    mapped = [tuple(basis @ v.co) for v in owner.data.vertices]
    error = max(math.dist(p, mapped[t]) for p, t in zip(positions, topology))
    if error != 0:
        raise ValueError('explicit basis does not reproduce every packed position exactly: %g' % error)
    # This pinned .blend import reverses every source triangle. Verify that
    # explicit rule, not an unordered three-ID match or an auto-selected winding.
    authored = Counter(cycle(list(reversed(p.vertices))) for p in owner.data.polygons)
    imported = Counter(cycle([topology[i] for i in indices[k:k+3]]) for k in range(0, ic, 3))
    if any(len(p.vertices) != 3 for p in owner.data.polygons) or authored != imported:
        raise ValueError('reversed authoring triangle multiset/winding mismatch')
    hulls, hull_reports, anchors = [], [], []
    owner_inverse = owner.matrix_world.inverted()
    for obj in sorted(bpy.data.objects, key=lambda o:o.name):
        if obj.name.startswith('UCX_'):
            if obj.parent != owner or obj.type != 'MESH' or obj.modifiers:
                raise ValueError('unbound or modified convex')
            transform = basis @ owner_inverse @ obj.matrix_world
            vertices = [list(transform @ v.co) for v in obj.data.vertices]
            faces = [list(p.vertices) for p in obj.data.polygons]
            if transform.to_3x3().determinant() < 0:
                faces = [list(reversed(f)) for f in faces]
            stats = audit_hull(vertices, faces)
            offsets, flat = [0], []
            for face in faces:
                flat.extend(face)
                offsets.append(len(flat))
            hulls.append(dict(name=obj.name, vertices=[c for v in vertices for c in v], faceOffsets=offsets, faceIndices=flat))
            hull_reports.append(dict(name=obj.name, **stats))
        elif obj.name.startswith('Anchor_'):
            if obj.parent != owner or obj.type != 'EMPTY':
                raise ValueError('unbound anchor')
            anchors.append(dict(name=obj.name, position=list((basis @ owner_inverse @ obj.matrix_world).translation)))
    if len(hulls) != 1 or len(anchors) != 21:
        raise ValueError('pinned authored helper counts changed')
    if sha(source) != source_sha:
        raise ValueError('source was modified')
    fixture = dict(schemaVersion=1, sourceSha256=source_sha, static16Sha256=sha(packed_path),
                   coordinateSystem='ImportedMeshLocal_MirrorBlenderOwnerX', hulls=hulls, anchors=anchors)
    payload = (json.dumps(fixture, indent=2)+'\n').encode('utf-8')
    report = dict(passed=True, blender=bpy.app.version_string, sourceSha256=source_sha,
                  upstreamEqualsPinned=True, sourceUnchanged=True, inputManifestSha256=sha(manifest_path),
                  static16Sha256=sha(packed_path), fixtureSha256=hashlib.sha256(payload).hexdigest(),
                  scriptSha256=sha(__file__), explicitBasis='(-x,y,z), owner local -> imported Mesh local',
                  mappedDisplayVertices=vc, authoredTopologyVertices=tc, positionMaximumError=error,
                  orientedTrianglesMatched=ic//3, submeshes=sc, hulls=hull_reports, anchors=len(anchors),
                  disposition='offline physics binding only; Unity cook/cut/scene/current-pose not yet verified')
    output.mkdir(parents=True)
    (output/'Megacity.json').write_bytes(payload)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
