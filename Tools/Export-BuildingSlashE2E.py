"""Private real-asset input for the Slash-driven building E2E (college_001 of the Megacity city). Never saves the source.
Run with Blender --background --factory-startup --disable-autoexec --python ... -- --sources <dir> --out <dir>
The exported geometry, convexes, anchors and flags are the author's own; nothing is repaired or replaced. Licensed
output stays under Assets/Licensed; this is a scenario exporter, not a product importer.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys

sys.dont_write_bytecode = True
import bpy

NAME = 'college_001'
CATEGORY = 'Buildings'
BUILDING = True


def main():
    global NAME, CATEGORY, BUILDING
    parser = argparse.ArgumentParser()
    parser.add_argument('--out', required=True)
    parser.add_argument('--sources', required=True)
    # Another placed asset of the city (the playable city's prop): its name, category and authored building flag.
    parser.add_argument('--name', default=NAME)
    parser.add_argument('--category', default=CATEGORY)
    parser.add_argument('--prop', action='store_true', help='the asset is a cuttable prop (ztk_building false)')
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    NAME, CATEGORY, BUILDING = args.name, args.category, not args.prop
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    spec = importlib.util.spec_from_file_location('hull_audit', Path(__file__).with_name('Export-Compact16uv-Physics.py'))
    audit = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(audit)
    from mathutils import Matrix
    # Blender (x, y, z up) to the Megacity FBX's Unity model space: x -> -x, z -> y, y -> -z.
    basis = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))
    source = Path(args.sources) / ('ITHappy_Megacity_' + CATEGORY + '--' + NAME + '.blend')
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    bpy.ops.wm.open_mainfile(filepath=str(source), use_scripts=False)
    owner = bpy.data.objects[NAME]
    if owner.type != 'MESH' or owner.data.shape_keys:
        raise ValueError('Expected a static authored mesh: ' + NAME)
    flags = {k: owner.get(k) for k in ('ztk_building', 'ztk_cuttable')}
    if flags['ztk_cuttable'] is not True or bool(flags['ztk_building']) is not BUILDING:
        raise ValueError('Unexpected authored flags: ' + str(flags))
    mesh = owner.evaluated_get(bpy.context.evaluated_depsgraph_get()).to_mesh()
    # A modifier that changes positions or topology is refused; only the normals may differ (Auto Smooth).
    if ([tuple(v.co) for v in mesh.vertices] != [tuple(v.co) for v in owner.data.vertices]
            or [tuple(p.vertices) for p in mesh.polygons] != [tuple(p.vertices) for p in owner.data.polygons]):
        raise ValueError('A modifier changes the authored geometry: ' + NAME)
    mesh.calc_loop_triangles()
    render, topology, indices = [], [], []
    # The basis is a reflection (determinant -1), so each triangle's loops are taken in reverse: Blender's
    # counter-clockwise outward winding stays outward in Unity. The normals are carried as they are.
    reflect = basis.to_3x3().determinant() < 0
    for triangle in mesh.loop_triangles:
        for loop_index in (reversed(triangle.loops) if reflect else triangle.loops):
            loop = mesh.loops[loop_index]
            p = basis @ mesh.vertices[loop.vertex_index].co
            n = basis.to_3x3() @ mesh.corner_normals[loop_index].vector
            uv = mesh.uv_layers.active.data[loop_index].uv
            render.append(dict(position=dict(x=p.x, y=p.y, z=p.z), normal=dict(x=n.x, y=n.y, z=n.z), uv=dict(x=uv.x, y=uv.y)))
            topology.append(loop.vertex_index)
            indices.append(len(indices))
    hulls, anchors, anchor_names, hull_names = [], [], [], []
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
            hull_names.append(obj.name)
            print(NAME, obj.name, stats)
        elif obj.name.startswith('Anchor_'):
            p = local.translation
            anchors.append(dict(x=p.x, y=p.y, z=p.z))
            anchor_names.append(obj.name)
    if not hulls or not anchors:
        raise ValueError('Authored convexes and anchors are required: ' + NAME)
    # Unity's front faces: a closed outward surface has a positive signed volume (sum a.(b x c)/6). Checked for the
    # drawn triangles and every convex before anything is written.
    def signed(points, triangles):
        total = 0.0
        for a, b, c in triangles:
            pa, pb, pc = points[a], points[b], points[c]
            total += (pa[0] * (pb[1] * pc[2] - pb[2] * pc[1]) - pa[1] * (pb[0] * pc[2] - pb[2] * pc[0])
                      + pa[2] * (pb[0] * pc[1] - pb[1] * pc[0])) / 6.0
        return total
    drawn = [(v['position']['x'], v['position']['y'], v['position']['z']) for v in render]
    drawn_volume = signed(drawn, [(i, i + 1, i + 2) for i in range(0, len(indices), 3)])
    hull_volumes = []
    for hull in hulls:
        points = [(v['x'], v['y'], v['z']) for v in hull['vertices']]
        fans = []
        for f in range(len(hull['faceOffsets']) - 1):
            face = hull['faceIndices'][hull['faceOffsets'][f]:hull['faceOffsets'][f + 1]]
            fans.extend((face[0], face[k], face[k + 1]) for k in range(1, len(face) - 1))
        hull_volumes.append(signed(points, fans))
    print('WINDING', NAME, 'drawn signed volume', drawn_volume, 'convex signed volumes', hull_volumes)
    if drawn_volume <= 0 or any(v <= 0 for v in hull_volumes):
        raise ValueError('Not outward in Unity (signed volume <= 0): ' + NAME)
    if hashlib.sha256(source.read_bytes()).hexdigest() != before:
        raise ValueError('The source changed')
    fixture = dict(schemaVersion=1, name=NAME, source=str(source), sha256=before,
                   isBuilding=BUILDING, isCuttable=True,
                   render=render, indices=indices, topology=topology, topologyCount=len(mesh.vertices),
                   hulls=hulls, hullNames=hull_names, anchors=anchors, anchorNames=anchor_names)
    target = out / (NAME + '.json')
    if target.exists():
        raise ValueError('Refusing to overwrite ' + str(target))
    target.write_text(json.dumps(fixture, allow_nan=False), encoding='utf-8')
    print('EXPORTED', NAME, 'render', len(render), 'topology', len(mesh.vertices), 'hulls', len(hulls), 'anchors', len(anchors))


if __name__ == '__main__':
    main()
