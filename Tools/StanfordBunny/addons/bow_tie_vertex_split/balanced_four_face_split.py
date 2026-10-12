"""Split balanced four-face edges into local two-face sheets transactionally."""
from collections import defaultdict

import bpy
import bmesh
from mathutils import Vector

from .boundary_weld import attributes
from .preserving_triangulate import custom_normal_is_close


def _topology(mesh):
    edge_faces = [[] for _ in mesh.edges]
    incident_faces = [set() for _ in mesh.vertices]
    for face in mesh.polygons:
        for vertex in face.vertices:
            incident_faces[vertex].add(face.index)
        for loop_id in face.loop_indices:
            loop = mesh.loops[loop_id]
            edge = mesh.edges[loop.edge_index]
            direction = 0 if loop.vertex_index == min(edge.vertices) else 1
            edge_faces[edge.index].append((face.index, loop_id, direction))
    return edge_faces, incident_faces


def _pair_choices(mesh, edge_faces):
    """Return both opposite-direction pairings, least normal discontinuity first."""
    result = {}
    for edge_id, entries in enumerate(edge_faces):
        forward = sorted(face for face, _, direction in entries if direction == 0)
        reverse = sorted(face for face, _, direction in entries if direction == 1)
        if len(forward) != 2 or len(reverse) != 2:
            continue
        choices = (
            ((forward[0], reverse[0]), (forward[1], reverse[1])),
            ((forward[0], reverse[1]), (forward[1], reverse[0])),
        )
        def score(choice):
            crease = sum(1.0 - max(-1.0, min(1.0,
                mesh.polygons[a].normal.dot(mesh.polygons[b].normal))) for a, b in choice)
            return crease, choice
        result[edge_id] = tuple(sorted(((score(choice), choice) for choice in choices),
                                       key=lambda item:item[0]))
    return result


def _balanced_pairs(mesh, edge_faces):
    return {edge_id:choices[0][1]
            for edge_id,choices in _pair_choices(mesh,edge_faces).items()}


def _networks(mesh, edge_ids):
    at_vertex = defaultdict(set)
    for edge_id in edge_ids:
        for vertex in mesh.edges[edge_id].vertices:
            at_vertex[vertex].add(edge_id)
    pending = set(edge_ids)
    result = []
    while pending:
        seed = min(pending)
        pending.remove(seed)
        component = {seed}
        stack = [seed]
        while stack:
            edge_id = stack.pop()
            for vertex in mesh.edges[edge_id].vertices:
                for other in sorted(at_vertex[vertex] & pending):
                    pending.remove(other)
                    component.add(other)
                    stack.append(other)
        result.append(component)
    return result


def _face_components(mesh, edge_faces, incident_faces, selected, pairs):
    """Return a component label for every (vertex, incident face) corner."""
    vertex_edges = [[] for _ in mesh.vertices]
    for edge in mesh.edges:
        for vertex in edge.vertices:
            vertex_edges[vertex].append(edge.index)
    labels = [{} for _ in mesh.vertices]
    for vertex, faces in enumerate(incident_faces):
        if not faces:
            continue
        parent = {face: face for face in faces}
        def root(face):
            while parent[face] != face:
                parent[face] = parent[parent[face]]
                face = parent[face]
            return face
        def union(a, b):
            a, b = root(a), root(b)
            if a != b:
                if a > b:
                    a, b = b, a
                parent[b] = a
        for edge_id in vertex_edges[vertex]:
            linked = [face for face, _, _ in edge_faces[edge_id]]
            if edge_id in selected:
                for a, b in pairs[edge_id]:
                    union(a, b)
            elif linked:
                for face in linked[1:]:
                    union(linked[0], face)
        components = defaultdict(list)
        for face in faces:
            components[root(face)].append(face)
        for component in components.values():
            label = min(component)
            for face in component:
                labels[vertex][face] = label
    return labels


def _network_is_splittable(mesh, network, labels, pairs):
    for edge_id in network:
        a, b = mesh.edges[edge_id].vertices
        keys = {
            (labels[a][pair[0]], labels[b][pair[0]])
            for pair in pairs[edge_id]
        }
        if len(keys) != 2:
            return False
    return True


