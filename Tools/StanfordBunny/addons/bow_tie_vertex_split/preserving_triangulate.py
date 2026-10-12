"""Triangulate without discarding coincident opposite-winding faces."""
from collections import defaultdict
import math
from itertools import combinations, product

import bmesh
import bpy
from mathutils import Vector

from .boundary_weld import attributes
from .triangulation_search import valid_triangulations, triangle_quality


CUSTOM_NORMAL_MAX_DISTANCE = 0.01
CUSTOM_NORMAL_MAX_ANGLE_DEGREES = math.degrees(
    2.0 * math.asin(CUSTOM_NORMAL_MAX_DISTANCE / 2.0))
NORMAL_RETRY_LIMIT = 32
NORMAL_RETRY_FACE_LIMIT = 6


class _CustomNormalVerificationError(ValueError):
    """Keep only source IDs: the failed staging mesh is deleted on rollback."""

    def __init__(self, message, source_faces, source_vertices=()):
        super().__init__(message)
        self.source_faces = tuple(sorted(set(source_faces)))
        self.source_vertices = tuple(sorted(set(source_vertices)))


def _verify_custom_normals(result, normals, corner_mapping, face_mapping):
    failed = []
    maximum_distance = maximum_angle = 0.0
    for face in result.polygons:
        for index in face.loop_indices:
            actual = result.corner_normals[index].vector
            expected = Vector(normals[corner_mapping[index]])
            maximum_distance = max(maximum_distance, (actual - expected).length)
            if actual.length_squared and expected.length_squared:
                dot = max(-1.0, min(1.0, actual.normalized().dot(expected.normalized())))
                maximum_angle = max(maximum_angle, math.degrees(math.acos(dot)))
            if not custom_normal_is_close(actual, expected):
                failed.append((face_mapping[face.index], corner_mapping[index]))
    if failed:
        error = _CustomNormalVerificationError(
            'Custom normal verification failed '
            f'(maximum vector error {maximum_distance:.6g}, '
            f'maximum angle {maximum_angle:.6g} degrees)',
            (face for face, _ in failed))
        error.source_corners = tuple(sorted({corner for _, corner in failed}))
        raise error


def custom_normal_is_close(actual, expected):
    """Compare decoded normals using the shared Blender re-encoding tolerance."""
    if (actual - expected).length > CUSTOM_NORMAL_MAX_DISTANCE:
        return False
    # Some existing degenerate corners decode to a zero vector. Two unchanged
    # zero vectors have no angle to compare, but must still count as preserved.
    if actual.length_squared == 0 or expected.length_squared == 0:
        return actual.length_squared == 0 and expected.length_squared == 0
    dot = max(-1.0, min(1.0, actual.normalized().dot(expected.normalized())))
    return math.degrees(math.acos(dot)) <= CUSTOM_NORMAL_MAX_ANGLE_DEGREES


def _temporary_int_layer(sequence, stem):
    name = stem
    while sequence.layers.int.get(name):
        name += '_'
    return sequence.layers.int.new(name)


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


def _topology_counts(bm):
    return {
        'boundary': sum(edge.is_boundary for edge in bm.edges),
        'wire': sum(edge.is_wire for edge in bm.edges),
        'multi': sum(len(edge.link_faces) > 2 for edge in bm.edges),
        'non_contiguous': sum(len(edge.link_faces) == 2 and not edge.is_contiguous
                              for edge in bm.edges),
        'bow_tie': sum(_fan_count(vertex) > 1 for vertex in bm.verts),
    }


def _collapsed_quad_plan(mesh, saved, weights):
    """Plan disjoint, attribute-compatible exact-coordinate vertex welds."""
    pair_faces = defaultdict(set)
    for face in mesh.polygons:
        if len(face.vertices) != 4 or face.area <= 0.0:
            continue
        vertices = tuple(face.vertices)
        positions = [tuple(mesh.vertices[index].co) for index in vertices]
        same = []
        for index in range(4):
            if positions[index] == positions[(index + 1) % 4]:
                same.append(tuple(sorted((vertices[index], vertices[(index + 1) % 4]))))
        if len(same) == 1 and len(set(positions)) == 3:
            pair_faces[same[0]].add(face.index)

    at_vertex = defaultdict(set)
    for pair in pair_faces:
        for vertex in pair:
            at_vertex[vertex].add(pair)
    candidate_pairs = sorted(pair for pair in pair_faces
                             if all(len(at_vertex[vertex]) == 1 for vertex in pair))

    neighbors = defaultdict(dict)
    for edge in mesh.edges:
        a, b = edge.vertices
        neighbors[a][b] = edge.index
        neighbors[b][a] = edge.index

    accepted = []
    rejected = []
    for pair in candidate_pairs:
        keep, drop = pair
        reason = None
        for name, (domain, _type, _properties, values) in saved.items():
            if domain == 'POINT' and not name.startswith('.select_'):
                if values[keep] != values[drop]:
                    reason = 'point attribute conflict: ' + name
                    break
        if reason is None and weights[keep] != weights[drop]:
            reason = 'vertex group weight conflict'
        if reason is None:
            common = (set(neighbors[keep]) & set(neighbors[drop])) - {keep, drop}
            for neighbor in sorted(common):
                left = neighbors[keep][neighbor]
                right = neighbors[drop][neighbor]
                for name, (domain, _type, _properties, values) in saved.items():
                    if domain == 'EDGE' and not name.startswith('.select_'):
                        if values[left] != values[right]:
                            reason = 'edge attribute conflict: ' + name
                            break
                if reason is not None:
                    break
        if reason is None:
            accepted.append(pair)
        else:
            rejected.append((pair, reason))
    faces = set().union(*(pair_faces[pair] for pair in accepted)) if accepted else set()
    return accepted, faces, rejected, sum(len(value) for value in pair_faces.values())


