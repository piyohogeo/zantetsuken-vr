"""Remove minimum enclosed face patches bounded by closed 3-face-edge networks."""
from collections import defaultdict

import bpy
import bmesh
from mathutils import Vector

from .boundary_weld import attributes
from .preserving_triangulate import custom_normal_is_close


def _fan_count(vertex):
    pending = set(vertex.link_faces)
    count = 0
    while pending:
        count += 1
        stack = [pending.pop()]
        while stack:
            face = stack.pop()
            for edge in face.edges:
                if vertex not in edge.verts:
                    continue
                for other in edge.link_faces:
                    if other in pending:
                        pending.remove(other)
                        stack.append(other)
    return count


def _edge_networks(bm):
    """Connected, closed networks made exclusively from exactly 3-face edges."""
    candidates = {edge for edge in bm.edges if len(edge.link_faces) == 3}
    at_vertex = defaultdict(set)
    for edge in candidates:
        for vertex in edge.verts:
            at_vertex[vertex].add(edge)
    result = []
    while candidates:
        seed = min(candidates, key=lambda edge: edge.index)
        candidates.remove(seed)
        network = {seed}
        stack = [seed]
        while stack:
            edge = stack.pop()
            for vertex in edge.verts:
                for other in sorted(at_vertex[vertex] & candidates,
                                    key=lambda item: item.index):
                    candidates.remove(other)
                    network.add(other)
                    stack.append(other)
        vertices = {vertex for edge in network for vertex in edge.verts}
        closed = (len(network) >= 3 and
                  all(sum(edge in network for edge in vertex.link_edges) == 2
                      for vertex in vertices))
        result.append((network, closed))
    return result


def _face_patches(bm):
    """Face components separated wherever edge incidence is not exactly two."""
    multi = {edge for edge in bm.edges if len(edge.link_faces) > 2}
    seeds = {face for edge in multi for face in edge.link_faces}
    seen = set()
    patches = []
    for seed in sorted(seeds, key=lambda face: face.index):
        if seed in seen:
            continue
        faces = {seed}
        seen.add(seed)
        stack = [seed]
        while stack:
            face = stack.pop()
            for edge in face.edges:
                if len(edge.link_faces) != 2:
                    continue
                for other in edge.link_faces:
                    if other not in seen:
                        seen.add(other)
                        faces.add(other)
                        stack.append(other)
        roots = {edge for face in faces for edge in face.edges if edge in multi}
        patches.append({'faces': faces, 'roots': roots})
    return patches


def _minimum_patch_set(network, patches):
    """Exact cover of a 3-face-edge network, minimizing faces then area."""
    candidates = []
    for patch in patches:
        if not (patch['roots'] & network) or not patch['roots'] <= network:
            continue
        cover = {}
        valid = True
        for edge in network:
            count = sum(face in patch['faces'] for face in edge.link_faces)
            if count > 1:
                valid = False
                break
            if count:
                cover[edge] = count
        if valid and cover:
            candidates.append({**patch, 'cover': cover})
    owners = {edge: [i for i, patch in enumerate(candidates)
                     if edge in patch['cover']] for edge in network}
    if any(not choices for choices in owners.values()):
        return None

    covered = {edge: 0 for edge in network}
    best = None
    best_score = None

    def search(chosen):
        nonlocal best, best_score
        face_union = set().union(*(candidates[i]['faces'] for i in chosen)) if chosen else set()
        score = (len(face_union), sum(face.calc_area() for face in face_union),
                 tuple(sorted(face.index for face in face_union)))
        if best_score is not None and score[:2] >= best_score[:2]:
            return
        pending = [edge for edge in network if covered[edge] == 0]
        if not pending:
            best = set(chosen)
            best_score = score
            return
        edge = min(pending, key=lambda item: (sum(i not in chosen for i in owners[item]),
                                               item.index))
        for index in owners[edge]:
            if index in chosen:
                continue
            patch = candidates[index]
            if any(covered[root] + amount > 1
                   for root, amount in patch['cover'].items()):
                continue
            chosen.add(index)
            for root, amount in patch['cover'].items():
                covered[root] += amount
            search(chosen)
            for root, amount in patch['cover'].items():
                covered[root] -= amount
            chosen.remove(index)

    search(set())
    return None if best is None else [candidates[index] for index in sorted(best)]


def _counts(bm):
    return {
        'boundary': sum(edge.is_boundary for edge in bm.edges),
        'wire': sum(edge.is_wire for edge in bm.edges),
        'multi': sum(len(edge.link_faces) > 2 for edge in bm.edges),
        'non_contiguous': sum(len(edge.link_faces) == 2 and not edge.is_contiguous
                              for edge in bm.edges),
        'bow_tie': sum(_fan_count(vertex) > 1 for vertex in bm.verts),
    }


