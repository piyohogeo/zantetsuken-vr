"""Private real-asset fixture for the building/anchored-prop scene. Never saves source blends.
Run with Blender --background --factory-startup --disable-autoexec --python ... -- --out ...
Licensed geometry stays under Assets/Licensed; this is a scenario exporter, not a product importer.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys

sys.dont_write_bytecode = True
import bpy
from mathutils import Matrix


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--out', required=True)
    parser.add_argument('--sources', required=True)
    parser.add_argument('--only', default='')
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    spec = importlib.util.spec_from_file_location('hull_audit', Path(__file__).with_name('Export-Compact16uv-Physics.py'))
    audit = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(audit)
    basis = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))
    for category, name in [('Buildings', 'bar_001'), ('Props', 'advertising_001')]:
        if args.only and args.only != name:
            continue
        source = Path(args.sources) / ('ITHappy_Megacity_' + category + '--' + name + '.blend')
        before = hashlib.sha256(source.read_bytes()).hexdigest()
        bpy.ops.wm.open_mainfile(filepath=str(source), use_scripts=False)
        owner = bpy.data.objects[name]
        if owner.type != 'MESH' or owner.data.shape_keys:
            raise ValueError('Expected static authored mesh: ' + name)
        flags = {k: owner.get(k) for k in ('ztk_building', 'ztk_cuttable')}
        if flags['ztk_cuttable'] is not True or flags['ztk_building'] != (category == 'Buildings'):
            raise ValueError('Unexpected authored flags: ' + str(flags))
        transform = basis
        mesh = owner.evaluated_get(bpy.context.evaluated_depsgraph_get()).to_mesh()
        # Smooth-normal modifiers are allowed only if positions and topology are unchanged.
        if ([tuple(v.co) for v in mesh.vertices] != [tuple(v.co) for v in owner.data.vertices]
                or [tuple(p.vertices) for p in mesh.polygons] != [tuple(p.vertices) for p in owner.data.polygons]):
            raise ValueError('Modifier changes authored geometry: ' + name)
        mesh.calc_loop_triangles()
        render = []
        topology = []
        indices = []
        for triangle in mesh.loop_triangles:
            for loop_index in triangle.loops:
                loop = mesh.loops[loop_index]
                p = transform @ mesh.vertices[loop.vertex_index].co
                n = transform.to_3x3() @ mesh.corner_normals[loop_index].vector
                uv = mesh.uv_layers.active.data[loop_index].uv
                render.append(dict(position=dict(x=p.x, y=p.y, z=p.z), normal=dict(x=n.x, y=n.y, z=n.z), uv=dict(x=uv.x, y=uv.y)))
                topology.append(loop.vertex_index)
                indices.append(len(indices))
        hulls, anchors = [], []
        inverse = owner.matrix_world.inverted()
        for obj in sorted(bpy.data.objects, key=lambda x: x.name):
            if obj.parent != owner:
                continue
            local = basis @ inverse @ obj.matrix_world
            if obj.name.startswith('UCX_'):
                vertices = [list(local @ v.co) for v in obj.data.vertices]
                faces = [list(p.vertices) for p in obj.data.polygons]
                if local.to_3x3().determinant() < 0:
                    faces = [list(reversed(f)) for f in faces]
                stats = audit.audit_hull(vertices, faces)
                offsets, flat = [0], []
                for face in faces:
                    flat.extend(face)
                    offsets.append(len(flat))
                hulls.append(dict(vertices=[dict(x=v[0], y=v[1], z=v[2]) for v in vertices], faceOffsets=offsets, faceIndices=flat))
                print(name, obj.name, stats)
            elif obj.name.startswith('Anchor_'):
                p = local.translation
                anchors.append(dict(x=p.x, y=p.y, z=p.z))
        if not hulls or not anchors:
            raise ValueError('Authored hulls and anchors required: ' + name)
        if hashlib.sha256(source.read_bytes()).hexdigest() != before:
            raise ValueError('Source changed')
        fixture = dict(schemaVersion=1, name=name, source=str(source), sha256=before,
                       isBuilding=flags['ztk_building'], isCuttable=True,
                       render=render, indices=indices, topology=topology, topologyCount=len(mesh.vertices),
                       hulls=hulls, anchors=anchors)
        target = out / (name + '.json')
        if target.exists():
            raise ValueError('Refusing to overwrite ' + str(target))
        target.write_text(json.dumps(fixture, allow_nan=False), encoding='utf-8')
        print('EXPORTED', name, 'building', fixture['isBuilding'], 'hulls', len(hulls), 'anchors', len(anchors))


if __name__ == '__main__':
    main()
