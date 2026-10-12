"""Join a boundary that doubles back exactly along one straight edge."""
import bpy
import bmesh

from .boundary_weld import attributes
from .collapsed_boundary_cleanup import _topology


def _components(bm):
    pending = {edge for edge in bm.edges if edge.is_boundary}
    while pending:
        seed = min(pending, key=lambda edge: edge.index)
        pending.remove(seed)
        component = {seed}
        stack = [seed]
        while stack:
            for vertex in stack.pop().verts:
                for edge in vertex.link_edges:
                    if edge in pending:
                        pending.remove(edge)
                        component.add(edge)
                        stack.append(edge)
        yield component


def _candidate(component):
    """Return the long-edge/segmented-path IDs only for a straight simple cycle."""
    vertices = {vertex for edge in component for vertex in edge.verts}
    if len(component) < 4 or len(vertices) != len(component):
        return None
    if any(sum(edge in component for edge in vertex.link_edges) != 2 for vertex in vertices):
        return None
    if any(edge.is_wire or len(edge.link_faces) > 2
           for vertex in vertices for edge in vertex.link_edges):
        return None
    ordered = sorted(component, key=lambda edge: edge.calc_length(), reverse=True)
    long_edge = ordered[0]
    start, end = long_edge.verts
    vector = end.co - start.co
    length = vector.length
    tolerance = max(1e-6, length * 1e-6)
    if length == 0 or ordered[1].calc_length() >= length - tolerance:
        return None
    interior = vertices - {start, end}
    if len({tuple(vertex.co) for vertex in vertices}) != len(vertices):
        return None
    positions = {}
    for vertex in interior:
        factor = (vertex.co - start.co).dot(vector) / vector.length_squared
        if not tolerance / length < factor < 1 - tolerance / length:
            return None
        if (vertex.co - (start.co + vector * factor)).length > tolerance:
            return None
        positions[vertex] = factor
    # The other edges must form one non-overlapping path from start to end.
    path = [start] + sorted(interior, key=lambda vertex: positions[vertex]) + [end]
    short = component - {long_edge}
    if {frozenset(edge.verts) for edge in short} != {
            frozenset((a, b)) for a, b in zip(path, path[1:])}:
        return None
    if len(long_edge.link_faces) != 1:
        return None
    return {'seed': min(edge.index for edge in component),
            'long_edge': long_edge.index,
            'path': tuple(vertex.index for vertex in path),
            'short_edges': tuple(edge.index for edge in short),
            'long_face': long_edge.link_faces[0].index,
            'boundary_count': len(component)}


def _cyclic_equal(before, after):
    if len(before) != len(after):
        return False
    doubled = after + after
    return any(doubled[index:index + len(before)] == before
               for index in range(len(after)))


