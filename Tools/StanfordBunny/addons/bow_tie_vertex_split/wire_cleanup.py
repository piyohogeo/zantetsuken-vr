"""Remove face-less edges and only vertices orphaned by that removal."""
import bpy
import bmesh
from mathutils import Vector
from .boundary_weld import attributes


def prepare_wire_cleanup(obj):
    mesh = obj.data
    report = {'edges_removed': 0, 'vertices_removed': 0}
    if mesh.shape_keys or any(m.type == 'MULTIRES' for m in obj.modifiers):
        raise ValueError('Shape keys / Multires are not supported')
    if obj.library or obj.override_library or mesh.library or mesh.override_library:
        raise ValueError('Linked / override data is not supported')
    if len(mesh.skin_vertices):
        raise ValueError('Wire cleanup does not support Skin vertex data')
    bm = bmesh.new()
    result = None
    try:
        bm.from_mesh(mesh)
        if not any(e.is_wire for e in bm.edges):
            return None, report
        saved = attributes(mesh, include_custom_normals=True)
        normals = [tuple(n.vector) for n in mesh.corner_normals] if mesh.has_custom_normals else None
        uv_names = [uv.name for uv in mesh.uv_layers]
        uv_active = mesh.uv_layers.active_index
        uv_render = [uv.active_render for uv in mesh.uv_layers]
        names = {}
        for domain, seq in [('POINT', bm.verts), ('EDGE', bm.edges)]:
            name = '_btw_wire_source_' + domain
            while seq.layers.int.get(name):
                name += '_'
            layer = seq.layers.int.new(name)
            names[domain] = name
            for i, item in enumerate(seq):
                item[layer] = i
        wires = [e for e in bm.edges if e.is_wire]
        touched = {v for e in wires for v in e.verts}
        report['edges_removed'] = len(wires)
        for edge in wires:
            bm.edges.remove(edge)
        # Never remove pre-existing isolated vertices or vertices used by faces.
        orphans = [v for v in touched if not v.link_edges and not v.link_faces]
        report['vertices_removed'] = len(orphans)
        for vert in orphans:
            bm.verts.remove(vert)
        mappings = {'FACE': list(range(len(mesh.polygons))), 'CORNER': list(range(len(mesh.loops)))}
        for domain, seq in [('POINT', bm.verts), ('EDGE', bm.edges)]:
            layer = seq.layers.int[names[domain]]
            mappings[domain] = [item[layer] for item in seq]
            seq.layers.int.remove(layer)
        result = mesh.copy()
        bm.to_mesh(result)
        result.update(calc_edges_loose=True)
        if len(result.polygons) != len(mesh.polygons) or len(result.loops) != len(mesh.loops):
            raise ValueError('Face / corner count changed')
        # Check actual vertex identity, not just equal coordinates.
        for new, old in zip(result.polygons, mesh.polygons):
            if [mappings['POINT'][v] for v in new.vertices] != list(old.vertices):
                raise ValueError('Face order or topology changed')
        for edge, source in zip(result.edges, mappings['EDGE']):
            if [mappings['POINT'][v] for v in edge.vertices] != list(mesh.edges[source].vertices):
                raise ValueError('Retained edge topology changed')
        for name, (domain, typ, props, values) in saved.items():
            attr = result.attributes.get(name)
            if attr is None:
                attr = result.attributes.new(name=name, type=typ, domain=domain)
            if attr.domain != domain or attr.data_type != typ:
                raise ValueError('Attribute schema changed: ' + name)
            for i, source in enumerate(mappings[domain]):
                for prop, value in zip(props, values[source]):
                    setattr(attr.data[i], prop, value)
        if normals is not None and 'custom_normal' not in saved:
            result.normals_split_custom_set(normals)
        result.update()
        actual = attributes(result, include_custom_normals=True)
        for name, (domain, typ, props, values) in saved.items():
            expected = [values[i] for i in mappings[domain]]
            # attributes() omits empty domains.
            if expected and actual.get(name) != (domain, typ, props, expected):
                raise ValueError('Attribute verification failed: ' + name)
        for v, source in zip(result.vertices, mappings['POINT']):
            old = mesh.vertices[source]
            if tuple(v.co) != tuple(old.co) or sorted((g.group,g.weight) for g in v.groups) != sorted((g.group,g.weight) for g in old.groups):
                raise ValueError('Vertex position / weights changed')
        if normals is not None:
            error = max(((n.vector - Vector(old)).length for n, old in zip(result.corner_normals, normals)), default=0)
            if error > 2e-4:
                raise ValueError(f'Custom normal verification failed (maximum vector error {error:.6g})')
        if [uv.name for uv in result.uv_layers] != uv_names:
            raise ValueError('UV layers changed')
        if uv_names:
            result.uv_layers.active_index = uv_active
            for uv, active in zip(result.uv_layers, uv_render):
                uv.active_render = active
        if (len(result.vertices), len(result.edges)) != (len(mesh.vertices)-report['vertices_removed'], len(mesh.edges)-report['edges_removed']):
            raise ValueError('Unexpected element counts')
        return result, report
    except Exception:
        if result is not None:
            bpy.data.meshes.remove(result)
        raise
    finally:
        bm.free()


class OBJECT_OT_remove_wire_geometry(bpy.types.Operator):
    bl_idname = 'object.remove_wire_geometry'
    bl_label = 'Remove Wire Edges'
    bl_description = 'Remove all face-less edges and vertices orphaned by their removal; keep all faces'
    bl_options = {'REGISTER', 'UNDO'}

    scope: bpy.props.EnumProperty(name='Target', items=[
        ('SELECTED', 'Selected Meshes', ''), ('ACTIVE', 'Active Mesh', '')], default='SELECTED')

    @classmethod
    def poll(cls, context):
        return context.mode == 'OBJECT' and (any(o.type == 'MESH' for o in context.selected_objects)
            or (context.active_object is not None and context.active_object.type == 'MESH'))

    def execute(self, context):
        targets = [context.active_object] if self.scope == 'ACTIVE' else context.selected_objects
        targets = sorted((o for o in targets if o and o.type == 'MESH'), key=lambda o:o.name)
        if not targets:
            self.report({'WARNING'}, 'No target mesh objects')
            return {'CANCELLED'}
        changed = edges = verts = skipped = 0
        for obj in targets:
            try:
                result, report = prepare_wire_cleanup(obj)
                if result is not None:
                    try:
                        obj.data = result
                    except Exception:
                        bpy.data.meshes.remove(result)
                        raise
                    result.name += '_WireClean'
                    changed += 1
                    edges += report['edges_removed']
                    verts += report['vertices_removed']
            except Exception as exc:
                skipped += 1
                self.report({'WARNING'}, f'{obj.name}: {exc}')
        self.report({'WARNING'} if skipped else {'INFO'},
                    f'{changed} mesh(es); {edges} wire edges removed; {verts} orphan vertices removed; {skipped} meshes skipped')
        return {'FINISHED'} if changed or not skipped else {'CANCELLED'}
