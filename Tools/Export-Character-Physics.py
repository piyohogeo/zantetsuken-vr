"""Pinned character UCX export; no repair, fitting, source save or positional weld.

The Unity bone-local payload is derived from these renderer-bind-local points
with the audited imported Mesh bindposes, not guessed Blender bone axis rules.
"""
import hashlib
import importlib.util
import json
from pathlib import Path
import bpy
from mathutils import Matrix

root = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('convex_audit', root / 'Tools/Export-Compact16uv-Physics.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)
manifest_path = root / 'Assets/Licensed/Compact16uvIntake/intake.json'
manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
out = root / 'Assets/Licensed/Compact16uvIntake/Resources/CharacterPhysicsMigration'
report_path = root / 'docs/diagnostics/compact16uv-character-physics/export.json'
if out.exists() or report_path.exists():
    raise RuntimeError('Refusing to overwrite an existing export; use a new version for changed input.')
mirror = Matrix.Diagonal((-1., 1., 1., 1.))
reports, payloads = [], []
def flat(points):
    return [float(x) for p in points for x in p]
for entry in manifest['assets']:
    if not entry['family'].startswith('character-'):
        continue
    source = root / entry['assetPath']
    assert audit.sha(source) == entry['sourceSha256']
    upstream_family = 'CasualCharacters' if entry['family'] == 'character-casual' else 'ProfessionalCharacters'
    upstream = root.parent / 'zantetsuken-blender-pipeline-pilot/Generated/BottomHalfUV/ITHappyCharacterMeshRepair' / upstream_family / '1.23.0-convex-remap_palette/Working' / source.name
    assert audit.sha(upstream) == entry['sourceSha256']
    bpy.ops.wm.open_mainfile(filepath=str(source), load_ui=False)
    bpy.context.view_layer.update()
    owner = bpy.data.objects[entry['objectName']]
    assert owner.type == 'MESH' and not owner.data.shape_keys
    owner_inverse = owner.matrix_world.inverted()
    hulls, statistics = [], []
    for obj in sorted(bpy.data.objects, key=lambda o: o.name):
        if not obj.name.startswith('UCX_'):
            continue
        parent = obj.parent
        assert obj.type == 'MESH' and not obj.modifiers and parent.parent_type == 'BONE'
        rig = parent.parent
        bone_name = obj['representative_bone']
        assert rig.type == 'ARMATURE' and bone_name == parent.parent_bone
        # Use the actual evaluated authoring parent frame, including Blender's
        # bone-parent offset, then map to the data-bone rest frame explicitly.
        bone_local = (rig.matrix_world @ rig.pose.bones[bone_name].matrix).inverted() @ obj.matrix_world
        rest_to_renderer = mirror @ owner_inverse @ rig.matrix_world @ rig.data.bones[bone_name].matrix_local
        transform = rest_to_renderer @ bone_local
        vertices = [transform @ v.co for v in obj.data.vertices]
        faces = [list(p.vertices) for p in obj.data.polygons]
        if transform.to_3x3().determinant() < 0:
            faces = [list(reversed(f)) for f in faces]
        try:
            stats = audit.audit_hull(vertices, faces)
            stats['convexAccepted'] = True
        except ValueError as error:
            diagnostics = []
            for label, points, polys in [('source', [v.co for v in obj.data.vertices], [list(p.vertices) for p in obj.data.polygons]), ('transformed', vertices, faces)]:
                extent = max(max(v[d] for v in points) - min(v[d] for v in points) for d in range(3))
                worst = 0
                for f in polys:
                    a,b,c = [points[i] for i in f[:3]]
                    n = audit.cross(audit.sub(b,a), audit.sub(c,a))
                    length = audit.dot(n,n)**.5
                    if length:
                        worst = max(worst, max(audit.dot(n,audit.sub(v,a))/length for v in points))
                diagnostics.append(dict(frame=label, extent=extent, worstOutside=worst, tolerance=max(1e-6,extent*1e-6)))
            print('CONVEX_FAILURE ' + json.dumps(dict(name=obj.name, reason=str(error), diagnostics=diagnostics)))
            stats = dict(vertices=len(vertices), faces=len(faces), convexAccepted=False, failure=str(error), diagnostics=diagnostics)
        offsets, indices = [0], []
        for face in faces:
            indices.extend(face)
            offsets.append(len(indices))
        hulls.append(dict(name=obj.name, boneName=bone_name, convexAccepted=stats['convexAccepted'],
                          rendererBindVertices=flat(vertices),
                          importedMeshLocalVertices=flat([mirror @ v.co for v in obj.data.vertices]),
                          faceOffsets=offsets, faceIndices=indices))
        posed = mirror @ owner_inverse @ obj.matrix_world
        pose_rest_error = max((transform @ v.co - posed @ v.co).length for v in obj.data.vertices)
        statistics.append(dict(name=obj.name, bone=bone_name, poseVsRestMax=pose_rest_error, **stats))
    assert len(hulls) == 19 and audit.sha(source) == entry['sourceSha256']
    fixture = dict(schemaVersion=1, family=entry['family'], sourceSha256=entry['sourceSha256'],
                   coordinateSystem='RendererBindLocal_MirrorBlenderOwnerX',
                   authoredDisplayVertices=flat([mirror @ v.co for v in owner.data.vertices]), hulls=hulls)
    payload = (json.dumps(fixture, indent=2) + '\n').encode('utf-8')
    filename = entry['family'] + '.json'
    payloads.append((filename, payload))
    reports.append(dict(family=entry['family'], sourceSha256=entry['sourceSha256'], upstreamEqualsPinned=True,
                        fixtureSha256=hashlib.sha256(payload).hexdigest(), fixtureFile=filename,
                        sourceUnchanged=True, hulls=statistics))
out.mkdir(parents=True)
for filename, payload in payloads:
    (out / filename).write_bytes(payload)
report_path.parent.mkdir(parents=True, exist_ok=True)
report_path.write_bytes((json.dumps(dict(schemaVersion=1, blender=bpy.app.version_string,
    scriptSha256=audit.sha(__file__), manifestSha256=audit.sha(manifest_path), assets=reports), indent=2) + '\n').encode('utf-8'))
print(json.dumps([dict(family=r['family'], hulls=len(r['hulls']), convexAccepted=sum(h['convexAccepted'] for h in r['hulls']), fixtureSha256=r['fixtureSha256'],
                       maxPoseVsRest=max(h['poseVsRestMax'] for h in r['hulls'])) for r in reports]))