def _set_attribute_values(attribute, saved, mapping):
    _domain, _data_type, properties, values = saved
    for target, source in enumerate(mapping):
        if source is None:
            continue
        for prop, value in zip(properties, values[source]):
            setattr(attribute.data[target], prop, value)


def _opposite_duplicate_count(mesh):
    groups = defaultdict(list)
    for polygon in mesh.polygons:
        groups[tuple(sorted(polygon.vertices))].append(polygon)
    count = 0
    for faces in groups.values():
        for index, face in enumerate(faces):
            if any(face.normal.dot(other.normal) < -0.999999 for other in faces[:index]):
                count += 1
    return count


def _quad_candidate(face, alternate):
    verts = tuple(face.verts)
    if alternate:
        diagonal = (verts[1], verts[3])
        triangles = ((verts[0], verts[1], verts[3]),
                     (verts[1], verts[2], verts[3]))
        mode = 'ALTERNATE'
    else:
        diagonal = (verts[0], verts[2])
        triangles = ((verts[0], verts[1], verts[2]),
                     (verts[0], verts[2], verts[3]))
        mode = 'FIXED'
    scale_squared = max(((verts[(index + 1) % 4].co - vert.co).length_squared
                         for index, vert in enumerate(verts)), default=0.0)
    epsilon = max(scale_squared * 1e-12, 1e-30)
    valid = all((b.co - a.co).cross(c.co - a.co).dot(face.normal) > epsilon
                for a, b, c in triangles)
    existing_triangles = {frozenset(item.verts) for item in face.verts[0].link_faces
                          if item is not face and len(item.verts) == 3}
    # A duplicate can be linked through any of the three vertices, not only v0.
    existing_triangles.update(
        frozenset(item.verts) for vert in face.verts for item in vert.link_faces
        if item is not face and len(item.verts) == 3)
    duplicates = sum(frozenset(triangle) in existing_triangles for triangle in triangles)
    edge = next((item for item in diagonal[0].link_edges
                 if diagonal[1] in item.verts), None)
    existing_faces = len(edge.link_faces) if edge is not None else 0
    resulting_faces = existing_faces + 2
    score = (duplicates, int(resulting_faces >= 4), existing_faces)
    return {'mode': mode, 'valid': valid, 'score': score, 'diagonal': diagonal,
            'existing_faces': existing_faces, 'resulting_faces': resulting_faces}


def _triangulate_best_effort(bm, source_faces):
    report = {'double_map': 0, 'steered_quads': 0,
              'four_face_diagonals_avoided': 0}
    quads = sorted((face for face in source_faces if len(face.verts) == 4),
                   key=lambda face: face.index)
    # N-gon diagonals are chosen by Blender's Beauty triangulation and cannot be
    # steered by this operator.  Materialize those constrained choices first so
    # the adjustable quad pass can see and avoid their actual diagonals.
    ngons = sorted((face for face in source_faces if len(face.verts) > 4),
                   key=lambda face: face.index)
    if ngons:
        operation = bmesh.ops.triangulate(
            bm, faces=ngons, quad_method='BEAUTY', ngon_method='BEAUTY')
        report['double_map'] += len(operation['face_map_double'])
    for face in quads:
        face.normal_update()
        fixed = _quad_candidate(face, False)
        alternate = _quad_candidate(face, True)
        candidates = [item for item in (fixed, alternate) if item['valid']]
        if not candidates:
            mode = 'BEAUTY'
        elif len(candidates) == 1:
            chosen = candidates[0]
            mode = chosen['mode']
            report['steered_quads'] += 1
        elif fixed['score'] == alternate['score']:
            mode = 'BEAUTY'
        else:
            chosen = min(candidates, key=lambda item: item['score'])
            other = alternate if chosen is fixed else fixed
            mode = chosen['mode']
            report['steered_quads'] += 1
            if chosen['resulting_faces'] < 4 <= other['resulting_faces']:
                report['four_face_diagonals_avoided'] += 1
        operation = bmesh.ops.triangulate(
            bm, faces=[face], quad_method=mode, ngon_method='BEAUTY')
        report['double_map'] += len(operation['face_map_double'])
    return report