def prepare_enclosed_multi_face_cleanup(obj):
    """Return a verified mesh copy and report; never mutate the source."""
    mesh = obj.data
    report = {'networks_found': 0, 'networks_removed': 0, 'networks_skipped': 0,
              'patches_removed': 0, 'faces_removed': 0, 'edges_removed': 0,
              'vertices_removed': 0, 'sharp_edges_added': 0, 'rejected': []}
    if mesh.shape_keys or any(modifier.type == 'MULTIRES' for modifier in obj.modifiers):
        raise ValueError('Shape keys / Multires are not supported')
    if obj.library or obj.override_library or mesh.library or mesh.override_library:
        raise ValueError('Linked / override data is not supported')
    if len(mesh.skin_vertices):
        raise ValueError('Enclosed multi-face cleanup does not support Skin vertex data')

    bm = bmesh.new()
    result = None
    try:
        bm.from_mesh(mesh)
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()
        bm.faces.ensure_lookup_table()
        saved = attributes(mesh)
        normals = ([tuple(normal.vector) for normal in mesh.corner_normals]
                   if mesh.has_custom_normals else None)
        weights = [tuple(sorted((group.group, group.weight) for group in vertex.groups))
                   for vertex in mesh.vertices]
        uv_names = [layer.name for layer in mesh.uv_layers]
        uv_active = mesh.uv_layers.active_index
        uv_render = [layer.active_render for layer in mesh.uv_layers]

        names = {}
        for domain, sequence in [('POINT', bm.verts), ('EDGE', bm.edges),
                                 ('FACE', bm.faces), ('CORNER', bm.loops)]:
            name = '_bt_enclosed_source_' + domain
            while sequence.layers.int.get(name):
                name += '_'
            layer = sequence.layers.int.new(name)
            names[domain] = name
            elements = ([loop for face in bm.faces for loop in face.loops]
                        if domain == 'CORNER' else sequence)
            for index, element in enumerate(elements):
                element[layer] = index

        original_counts = _counts(bm)
        initial = _edge_networks(bm)
        report['networks_found'] = len(initial)
        attempted = set()
        sharp_source_edges = set()

        while True:
            edge_layer = bm.edges.layers.int[names['EDGE']]
            available = []
            for network, closed in _edge_networks(bm):
                signature = tuple(sorted(edge[edge_layer] for edge in network))
                if signature not in attempted:
                    available.append((signature, network, closed))
            if not available:
                break
            signature, network, closed = min(available, key=lambda item: item[0])
            attempted.add(signature)
            if not closed:
                report['networks_skipped'] += 1
                report['rejected'].append(
                    f'Edge {signature[0]}: 3-face edge network is not a closed loop')
                continue
            chosen = _minimum_patch_set(network, _face_patches(bm))
            if not chosen:
                report['networks_skipped'] += 1
                report['rejected'].append(
                    f'Edge {signature[0]}: no isolated face-patch cover for the closed network')
                continue

            face_layer = bm.faces.layers.int[names['FACE']]
            chosen_sources = {face[face_layer] for patch in chosen for face in patch['faces']}
            before = _counts(bm)
            trial = bm.copy()
            try:
                trial_face_layer = trial.faces.layers.int[names['FACE']]
                removed = [face for face in trial.faces
                           if face[trial_face_layer] in chosen_sources]
                bmesh.ops.delete(trial, geom=removed, context='FACES')
                after = _counts(trial)
                trial_edge_layer = trial.edges.layers.int[names['EDGE']]
                remaining_target = {edge[trial_edge_layer]: len(edge.link_faces)
                                    for edge in trial.edges
                                    if edge[trial_edge_layer] in signature}
                if set(remaining_target) != set(signature) or any(count != 2 for count in remaining_target.values()):
                    raise ValueError('target 3-face edges did not all become 2-face edges')
                if after['multi'] != before['multi'] - len(network):
                    raise ValueError('trial deletion changed unexpected multi-face edges')
                if after['boundary'] > before['boundary']:
                    raise ValueError('trial deletion increased boundary edges')
                if after['wire'] > before['wire']:
                    raise ValueError('trial deletion created wire edges')
                if after['non_contiguous'] > before['non_contiguous']:
                    raise ValueError('trial deletion created non-contiguous edges')
                if after['bow_tie'] > before['bow_tie']:
                    raise ValueError('trial deletion created bow-tie vertices')
            except Exception as exc:
                trial.free()
                report['networks_skipped'] += 1
                report['rejected'].append(f'Edge {signature[0]}: {exc}')
                continue

            bm.free()
            bm = trial
            sharp_source_edges.update(signature)
            report['networks_removed'] += 1
            report['patches_removed'] += len(chosen)
            report['faces_removed'] += len(chosen_sources)

        if not report['networks_removed']:
            return None, report

        mappings = {}
        for domain, sequence in [('POINT', bm.verts), ('EDGE', bm.edges),
                                 ('FACE', bm.faces), ('CORNER', bm.loops)]:
            layer = sequence.layers.int[names[domain]]
            elements = ([loop for face in bm.faces for loop in face.loops]
                        if domain == 'CORNER' else sequence)
            mappings[domain] = [element[layer] for element in elements]
            sequence.layers.int.remove(layer)

        report['edges_removed'] = len(mesh.edges) - len(bm.edges)
        report['vertices_removed'] = len(mesh.vertices) - len(bm.verts)
        result = mesh.copy()
        bm.to_mesh(result)
        result.update(calc_edges_loose=True)
        for name, (domain, data_type, properties, values) in saved.items():
            attribute = result.attributes.get(name)
            if attribute is None:
                attribute = result.attributes.new(name=name, type=data_type, domain=domain)
            for target, source in enumerate(mappings[domain]):
                for prop, value in zip(properties, values[source]):
                    setattr(attribute.data[target], prop, value)

        for edge, source in zip(result.edges, mappings['EDGE']):
            if source in sharp_source_edges:
                if not mesh.edges[source].use_edge_sharp:
                    report['sharp_edges_added'] += 1
                edge.use_edge_sharp = True
        if normals is not None:
            result.normals_split_custom_set([normals[source]
                                             for source in mappings['CORNER']])
        result.update()

        actual = attributes(result)
        for name, (domain, data_type, properties, values) in saved.items():
            expected = [values[source] for source in mappings[domain]]
            if name == 'sharp_edge':
                expected = [(True,) if source in sharp_source_edges else value
                            for source, value in zip(mappings[domain], expected)]
            if expected and actual.get(name) != (domain, data_type, properties, expected):
                raise ValueError('Attribute verification failed: ' + name)
        for vertex, source in zip(result.vertices, mappings['POINT']):
            if (tuple(vertex.co) != tuple(mesh.vertices[source].co)
                    or tuple(sorted((group.group, group.weight) for group in vertex.groups))
                    != weights[source]):
                raise ValueError('Vertex position / weights changed')
        if normals is not None:
            error = max(((normal.vector - Vector(normals[source])).length
                         for normal, source in zip(result.corner_normals,
                                                   mappings['CORNER'])), default=0.0)
            if any(not custom_normal_is_close(normal.vector, Vector(normals[source]))
                   for normal, source in zip(result.corner_normals, mappings['CORNER'])):
                raise ValueError(
                    f'Custom normal verification failed (maximum vector error {error:.6g})')
        if [layer.name for layer in result.uv_layers] != uv_names:
            raise ValueError('UV layers changed')
        if uv_names:
            result.uv_layers.active_index = uv_active
            for layer, active in zip(result.uv_layers, uv_render):
                layer.active_render = active

        final_bm = bmesh.new()
        try:
            final_bm.from_mesh(result)
            final_counts = _counts(final_bm)
        finally:
            final_bm.free()
        if final_counts['boundary'] > original_counts['boundary']:
            raise ValueError('Final mesh has new boundary edges')
        if final_counts['wire'] > original_counts['wire']:
            raise ValueError('Final mesh has new wire edges')
        return result, report
    except Exception:
        if result is not None:
            bpy.data.meshes.remove(result)
        raise
    finally:
        bm.free()