def plan_balanced_four_face_split(mesh):
    edge_faces, incident_faces = _topology(mesh)
    choices = _pair_choices(mesh, edge_faces)
    pairs = {edge_id:options[0][1] for edge_id,options in choices.items()}
    networks = _networks(mesh, pairs)
    accepted = []
    rejected = []
    alternative_pairings = 0
    for network in networks:
        ordered = sorted(network)
        if len(ordered) > 16:
            rejected.append((min(network), 'pairing search limit exceeded (more than 16 connected edges)'))
            continue
        best = None
        for mask in range(1 << len(ordered)):
            trial = dict(pairs)
            cost = 0.0
            for offset, edge_id in enumerate(ordered):
                option = (mask >> offset) & 1
                cost += choices[edge_id][option][0][0]
                trial[edge_id] = choices[edge_id][option][1]
            labels = _face_components(mesh, edge_faces, incident_faces, network, trial)
            if not _network_is_splittable(mesh, network, labels, trial):
                continue
            key = (cost, mask)
            if best is None or key < best[0]:
                best = (key, trial)
        if best is None:
            rejected.append((min(network), 'cannot be separated without cutting a normal edge'))
            continue
        pairs.update({edge_id:best[1][edge_id] for edge_id in network})
        alternative_pairings += sum(pairs[edge_id] != choices[edge_id][0][1]
                                    for edge_id in network)
        accepted.append(network)
    selected = set().union(*accepted) if accepted else set()
    labels = _face_components(mesh, edge_faces, incident_faces, selected, pairs)

    assignments = {}
    sources = []
    for vertex, face_labels in enumerate(labels):
        components = sorted(set(face_labels.values()))
        for offset, label in enumerate(components):
            target = vertex
            if offset:
                target = len(mesh.vertices) + len(sources)
                sources.append(vertex)
            for face, component in face_labels.items():
                if component == label:
                    assignments[vertex, face] = target

    corners = []
    for face in mesh.polygons:
        corners.extend(assignments.get((vertex, face.index), vertex)
                       for vertex in face.vertices)

    planned_edges = [None] * len(mesh.edges)
    edge_sources = []
    corner_edges = [None] * len(mesh.loops)
    split_edges = 0
    for edge_id, edge in enumerate(mesh.edges):
        entries = edge_faces[edge_id]
        if not entries:
            planned_edges[edge_id] = tuple(edge.vertices)
            continue
        groups = defaultdict(list)
        a, b = edge.vertices
        for face, loop_id, _ in entries:
            key = (assignments.get((a, face), a), assignments.get((b, face), b))
            groups[key].append((face, loop_id))
        ordered = sorted(groups.items(), key=lambda item:(min(face for face, _ in item[1]), item[0]))
        if edge_id in selected:
            if len(ordered) != 2 or any(len(items) != 2 for _, items in ordered):
                raise ValueError('Balanced four-face edge did not split into two face pairs')
            split_edges += 1
        elif len(ordered) != 1:
            raise ValueError('A non-target edge would be split')
        for group_index, (vertices, items) in enumerate(ordered):
            if group_index == 0:
                new_edge_id = edge_id
                planned_edges[edge_id] = vertices
            else:
                new_edge_id = len(planned_edges)
                planned_edges.append(vertices)
                edge_sources.append(edge_id)
            for _, loop_id in items:
                corner_edges[loop_id] = new_edge_id
    if any(edge is None for edge in planned_edges) or any(edge is None for edge in corner_edges):
        raise ValueError('Incomplete topology plan')
    return {
        'sources': sources,
        'corners': corners,
        'corner_edges': corner_edges,
        'edges': planned_edges,
        'edge_sources': edge_sources,
        'candidate_edges': len(pairs),
        'networks': len(networks),
        'accepted_networks': len(accepted),
        'alternative_pairings': alternative_pairings,
        'rejected_network_edges': rejected,
        'selected_edges': sorted(selected),
        'split_edges': split_edges,
    }


def _edge_counts(mesh):
    counts = [0] * len(mesh.edges)
    for loop in mesh.loops:
        counts[loop.edge_index] += 1
    return counts