def _zero_triangle_count(bm, face_source_name, source_id):
    """Count geometrically collapsed triangles using double-precision arithmetic."""
    layer = bm.faces.layers.int[face_source_name]
    count = 0
    for face in bm.faces:
        if face[layer] != source_id or len(face.verts) != 3:
            continue
        a, b, c = (tuple(float(value) for value in vertex.co)
                   for vertex in face.verts)
        u = tuple(b[i] - a[i] for i in range(3))
        v = tuple(c[i] - a[i] for i in range(3))
        cross = (u[1]*v[2] - u[2]*v[1],
                 u[2]*v[0] - u[0]*v[2],
                 u[0]*v[1] - u[1]*v[0])
        area_twice = math.sqrt(sum(value*value for value in cross))
        scale_squared = max(
            sum((a[i]-b[i])**2 for i in range(3)),
            sum((b[i]-c[i])**2 for i in range(3)),
            sum((c[i]-a[i])**2 for i in range(3)))
        if area_twice <= scale_squared * 1e-12:
            count += 1
    return count


def _ngon_trial(bm, source_id, face_source_name, corner_source_name,
                recreate=False):
    """Triangulate one source n-gon on a disposable BMesh copy."""
    trial = bm.copy()
    try:
        face_layer = trial.faces.layers.int[face_source_name]
        corner_layer = trial.loops.layers.int[corner_source_name]
        face = next(face for face in trial.faces
                    if face[face_layer] == source_id and len(face.verts) > 4)
        if recreate:
            vertices = tuple(face.verts)
            material = face.material_index
            smooth = face.smooth
            corners = {loop.vert: loop[corner_layer] for loop in face.loops}
            uv_values = {}
            for name in trial.loops.layers.uv.keys():
                layer = trial.loops.layers.uv[name]
                uv_values[name] = {loop.vert: tuple(loop[layer].uv)
                                   for loop in face.loops}
            bmesh.ops.delete(trial, geom=[face], context='FACES_ONLY')
            face = trial.faces.new(vertices)
            face[face_layer] = source_id
            face.material_index = material
            face.smooth = smooth
            for loop in face.loops:
                loop[corner_layer] = corners[loop.vert]
                for name, values in uv_values.items():
                    loop[trial.loops.layers.uv[name]].uv = values[loop.vert]
        operation = bmesh.ops.triangulate(
            trial, faces=[face], quad_method='BEAUTY',
            ngon_method='EAR_CLIP' if recreate else 'BEAUTY')
        return trial, _zero_triangle_count(trial, face_source_name, source_id), len(
            operation['face_map_double'])
    except Exception:
        trial.free()
        raise


def _replace_face_pattern(bm, face, triangles, face_layer, corner_layer):
    """Materialize a pattern without losing source face/corner provenance.

    Called only on disposable meshes. BMesh can reject an existing identical
    triangle; such a candidate must be discarded, never delete the other face.
    All original domain attributes are subsequently restored by provenance.
    """
    source_id = face[face_layer]
    material, smooth = face.material_index, face.smooth
    corners = {loop.vert: loop[corner_layer] for loop in face.loops}
    uv_values = {
        name: {loop.vert: tuple(loop[bm.loops.layers.uv[name]].uv) for loop in face.loops}
        for name in bm.loops.layers.uv.keys()}
    for triangle in triangles:
        new_face = bm.faces.new(triangle, face)
        new_face[face_layer] = source_id
        new_face.material_index = material
        new_face.smooth = smooth
        for loop in new_face.loops:
            loop[corner_layer] = corners[loop.vert]
            for name, values in uv_values.items():
                loop[bm.loops.layers.uv[name]].uv = values[loop.vert]
    bmesh.ops.delete(bm, geom=[face], context='FACES_ONLY')
    bm.normal_update()


def _pattern_risk(face, pattern):
    verts = tuple(face.verts)
    existing = {frozenset(other.verts) for vert in verts for other in vert.link_faces
                if other is not face and len(other.verts) == 3}
    boundary = {frozenset((verts[i], verts[(i + 1) % len(verts)]))
                for i in range(len(verts))}
    triangles = [tuple(verts[i] for i in triangle) for triangle in pattern]
    duplicates = sum(frozenset(triangle) in existing for triangle in triangles)
    diagonals = {frozenset((triangle[i], triangle[(i + 1) % 3]))
                 for triangle in triangles for i in range(3)} - boundary
    incidences = []
    for diagonal in diagonals:
        a, b = tuple(diagonal)
        edge = next((edge for edge in a.link_edges if b in edge.verts), None)
        incidences.append(len(edge.link_faces) if edge is not None else 0)
    return duplicates, sum(count >= 2 for count in incidences), sum(incidences)


