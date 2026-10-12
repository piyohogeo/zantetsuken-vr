"""Choose a split-only 3-face repair by verified provisional cap seam angles."""
from collections import defaultdict
import itertools
import math
import time

import bpy
from mathutils import Vector

from .balanced_four_face_split import _topology, _face_components
from .boundary_weld import attributes
from .hard_cap import prepare_hard_cap
from .non_manifold_check import classify_mesh
from .preserving_triangulate import custom_normal_is_close


MAX_CANDIDATES = 64
MAX_SEARCH_SECONDS = 30.0
ANGLE_TIE_TOLERANCE_DEGREES = 1e-4


class _SearchIncomplete(Exception):
    pass


def _check_deadline(start):
    # Blender mesh operations run synchronously. Check between operations and
    # after each cap; a late result must never become a partial-search winner.
    if time.monotonic() - start > MAX_SEARCH_SECONDS:
        raise _SearchIncomplete('Complete candidate search exceeded the time limit')


def _candidate_plan(mesh, edge_faces, incident_faces, pairs):
    """Plan every target simultaneously; reject reconnection or other edge cuts."""
    selected = set(pairs)
    labels = _face_components(mesh, edge_faces, incident_faces, selected, pairs)
    affected = {vertex for edge in selected for vertex in mesh.edges[edge].vertices}
    assignments = {}
    sources = []
    for vertex, face_labels in enumerate(labels):
        if vertex not in affected:
            for face in face_labels:
                assignments[vertex, face] = vertex
            continue
        for offset, component in enumerate(sorted(set(face_labels.values()))):
            target = vertex if not offset else len(mesh.vertices) + len(sources)
            if offset:
                sources.append(vertex)
            for face, label in face_labels.items():
                if label == component:
                    assignments[vertex, face] = target

    planned_edges = [None] * len(mesh.edges)
    edge_sources = []
    corner_edges = [None] * len(mesh.loops)
    descendants = {}
    sharp_edges = set()
    for edge in mesh.edges:
        entries = edge_faces[edge.index]
        if not entries:
            planned_edges[edge.index] = tuple(edge.vertices)
            continue
        a, b = edge.vertices
        groups = defaultdict(list)
        for face, loop, _ in entries:
            groups[assignments[a, face], assignments[b, face]].append((face, loop))
        ordered = sorted(groups.items(), key=lambda item: (min(f for f, _ in item[1]), item[0]))
        if edge.index in selected:
            if sorted(len(records) for _, records in ordered) != [1, 2]:
                return None
            paired = next({face for face, _ in records} for _, records in ordered if len(records) == 2)
            if paired != set(pairs[edge.index][0]):
                return None
            descendants[edge.index] = []
        elif len(ordered) != 1:
            return None
        for offset, (vertices, records) in enumerate(ordered):
            target = edge.index if not offset else len(planned_edges)
            if offset:
                planned_edges.append(vertices)
                edge_sources.append(edge.index)
            else:
                planned_edges[target] = vertices
            if edge.index in selected:
                descendants[edge.index].append(target)
                if len(records) == 2:
                    sharp_edges.add(target)
            for _, loop in records:
                corner_edges[loop] = target
    if any(edge is None for edge in planned_edges) or any(edge is None for edge in corner_edges):
        raise ValueError('Incomplete three-face topology plan')
    return {
        'sources': sources,
        'edges': planned_edges,
        'edge_sources': edge_sources,
        'corners': [assignments[vertex, face.index]
                    for face in mesh.polygons for vertex in face.vertices],
        'corner_edges': corner_edges,
        'descendants': descendants,
        'sharp_edges': sharp_edges,
        'pairs': pairs,
    }