def prepare_balanced_four_face_split(obj):
    """Return a verified mesh copy and report; leave the source untouched."""
    from . import _point_values, mesh_plan

    mesh = obj.data
    if mesh.shape_keys or any(modifier.type == 'MULTIRES' for modifier in obj.modifiers):
        raise ValueError('Shape keys / Multires are not supported')
    if obj.library or obj.override_library or mesh.library or mesh.override_library:
        raise ValueError('Linked / override data is not supported')
    plan = plan_balanced_four_face_split(mesh)
    report = {
        'candidate_edges': plan['candidate_edges'],
        'networks': plan['networks'],
        'accepted_networks': plan['accepted_networks'],
        'alternative_pairings': plan['alternative_pairings'],
        'split_edges': plan['split_edges'],
        'vertices_added': len(plan['sources']),
        'rejected': [f'Edge {edge}: sheet pairing {reason}'
                     for edge,reason in plan['rejected_network_edges']],
    }
    if not plan['split_edges']:
        return None, report

    saved = attributes(mesh)
    point_values = _point_values(mesh, plan['sources'])
    weights = [[(group.group, group.weight) for group in mesh.vertices[source].groups]
               for source in plan['sources']]
    skin_values = [(layer.name, [
        (tuple(layer.data[source].radius), layer.data[source].use_root,
         layer.data[source].use_loose) for source in plan['sources']
    ]) for layer in mesh.skin_vertices]
    normals = ([tuple(normal.vector) for normal in mesh.corner_normals]
               if mesh.has_custom_normals else None)
    before_counts = _edge_counts(mesh)
    before_boundary = sum(count == 1 for count in before_counts)
    before_non_contiguous = 0
    bm = bmesh.new()
    try:
        bm.from_mesh(mesh)
        before_non_contiguous = sum(len(edge.link_faces) == 2 and not edge.is_contiguous
                                    for edge in bm.edges)
    finally:
        bm.free()

    result = None
    staged_obj = None
    try:
        result = mesh.copy()
        staged_obj = obj.copy()
        staged_obj.data = result
        vertex_base = len(result.vertices)
        result.vertices.add(len(plan['sources']))
        for name, records in point_values:
            attr = result.attributes[name]
            for offset, record in enumerate(records):
                for prop, value in record.items():
                    setattr(attr.data[vertex_base + offset], prop, value)
        for offset, groups in enumerate(weights):
            for group_index, weight in groups:
                staged_obj.vertex_groups[group_index].add([vertex_base + offset], weight, 'REPLACE')
        for name, records in skin_values:
            layer = result.skin_vertices[name]
            for offset, (radius, use_root, use_loose) in enumerate(records):
                item = layer.data[vertex_base + offset]
                item.radius = radius
                item.use_root = use_root
                item.use_loose = use_loose

        if plan['edge_sources']:
            result.edges.add(len(plan['edge_sources']))
            for name, (domain, _, props, values) in saved.items():
                if domain != 'EDGE':
                    continue
                attr = result.attributes[name]
                for offset, source in enumerate(plan['edge_sources']):
                    for prop, value in zip(props, values[source]):
                        setattr(attr.data[len(mesh.edges) + offset], prop, value)
        result.loops.foreach_set('vertex_index', plan['corners'])
        result.loops.foreach_set('edge_index', plan['corner_edges'])
        result.edges.foreach_set('vertices', [vertex for edge in plan['edges'] for vertex in edge])
        result.update(calc_edges=False, calc_edges_loose=True)
        if normals is not None:
            result.normals_split_custom_set(normals)
            # Re-encoding custom normals may sharpen unrelated edges. Keep
            # every source edge and its duplicated sheet edge exactly as saved.
            for index, edge in enumerate(mesh.edges):
                result.edges[index].use_edge_sharp = edge.use_edge_sharp
            for offset, source in enumerate(plan['edge_sources']):
                result.edges[len(mesh.edges) + offset].use_edge_sharp = (
                    mesh.edges[source].use_edge_sharp)
            result.update()

        actual = attributes(result)
        for name, (domain, data_type, props, values) in saved.items():
            expected = list(values)
            if domain == 'POINT':
                expected += [values[source] for source in plan['sources']]
            elif domain == 'EDGE':
                expected += [values[source] for source in plan['edge_sources']]
            if actual.get(name) != (domain, data_type, props, expected):
                raise ValueError('Attribute verification failed: ' + name)
        for index, vertex in enumerate(result.vertices):
            source = index if index < len(mesh.vertices) else plan['sources'][index-len(mesh.vertices)]
            old = mesh.vertices[source]
            if (tuple(vertex.co) != tuple(old.co)
                    or sorted((group.group, group.weight) for group in vertex.groups)
                    != sorted((group.group, group.weight) for group in old.groups)):
                raise ValueError('Vertex position / weights changed')
        if normals is not None:
            error = max(((normal.vector-Vector(normals[index])).length
                         for index, normal in enumerate(result.corner_normals)), default=0.0)
            if any(not custom_normal_is_close(normal.vector, Vector(normals[index]))
                   for index, normal in enumerate(result.corner_normals)):
                raise ValueError(f'Custom normal verification failed (maximum vector error {error:.6g})')

        after_counts = _edge_counts(result)
        if sum(count == 1 for count in after_counts) != before_boundary:
            raise ValueError('Sheet split changed the number of boundary edges')
        if any(count > 2 for count in after_counts[len(mesh.edges):]):
            raise ValueError('A duplicated edge still has more than two incident faces')
        if any(after_counts[edge] != 2 for edge in plan['selected_edges']):
            raise ValueError('A selected four-face edge did not become a two-face edge')
        if any(after_counts[len(mesh.edges)+offset] != 2
               for offset in range(len(plan['edge_sources']))):
            raise ValueError('A duplicated sheet edge is not shared by exactly two faces')
        check = bmesh.new()
        try:
            check.from_mesh(result)
            after_non_contiguous = sum(len(edge.link_faces) == 2 and not edge.is_contiguous
                                       for edge in check.edges)
            if after_non_contiguous > before_non_contiguous:
                raise ValueError('Sheet split created a non-contiguous edge')
        finally:
            check.free()
        if mesh_plan(result)['sources']:
            raise ValueError('Bow-tie vertices remain after sheet split')
        if len(result.polygons) != len(mesh.polygons) or len(result.loops) != len(mesh.loops):
            raise ValueError('Face or corner count changed')
        bpy.data.objects.remove(staged_obj)
        staged_obj = None
        return result, report
    except Exception:
        if staged_obj is not None:
            bpy.data.objects.remove(staged_obj)
        if result is not None and result.users == 0:
            bpy.data.meshes.remove(result)
        raise


