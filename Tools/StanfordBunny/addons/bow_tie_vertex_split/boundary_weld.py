"""Conservative exact-coordinate welding of collapsed four-edge boundaries."""
import bmesh
import bpy

TOPOLOGY = {'position', '.edge_verts', '.corner_vert', '.corner_edge', 'custom_normal'}


def frozen(value):
    if isinstance(value, (str, bytes, int, float, bool)):
        return value
    return tuple(frozen(x) for x in value)


def attributes(mesh, include_custom_normals=False):
    result = {}
    for attr in mesh.attributes:
        if attr.name in TOPOLOGY and not (include_custom_normals and attr.name == 'custom_normal'):
            continue
        if not len(attr.data):
            continue
        props = [p.identifier for p in attr.data[0].bl_rna.properties
                 if p.identifier != 'rna_type' and not p.is_readonly]
        if attr.data_type in {'FLOAT_COLOR', 'BYTE_COLOR'}:
            props = ['color' if attr.data_type == 'FLOAT_COLOR' else 'color_srgb']
        if not props:
            raise ValueError('Unsupported attribute: ' + attr.name)
        result[attr.name] = (attr.domain, attr.data_type, props,
                            [tuple(frozen(getattr(item, p)) for p in props) for item in attr.data])
    return result


def boundary_quads(bm):
    """Only complete boundary components with 4 edges and degree 2 everywhere."""
    pending = {e for e in bm.edges if e.is_boundary}
    found = []
    while pending:
        seed = pending.pop()
        component = {seed}
        stack = [seed]
        while stack:
            edge = stack.pop()
            for v in edge.verts:
                for other in v.link_edges:
                    if other in pending:
                        pending.remove(other)
                        component.add(other)
                        stack.append(other)
        if len(component) != 4:
            continue
        verts = {v for e in component for v in e.verts}
        if len(verts) != 4 or any(sum(e in component for e in v.link_edges) != 2 for v in verts):
            continue
        start = min(verts, key=lambda v: v.index)
        ring = [start]
        previous = None
        current = start
        for _ in range(3):
            edge = min((e for e in current.link_edges if e in component and e != previous),
                       key=lambda e: e.index)
            current = edge.other_vert(current)
            ring.append(current)
            previous = edge
        pairs = [(ring[i], ring[i + 2]) for i in range(2)
                 if tuple(ring[i].co) == tuple(ring[i + 2].co)]
        if len(pairs) == 1 and all(e.calc_length() > 0 for e in component):
            found.append((ring, pairs[0]))
    return sorted(found, key=lambda item: min(v.index for v in item[0]))


