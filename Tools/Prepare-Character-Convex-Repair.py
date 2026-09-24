"""Blender-only, fresh private intake of SHA-pinned upstream repaired copies.

No save/retriangulation/repair of .blend files. Keep the original intake intact.
"""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import sys
import uuid

sys.dont_write_bytecode = True
import bpy
from mathutils import Matrix

root = Path(__file__).resolve().parents[1]
pipeline = root.parent/'zantetsuken-blender-pipeline-pilot'
old_root = root/'Assets/Licensed/Compact16uvIntake'
private = root/'Assets/Licensed/Compact16uvConvexRepair'
public = root/'docs/diagnostics/compact16uv-character-physics-repaired'
spec = importlib.util.spec_from_file_location('audit', root/'Tools/Export-Compact16uv-Physics.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes((json.dumps(data, indent=2)+'\n').encode('utf-8'))


def new_meta(source, target):
    original = source.read_text(encoding='utf-8')
    guid = uuid.uuid5(uuid.NAMESPACE_URL, 'zantetsuken/compact16uv-convex-repair/' + str(target.relative_to(root))).hex
    changed, count = re.subn(r'(?m)^guid: [0-9a-f]{32}$', 'guid: '+guid, original)
    require(count == 1, 'missing/ambiguous importer GUID')
    target.write_bytes(changed.encode('utf-8'))
    return dict(originalSha256=audit.sha(source), copiedSha256=audit.sha(target),
                settingsUnchangedExceptGuid=True, newGuid=guid)


def flat(points):
    return [float(c) for p in points for c in p]


require(not private.exists() and not (public/'export.json').exists(), 'refusing to overwrite intake or evidence')
repair_report_path = pipeline/'docs/diagnostics/character-convex-diagonal-repair/report.json'
repair = json.loads(repair_report_path.read_text(encoding='utf-8'))
require(repair['passed'], 'upstream repair not accepted')
manifest = json.loads((old_root/'intake.json').read_text(encoding='utf-8-sig'))
source_manifest_sha = audit.sha(old_root/'intake.json')
old_entries = [e for e in manifest['assets'] if e['family'].startswith('character-')]
require(len(old_entries) == 2, 'expected two representatives')
records, reports = [], []
mirror = Matrix.Diagonal((-1.,1.,1.,1.))
for entry in old_entries:
    family = 'CasualCharacters' if entry['family'] == 'character-casual' else 'ProfessionalCharacters'
    upstream, = [a for a in repair['assets'] if a['family'] == family]
    source = pipeline/upstream['output']
    require(audit.sha(source) == upstream['outputSha256'], 'upstream repaired SHA mismatch')
    require(audit.sha(root/entry['assetPath']) == entry['sourceSha256'] == upstream['sourceSha256'], 'original source SHA mismatch')
    record = copy.deepcopy(entry)
    destination = private/entry['family']/source.name
    destination.parent.mkdir(parents=True)
    shutil.copyfile(source, destination)
    importer = new_meta(Path(str(root/entry['assetPath'])+'.meta'), Path(str(destination)+'.meta'))
    old_texture = (root/entry['assetPath']).parent/'textures/Textures1_bottom_256.png'
    require(audit.sha(old_texture) == entry['textureSha256'], 'original texture SHA mismatch')
    texture = destination.parent/'textures'/old_texture.name
    texture.parent.mkdir()
    shutil.copyfile(old_texture, texture)
    texture_importer = new_meta(Path(str(old_texture)+'.meta'), Path(str(texture)+'.meta'))
    record.update(assetPath=destination.relative_to(root).as_posix(), sourceSha256=upstream['outputSha256'],
                  importerSha256=importer['copiedSha256'], originalAssetPath=entry['assetPath'],
                  originalSourceSha256=entry['sourceSha256'])
    bpy.ops.wm.open_mainfile(filepath=str(source), load_ui=False)
    bpy.context.view_layer.update()
    owner = bpy.data.objects[entry['objectName']]
    require(owner.type == 'MESH' and not owner.data.shape_keys, 'unsupported display mesh')
    inverse = owner.matrix_world.inverted()
    hulls, stats = [], []
    for obj in sorted(bpy.data.objects, key=lambda o:o.name):
        if not obj.name.startswith('UCX_'):
            continue
        parent = obj.parent
        require(obj.type == 'MESH' and not obj.modifiers and parent.parent_type == 'BONE', 'invalid UCX')
        rig, bone = parent.parent, obj['representative_bone']
        require(rig.type == 'ARMATURE' and parent.parent_bone == bone, 'invalid bone binding')
        local = (rig.matrix_world @ rig.pose.bones[bone].matrix).inverted() @ obj.matrix_world
        transform = mirror @ inverse @ rig.matrix_world @ rig.data.bones[bone].matrix_local @ local
        points = [transform @ v.co for v in obj.data.vertices]
        faces = [list(p.vertices) for p in obj.data.polygons]
        source_stats = audit.audit_hull([tuple(v.co) for v in obj.data.vertices], faces)
        if transform.to_3x3().determinant() < 0:
            faces = [f[::-1] for f in faces]
        transformed_stats = audit.audit_hull(points, faces)
        offsets, indices = [0], []
        for face in faces:
            indices.extend(face)
            offsets.append(len(indices))
        hulls.append(dict(name=obj.name, boneName=bone, convexAccepted=True,
            rendererBindVertices=flat(points), importedMeshLocalVertices=flat([mirror @ v.co for v in obj.data.vertices]),
            faceOffsets=offsets, faceIndices=indices))
        stats.append(dict(name=obj.name, source=source_stats, rendererBind=transformed_stats))
    fixture = dict(schemaVersion=1, family=entry['family'], sourceSha256=record['sourceSha256'],
        coordinateSystem='RendererBindLocal_MirrorBlenderOwnerX', authoredDisplayVertices=flat([mirror @ v.co for v in owner.data.vertices]), hulls=hulls)
    old_fixture_path = old_root/'Resources/CharacterPhysicsMigration'/(entry['family']+'.json')
    old_fixture = json.loads(old_fixture_path.read_text())
    require(len(hulls) == 19 and fixture['authoredDisplayVertices'] == old_fixture['authoredDisplayVertices'], 'display geometry changed')
    changed_faces = []
    for original, current in zip(old_fixture['hulls'], hulls):
        for key in ('name','boneName','rendererBindVertices','importedMeshLocalVertices','faceOffsets'):
            require(current[key] == original[key], 'non-face fixture change: '+key)
        name = current['name']
        expected = copy.deepcopy(original['faceIndices'])
        edits = upstream['repairs'].get(name, [])
        for fi, old_face, new_face in edits:
            start, end = current['faceOffsets'][fi:fi+2]
            require(expected[start:end] == old_face[::-1], 'old transformed winding differs')
            expected[start:end] = new_face[::-1]
            changed_faces.append(dict(name=name, faceIndex=fi))
        require(expected == current['faceIndices'], 'unexpected face change')
    require(len(changed_faces) == (4 if family == 'CasualCharacters' else 2), 'unexpected repair count')
    fixture_path = private/'Resources/CharacterPhysicsMigration'/(entry['family']+'.json')
    write_json(fixture_path, fixture)
    require(audit.sha(source) == audit.sha(destination) == record['sourceSha256'], 'source/copy modified')
    require(audit.sha(root/entry['assetPath']) == entry['sourceSha256'], 'original changed')
    reports.append(dict(family=entry['family'], sourceSha256=record['sourceSha256'],
        originalSourceSha256=entry['sourceSha256'], fixtureSha256=audit.sha(fixture_path),
        originalFixtureSha256=audit.sha(old_fixture_path), importer=importer, textureImporter=texture_importer,
        textureSha256=audit.sha(texture), displayAndHullPositionsExactlyUnchanged=True, changedFaces=changed_faces,
        hulls=stats))
    records.append(record)
require(audit.sha(old_root/'intake.json') == source_manifest_sha, 'original manifest changed')
write_json(private/'intake.json', dict(schemaVersion=1, originalManifestSha256=source_manifest_sha,
    upstreamRepairReportSha256=audit.sha(repair_report_path), assets=records))
write_json(public/'export.json', dict(schemaVersion=1, blender=bpy.app.version_string,
    scriptSha256=audit.sha(__file__), upstreamRepairReportSha256=audit.sha(repair_report_path),
    originalManifestSha256=source_manifest_sha, manifestSha256=audit.sha(private/'intake.json'), assets=reports))
print(json.dumps([dict(family=r['family'], fixtureSha256=r['fixtureSha256'], hulls=len(r['hulls'])) for r in reports]))