def _steer_ngon_trial(bm, chosen, source_id, face_source_name, corner_source_name):
    """Prefer strictly less risky n-gon patterns, leaving ties to Blender."""
    face_layer = bm.faces.layers.int[face_source_name]
    corner_layer = bm.loops.layers.int[corner_source_name]
    face = next(face for face in bm.faces if face[face_layer] == source_id)
    face.normal_update()
    coords = [tuple(vertex.co) for vertex in face.verts]
    corner_indices = {loop[corner_layer]: i for i, loop in enumerate(face.loops)}
    chosen_face_layer = chosen.faces.layers.int[face_source_name]
    chosen_corner_layer = chosen.loops.layers.int[corner_source_name]
    baseline = tuple(tuple(corner_indices[loop[chosen_corner_layer]] for loop in part.loops)
                     for part in chosen.faces if part[chosen_face_layer] == source_id)
    baseline_risk = _pattern_risk(face, baseline)
    # No duplicate or occupied diagonal: avoid needless candidate enumeration.
    if baseline_risk == (0, 0, 0):
        return chosen, False
    candidates = valid_triangulations(coords, tuple(face.normal))
    ranked = sorted((_pattern_risk(face, pattern), triangle_quality(coords, pattern), pattern)
                    for pattern in candidates)
    for risk, _quality, pattern in ranked:
        if risk >= baseline_risk:
            break
        trial = bm.copy()
        try:
            layer = trial.faces.layers.int[face_source_name]
            corners = trial.loops.layers.int[corner_source_name]
            target = next(face for face in trial.faces if face[layer] == source_id)
            verts = tuple(target.verts)
            _replace_face_pattern(trial, target,
                                  [tuple(verts[i] for i in triangle) for triangle in pattern],
                                  layer, corners)
        except ValueError:
            trial.free()
            continue
        except Exception:
            trial.free()
            raise
        chosen.free()
        return trial, True
    return chosen, False


def _triangulate_avoiding_zero(bm, source_faces, face_source, corner_source,
                              minimize_four_face_edges, avoid_zero=True, steer_ngons=True):
    """Prefer a zero-free n-gon split; retain Beauty if alternatives are no better."""
    report = {'double_map': 0, 'steered_quads': 0,
              'four_face_diagonals_avoided': 0,
              'steered_ngons': 0,
              'ngons_steered_away_from_zero': 0,
              'zero_area_triangles_avoided': 0}
    ngon_sources = sorted(face[face_source] for face in source_faces
                          if len(face.verts) > 4)
    quad_sources = {face[face_source] for face in source_faces
                    if len(face.verts) == 4}
    face_source_name = face_source.name
    corner_source_name = corner_source.name
    original = bm
    try:
        for source_id in ngon_sources:
            default, default_zero, default_double = _ngon_trial(
                bm, source_id, face_source_name, corner_source_name)
            chosen, chosen_double = default, default_double
            if avoid_zero and default_zero:
                try:
                    alternate, alternate_zero, alternate_double = _ngon_trial(
                        bm, source_id, face_source_name, corner_source_name,
                        recreate=True)
                except (ValueError, KeyError):
                    alternate = None
                except Exception:
                    default.free()
                    raise
                if alternate is not None:
                    if alternate_zero < default_zero:
                        default.free()
                        chosen, chosen_double = alternate, alternate_double
                        report['ngons_steered_away_from_zero'] += 1
                        report['zero_area_triangles_avoided'] += (
                            default_zero - alternate_zero)
                    else:
                        alternate.free()
            if minimize_four_face_edges and steer_ngons:
                before_steer_zero = _zero_triangle_count(chosen, face_source_name, source_id)
                try:
                    chosen, steered = _steer_ngon_trial(
                        bm, chosen, source_id, face_source_name, corner_source_name)
                except Exception:
                    chosen.free()
                    raise
                if steered:
                    report['steered_ngons'] += 1
                    chosen_double = 0
                    if avoid_zero and before_steer_zero:
                        report['ngons_steered_away_from_zero'] += 1
                        report['zero_area_triangles_avoided'] += before_steer_zero
            if bm is not original:
                bm.free()
            bm = chosen
            report['double_map'] += chosen_double
        quad_faces = [face for face in bm.faces if len(face.verts) == 4
                      and face[bm.faces.layers.int[face_source_name]] in quad_sources]
        if quad_faces:
            if minimize_four_face_edges:
                quad_report = _triangulate_best_effort(bm, quad_faces)
                for key, value in quad_report.items():
                    report[key] += value
            else:
                operation = bmesh.ops.triangulate(
                    bm, faces=quad_faces, quad_method='BEAUTY', ngon_method='BEAUTY')
                report['double_map'] += len(operation['face_map_double'])
    except Exception:
        if bm is not original:
            bm.free()
        raise
    if bm is not original:
        original.free()
    return bm, report