def prepare_collinear_boundary_join(obj):
    """Return a checked mesh copy and report; never modify the source object."""
    mesh = obj.data
    report = {'candidates': 0, 'joined': 0, 'boundary_edges_removed': 0,
              'rejected': []}
    if mesh.shape_keys or len(mesh.skin_vertices) or any(mod.type == 'MULTIRES' for mod in obj.modifiers):
        raise ValueError('Shape keys / Skin / Multires are not supported')
    if mesh.has_custom_normals:
        raise ValueError('Custom normals are not supported by this straight-seam join')
    if obj.library or obj.override_library or mesh.library or mesh.override_library:
        raise ValueError('Linked / override data is not supported')
    saved = attributes(mesh)
    uv_names = [layer.name for layer in mesh.uv_layers]
    if any(domain == 'CORNER' and (name not in uv_names or typ != 'FLOAT2')
           for name, (domain, typ, _props, _values) in saved.items()):
        raise ValueError('Unsupported non-UV corner attribute')
    weights = [tuple(sorted((group.group, group.weight) for group in vertex.groups))
               for vertex in mesh.vertices]
    original_faces = [tuple(face.vertices) for face in mesh.polygons]
    original_areas = [face.area for face in mesh.polygons]
    original_zero = sum(area <= 0 for area in original_areas)
    uv_records = {
        layer.name: {(face.index, mesh.loops[index].vertex_index): tuple(layer.data[index].uv)
                     for face in mesh.polygons for index in face.loop_indices}
        for layer in mesh.uv_layers}
    bm = bmesh.new()
    result = None
    try:
        bm.from_mesh(mesh)
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()
        bm.faces.ensure_lookup_table()
        candidates = [item for component in _components(bm)
                      if (item := _candidate(component)) is not None]
        candidates.sort(key=lambda item: item['seed'])
        report['candidates'] = len(candidates)
        if not candidates:
            return None, report
        names = {}
        for domain, seq in (('POINT', bm.verts), ('EDGE', bm.edges), ('FACE', bm.faces)):
            name = '_bt_collinear_source_' + domain
            while seq.layers.int.get(name):
                name += '_'
            layer = seq.layers.int.new(name)
            names[domain] = name
            for index, element in enumerate(seq):
                element[layer] = index + 1
        before_topology = _topology(bm)
        new_uv = {name: {} for name in uv_names}
        for candidate in candidates:
            trial = bm.copy()
            try:
                vl = trial.verts.layers.int[names['POINT']]
                el = trial.edges.layers.int[names['EDGE']]
                by_vertex = {vertex[vl]-1: vertex for vertex in trial.verts}
                by_edge = {edge[el]-1: edge for edge in trial.edges if edge[el]}
                path = [by_vertex[index] for index in candidate['path']]
                long_edge = by_edge[candidate['long_edge']]
                short_edges = [by_edge[index] for index in candidate['short_edges']]
                short_sources = {frozenset(vertex[vl]-1 for vertex in edge.verts): edge[el]
                                 for edge in short_edges}
                start, end = path[0], path[-1]
                whole = end.co - start.co
                for name in uv_names:
                    left = uv_records[name][(candidate['long_face'], candidate['path'][0])]
                    right = uv_records[name][(candidate['long_face'], candidate['path'][-1])]
                    for vertex in path[1:-1]:
                        factor = (vertex.co-start.co).dot(whole)/whole.length_squared
                        new_uv[name][(candidate['long_face'], vertex[vl]-1)] = tuple(
                            left[i] + (right[i]-left[i])*factor for i in range(2))
                mapping = {}
                current = start
                for target in path[1:-1]:
                    segment = next(edge for edge in current.link_edges if end in edge.verts)
                    factor = (target.co-current.co).length / segment.calc_length()
                    _new_edge, made = bmesh.utils.edge_split(segment, current, factor)
                    if (made.co-target.co).length > max(1e-6, whole.length*1e-6):
                        raise ValueError('Edge split changed geometry')
                    mapping[made] = target
                    current = made
                bmesh.ops.weld_verts(trial, targetmap=mapping)
                for edge in trial.edges:
                    key = frozenset(vertex[vl]-1 for vertex in edge.verts)
                    if key in short_sources:
                        edge[el] = short_sources[key]
                        original = mesh.edges[edge[el]-1]
                        edge.smooth = not original.use_edge_sharp
                trial.normal_update()
                after = _topology(trial)
                if after['boundary'] != before_topology['boundary']-candidate['boundary_count']:
                    raise ValueError('Boundary edges were not eliminated exactly')
                if any(after[key] > before_topology[key]
                       for key in ('wire', 'multi', 'non_contiguous', 'bow_tie')):
                    raise ValueError('A new non-manifold condition appeared')
                if (len(trial.verts) != len(bm.verts) or len(trial.faces) != len(bm.faces)
                        or len(trial.edges) != len(bm.edges)-1):
                    raise ValueError('Unexpected vertex / edge / face count')
                bm.free()
                bm = trial
                trial = None
                before_topology = after
                report['joined'] += 1
                report['boundary_edges_removed'] += candidate['boundary_count']
            except (ValueError, KeyError, StopIteration) as exc:
                report['rejected'].append(f"Edge {candidate['seed']}: {exc}")
                for values in new_uv.values():
                    for index in candidate['path'][1:-1]:
                        values.pop((candidate['long_face'], index), None)
            finally:
                if trial is not None:
                    trial.free()
        if not report['joined']:
            return None, report
        mappings = {}
        for domain, seq in (('POINT', bm.verts), ('EDGE', bm.edges), ('FACE', bm.faces)):
            layer = seq.layers.int[names[domain]]
            mappings[domain] = [element[layer]-1 for element in seq]
            seq.layers.int.remove(layer)
        if (sorted(mappings['POINT']) != list(range(len(mesh.vertices)))
                or sorted(mappings['FACE']) != list(range(len(mesh.polygons)))
                or len(set(mappings['EDGE'])) != len(mappings['EDGE'])
                or any(source < 0 or source >= len(mesh.edges) for source in mappings['EDGE'])):
            raise ValueError('Source element identity was lost')
        result = mesh.copy()
        bm.to_mesh(result)
        result.update(calc_edges_loose=True)
        for name, (domain, typ, props, values) in saved.items():
            attr = result.attributes.get(name)
            if attr is None:
                attr = result.attributes.new(name=name, type=typ, domain=domain)
            if attr.domain != domain or attr.data_type != typ:
                raise ValueError('Attribute schema changed: ' + name)
            if domain == 'CORNER':
                for face in result.polygons:
                    source_face = mappings['FACE'][face.index]
                    for loop_id in face.loop_indices:
                        vertex_source = mappings['POINT'][result.loops[loop_id].vertex_index]
                        key = (source_face, vertex_source)
                        uv = uv_records[name].get(key, new_uv[name].get(key))
                        if uv is None:
                            raise ValueError('New UV corner lacks interpolation source')
                        setattr(attr.data[loop_id], props[0], uv)
            else:
                for index, source_index in enumerate(mappings[domain]):
                    for prop, value in zip(props, values[source_index]):
                        setattr(attr.data[index], prop, value)
        result.update(calc_edges_loose=True)
        for index, source_index in enumerate(mappings['POINT']):
            vertex = result.vertices[index]
            if tuple(vertex.co) != tuple(mesh.vertices[source_index].co):
                raise ValueError('Vertex position changed')
            if tuple(sorted((group.group, group.weight) for group in vertex.groups)) != weights[source_index]:
                raise ValueError('Vertex weights changed')
        for face in result.polygons:
            source_index = mappings['FACE'][face.index]
            source_corners = list(original_faces[source_index])
            corners = [mappings['POINT'][index] for index in face.vertices]
            original_subset = [index for index in corners if index in source_corners]
            if not _cyclic_equal(source_corners, original_subset):
                raise ValueError('Existing face corner order changed')
            if abs(face.area-original_areas[source_index]) > max(1e-5, original_areas[source_index]*1e-5):
                raise ValueError('Existing face area changed')
        if sum(face.area <= 0 for face in result.polygons) > original_zero:
            raise ValueError('New zero-area face appeared')
        actual = attributes(result)
        for name, (domain, typ, props, values) in saved.items():
            if domain == 'CORNER':
                if name not in actual or actual[name][:3] != (domain, typ, props):
                    raise ValueError('UV attribute schema changed: ' + name)
                for face in result.polygons:
                    source_face = mappings['FACE'][face.index]
                    for loop_id in face.loop_indices:
                        vertex_source = mappings['POINT'][result.loops[loop_id].vertex_index]
                        key = (source_face, vertex_source)
                        expected = uv_records[name].get(key, new_uv[name].get(key))
                        observed = tuple(result.uv_layers[name].data[loop_id].uv)
                        if expected is None or any(abs(a-b) > 1e-6
                                                   for a, b in zip(observed, expected)):
                            raise ValueError('UV verification failed: ' + name)
                continue
            if actual.get(name) != (domain, typ, props,
                                    [values[index] for index in mappings[domain]]):
                raise ValueError('Attribute verification failed: ' + name)
        if [layer.name for layer in result.uv_layers] != uv_names:
            raise ValueError('UV layers changed')
        result.uv_layers.active_index = mesh.uv_layers.active_index
        for old, new in zip(mesh.uv_layers, result.uv_layers):
            new.active_render = old.active_render
        return result, report
    except Exception:
        if result is not None:
            bpy.data.meshes.remove(result)
        raise
    finally:
        bm.free()