def _materialize_candidate(obj, plan):
    """Return an independently verified split mesh; clean failed copies."""
    from . import _point_values

    mesh = obj.data
    saved = attributes(mesh)
    point_values = _point_values(mesh, plan['sources'])
    normals = [tuple(n.vector) for n in mesh.corner_normals] if mesh.has_custom_normals else None
    uv_settings = ([layer.name for layer in mesh.uv_layers], mesh.uv_layers.active_index,
                   [layer.active_render for layer in mesh.uv_layers])
    before_faces, _ = _topology(mesh)
    result = None
    stage = None
    try:
        result = mesh.copy()
        stage = obj.copy()
        stage.data = result
        result.vertices.add(len(plan['sources']))
        for name, records in point_values:
            for offset, record in enumerate(records):
                for prop, value in record.items():
                    setattr(result.attributes[name].data[len(mesh.vertices) + offset], prop, value)
        for offset, source in enumerate(plan['sources']):
            for group in mesh.vertices[source].groups:
                stage.vertex_groups[group.group].add(
                    [len(mesh.vertices) + offset], group.weight, 'REPLACE')
        result.edges.add(len(plan['edge_sources']))
        for name, (domain, _, props, values) in saved.items():
            if domain == 'EDGE':
                for offset, source in enumerate(plan['edge_sources']):
                    for prop, value in zip(props, values[source]):
                        setattr(result.attributes[name].data[len(mesh.edges) + offset], prop, value)
        result.loops.foreach_set('vertex_index', plan['corners'])
        result.loops.foreach_set('edge_index', plan['corner_edges'])
        result.edges.foreach_set('vertices', [v for edge in plan['edges'] for v in edge])
        result.update(calc_edges=False, calc_edges_loose=True)
        for edge in plan['sharp_edges']:
            result.edges[edge].use_edge_sharp = True
        result.update()
        if normals is not None:
            result.normals_split_custom_set(normals)
        # The normal setter may sharpen unrelated edges. Restore every edge
        # except the deliberate two-face descendants of the repaired roots.
        inverse_edges = list(range(len(mesh.edges))) + plan['edge_sources']
        for index, source in enumerate(inverse_edges):
            result.edges[index].use_edge_sharp = (
                index in plan['sharp_edges'] or mesh.edges[source].use_edge_sharp)
        result.update()

        actual = attributes(result)
        for name, (domain, typ, props, values) in saved.items():
            expected = list(values)
            if domain == 'POINT':
                expected += [values[source] for source in plan['sources']]
            elif domain == 'EDGE':
                expected += [values[source] for source in plan['edge_sources']]
            if name == 'sharp_edge' and domain == 'EDGE':
                expected = [(True,) if index in plan['sharp_edges'] else value
                            for index, value in enumerate(expected)]
            if actual.get(name) != (domain, typ, props, expected):
                raise ValueError('Attribute verification failed: ' + name)
        inverse_vertices = list(range(len(mesh.vertices))) + plan['sources']
        for index, source in enumerate(inverse_vertices):
            vertex, old = result.vertices[index], mesh.vertices[source]
            if (tuple(vertex.co) != tuple(old.co)
                    or sorted((g.group, g.weight) for g in vertex.groups)
                    != sorted((g.group, g.weight) for g in old.groups)):
                raise ValueError('Vertex position / weights changed')
        if normals is not None:
            if not result.has_custom_normals or len(result.corner_normals) != len(normals):
                raise ValueError('Custom normal data was lost')
            if any(not custom_normal_is_close(n.vector, Vector(normals[index]))
                   for index, n in enumerate(result.corner_normals)):
                raise ValueError('Custom normal verification failed')
        if (len(result.polygons) != len(mesh.polygons)
                or len(result.loops) != len(mesh.loops)
                or len(result.vertices) != len(inverse_vertices)
                or len(result.edges) != len(inverse_edges)):
            raise ValueError('Unexpected mesh element counts')
        for face, old in zip(result.polygons, mesh.polygons):
            if (tuple(inverse_vertices[v] for v in face.vertices) != tuple(old.vertices)
                    or face.material_index != old.material_index
                    or face.use_smooth != old.use_smooth
                    or face.loop_start != old.loop_start or face.loop_total != old.loop_total):
                raise ValueError('Original face / corner data changed')
        if ([layer.name for layer in result.uv_layers], result.uv_layers.active_index,
                [layer.active_render for layer in result.uv_layers]) != uv_settings:
            raise ValueError('UV layer settings changed')
        after_faces, _ = _topology(result)
        for edge, children in plan['descendants'].items():
            if sorted(len(after_faces[child]) for child in children) != [1, 2]:
                raise ValueError('Target edge did not become a 2+1 split')
        for index, entries in enumerate(before_faces):
            if index not in plan['descendants']:
                if [f for f, _, _ in after_faces[index]] != [f for f, _, _ in entries]:
                    raise ValueError('A non-target edge changed face incidence')
                if {inverse_vertices[v] for v in result.edges[index].vertices} != set(mesh.edges[index].vertices):
                    raise ValueError('A non-target edge changed source vertices')
        if (sum(len(e) == 1 for e in after_faces)
                != sum(len(e) == 1 for e in before_faces) + len(plan['descendants'])):
            raise ValueError('Unexpected boundary increase')
        classified = classify_mesh(result, True)
        if any(classified[key] for key in ('wire', 'multi_face', 'non_contiguous', 'vertices')):
            raise ValueError('Split mesh still has unsupported non-manifold geometry')
        bpy.data.objects.remove(stage)
        stage = None
        return result
    except Exception:
        if stage is not None:
            bpy.data.objects.remove(stage)
        if result is not None:
            bpy.data.meshes.remove(result)
        raise