def _prepare_preserving_triangulation_once(obj, minimize_four_face_edges=False,
                                          simplify_collapsed_quads=False,
                                          triangulation_overrides=None, steer_ngons=True):
    """Return a triangulated mesh copy and report; never mutate the source."""
    mesh = obj.data
    report = {'source_faces': 0, 'triangles': len(mesh.polygons),
              'opposite_duplicates': 0, 'double_map': 0,
              'steered_quads': 0, 'four_face_diagonals_avoided': 0,
              'new_four_face_edges': 0, 'collapsed_quads_found': 0,
              'collapsed_quads_simplified': 0, 'coincident_vertices_merged': 0,
              'collapsed_quads_rejected': [], 'zero_area_triangles': 0,
              'ngons_steered_away_from_zero': 0,
              'zero_area_triangles_avoided': 0}
    report.update(steered_ngons=0, normal_retry_attempts=0, normal_retry_faces=0)
    if all(len(face.vertices) == 3 for face in mesh.polygons):
        report['opposite_duplicates'] = _opposite_duplicate_count(mesh)
        return None, report
    if mesh.shape_keys or any(modifier.type == 'MULTIRES' for modifier in obj.modifiers):
        raise ValueError('Shape keys / Multires are not supported')
    if obj.library or obj.override_library or mesh.library or mesh.override_library:
        raise ValueError('Linked / override data is not supported')
    if len(mesh.skin_vertices):
        raise ValueError('Preserving Triangulate does not support Skin vertex data')
    if any(len(set(face.vertices)) != len(face.vertices) for face in mesh.polygons):
        raise ValueError('A face contains a repeated vertex')

    saved = attributes(mesh)
    weights = [tuple(sorted((group.group, group.weight) for group in vertex.groups))
               for vertex in mesh.vertices]
    normals = ([tuple(normal.vector) for normal in mesh.corner_normals]
               if mesh.has_custom_normals else None)
    uv_names = [layer.name for layer in mesh.uv_layers]
    uv_active = mesh.uv_layers.active_index
    uv_render = [layer.active_render for layer in mesh.uv_layers]
    expected_faces = sum(len(face.vertices) - 2 for face in mesh.polygons)
    original_edge_faces = {tuple(sorted(edge.vertices)): 0 for edge in mesh.edges}
    for face in mesh.polygons:
        for key in face.edge_keys:
            original_edge_faces[tuple(sorted(key))] += 1
    source_loop = {(face.index, loop.vertex_index): loop.index
                   for face in mesh.polygons
                   for loop in (mesh.loops[index] for index in face.loop_indices)}

    bm = bmesh.new()
    result = None
    try:
        bm.from_mesh(mesh)
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()
        bm.faces.ensure_lookup_table()
        vertex_source = _temporary_int_layer(bm.verts, '_btw_tri_vertex')
        edge_source = _temporary_int_layer(bm.edges, '_btw_tri_edge')
        face_source = _temporary_int_layer(bm.faces, '_btw_tri_face')
        corner_source = _temporary_int_layer(bm.loops, '_btw_tri_corner')
        for index, vertex in enumerate(bm.verts):
            vertex[vertex_source] = index + 1
        for index, edge in enumerate(bm.edges):
            edge[edge_source] = index + 1
        for index, face in enumerate(bm.faces):
            face[face_source] = index + 1
        for index, loop in enumerate(loop for face in bm.faces for loop in face.loops):
            loop[corner_source] = index + 1

        merged_sources = []
        simplified_source_faces = set()
        if simplify_collapsed_quads:
            pairs, simplified_source_faces, rejected, found = _collapsed_quad_plan(
                mesh, saved, weights)
            report['collapsed_quads_found'] = found
            report['collapsed_quads_rejected'] = [
                f'Vertices {pair[0]}/{pair[1]}: {reason}' for pair, reason in rejected]
            if pairs:
                before_merge = _topology_counts(bm)
                by_source = {vertex[vertex_source] - 1: vertex for vertex in bm.verts}
                targetmap = {by_source[drop]: by_source[keep] for keep, drop in pairs}
                bmesh.ops.weld_verts(bm, targetmap=targetmap)
                bm.normal_update()
                after_merge = _topology_counts(bm)
                for key in ('boundary', 'wire', 'multi', 'non_contiguous', 'bow_tie'):
                    if after_merge[key] > before_merge[key]:
                        raise ValueError(
                            f'Collapsed-quad simplification increased {key.replace("_", " ")}')
                remaining = {face[face_source] - 1: face for face in bm.faces}
                if any(source not in remaining or len(remaining[source].verts) != 3
                       or remaining[source].calc_area() <= 0.0
                       for source in simplified_source_faces):
                    raise ValueError('A collapsed quad did not simplify to one positive-area triangle')
                merged_sources = [drop for _keep, drop in pairs]
                report['coincident_vertices_merged'] = len(pairs)
                report['collapsed_quads_simplified'] = len(simplified_source_faces)
                expected_faces -= len(simplified_source_faces)

        source_faces = [face for face in bm.faces if len(face.verts) > 3]
        report['source_faces'] = len(source_faces)
        if triangulation_overrides:
            for source, triangles in sorted(triangulation_overrides.items()):
                face = next((face for face in bm.faces if face[face_source] == source + 1), None)
                if face is None or len(face.verts) < 4:
                    raise ValueError('Retry source face changed during preprocessing')
                vertices = {vertex[vertex_source] - 1: vertex for vertex in face.verts}
                if set(vertices) != set(mesh.polygons[source].vertices):
                    raise ValueError('Retry source corners changed during preprocessing')
                _replace_face_pattern(
                    bm, face, [tuple(vertices[index] for index in triangle) for triangle in triangles],
                    face_source, corner_source)
            source_faces = [face for face in bm.faces if len(face.verts) > 3]
        if simplify_collapsed_quads or minimize_four_face_edges:
            source_layer_names = (vertex_source.name, edge_source.name,
                                  face_source.name, corner_source.name)
            bm, zero_report = _triangulate_avoiding_zero(
                bm, source_faces, face_source, corner_source,
                minimize_four_face_edges, avoid_zero=simplify_collapsed_quads,
                steer_ngons=steer_ngons)
            report.update(zero_report)
            vertex_source = bm.verts.layers.int[source_layer_names[0]]
            edge_source = bm.edges.layers.int[source_layer_names[1]]
            face_source = bm.faces.layers.int[source_layer_names[2]]
            corner_source = bm.loops.layers.int[source_layer_names[3]]
        else:
            operation = bmesh.ops.triangulate(
                bm, faces=source_faces, quad_method='BEAUTY', ngon_method='BEAUTY')
            report['double_map'] = len(operation['face_map_double'])
        bm.normal_update()
        bm.verts.index_update()
        bm.edges.index_update()
        bm.faces.index_update()
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()
        bm.faces.ensure_lookup_table()

        point_mapping = [vertex[vertex_source] - 1 for vertex in bm.verts]
        edge_mapping = [(edge[edge_source] - 1) if edge[edge_source] else None
                        for edge in bm.edges]
        face_mapping = [face[face_source] - 1 for face in bm.faces]
        if any(source < 0 or source >= len(mesh.polygons) for source in face_mapping):
            raise ValueError('Triangulation lost source-face correspondence')
        corner_mapping = [loop[corner_source] - 1
                          for face in bm.faces for loop in face.loops]
        if any(source < 0 or source >= len(mesh.loops) for source in corner_mapping):
            raise ValueError('Triangulation lost source-corner correspondence')
        if not merged_sources:
            if any(loop[corner_source] - 1 != source_loop.get(
                    (face[face_source] - 1, loop.vert[vertex_source] - 1))
                   for face in bm.faces for loop in face.loops):
                raise ValueError('Triangulation changed source face/vertex corner correspondence')

        bm.verts.layers.int.remove(vertex_source)
        bm.edges.layers.int.remove(edge_source)
        bm.faces.layers.int.remove(face_source)
        bm.loops.layers.int.remove(corner_source)
        result = mesh.copy()
        bm.to_mesh(result)
        result.update(calc_edges_loose=True)

        mappings = {'POINT': point_mapping, 'EDGE': edge_mapping,
                    'FACE': face_mapping, 'CORNER': corner_mapping}
        for name, record in saved.items():
            domain, data_type, _properties, _values = record
            attribute = result.attributes.get(name)
            if attribute is None:
                attribute = result.attributes.new(name=name, type=data_type, domain=domain)
            _set_attribute_values(attribute, record, mappings[domain])
        for index, source in enumerate(face_mapping):
            result.polygons[index].material_index = mesh.polygons[source].material_index
            result.polygons[index].use_smooth = mesh.polygons[source].use_smooth
        if normals is not None:
            result.normals_split_custom_set([normals[source] for source in corner_mapping])
            # Blender may sharpen an unrelated existing edge while rebuilding
            # custom normals. Restore only source edges; new diagonals retain
            # their triangulation-generated sharpness.
            for target, source in enumerate(edge_mapping):
                if source is not None:
                    result.edges[target].use_edge_sharp = mesh.edges[source].use_edge_sharp
        result.update()
        if uv_names:
            result.uv_layers.active_index = uv_active
            for layer, active_render in zip(result.uv_layers, uv_render):
                layer.active_render = active_render

        if len(result.vertices) != len(mesh.vertices) - len(merged_sources):
            raise ValueError('Unexpected vertex count after triangulation')
        if len(result.polygons) != expected_faces or any(len(face.vertices) != 3 for face in result.polygons):
            raise ValueError('Unexpected triangulated face count')
        for vertex, source in zip(result.vertices, point_mapping):
            if tuple(vertex.co) != tuple(mesh.vertices[source].co):
                raise ValueError('Vertex position changed')
            actual_weights = tuple(sorted((group.group, group.weight) for group in vertex.groups))
            if actual_weights != weights[source]:
                raise ValueError('Vertex group weights changed')
        actual = attributes(result)
        for name, record in saved.items():
            domain, data_type, properties, values = record
            current = actual.get(name)
            if current is None or current[:3] != (domain, data_type, properties):
                raise ValueError('Attribute changed: ' + name)
            for target, source in enumerate(mappings[domain]):
                if source is not None and current[3][target] != values[source]:
                    raise ValueError('Attribute value changed: ' + name)
        if normals is not None:
            _verify_custom_normals(result, normals, corner_mapping, face_mapping)
        if [layer.name for layer in result.uv_layers] != uv_names:
            raise ValueError('UV layers changed')
        if simplify_collapsed_quads:
            report['zero_area_triangles'] = sum(
                len(mesh.polygons[source].vertices) > 3 and face.area <= 0.0
                for face, source in zip(result.polygons, face_mapping))
        report['triangles'] = len(result.polygons)
        report['opposite_duplicates'] = _opposite_duplicate_count(result)
        result_edge_faces = defaultdict(int)
        for face in result.polygons:
            for key in face.edge_keys:
                result_edge_faces[tuple(sorted(key))] += 1
        report['new_four_face_edges'] = sum(
            count >= 4 and original_edge_faces.get(key, 0) < 4
            for key, count in result_edge_faces.items())
        return result, report
    except Exception as error:
        if isinstance(error, _CustomNormalVerificationError):
            error.source_vertices = tuple(sorted({mesh.loops[index].vertex_index
                                                 for index in getattr(error, 'source_corners', ())}))
        if result is not None:
            bpy.data.meshes.remove(result)
        raise
    finally:
        bm.free()