def prepare_boundary_weld(obj):
    """Return (new mesh or None, report). No source mesh or object is modified."""
    mesh = obj.data
    if mesh.shape_keys or any(m.type == 'MULTIRES' for m in obj.modifiers):
        raise ValueError('Shape keys / Multires are not supported')
    if obj.library or obj.override_library or mesh.library or mesh.override_library:
        raise ValueError('Linked / override data is not supported')
    if len(mesh.skin_vertices):
        raise ValueError('Boundary weld does not support Skin vertex data')
    bm = bmesh.new()
    result = None
    report = {'candidates': 0, 'welded': 0, 'rejected': []}
    try:
        bm.from_mesh(mesh)
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()
        candidates = boundary_quads(bm)
        report['candidates'] = len(candidates)
        if not candidates:
            return None, report
        candidate_ids = [([v.index for v in ring], [v.index for v in pair]) for ring, pair in candidates]
        saved = attributes(mesh)
        weights = [tuple(sorted((g.group, g.weight) for g in v.groups)) for v in mesh.vertices]
        normals = [tuple(n.vector) for n in mesh.corner_normals] if mesh.has_custom_normals else None
        uv_names = [layer.name for layer in mesh.uv_layers]
        uv_active = mesh.uv_layers.active_index
        uv_render = [layer.active_render for layer in mesh.uv_layers]
        # Source IDs survive weld's face/loop replacement and index compaction.
        names = {}
        for domain, seq in [('POINT', bm.verts), ('EDGE', bm.edges), ('FACE', bm.faces), ('CORNER', bm.loops)]:
            name = '_btw_source_' + domain
            while seq.layers.int.get(name):
                name += '_'
            layer = seq.layers.int.new(name)
            names[domain] = name
            elements = [l for f in bm.faces for l in f.loops] if domain == 'CORNER' else seq
            for index, element in enumerate(elements):
                element[layer] = index

        def compatible(domain, left, right):
            return all(values[left] == values[right]
                       for dom, _, _, values in saved.values() if dom == domain)

        for ring_ids, pair_ids in candidate_ids:
            trial = bm.copy()
            try:
                vl = trial.verts.layers.int[names['POINT']]
                el = trial.edges.layers.int[names['EDGE']]
                by_id = {v[vl]: v for v in trial.verts}
                a, b = [by_id[i] for i in pair_ids]
                ring = [by_id[i] for i in ring_ids]
                if not compatible('POINT', a[vl], b[vl]) or weights[a[vl]] != weights[b[vl]]:
                    raise ValueError('Conflicting point attributes or vertex weights')
                if set(a.link_faces) & set(b.link_faces):
                    raise ValueError('Both vertices belong to an existing face')
                if len(a.link_edges) != len(a.link_faces) + 1 or len(b.link_edges) != len(b.link_faces) + 1:
                    raise ValueError('Unexpected vertex fan / wire connection')
                # Every duplicate edge must have identical edge-domain attributes.
                for e in a.link_edges:
                    peer = trial.edges.get((b, e.other_vert(a)))
                    if peer and not compatible('EDGE', e[el], peer[el]):
                        raise ValueError('Conflicting edge attributes')
                before = (len(trial.verts), len(trial.edges), len(trial.faces),
                          sum(len(f.loops) for f in trial.faces), sum(e.is_boundary for e in trial.edges))
                bmesh.ops.weld_verts(trial, targetmap={a: b})
                after = (len(trial.verts), len(trial.edges), len(trial.faces),
                         sum(len(f.loops) for f in trial.faces), sum(e.is_boundary for e in trial.edges))
                if after != (before[0]-1, before[1]-2, before[2], before[3], before[4]-4):
                    raise ValueError('Unexpected topology change')
                if not all(v.is_manifold for v in ring if v.is_valid):
                    raise ValueError('Resulting local vertices are not manifold')
                if any(not e.is_contiguous for v in ring if v.is_valid for e in v.link_edges):
                    raise ValueError('Resulting local edge orientation is inconsistent')
                bm.free()
                bm = trial
                trial = None
                report['welded'] += 1
            except (ValueError, KeyError) as exc:
                report['rejected'].append({'vertices': pair_ids, 'reason': str(exc)})
            finally:
                if trial is not None:
                    trial.free()
        if not report['welded']:
            return None, report
        mappings = {}
        for domain, seq in [('POINT', bm.verts), ('EDGE', bm.edges), ('FACE', bm.faces), ('CORNER', bm.loops)]:
            layer = seq.layers.int[names[domain]]
            elements = [l for f in bm.faces for l in f.loops] if domain == 'CORNER' else seq
            mappings[domain] = [item[layer] for item in elements]
            seq.layers.int.remove(layer)
        # Face/corner identity must be one-to-one. No old face may disappear.
        if sorted(mappings['FACE']) != list(range(len(mesh.polygons))) or sorted(mappings['CORNER']) != list(range(len(mesh.loops))):
            raise ValueError('Face / corner identity was not preserved')
        result = mesh.copy()
        bm.to_mesh(result)
        result.update()
        # Restore ALL non-topological generic attributes explicitly, including
        # types that BMesh cannot round-trip, using their original-domain IDs.
        for name, (domain, typ, props, values) in saved.items():
            attr = result.attributes.get(name)
            if attr is None:
                attr = result.attributes.new(name=name, type=typ, domain=domain)
            if attr.domain != domain or attr.data_type != typ:
                raise ValueError('Attribute schema changed: ' + name)
            for i, source in enumerate(mappings[domain]):
                for prop, value in zip(props, values[source]):
                    setattr(attr.data[i], prop, value)
        if normals is not None:
            result.normals_split_custom_set([normals[i] for i in mappings['CORNER']])
        result.update()
        for i, source in enumerate(mappings['POINT']):
            v = result.vertices[i]
            if tuple(v.co) != tuple(mesh.vertices[source].co) or tuple(sorted((g.group,g.weight) for g in v.groups)) != weights[source]:
                raise ValueError('Vertex position / weights changed')
        actual = attributes(result)
        for name, (domain, typ, props, values) in saved.items():
            if actual.get(name) != (domain, typ, props, [values[i] for i in mappings[domain]]):
                raise ValueError('Attribute verification failed: ' + name)
        if normals is not None:
            from mathutils import Vector
            if any((n.vector - Vector(normals[source])).length > 2e-4
                   for n, source in zip(result.corner_normals, mappings['CORNER'])):
                raise ValueError('Custom normal verification failed')
        if [layer.name for layer in result.uv_layers] != uv_names:
            raise ValueError('UV layers changed')
        result.uv_layers.active_index = uv_active
        for layer, active in zip(result.uv_layers, uv_render):
            layer.active_render = active
        # Same face-corner positions, same order, same face area: geometrically
        # no existing face is modified even though two vertex IDs now coincide.
        for face, source in zip(result.polygons, mappings['FACE']):
            old = mesh.polygons[source]
            if [tuple(result.vertices[i].co) for i in face.vertices] != [tuple(mesh.vertices[i].co) for i in old.vertices]:
                raise ValueError('Existing face geometry changed')
        return result, report
    except Exception:
        if result is not None:
            bpy.data.meshes.remove(result)
        raise
    finally:
        bm.free()