def _score_capped_candidate(obj, split_mesh, plan):
    """Score both descendants at every target after a disposable fixed-policy cap."""
    stage = None
    capped = None
    try:
        stage = obj.copy()
        stage.data = split_mesh
        capped, report = prepare_hard_cap(
            stage, optimized_metrics={'AREA'}, repair_coincident=True,
            allow_opposite_multi_face_edges=True, cap_isolated_single_faces=True)
        if capped is None or report.get('rejected'):
            raise ValueError('Provisional Optimized Cap did not complete: '
                             + '; '.join(report.get('rejected', [])))
        # Welding can renumber coincident vertices while leaving their
        # coordinates indistinguishable. Without an explicit provenance map,
        # that would make the identity of the two sheets ambiguous.
        if report.get('welded_vertices', 0) or len(capped.vertices) != len(split_mesh.vertices):
            raise ValueError('Provisional cap changed vertex identity; target seams cannot be scored safely')
        if (len(capped.polygons) < len(split_mesh.polygons)
                or len(capped.loops) < len(split_mesh.loops)
                or any(tuple(a.co) != tuple(b.co)
                       for a, b in zip(capped.vertices, split_mesh.vertices))
                or any((tuple(capped.polygons[f.index].vertices),
                        capped.polygons[f.index].loop_start, capped.polygons[f.index].loop_total)
                       != (tuple(f.vertices), f.loop_start, f.loop_total)
                       for f in split_mesh.polygons)
                or any(capped.loops[loop.index].vertex_index != loop.vertex_index
                       for loop in split_mesh.loops)):
            raise ValueError('Provisional cap changed original vertex / face / corner identity')
        classified = classify_mesh(capped, True)
        if any(classified[key] for key in ('wire', 'boundary', 'multi_face',
                                           'non_contiguous', 'vertices')):
            raise ValueError('Provisional cap is not closed and valid under Allow 4-face')
        edge_faces, _ = _topology(capped)
        # Do not rely on coordinates: distinct sheets deliberately coincide.
        # Match the unique topological vertex pair of each split descendant.
        by_vertices = defaultdict(list)
        for edge in capped.edges:
            by_vertices[tuple(sorted(edge.vertices))].append(edge.index)
        targets = {}
        for children in plan['descendants'].values():
            for child in children:
                key = tuple(sorted(split_mesh.edges[child].vertices))
                matches = by_vertices.get(key, ())
                if len(matches) != 1 or len(edge_faces[matches[0]]) != 2:
                    raise ValueError('Provisional cap did not retain a two-face target seam')
                # Cap repair is permitted only if the same source vertices
                # still describe this seam; no relocated / remapped scoring.
                if any(v >= len(capped.vertices)
                       or tuple(capped.vertices[v].co) != tuple(split_mesh.vertices[v].co)
                       for v in key):
                    raise ValueError('Provisional cap changed target seam vertices')
                targets[key] = matches[0]
        capped.calc_loop_triangles()
        normals = defaultdict(list)
        for triangle in capped.loop_triangles:
            vertices = tuple(triangle.vertices)
            for index in range(3):
                key = tuple(sorted((vertices[index], vertices[(index + 1) % 3])))
                if key in targets:
                    normals[key].append((triangle.polygon_index, triangle.normal.copy()))
        angles = []
        for key, edge in targets.items():
            adjacent = normals[key]
            expected = {face for face, _, _ in edge_faces[edge]}
            if len(adjacent) != 2 or {face for face, _ in adjacent} != expected:
                raise ValueError('Target seam has no unique adjacent triangle pair')
            vectors = [normal for _, normal in adjacent]
            if any(not all(math.isfinite(x) for x in normal)
                   or not math.isfinite(normal.length) or normal.length <= 1e-12
                   for normal in vectors):
                raise ValueError('Target seam has a degenerate triangle normal')
            dot = vectors[0].normalized().dot(vectors[1].normalized())
            if not math.isfinite(dot):
                raise ValueError('Target seam angle is not finite')
            angle = math.degrees(math.acos(max(-1.0, min(1.0, dot))))
            if not math.isfinite(angle):
                raise ValueError('Target seam angle is not finite')
            angles.append(angle)
        if len(angles) != 2 * len(plan['descendants']):
            raise ValueError('Incomplete target seam angle measurement')
        return max(angles)
    finally:
        if capped is not None:
            bpy.data.meshes.remove(capped)
        if stage is not None:
            bpy.data.objects.remove(stage)