def _normal_retry_options(mesh, error):
    primary = [index for index in error.source_faces
               if len(mesh.polygons[index].vertices) > 3]
    vertices = set(error.source_vertices) or {
        vertex for index in error.source_faces for vertex in mesh.polygons[index].vertices}
    neighbors = [face.index for face in mesh.polygons
                 if len(face.vertices) > 3 and face.index not in primary
                 and vertices.intersection(face.vertices)]
    options = []
    for source in primary + neighbors:
        face = mesh.polygons[source]
        coords = [tuple(mesh.vertices[index].co) for index in face.vertices]
        candidates = valid_triangulations(coords, tuple(face.normal))
        ranked = sorted(candidates, key=lambda pattern: (triangle_quality(coords, pattern), pattern))
        if not ranked:
            continue
        # Bound both the number of polygons and alternatives tested per polygon.
        options.append((source, [tuple(tuple(face.vertices[i] for i in triangle)
                                       for triangle in pattern) for pattern in ranked[:8]]))
        if len(options) >= NORMAL_RETRY_FACE_LIMIT:
            break
    return options


def _normal_retry_plans(options):
    """Bounded single-face and interacting-pair exploration, not a global solver."""
    seen = set()
    singles = 0
    for choice in range(max((len(patterns) for _, patterns in options), default=0)):
        for source, patterns in options:
            if choice >= len(patterns):
                continue
            key = ((source, patterns[choice]),)
            if key in seen:
                continue
            seen.add(key)
            yield dict(key)
            singles += 1
            if singles >= NORMAL_RETRY_LIMIT // 2:
                break
        if singles >= NORMAL_RETRY_LIMIT // 2:
            break
    # Try lower-ranked pattern pairs fairly across source-face pairs.
    pairs = list(combinations(options, 2))
    for level in range(15):
        for (a, left), (b, right) in pairs:
            for i, j in product(range(len(left)), range(len(right))):
                if i + j != level:
                    continue
                key = tuple(sorted(((a, left[i]), (b, right[j]))))
                if key not in seen:
                    seen.add(key)
                    yield dict(key)
                if len(seen) >= NORMAL_RETRY_LIMIT:
                    return


