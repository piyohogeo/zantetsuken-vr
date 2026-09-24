"""Localize source convexity failures without changing source or its acceptance."""
import importlib.util
import json
from pathlib import Path
import bpy
root = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('convex_audit', root / 'Tools/Export-Compact16uv-Physics.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)
out = root / 'docs/diagnostics/compact16uv-character-physics'
report = json.loads((out / 'export.json').read_text())
manifest = json.loads((root / 'Assets/Licensed/Compact16uvIntake/intake.json').read_text(encoding='utf-8-sig'))
results = []
for asset in report['assets']:
    entry, = [e for e in manifest['assets'] if e['family'] == asset['family']]
    source = root / entry['assetPath']
    assert audit.sha(source) == asset['sourceSha256']
    bpy.ops.wm.open_mainfile(filepath=str(source), load_ui=False)
    for rejected in [h for h in asset['hulls'] if not h['convexAccepted']]:
        obj = bpy.data.objects[rejected['name']]
        points = [tuple(v.co) for v in obj.data.vertices]
        extent = max(max(v[a] for v in points) - min(v[a] for v in points) for a in range(3))
        tolerance = max(1e-6, extent * 1e-6)
        faces = []
        for face in obj.data.polygons:
            ids = list(face.vertices)
            a, b, c = [points[i] for i in ids[:3]]
            n = audit.cross(audit.sub(b,a), audit.sub(c,a))
            length = audit.dot(n,n)**.5
            distances = [audit.dot(n,audit.sub(p,a))/length for p in points]
            worst = max(distances)
            if worst <= tolerance:
                continue
            edges = [audit.sub(points[ids[i]], points[ids[(i+1)%3]]) for i in range(3)]
            longest = max(audit.dot(e,e)**.5 for e in edges)
            faces.append(dict(faceIndex=face.index, vertexIds=ids, outsideVertex=distances.index(worst),
                              outsideDistance=worst, doubleArea=length, minimumTriangleAltitude=length/longest))
        # Read-only hypothesis: test the other diagonal of the two offending
        # triangles in Python lists. Never assign these faces to a Blender mesh.
        alternative = dict(tested=False)
        if len(faces) == 2:
            first, second = [f['vertexIds'] for f in faces]
            shared = set(first) & set(second)
            if len(shared) == 2 and len(set(first) | set(second)) == 4:
                edges = [(f[i], f[(i+1)%3]) for f in (first, second) for i in range(3)]
                boundary = {a:b for a,b in edges if (b,a) not in edges}
                cycle = [min(shared)]
                for _ in range(3):
                    cycle.append(boundary[cycle[-1]])
                assert cycle[2] in shared and boundary[cycle[-1]] == cycle[0]
                replacements = [[cycle[0], cycle[1], cycle[3]], [cycle[1], cycle[2], cycle[3]]]
                candidate = [list(p.vertices) for p in obj.data.polygons]
                for failed, replacement in zip(faces, replacements):
                    candidate[failed['faceIndex']] = replacement
                try:
                    candidate_stats = audit.audit_hull(points, candidate)
                    alternative = dict(tested=True, passed=True, sourceModified=False, vertexPositionsUnchanged=True,
                                       replacementFaces=replacements, **candidate_stats)
                except ValueError as error:
                    alternative = dict(tested=True, passed=False, reason=str(error), sourceModified=False)
        results.append(dict(family=entry['family'], name=obj.name, sourceSha256=entry['sourceSha256'], tolerance=tolerance,
                            floatResolution32Ulp=extent*32*1.1920929e-7, failingFaces=faces, alternateDiagonalHypothesis=alternative))
        assert audit.sha(source) == entry['sourceSha256']
assert len(results) == 3 and all(r['failingFaces'] for r in results)
(out / 'failure-localization.json').write_bytes((json.dumps(results, indent=2) + '\n').encode('utf-8'))
print(json.dumps(results, indent=2))