def prepare_three_face_split(obj):
    """Return only the uniquely best verified split; never assign source data."""
    report = {
        'candidate_edges': 0, 'raw_candidates': 0, 'valid_candidates': 0,
        'evaluated_candidates': 0, 'split_edges': 0, 'vertices_added': 0,
        'new_boundary_edges': 0, 'max_fold_angle': None,
        'selected_pairs': None, 'rejected': [],
    }
    start = time.monotonic()
    mesh = obj.data
    if (mesh.shape_keys or len(mesh.skin_vertices)
            or any(modifier.type == 'MULTIRES' for modifier in obj.modifiers)):
        report['rejected'].append('Shape keys / Skin / Multires are not supported')
        return None, report
    if obj.library or obj.override_library or mesh.library or mesh.override_library:
        report['rejected'].append('Linked / override data is not supported')
        return None, report
    edge_faces, incident_faces = _topology(mesh)
    selected = [index for index, entries in enumerate(edge_faces) if len(entries) == 3]
    report['candidate_edges'] = len(selected)
    if not selected:
        return None, report
    choices = []
    raw_count = 1
    for edge in selected:
        options = [((a[0], b[0]),) for a, b in itertools.combinations(edge_faces[edge], 2)
                   if a[2] != b[2]]
        if not options:
            report['rejected'].append(f'Edge {edge}: no opposite-traversal face pair')
            return None, report
        choices.append(options)
        raw_count *= len(options)
        if raw_count > MAX_CANDIDATES:
            report['raw_candidates'] = raw_count
            report['rejected'].append(
                f'Complete pairing search exceeds the {MAX_CANDIDATES}-candidate limit')
            return None, report
    report['raw_candidates'] = raw_count
    best = None
    best_plan = None
    best_score = math.inf
    scores = []
    try:
        for index, combination in enumerate(itertools.product(*choices)):
            _check_deadline(start)
            pairs = dict(zip(selected, combination))
            plan = _candidate_plan(mesh, edge_faces, incident_faces, pairs)
            _check_deadline(start)
            if plan is None:
                continue
            report['valid_candidates'] += 1
            trial = None
            try:
                trial = _materialize_candidate(obj, plan)
                _check_deadline(start)
                score = _score_capped_candidate(obj, trial, plan)
                _check_deadline(start)
                if not math.isfinite(score):
                    raise ValueError('Non-finite maximum fold angle')
                scores.append(score)
                report['evaluated_candidates'] += 1
                if score < best_score:
                    if best is not None:
                        bpy.data.meshes.remove(best)
                    best, best_plan, best_score = trial, plan, score
                    trial = None
            except _SearchIncomplete:
                raise
            except ValueError as exc:
                report['rejected'].append(f'Candidate {index + 1}: {exc}')
            finally:
                if trial is not None:
                    bpy.data.meshes.remove(trial)
        _check_deadline(start)
        if best is None:
            reason = ('No 2+1 pairing can avoid cutting a non-target edge'
                      if not report['valid_candidates']
                      else 'No complete, verified split-and-cap candidate')
            if report['rejected']:
                reason += ': ' + report['rejected'][0]
            report['rejected'].append(reason)
            return None, report
        report['max_fold_angle'] = best_score
        tied = sum(abs(score - best_score) <= ANGLE_TIE_TOLERANCE_DEGREES for score in scores)
        if tied != 1:
            report['rejected'].append(
                f'{tied} candidates tie within {ANGLE_TIE_TOLERANCE_DEGREES:g} degrees; mesh unchanged')
            return None, report
        report.update(split_edges=len(selected), vertices_added=len(best_plan['sources']),
                      new_boundary_edges=len(selected), selected_pairs=best_plan['pairs'])
        result = best
        best = None
        return result, report
    except _SearchIncomplete as exc:
        report['rejected'].append(str(exc) + '; mesh unchanged')
        return None, report
    except Exception as exc:
        report['rejected'].append(f'Candidate search failed: {exc}; mesh unchanged')
        return None, report
    finally:
        if best is not None:
            bpy.data.meshes.remove(best)


