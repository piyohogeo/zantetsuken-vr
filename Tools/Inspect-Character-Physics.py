"""Read-only authoring inventory; no save, hull generation or coordinate fitting."""
import hashlib
import json
from pathlib import Path
import bpy

root = Path(__file__).resolve().parents[1]
manifest = json.loads((root / 'Assets/Licensed/Compact16uvIntake/intake.json').read_text(encoding='utf-8-sig'))
results = []
def ancestry(obj):
    chain = []
    while obj is not None:
        chain.append(dict(name=obj.name, parentType=obj.parent_type, parentBone=obj.parent_bone,
                          properties={str(k): obj[k] for k in obj.keys() if isinstance(obj[k], (str, int, float, bool))},
                          constraints=[dict(type=c.type, target=getattr(getattr(c, 'target', None), 'name', None), subtarget=getattr(c, 'subtarget', None)) for c in obj.constraints]))
        obj = obj.parent
    return chain
for entry in manifest['assets']:
    if not entry['family'].startswith('character-'):
        continue
    source = root / entry['assetPath']
    assert hashlib.sha256(source.read_bytes()).hexdigest() == entry['sourceSha256']
    bpy.ops.wm.open_mainfile(filepath=str(source), load_ui=False)
    hulls = [o for o in bpy.data.objects if o.name.startswith('UCX_')]
    armatures = [o for o in bpy.data.objects if o.type == 'ARMATURE']
    result = dict(
        family=entry['family'], sourceSha256=entry['sourceSha256'],
        hullCount=len(hulls), armatures=[dict(name=o.name, bones=len(o.data.bones)) for o in armatures],
        hulls=[dict(name=o.name, type=o.type, parent=None if o.parent is None else o.parent.name,
                    parentType=o.parent_type, parentBone=o.parent_bone,
                    vertices=len(o.data.vertices) if o.type == 'MESH' else 0,
                    faces=len(o.data.polygons) if o.type == 'MESH' else 0,
                    properties={str(k): str(o[k]) for k in o.keys()},
                    ancestry=ancestry(o)) for o in hulls]
    )
    result['allRepresentativeBonesMatchParents'] = all(
        o.parent is not None and o.parent.parent_type == 'BONE'
        and o.get('representative_bone') == o.parent.parent_bone
        and o.parent.parent is not None and o.parent.parent.type == 'ARMATURE'
        for o in hulls)
    assert len(hulls) == 19 and result['allRepresentativeBonesMatchParents']
    results.append(result)
    print('CHARACTER_PHYSICS_INVENTORY ' + json.dumps({k: v for k, v in result.items() if k != 'hulls'}))
destination = root / 'docs/diagnostics/compact16uv-character-scene/physics-inventory.json'
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_bytes((json.dumps(dict(blender=bpy.app.version_string, disposition='Authoring metadata only; no pose binding/cook/physics registration verified', assets=results), indent=2, ensure_ascii=False) + '\n').encode('utf-8'))
