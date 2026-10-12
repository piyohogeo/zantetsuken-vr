"""Remove the minimum face-patch set that hangs from multi-face edges by boundaries."""
import bpy
import bmesh
from mathutils import Vector
from .boundary_weld import attributes


def _patches(bm):
    """Face components separated at boundary, wire and multi-face edges."""
    multi = {e for e in bm.edges if len(e.link_faces) > 2}
    seeds = {f for e in multi for f in e.link_faces}
    seen = set()
    result = []
    for seed in sorted(seeds, key=lambda f:f.index):
        if seed in seen:
            continue
        part = {seed}; seen.add(seed); stack = [seed]
        while stack:
            face = stack.pop()
            for edge in face.edges:
                if len(edge.link_faces) != 2:
                    continue
                for other in edge.link_faces:
                    if other not in seen:
                        seen.add(other); part.add(other); stack.append(other)
        roots = {e for f in part for e in f.edges if e in multi}
        rims = {e for f in part for e in f.edges if e.is_boundary}
        result.append({'faces':part, 'roots':roots, 'rims':rims})
    return multi, result


def _minimum_boundary_patches(multi, patches):
    """Exact branch-and-bound set multicover, minimizing faces then area."""
    candidates = [p for p in patches if p['roots'] and p['rims']]
    need = {e:len(e.link_faces)-2 for e in multi}
    owners = {e:[i for i,p in enumerate(candidates) if e in p['roots']] for e in multi}
    if any(len(owners[e]) < need[e] for e in multi):
        return None
    counts = {e:0 for e in multi}; best = None; best_score = None
    def search(chosen):
        nonlocal best,best_score
        score=(sum(len(candidates[i]['faces']) for i in chosen),
               sum(f.calc_area() for i in chosen for f in candidates[i]['faces']),
               tuple(sorted(min(f.index for f in candidates[i]['faces']) for i in chosen)))
        if best_score is not None and score[:2] >= best_score[:2]:
            return
        pending=[e for e in multi if counts[e] < need[e]]
        if not pending:
            # A patch spanning several roots must not remove every incident face.
            if all(sum(f not in set().union(*(candidates[i]['faces'] for i in chosen))
                           for f in e.link_faces) >= 1 for e in multi):
                best=set(chosen);best_score=score
            return
        edge=min(pending,key=lambda e:(sum(i not in chosen for i in owners[e]),e.index))
        for i in owners[edge]:
            if i in chosen:continue
            chosen.add(i)
            for root in candidates[i]['roots']:counts[root]+=1
            search(chosen)
            for root in candidates[i]['roots']:counts[root]-=1
            chosen.remove(i)
    search(set())
    return None if best is None else [candidates[i] for i in sorted(best)]


def _fan_count(vertex):
    pending=set(vertex.link_faces);count=0
    while pending:
        count+=1;stack=[pending.pop()]
        while stack:
            face=stack.pop()
            for edge in face.edges:
                if vertex not in edge.verts:continue
                for other in edge.link_faces:
                    if other in pending:pending.remove(other);stack.append(other)
    return count