def prepare_preserving_triangulation(obj, minimize_four_face_edges=False,
                                     simplify_collapsed_quads=False):
    """Stage and verify first; retry local diagonals without relaxing normals."""
    try:
        return _prepare_preserving_triangulation_once(
            obj, minimize_four_face_edges, simplify_collapsed_quads)
    except _CustomNormalVerificationError as error:
        if not minimize_four_face_edges:
            raise
        original_error = error
    attempts = 0
    options = _normal_retry_options(obj.data, original_error)
    for overrides in _normal_retry_plans(options):
        # Reserve the last attempt for the old, fully verified Beauty path.
        if attempts >= NORMAL_RETRY_LIMIT - 1:
            break
        attempts += 1
        try:
            result, report = _prepare_preserving_triangulation_once(
                obj, minimize_four_face_edges, simplify_collapsed_quads, overrides)
        except ValueError:
            # Includes candidate materialization and final preservation checks.
            # Each failed attempt has already freed its entire staging mesh.
            continue
        report['normal_retry_attempts'] = attempts
        report['normal_retry_faces'] = len(overrides)
        return result, report
    attempts += 1
    try:
        result, report = _prepare_preserving_triangulation_once(
            obj, minimize_four_face_edges, simplify_collapsed_quads, steer_ngons=False)
    except ValueError:
        pass
    else:
        report['normal_retry_attempts'] = attempts
        return result, report
    raise _CustomNormalVerificationError(
        f'{original_error}; no verified alternative after {attempts} local retries',
        original_error.source_faces, original_error.source_vertices)


