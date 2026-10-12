"""Remove vertices that have neither edges nor faces."""
import bpy
import bmesh
from mathutils import Vector
from .boundary_weld import attributes


def prepare_isolated_vertex_cleanup(obj):
    mesh=obj.data
    report={'vertices_removed':0}
    if mesh.shape_keys or any(m.type=='MULTIRES' for m in obj.modifiers):
        raise ValueError('Shape keys / Multires are not supported')
    if obj.library or obj.override_library or mesh.library or mesh.override_library:
        raise ValueError('Linked / override data is not supported')
    if len(mesh.skin_vertices):raise ValueError('Isolated vertex cleanup does not support Skin vertex data')
    bm=bmesh.new();result=None
    try:
        bm.from_mesh(mesh);bm.verts.ensure_lookup_table()
        isolated=[v for v in bm.verts if not v.link_edges and not v.link_faces]
        if not isolated:return None,report
        isolated_ids={v.index for v in isolated}
        report['vertices_removed']=len(isolated)
        saved=attributes(mesh,include_custom_normals=True)
        normals=[tuple(n.vector) for n in mesh.corner_normals] if mesh.has_custom_normals else None
        uv_names=[u.name for u in mesh.uv_layers];uv_active=mesh.uv_layers.active_index
        uv_render=[u.active_render for u in mesh.uv_layers]
        name='_bt_isolated_source_POINT'
        while bm.verts.layers.int.get(name):name+='_'
        layer=bm.verts.layers.int.new(name)
        for i,v in enumerate(bm.verts):v[layer]=i
        # Layer creation may invalidate stored BMesh element references.
        for v in list(bm.verts):
            if v.index in isolated_ids:bm.verts.remove(v)
        point_sources=[v[layer] for v in bm.verts];bm.verts.layers.int.remove(layer)
        mappings={'POINT':point_sources,'EDGE':list(range(len(mesh.edges))),
                  'FACE':list(range(len(mesh.polygons))),'CORNER':list(range(len(mesh.loops)))}
        result=mesh.copy();bm.to_mesh(result);result.update(calc_edges_loose=True)
        if (len(result.vertices),len(result.edges),len(result.polygons),len(result.loops)) != (
                len(mesh.vertices)-len(isolated),len(mesh.edges),len(mesh.polygons),len(mesh.loops)):
            raise ValueError('Unexpected element counts')
        for face,old in zip(result.polygons,mesh.polygons):
            if [point_sources[v] for v in face.vertices]!=list(old.vertices):
                raise ValueError('Existing face topology changed')
        for edge,old in zip(result.edges,mesh.edges):
            if [point_sources[v] for v in edge.vertices]!=list(old.vertices):
                raise ValueError('Existing edge topology changed')
        for name,(domain,typ,props,values) in saved.items():
            attr=result.attributes.get(name)
            if attr is None:attr=result.attributes.new(name=name,type=typ,domain=domain)
            for i,source in enumerate(mappings[domain]):
                for prop,value in zip(props,values[source]):setattr(attr.data[i],prop,value)
        if normals is not None and 'custom_normal' not in saved:
            result.normals_split_custom_set(normals)
        result.update()
        actual=attributes(result,include_custom_normals=True)
        for name,(domain,typ,props,values) in saved.items():
            expected=[values[i] for i in mappings[domain]]
            if expected and actual.get(name)!=(domain,typ,props,expected):
                raise ValueError('Attribute verification failed: '+name)
        for v,source in zip(result.vertices,point_sources):
            old=mesh.vertices[source]
            if tuple(v.co)!=tuple(old.co) or sorted((g.group,g.weight) for g in v.groups)!=sorted((g.group,g.weight) for g in old.groups):
                raise ValueError('Vertex position / weights changed')
        if normals is not None:
            error=max(((n.vector-Vector(old)).length for n,old in zip(result.corner_normals,normals)),default=0)
            if error>2e-4:raise ValueError(f'Custom normal verification failed (maximum vector error {error:.6g})')
        if [u.name for u in result.uv_layers]!=uv_names:raise ValueError('UV layers changed')
        if uv_names:
            result.uv_layers.active_index=uv_active
            for uv,active in zip(result.uv_layers,uv_render):uv.active_render=active
        if any(not v.link_edges and not v.link_faces for v in bm.verts):
            raise ValueError('Isolated vertices remain')
        return result,report
    except Exception:
        if result is not None:bpy.data.meshes.remove(result)
        raise
    finally:bm.free()


class OBJECT_OT_remove_isolated_vertices(bpy.types.Operator):
    bl_idname='object.remove_isolated_vertices'
    bl_label='Remove Isolated Vertices'
    bl_description='Remove vertices with no edges and no faces; preserve all connected geometry'
    bl_options={'REGISTER','UNDO'}
    scope:bpy.props.EnumProperty(name='Target',items=[('SELECTED','Selected Meshes',''),('ACTIVE','Active Mesh','')],default='SELECTED')
    @classmethod
    def poll(cls,context):
        return context.mode=='OBJECT' and (any(o.type=='MESH' for o in context.selected_objects) or
            (context.active_object is not None and context.active_object.type=='MESH'))
    def execute(self,context):
        targets=[context.active_object] if self.scope=='ACTIVE' else context.selected_objects
        targets=sorted((o for o in targets if o and o.type=='MESH'),key=lambda o:o.name)
        changed=vertices=failed=0
        for obj in targets:
            try:
                result,report=prepare_isolated_vertex_cleanup(obj)
                if result is not None:
                    try:obj.data=result
                    except Exception:bpy.data.meshes.remove(result);raise
                    result.name+='_IsolatedClean';changed+=1;vertices+=report['vertices_removed']
            except Exception as exc:failed+=1;self.report({'WARNING'},f'{obj.name}: {exc}')
        self.report({'WARNING'} if failed else {'INFO'},
                    f'{changed} mesh(es); {vertices} isolated vertices removed; {failed} meshes failed')
        return {'FINISHED'} if changed or not failed else {'CANCELLED'}