def prepare_boundary_flap_cleanup(obj):
    """Return a verified mesh copy and report; never mutate the source."""
    mesh=obj.data
    report={'patches_removed':0,'faces_removed':0,'edges_removed':0,'vertices_removed':0,
            'sharp_edges_added':0,'rejected':[]}
    if mesh.shape_keys or any(m.type=='MULTIRES' for m in obj.modifiers):
        raise ValueError('Shape keys / Multires are not supported')
    if obj.library or obj.override_library or mesh.library or mesh.override_library:
        raise ValueError('Linked / override data is not supported')
    if len(mesh.skin_vertices):raise ValueError('Boundary flap cleanup does not support Skin vertex data')
    bm=bmesh.new();result=None
    try:
        bm.from_mesh(mesh);bm.verts.ensure_lookup_table();bm.edges.ensure_lookup_table();bm.faces.ensure_lookup_table()
        multi,patches=_patches(bm)
        if not multi:return None,report
        root_edge_ids={e.index for e in multi}
        chosen=_minimum_boundary_patches(multi,patches)
        if not chosen:
            report['rejected'].append('No boundary-attached patch set can resolve all multi-face edges')
            return None,report
        chosen_face_ids=[{f.index for f in p['faces']} for p in chosen]
        # Packed custom-normal values depend on adjacent topology. Preserve the
        # decoded corner vectors instead when deleting faces changes that basis.
        saved=attributes(mesh)
        normals=[tuple(n.vector) for n in mesh.corner_normals] if mesh.has_custom_normals else None
        uv_names=[u.name for u in mesh.uv_layers];uv_active=mesh.uv_layers.active_index
        uv_render=[u.active_render for u in mesh.uv_layers]
        names={}
        for domain,seq in [('POINT',bm.verts),('EDGE',bm.edges),('FACE',bm.faces),('CORNER',bm.loops)]:
            name='_bt_flap_source_'+domain
            while seq.layers.int.get(name):name+='_'
            layer=seq.layers.int.new(name);names[domain]=name
            elements=[l for f in bm.faces for l in f.loops] if domain=='CORNER' else seq
            for i,item in enumerate(elements):item[layer]=i
        before=(len(bm.verts),len(bm.edges),len(bm.faces),sum(e.is_boundary for e in bm.edges),
                sum(len(e.link_faces)==2 and not e.is_contiguous for e in bm.edges))
        removed={f for f in bm.faces if any(f.index in ids for ids in chosen_face_ids)}
        report['patches_removed']=len(chosen);report['faces_removed']=len(removed)
        bmesh.ops.delete(bm,geom=list(removed),context='FACES')
        if any(len(e.link_faces)>2 for e in bm.edges):raise ValueError('Multi-face edges remain after trial deletion')
        if any(e.is_wire for e in bm.edges):raise ValueError('Trial deletion created wire edges')
        if sum(e.is_boundary for e in bm.edges)>before[3]:raise ValueError('Trial deletion increased boundary edges')
        if sum(len(e.link_faces)==2 and not e.is_contiguous for e in bm.edges)>before[4]:
            raise ValueError('Trial deletion created non-contiguous edges')
        if any(_fan_count(v)>1 for v in bm.verts):raise ValueError('Trial deletion created bow-tie vertices')
        mappings={}
        for domain,seq in [('POINT',bm.verts),('EDGE',bm.edges),('FACE',bm.faces),('CORNER',bm.loops)]:
            layer=seq.layers.int[names[domain]]
            elements=[l for f in bm.faces for l in f.loops] if domain=='CORNER' else seq
            mappings[domain]=[item[layer] for item in elements];seq.layers.int.remove(layer)
        report['edges_removed']=before[1]-len(bm.edges);report['vertices_removed']=before[0]-len(bm.verts)
        result=mesh.copy();bm.to_mesh(result);result.update(calc_edges_loose=True)
        for name,(domain,typ,props,values) in saved.items():
            attr=result.attributes.get(name)
            if attr is None:attr=result.attributes.new(name=name,type=typ,domain=domain)
            for i,source in enumerate(mappings[domain]):
                for prop,value in zip(props,values[source]):setattr(attr.data[i],prop,value)
        # Removing the third face exposes a two-face seam. Preserve differing
        # custom normals (and make behavior independent of normal presence) by
        # deliberately marking only those former multi-face roots sharp.
        for edge,source in zip(result.edges,mappings['EDGE']):
            if source in root_edge_ids:
                if not mesh.edges[source].use_edge_sharp:report['sharp_edges_added']+=1
                edge.use_edge_sharp=True
        if normals is not None:
            result.normals_split_custom_set([normals[i] for i in mappings['CORNER']])
        result.update()
        actual=attributes(result)
        for name,(domain,typ,props,values) in saved.items():
            expected=[values[i] for i in mappings[domain]]
            if name=='sharp_edge':
                expected=[(True,) if source in root_edge_ids else value
                          for source,value in zip(mappings[domain],expected)]
            if expected and actual.get(name)!=(domain,typ,props,expected):raise ValueError('Attribute verification failed: '+name)
        for v,source in zip(result.vertices,mappings['POINT']):
            old=mesh.vertices[source]
            if tuple(v.co)!=tuple(old.co) or sorted((g.group,g.weight) for g in v.groups)!=sorted((g.group,g.weight) for g in old.groups):
                raise ValueError('Vertex position / weights changed')
        if normals is not None:
            error=max(((n.vector-Vector(normals[s])).length for n,s in zip(result.corner_normals,mappings['CORNER'])),default=0)
            # Blender 4.5's packed-normal re-encoding is topology-dependent;
            # the observed quantization is below 0.02 degrees on real assets.
            if error>5e-4:raise ValueError(f'Custom normal verification failed (maximum vector error {error:.6g})')
        if [u.name for u in result.uv_layers]!=uv_names:raise ValueError('UV layers changed')
        if uv_names:
            result.uv_layers.active_index=uv_active
            for uv,active in zip(result.uv_layers,uv_render):uv.active_render=active
        if (len(result.vertices),len(result.edges),len(result.polygons))!=(before[0]-report['vertices_removed'],before[1]-report['edges_removed'],before[2]-report['faces_removed']):
            raise ValueError('Unexpected final element counts')
        return result,report
    except Exception:
        if result is not None:bpy.data.meshes.remove(result)
        raise
    finally:bm.free()


class OBJECT_OT_remove_boundary_flaps(bpy.types.Operator):
    bl_idname='object.remove_boundary_flaps'
    bl_label='Remove Boundary Flaps'
    bl_description='Remove the minimum boundary-attached face-patch set needed to resolve multi-face edges'
    bl_options={'REGISTER','UNDO'}
    scope:bpy.props.EnumProperty(name='Target',items=[('SELECTED','Selected Meshes',''),('ACTIVE','Active Mesh','')],default='SELECTED')
    @classmethod
    def poll(cls,context):
        return context.mode=='OBJECT' and (any(o.type=='MESH' for o in context.selected_objects) or
            (context.active_object is not None and context.active_object.type=='MESH'))
    def execute(self,context):
        targets=[context.active_object] if self.scope=='ACTIVE' else context.selected_objects
        targets=sorted((o for o in targets if o and o.type=='MESH'),key=lambda o:o.name)
        changed=patches=faces=failed=0
        for obj in targets:
            try:
                result,report=prepare_boundary_flap_cleanup(obj)
                for warning in report['rejected']:self.report({'WARNING'},f'{obj.name}: {warning}')
                if result is not None:
                    try:obj.data=result
                    except Exception:bpy.data.meshes.remove(result);raise
                    result.name+='_BoundaryFlapsRemoved';changed+=1
                    patches+=report['patches_removed'];faces+=report['faces_removed']
            except Exception as exc:failed+=1;self.report({'WARNING'},f'{obj.name}: {exc}')
        self.report({'WARNING'} if failed else {'INFO'},f'{changed} mesh(es); {patches} patches / {faces} faces removed; {failed} meshes failed')
        return {'FINISHED'} if changed or not failed else {'CANCELLED'}