class OBJECT_OT_remove_enclosed_multi_face_patches(bpy.types.Operator):
    bl_idname = 'object.remove_enclosed_multi_face_patches'
    bl_label = 'Remove Enclosed Multi-Face Patches'
    bl_description = ('Remove the minimum enclosed face-patch set bounded by closed '
                      '3-face-edge loops; reject new boundaries')
    bl_options = {'REGISTER', 'UNDO'}

    scope: bpy.props.EnumProperty(
        name='Target',
        items=[('SELECTED', 'Selected Meshes', ''), ('ACTIVE', 'Active Mesh', '')],
        default='SELECTED')

    @classmethod
    def poll(cls, context):
        return context.mode == 'OBJECT' and (
            any(obj.type == 'MESH' for obj in context.selected_objects)
            or (context.active_object is not None and context.active_object.type == 'MESH'))

    def execute(self, context):
        targets = [context.active_object] if self.scope == 'ACTIVE' else context.selected_objects
        targets = sorted((obj for obj in targets if obj and obj.type == 'MESH'),
                         key=lambda obj: obj.name)
        changed = networks = patches = faces = skipped = failed = 0
        for obj in targets:
            try:
                result, report = prepare_enclosed_multi_face_cleanup(obj)
                skipped += report['networks_skipped']
                for warning in report['rejected']:
                    self.report({'WARNING'}, f'{obj.name}: {warning}')
                if result is not None:
                    try:
                        obj.data = result
                    except Exception:
                        bpy.data.meshes.remove(result)
                        raise
                    result.name += '_EnclosedPatchesRemoved'
                    changed += 1
                    networks += report['networks_removed']
                    patches += report['patches_removed']
                    faces += report['faces_removed']
            except Exception as exc:
                failed += 1
                self.report({'WARNING'}, f'{obj.name}: {exc}')
        message = (f'{changed} mesh(es); {networks} networks / {patches} patches / '
                   f'{faces} faces removed; {skipped} networks skipped; {failed} meshes failed')
        self.report({'WARNING'} if skipped or failed else {'INFO'}, message)
        return {'FINISHED'} if changed or not failed else {'CANCELLED'}