class OBJECT_OT_split_three_face_sheets(bpy.types.Operator):
    bl_idname = 'object.split_three_face_sheets'
    bl_label = 'Split 3-Face Sheets'
    bl_description = ('Split 3-face edges by the unique smallest worst target-seam fold. '
                      'Trial cap: Area only, Repair Coincident, Allow 4-face, and Cap Isolated '
                      'Single Faces on. Keep only the split; add one boundary per split edge')
    bl_options = {'REGISTER', 'UNDO'}

    scope: bpy.props.EnumProperty(
        name='Target', items=[('SELECTED', 'Selected Meshes', ''),
                              ('ACTIVE', 'Active Mesh', '')], default='SELECTED')

    @classmethod
    def poll(cls, context):
        if context.mode != 'OBJECT':
            cls.poll_message_set('Switch to Object Mode before splitting 3-face sheets')
            return False
        return (any(obj.type == 'MESH' for obj in context.selected_objects)
                or context.active_object is not None and context.active_object.type == 'MESH')

    def execute(self, context):
        targets = [context.active_object] if self.scope == 'ACTIVE' else context.selected_objects
        targets = sorted((obj for obj in targets if obj and obj.type == 'MESH'), key=lambda obj: obj.name)
        changed = edges = vertices = boundaries = failed = 0
        summaries = []
        for obj in targets:
            try:
                result, report = prepare_three_face_split(obj)
                if result is None:
                    reason = report['rejected'][-1] if report['rejected'] else 'No 3-face edges'
                    summaries.append(f'{obj.name}: skipped; {reason}')
                    if report['rejected']:
                        self.report({'WARNING'}, f'{obj.name}: {reason}')
                    continue
                try:
                    obj.data = result
                except Exception:
                    bpy.data.meshes.remove(result)
                    raise
                result.name += '_ThreeFaceSheetsSplit'
                changed += 1
                edges += report['split_edges']
                vertices += report['vertices_added']
                boundaries += report['new_boundary_edges']
                summaries.append(
                    f'{obj.name}: {report["split_edges"]} edges; +{report["vertices_added"]} vertices; '
                    f'+{report["new_boundary_edges"]} boundaries; max fold {report["max_fold_angle"]:.4f} deg')
            except Exception as exc:
                failed += 1
                summaries.append(f'{obj.name}: failed; {exc}')
                self.report({'WARNING'}, summaries[-1])
        if hasattr(context.scene, 'btw_three_face_result'):
            context.scene.btw_three_face_result = '\n'.join(
                ['Split only; cap used for evaluation only.'] + summaries)
        self.report({'WARNING'} if failed else {'INFO'},
                    f'{changed} mesh(es); {edges} edges split; +{vertices} vertices; '
                    f'+{boundaries} boundaries; {failed} failed (cap evaluation only)')
        return {'FINISHED'} if changed or not failed else {'CANCELLED'}
