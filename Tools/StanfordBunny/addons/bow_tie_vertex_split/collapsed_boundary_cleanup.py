"""Remove exact zero-area boundary ribbons before Optimized Cap."""
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


def _topology(bm):
    return {
        'boundary': sum(edge.is_boundary for edge in bm.edges),
        'wire': sum(edge.is_wire for edge in bm.edges),
        'multi': sum(len(edge.link_faces) > 2 for edge in bm.edges),
        'non_contiguous': sum(len(edge.link_faces) == 2 and not edge.is_contiguous
                              for edge in bm.edges),
        'bow_tie': sum(_fan_count(vertex) > 1 for vertex in bm.verts),
    }


def _networks(bm):
    """Zero-area face components joined through exact zero-length edges."""
    zero = {face for face in bm.faces if face.calc_area() == 0.0
            and any(edge.calc_length() == 0.0 for edge in face.edges)}
    pending = set(zero)
    networks = []
    while pending:
        seed = min(pending, key=lambda face: face.index)
        pending.remove(seed)
        part = {seed}
        stack = [seed]
        while stack:
            face = stack.pop()
            for edge in face.edges:
                if edge.calc_length() != 0.0:
                    continue
                for other in edge.link_faces:
                    if other in pending:
                        pending.remove(other)
                        part.add(other)
                        stack.append(other)
        if not any(edge.is_boundary and edge.calc_length() == 0.0
                   for face in part for edge in face.edges):
            continue
        groups = {}
        for vertex in {vertex for face in part for vertex in face.verts}:
            groups.setdefault(tuple(vertex.co), []).append(vertex.index)
        pairs = []
        for ids in groups.values():
            if len(ids) > 2:
                pairs = []
                break
            if len(ids) == 2:
                pairs.append(tuple(sorted(ids)))
        if pairs and all(len({tuple(vertex.co) for vertex in face.verts}) == 2
                         for face in part):
            networks.append((tuple(sorted(face.index for face in part)),
                             tuple(sorted(pairs))))
    return sorted(networks, key=lambda item: item[0][0])