class OBJECT_OT_split_balanced_four_face_sheets(bpy.types.Operator):
    bl_idname = 'object.split_balanced_four_face_sheets'
    bl_label = 'Split Balanced 4-Face Sheets'
    bl_description = ('Split balanced 4-face edges into two local 2-face sheets, then split '
                      'vertex-only face fans; reject new boundaries')
    bl_options = {'REGISTER', 'UNDO'}

    scope: bpy.props.EnumProperty(
        name='Target',
        items=[('SELECTED', 'Selected Meshes', ''), ('ACTIVE', 'Active Mesh', '')],
        default='SELECTED')

    @classmethod
    def poll(cls, context):
        if context.mode != 'OBJECT':
            cls.poll_message_set('Switch to Object Mode before splitting 4-face sheets')
            return False
        available = (any(obj.type == 'MESH' for obj in context.selected_objects)
                     or (context.active_object is not None
                         and context.active_object.type == 'MESH'))
        if not available:
            cls.poll_message_set('Select a mesh object')
        return available

    def execute(self, context):
        targets = [context.active_object] if self.scope == 'ACTIVE' else context.selected_objects
        targets = sorted((obj for obj in targets if obj and obj.type == 'MESH'), key=lambda obj:obj.name)
        changed = edges = vertices = rejected = failed = 0
        for obj in targets:
            try:
                result, report = prepare_balanced_four_face_split(obj)
                rejected += len(report['rejected'])
                for warning in report['rejected']:
                    self.report({'WARNING'}, f'{obj.name}: {warning}')
                if result is not None:
                    try:
                        obj.data = result
                    except Exception:
                        bpy.data.meshes.remove(result)
                        raise
                    result.name += '_FourFaceSheetsSplit'
                    changed += 1
                    edges += report['split_edges']
                    vertices += report['vertices_added']
            except Exception as exc:
                failed += 1
                self.report({'WARNING'}, f'{obj.name}: {exc}')
        message = (f'{changed} mesh(es); {edges} balanced 4-face edges split; '
                   f'{vertices} vertices added; {rejected} networks skipped; {failed} meshes failed')
        self.report({'WARNING'} if rejected or failed else {'INFO'}, message)
        return {'FINISHED'} if changed or not failed else {'CANCELLED'}