class OBJECT_OT_preserving_triangulate(bpy.types.Operator):
    bl_idname = 'object.preserving_triangulate'
    bl_label = 'Triangulate Preserving Opposite Faces'
    bl_description = ('Triangulate all faces while preserving coincident faces with opposite winding; '
                      'unlike Blender Face > Triangulate Faces')
    bl_options = {'REGISTER', 'UNDO'}

    scope: bpy.props.EnumProperty(
        name='Target',
        items=[('SELECTED', 'Selected Meshes', 'Process all selected mesh objects'),
               ('ACTIVE', 'Active Mesh', 'Process only the active mesh object')],
        default='SELECTED')
    minimize_four_face_edges: bpy.props.BoolProperty(
        name='Minimize New 4-Face Edges (Best Effort)', default=False,
        description=('Prefer quad/n-gon splits avoiding duplicate triangles and new 4-face '
                     'edges; retry local splits if custom normals fail verification. '
                     'Best effort: up to 16 corners, 256 candidates, 32 normal retries'))
    simplify_collapsed_quads: bpy.props.BoolProperty(
        name='Simplify Collapsed Quads', default=False,
        description=('Merge one exact-coordinate adjacent vertex pair so a spatially '
                     'three-corner quad becomes one positive-area triangle; also try to avoid '
                     'zero-area n-gon triangles and report any that remain'))

    @classmethod
    def poll(cls, context):
        return context.mode == 'OBJECT' and (
            any(obj.type == 'MESH' for obj in context.selected_objects)
            or (context.active_object is not None and context.active_object.type == 'MESH'))

    def execute(self, context):
        targets = ([context.active_object] if self.scope == 'ACTIVE'
                   else list(context.selected_objects))
        targets = sorted((obj for obj in targets if obj and obj.type == 'MESH'),
                         key=lambda obj: obj.name)
        changed = source_faces = triangles = duplicates = failed = 0
        steered = avoided = new_four_face = 0
        ngons = retries = retry_faces = 0
        simplified = merged = rejected_collapsed = zero_area = zero_avoided = 0
        for obj in targets:
            try:
                result, report = prepare_preserving_triangulation(
                    obj, self.minimize_four_face_edges, self.simplify_collapsed_quads)
                source_faces += report['source_faces']
                triangles += report['triangles']
                duplicates += report['opposite_duplicates']
                steered += report['steered_quads']
                ngons += report['steered_ngons']
                retries += report['normal_retry_attempts']
                retry_faces += report['normal_retry_faces']
                avoided += report['four_face_diagonals_avoided']
                new_four_face += report['new_four_face_edges']
                simplified += report['collapsed_quads_simplified']
                merged += report['coincident_vertices_merged']
                rejected_collapsed += len(report['collapsed_quads_rejected'])
                zero_area += report['zero_area_triangles']
                zero_avoided += report['zero_area_triangles_avoided']
                for warning in report['collapsed_quads_rejected']:
                    self.report({'WARNING'}, f'{obj.name}: {warning}')
                if result is not None:
                    old_mesh = obj.data
                    try:
                        obj.data = result
                    except Exception:
                        bpy.data.meshes.remove(result)
                        raise
                    result.name = old_mesh.name + '_PreserveTriangulate'
                    changed += 1
            except Exception as exc:
                failed += 1
                self.report({'WARNING'}, f'{obj.name}: {exc}')
        self.report({'WARNING'} if failed or zero_area else {'INFO'},
                    f'{changed} mesh(es); {source_faces} faces triangulated; '
                    f'{triangles} total triangles; {zero_area} zero-area triangles remaining '
                    f'/ {zero_avoided} avoided; {duplicates} opposite duplicates preserved; '
                    f'{avoided} 4-face diagonals avoided; {new_four_face} new 4-face edges; '
                    f'{ngons} n-gons steered; {retry_faces} faces recovered / {retries} normal retries; '
                    f'{simplified} collapsed quads simplified / {merged} vertices merged; '
                    f'{rejected_collapsed} collapsed candidates rejected; {failed} meshes failed')
        return {'FINISHED'} if changed or not failed else {'CANCELLED'}