def prepare_collapsed_boundary_cleanup(obj):
    """Return a checked mesh copy; source object and mesh remain untouched."""
    mesh = obj.data
    report = {'candidate_networks': 0, 'networks_removed': 0,
              'faces_removed': 0, 'vertices_removed': 0,
              'edges_removed': 0, 'rejected': []}
    if mesh.shape_keys or any(mod.type == 'MULTIRES' for mod in obj.modifiers):
        raise ValueError('Shape keys / Multires are not supported')
    if obj.library or obj.override_library or mesh.library or mesh.override_library:
        raise ValueError('Linked / override data is not supported')
    if len(mesh.skin_vertices):
        raise ValueError('Collapsed boundary cleanup does not support Skin vertex data')
    saved = attributes(mesh)
    weights = [tuple(sorted((group.group, group.weight) for group in vertex.groups))
               for vertex in mesh.vertices]
    normals = ([tuple(normal.vector) for normal in mesh.corner_normals]
               if mesh.has_custom_normals else None)
    uv_names = [layer.name for layer in mesh.uv_layers]
    uv_active = mesh.uv_layers.active_index
    uv_render = [layer.active_render for layer in mesh.uv_layers]
    bm = bmesh.new()
    result = None
    try:
        bm.from_mesh(mesh)
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()
        bm.faces.ensure_lookup_table()
        networks = _networks(bm)
        report['candidate_networks'] = len(networks)
        if not networks:
            return None, report
        names = {}
        for domain, seq in [('POINT', bm.verts), ('EDGE', bm.edges),
                            ('FACE', bm.faces), ('CORNER', bm.loops)]:
            name = '_bt_collapsed_source_' + domain
            while seq.layers.int.get(name):
                name += '_'
            layer = seq.layers.int.new(name)
            names[domain] = name
            elements = ([loop for face in bm.faces for loop in face.loops]
                        if domain == 'CORNER' else seq)
            for index, element in enumerate(elements):
                element[layer] = index

        deleted_faces = set()
        deleted_corners = set()
        for face_ids, pair_ids in networks:
            trial = bm.copy()
            try:
                vl = trial.verts.layers.int[names['POINT']]
                el = trial.edges.layers.int[names['EDGE']]
                fl = trial.faces.layers.int[names['FACE']]
                cl = trial.loops.layers.int[names['CORNER']]
                faces = {face for face in trial.faces if face[fl] in face_ids}
                if len(faces) != len(face_ids) or any(face.calc_area() != 0.0 for face in faces):
                    raise ValueError('Candidate faces changed')
                by_id = {vertex[vl]: vertex for vertex in trial.verts}
                target = {}
                for keep, drop in pair_ids:
                    a, b = by_id[keep], by_id[drop]
                    if (tuple(a.co) != tuple(b.co)
                            or any(values[keep] != values[drop]
                                   for domain, _, _, values in saved.values()
                                   if domain == 'POINT')
                            or weights[keep] != weights[drop]):
                        raise ValueError('Conflicting point attributes or weights')
                    if not set(a.link_faces).intersection(b.link_faces).issubset(faces):
                        raise ValueError('Weld would alter a retained face')
                    target[b] = a
                def merged(vertex):
                    return target.get(vertex, vertex)
                edge_sources = {}
                for edge in trial.edges:
                    outside = any(face not in faces for face in edge.link_faces)
                    key = frozenset(merged(vertex) for vertex in edge.verts)
                    if len(key) < 2:
                        if outside or edge.is_wire:
                            raise ValueError('Weld would collapse a retained edge')
                        continue
                    if outside:
                        prior = edge_sources.get(key)
                        if prior is not None and any(values[prior] != values[edge[el]]
                                                     for domain, _, _, values in saved.values()
                                                     if domain == 'EDGE'):
                            raise ValueError('Conflicting merged edge attributes')
                        edge_sources[key] = edge[el]
                before = _topology(trial)
                gone_corners = {loop[cl] for face in faces for loop in face.loops}
                bmesh.ops.delete(trial, geom=list(faces), context='FACES')
                surviving = {vertex[vl]: vertex for vertex in trial.verts}
                weld = {surviving[drop]: surviving[keep] for keep, drop in pair_ids
                        if keep in surviving and drop in surviving}
                if weld:
                    bmesh.ops.weld_verts(trial, targetmap=weld)
                after = _topology(trial)
                if any(after[key] > before[key] for key in before):
                    raise ValueError('Cleanup increased a non-manifold or boundary category')
                retained = [face[fl] for face in trial.faces]
                if (len(retained) != len(mesh.polygons) - len(deleted_faces) - len(face_ids)
                        or len(set(retained)) != len(retained)
                        or set(retained) != set(range(len(mesh.polygons))) - deleted_faces - set(face_ids)):
                    raise ValueError('A retained face was removed or duplicated')
                retained_corners = [loop[cl] for face in trial.faces for loop in face.loops]
                if (len(set(retained_corners)) != len(retained_corners)
                        or set(retained_corners) != set(range(len(mesh.loops)))
                        - deleted_corners - gone_corners):
                    raise ValueError('A retained corner was removed or duplicated')
                for face in trial.faces:
                    source = mesh.polygons[face[fl]]
                    if ([tuple(loop.vert.co) for loop in face.loops]
                            != [tuple(mesh.vertices[index].co) for index in source.vertices]):
                        raise ValueError('Retained face geometry changed')
                bm.free()
                bm = trial
                trial = None
                deleted_faces.update(face_ids)
                deleted_corners.update(gone_corners)
                report['networks_removed'] += 1
                report['faces_removed'] += len(face_ids)
            except (ValueError, KeyError) as exc:
                report['rejected'].append(f'Face {face_ids[0]}: {exc}')
            finally:
                if trial is not None:
                    trial.free()
        if not report['networks_removed']:
            return None, report

        mappings = {}
        for domain, seq in [('POINT', bm.verts), ('EDGE', bm.edges),
                            ('FACE', bm.faces), ('CORNER', bm.loops)]:
            layer = seq.layers.int[names[domain]]
            elements = ([loop for face in bm.faces for loop in face.loops]
                        if domain == 'CORNER' else seq)
            mappings[domain] = [element[layer] for element in elements]
            seq.layers.int.remove(layer)
        report['vertices_removed'] = len(mesh.vertices) - len(mappings['POINT'])
        report['edges_removed'] = len(mesh.edges) - len(mappings['EDGE'])
        result = mesh.copy()
        bm.to_mesh(result)
        result.update(calc_edges_loose=True)
        for name, (domain, data_type, props, values) in saved.items():
            attr = result.attributes.get(name)
            if attr is None:
                attr = result.attributes.new(name=name, type=data_type, domain=domain)
            if attr.domain != domain or attr.data_type != data_type:
                raise ValueError('Attribute schema changed: ' + name)
            for index, source in enumerate(mappings[domain]):
                for prop, value in zip(props, values[source]):
                    setattr(attr.data[index], prop, value)
        if normals is not None:
            result.normals_split_custom_set([normals[source] for source in mappings['CORNER']])
            for index, source in enumerate(mappings['EDGE']):
                result.edges[index].use_edge_sharp = mesh.edges[source].use_edge_sharp
        result.update()
        actual = attributes(result)
        for name, (domain, data_type, props, values) in saved.items():
            expected = [values[source] for source in mappings[domain]]
            if actual.get(name) != (domain, data_type, props, expected):
                raise ValueError('Attribute verification failed: ' + name)
        for vertex, source in zip(result.vertices, mappings['POINT']):
            old = mesh.vertices[source]
            if (tuple(vertex.co) != tuple(old.co)
                    or tuple(sorted((g.group, g.weight) for g in vertex.groups)) != weights[source]):
                raise ValueError('Vertex position or weights changed')
        for face, source in zip(result.polygons, mappings['FACE']):
            old = mesh.polygons[source]
            if ([tuple(result.vertices[index].co) for index in face.vertices]
                    != [tuple(mesh.vertices[index].co) for index in old.vertices]):
                raise ValueError('Retained face geometry changed')
        if normals is not None and any(
                not custom_normal_is_close(normal.vector, Vector(normals[source]))
                for normal, source in zip(result.corner_normals, mappings['CORNER'])):
            raise ValueError('Custom normal verification failed')
        if [layer.name for layer in result.uv_layers] != uv_names:
            raise ValueError('UV layers changed')
        if uv_names:
            result.uv_layers.active_index = uv_active
            for layer, active in zip(result.uv_layers, uv_render):
                layer.active_render = active
        if (len(result.polygons) != len(mesh.polygons) - report['faces_removed']
                or any(face.area > 0.0 and source in deleted_faces
                       for face, source in zip(result.polygons, mappings['FACE']))):
            raise ValueError('Unexpected final face count')
        return result, report
    except Exception:
        if result is not None:
            bpy.data.meshes.remove(result)
        raise
    finally:
        bm.free()