class OBJECT_OT_weld_collapsed_boundaries(bpy.types.Operator):
    bl_idname = 'object.weld_collapsed_boundaries'
    bl_label = 'Weld Collapsed Boundary Quads'
    bl_description = 'Weld exact coincident opposite vertices in isolated 4-edge boundary loops; run before F'
    bl_options = {'REGISTER', 'UNDO'}

    scope: bpy.props.EnumProperty(name='Target', items=[
        ('SELECTED', 'Selected Meshes', ''), ('ACTIVE', 'Active Mesh', '')], default='SELECTED')

    @classmethod
    def poll(cls, context):
        return context.mode == 'OBJECT' and (any(o.type == 'MESH' for o in context.selected_objects)
                or (context.active_object is not None and context.active_object.type == 'MESH'))

    def execute(self, context):
        targets = [context.active_object] if self.scope == 'ACTIVE' else context.selected_objects
        total = changed = rejected = skipped = 0
        for obj in sorted((o for o in targets if o and o.type == 'MESH'), key=lambda o:o.name):
            try:
                result, report = prepare_boundary_weld(obj)
                rejected += len(report['rejected'])
                for item in report['rejected']:
                    self.report({'WARNING'}, f"{obj.name} {item['vertices']}: {item['reason']}")
                if result is not None:
                    try:
                        obj.data = result
                    except Exception:
                        bpy.data.meshes.remove(result)
                        raise
                    result.name += '_BoundaryWeld'
                    changed += 1
                    total += report['welded']
            except Exception as exc:
                skipped += 1
                self.report({'WARNING'}, f'{obj.name}: {exc}')
        self.report({'WARNING'} if rejected or skipped else {'INFO'},
                    f'{changed} mesh(es); {total} loops welded; {rejected} candidates rejected; {skipped} meshes skipped')
        return {'FINISHED'} if changed or not skipped else {'CANCELLED'}