class OBJECT_OT_join_collinear_boundary_seams(bpy.types.Operator):
    bl_idname = 'object.join_collinear_boundary_seams'
    bl_label = 'Join Collinear Boundary Seams'
    bl_description = 'Join a boundary path overlapping one long straight edge without moving source vertices'
    bl_options = {'REGISTER', 'UNDO'}

    scope: bpy.props.EnumProperty(name='Target', items=[
        ('SELECTED', 'Selected Meshes', ''), ('ACTIVE', 'Active Mesh', '')], default='SELECTED')

    @classmethod
    def poll(cls, context):
        return context.mode == 'OBJECT' and (any(obj.type == 'MESH' for obj in context.selected_objects)
                or (context.active_object is not None and context.active_object.type == 'MESH'))

    def execute(self, context):
        targets = ([context.active_object] if self.scope == 'ACTIVE'
                   else context.selected_objects)
        changed = joined = removed = rejected = failed = 0
        for obj in sorted((item for item in targets if item and item.type == 'MESH'),
                          key=lambda item: item.name):
            try:
                result, report = prepare_collinear_boundary_join(obj)
                rejected += len(report['rejected'])
                for message in report['rejected']:
                    self.report({'WARNING'}, f'{obj.name}: {message}')
                if result is not None:
                    try:
                        obj.data = result
                    except Exception:
                        bpy.data.meshes.remove(result)
                        raise
                    result.name += '_CollinearSeamsJoined'
                    changed += 1
                    joined += report['joined']
                    removed += report['boundary_edges_removed']
            except Exception as exc:
                failed += 1
                self.report({'WARNING'}, f'{obj.name}: {exc}')
        self.report({'WARNING'} if rejected or failed else {'INFO'},
                    f'{changed} mesh(es); {joined} seams joined; {removed} boundary edges removed; '
                    f'{rejected} candidates rejected; {failed} meshes failed')
        return {'FINISHED'} if changed or not failed else {'CANCELLED'}