class OBJECT_OT_remove_collapsed_boundary_faces(bpy.types.Operator):
    bl_idname = 'object.remove_collapsed_boundary_faces'
    bl_label = 'Remove Collapsed Boundary Faces'
    bl_description = 'Safely remove exact zero-area boundary ribbons and weld their coincident vertices'
    bl_options = {'REGISTER', 'UNDO'}
    scope: bpy.props.EnumProperty(name='Target', items=[
        ('SELECTED', 'Selected Meshes', ''), ('ACTIVE', 'Active Mesh', '')],
        default='SELECTED')

    @classmethod
    def poll(cls, context):
        return (context.mode == 'OBJECT' and
                (any(obj.type == 'MESH' for obj in context.selected_objects)
                 or (context.active_object is not None
                     and context.active_object.type == 'MESH')))

    def execute(self, context):
        targets = ([context.active_object] if self.scope == 'ACTIVE'
                   else context.selected_objects)
        targets = sorted((obj for obj in targets if obj and obj.type == 'MESH'),
                         key=lambda obj: obj.name)
        changed = networks = faces = failed = 0
        for obj in targets:
            try:
                result, report = prepare_collapsed_boundary_cleanup(obj)
                for warning in report['rejected']:
                    self.report({'WARNING'}, f'{obj.name}: {warning}')
                if result is not None:
                    try:
                        obj.data = result
                    except Exception:
                        bpy.data.meshes.remove(result)
                        raise
                    result.name += '_CollapsedBoundaryRemoved'
                    changed += 1
                    networks += report['networks_removed']
                    faces += report['faces_removed']
            except Exception as exc:
                failed += 1
                self.report({'WARNING'}, f'{obj.name}: {exc}')
        self.report({'WARNING'} if failed else {'INFO'},
                    f'{changed} mesh(es); {networks} networks / {faces} faces removed; '
                    f'{failed} meshes failed')
        return {'FINISHED'} if changed or not failed else {'CANCELLED'}
