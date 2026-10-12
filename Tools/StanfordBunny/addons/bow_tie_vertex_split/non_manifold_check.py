"""Read-only Blender-style non-manifold classification."""
import bpy
import bmesh


CATEGORY_UI = (
    ('wire', 'Wire'),
    ('boundary', 'Boundaries'),
    ('multi_face', 'Multiple Faces'),
    ('non_contiguous', 'Non-Contiguous'),
    ('vertices', 'Vertices'),
)


def edge_direction_counts(edge):
    """Return the two face-loop traversal counts along a BMesh edge."""
    a = min(v.index for v in edge.verts)
    counts = [0, 0]
    for loop in edge.link_loops:
        counts[0 if loop.vert.index == a else 1] += 1
    return tuple(counts)


def is_allowed_opposite_multi_face_edge(edge):
    """Policy exception for balanced 4-face edges only."""
    counts = edge_direction_counts(edge)
    return len(edge.link_faces) == 4 and counts == (2, 2)


def vertex_only_issue(vertex, allow_opposite_multi_face_edges=False):
    """Match Blender's Vertices category without folding in normal boundaries."""
    if not vertex.link_faces or any(not edge.link_faces for edge in vertex.link_edges):
        return True
    if any(len(edge.link_faces) > 2 and not (
            allow_opposite_multi_face_edges and is_allowed_opposite_multi_face_edge(edge)
    ) for edge in vertex.link_edges):
        return True

    faces = list(vertex.link_faces)
    if len(faces) < 2:
        return False
    remaining = set(faces)
    components = 0
    while remaining:
        components += 1
        stack = [remaining.pop()]
        while stack:
            face = stack.pop()
            for edge in face.edges:
                if vertex not in edge.verts:
                    continue
                for neighbor in edge.link_faces:
                    if neighbor in remaining:
                        remaining.remove(neighbor)
                        stack.append(neighbor)
    return components > 1


def classify_mesh(mesh, allow_opposite_multi_face_edges=False):
    bm = bmesh.new()
    try:
        bm.from_mesh(mesh)
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()
        allowed = {
            edge.index for edge in bm.edges
            if allow_opposite_multi_face_edges and is_allowed_opposite_multi_face_edge(edge)
        }
        return {
            'wire': {edge.index for edge in bm.edges if not edge.link_faces},
            'boundary': {edge.index for edge in bm.edges if len(edge.link_faces) == 1},
            'multi_face': {
                edge.index for edge in bm.edges
                if len(edge.link_faces) > 2 and edge.index not in allowed
            },
            'non_contiguous': {
                edge.index for edge in bm.edges
                if len(edge.link_faces) == 2 and not edge.is_contiguous
            },
            'vertices': {
                vertex.index for vertex in bm.verts
                if vertex_only_issue(vertex, allow_opposite_multi_face_edges)
            },
            'allowed_opposite_multi_face': allowed,
        }
    finally:
        bm.free()


class OBJECT_OT_check_non_manifold(bpy.types.Operator):
    bl_idname = 'object.check_non_manifold'
    bl_label = 'Check Non-Manifold'
    bl_description = 'Read-only non-manifold check using Blender category semantics'
    bl_options = {'REGISTER'}

    scope: bpy.props.EnumProperty(
        name='Target',
        items=[('SELECTED', 'Selected Meshes', ''), ('ACTIVE', 'Active Mesh', '')],
        default='SELECTED')
    wire: bpy.props.BoolProperty(name='Wire', default=True)
    boundary: bpy.props.BoolProperty(name='Boundaries', default=True)
    multi_face: bpy.props.BoolProperty(name='Multiple Faces', default=True)
    non_contiguous: bpy.props.BoolProperty(name='Non-Contiguous', default=True)
    vertices: bpy.props.BoolProperty(name='Vertices', default=True)
    allow_opposite_multi_face_edges: bpy.props.BoolProperty(
        name='Allow Opposite 4-Face Edges', default=False,
        description='Ignore only 4-face edges whose traversal directions are balanced 2-to-2')

    @classmethod
    def poll(cls, context):
        return context.mode == 'OBJECT' and (
            any(obj.type == 'MESH' for obj in context.selected_objects)
            or (context.active_object is not None and context.active_object.type == 'MESH'))

    def execute(self, context):
        enabled = [key for key, _ in CATEGORY_UI if getattr(self, key)]
        if not enabled:
            self.report({'WARNING'}, 'Select at least one category')
            return {'CANCELLED'}
        targets = [context.active_object] if self.scope == 'ACTIVE' else context.selected_objects
        targets = sorted((obj for obj in targets if obj and obj.type == 'MESH'), key=lambda obj: obj.name)
        if not targets:
            self.report({'WARNING'}, 'No target mesh objects')
            return {'CANCELLED'}

        totals = {key: 0 for key, _ in CATEGORY_UI}
        allowed = 0
        lines = []
        for obj in targets:
            result = classify_mesh(obj.data, self.allow_opposite_multi_face_edges)
            for key in totals:
                totals[key] += len(result[key])
            allowed += len(result['allowed_opposite_multi_face'])
            counts = ', '.join(f'{label} {len(result[key])}' for key, label in CATEGORY_UI if key in enabled)
            lines.append(f'{obj.name}: {counts}')
        issues = sum(totals[key] for key in enabled)
        status = 'PASS' if issues == 0 else 'FAIL'
        summary = f'{status}: {len(targets)} mesh(es), {issues} selected-category issue(s)'
        if self.allow_opposite_multi_face_edges:
            summary += f', {allowed} opposite 4-face edge(s) allowed'
        context.scene.btw_non_manifold_result = '\n'.join([summary] + lines)
        self.report({'INFO'} if issues == 0 else {'WARNING'}, summary)
        return {'FINISHED'}
