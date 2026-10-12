"""Append flat n-gon caps without rebuilding the existing mesh."""
import bpy
import bmesh
from mathutils import Vector
from .boundary_weld import attributes
from .preserving_triangulate import custom_normal_is_close as _custom_normal_is_close


def cap_plan(mesh, allow_self_intersections=False, spatial=False, allow_coincident=False,
             cap_isolated_single_faces=False):
    bm = bmesh.new()
    plans, rejected = [], []
    try:
        bm.from_mesh(mesh)
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()
        remaining = {e for e in bm.edges if e.is_boundary}
        while remaining:
            seed = min(remaining, key=lambda e:e.index)
            remaining.remove(seed)
            edges = {seed}
            stack = [seed]
            while stack:
                for v in stack.pop().verts:
                    for edge in v.link_edges:
                        if edge in remaining:
                            remaining.remove(edge); edges.add(edge); stack.append(edge)
            try:
                vertices = {v for e in edges for v in e.verts}
                if len(vertices) < 3 or len(vertices) != len(edges) or any(sum(e in edges for e in v.link_edges) != 2 for v in vertices):
                    raise ValueError('Branched / non-cycle boundary')
                if any(e.is_wire or len(e.link_faces)>2 for v in vertices for e in v.link_edges):
                    raise ValueError('Wire / multi-face edge touches boundary')
                # Reverse the existing face traversal at every boundary edge.
                next_step = {}
                for edge in edges:
                    loop = edge.link_loops[0]
                    start, end = loop.link_loop_next.vert, loop.vert
                    if start in next_step:
                        raise ValueError('Inconsistent boundary orientation')
                    next_step[start] = (end, edge)
                start = min(vertices,key=lambda v:v.index)
                ring, edge_ids = [], []
                v = start
                for _ in edges:
                    if v in ring or v not in next_step:
                        raise ValueError('Invalid boundary cycle')
                    ring.append(v)
                    v, edge = next_step[v]
                    edge_ids.append(edge.index)
                if v != start:
                    raise ValueError('Open boundary')
                points = [v.co.copy() for v in ring]
                if not allow_coincident and len({tuple(p) for p in points}) != len(points):
                    raise ValueError('Coincident boundary vertices; weld first')
                center = sum(points,Vector())/len(points)
                normal = sum(((points[i]-center).cross(points[(i+1)%len(points)]-center) for i in range(len(points))),Vector())
                size = max((p-center).length for p in points)
                if size == 0:raise ValueError('Degenerate boundary')
                if normal.length <= size*size*1e-10:
                    if not spatial:raise ValueError('Degenerate boundary')
                    # Only used for provisional UVs. Optimization itself never
                    # uses a fitted plane or projected polygon.
                    normal=max(((points[i]-center).cross(points[j]-center) for i in range(len(points)) for j in range(i+1,len(points))),key=lambda v:v.length_squared)
                    if normal.length<=size*size*1e-10:raise ValueError('Collinear boundary')
                normal.normalize()
                axis = max(range(3),key=lambda i:abs(normal[i]))
                keep = [i for i in range(3) if i != axis]
                xy = [(p[keep[0]],p[keep[1]]) for p in points]
                def orient(a,b,c):
                    return (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])
                eps = size*size*1e-12
                def intersects(a,b,c,d):
                    x,y,z,w = orient(a,b,c),orient(a,b,d),orient(c,d,a),orient(c,d,b)
                    # Include collinear overlap and non-adjacent touching.
                    return (max(min(a[0],b[0]),min(c[0],d[0])) <= min(max(a[0],b[0]),max(c[0],d[0]))
                            and max(min(a[1],b[1]),min(c[1],d[1])) <= min(max(a[1],b[1]),max(c[1],d[1]))
                            and min(x,y)<=eps and max(x,y)>=-eps and min(z,w)<=eps and max(z,w)>=-eps)
                for i in range(len(xy)):
                    for j in range(i+1,len(xy)):
                        if j==i+1 or (i==0 and j==len(xy)-1): continue
                        if not allow_self_intersections and intersects(xy[i],xy[(i+1)%len(xy)],xy[j],xy[(j+1)%len(xy)]):
                            raise ValueError('Self-intersecting projected boundary')
                spanning = [f for f in start.link_faces
                            if len(f.verts)==len(vertices) and set(f.verts)==vertices]
                if spanning:
                    isolated = (len(spanning)==1
                                and all(len(edge.link_faces)==1 for edge in spanning[0].edges))
                    if not (cap_isolated_single_faces and isolated):
                        raise ValueError('Existing face already spans this loop')
                lo = [min(p[i] for p in xy) for i in range(2)]
                span = max(max(p[i] for p in xy)-lo[i] for i in range(2))
                uv = [((p[0]-lo[0])/span,(p[1]-lo[1])/span) for p in xy]
                if spatial:
                    # Each cap vertex inherits the corner on the existing face
                    # across its outgoing boundary edge. UV seams are resolved
                    # deterministically, never averaged across texture islands.
                    uv = {}
                    for name in bm.loops.layers.uv.keys():
                        layer = bm.loops.layers.uv[name]
                        uv[name] = [tuple(next_step[v][1].link_loops[0].link_loop_next[layer].uv)
                                    for v in ring]
                plans.append((tuple(v.index for v in ring),edge_ids,uv,seed.link_faces[0].material_index))
            except ValueError as exc:
                rejected.append(f'Edge {seed.index}: {exc}')
        return plans, rejected
    finally:
        bm.free()


def prepare_hard_cap(obj, allow_self_intersections=False, optimized_metrics=None,
                     repair_coincident=False, allow_opposite_multi_face_edges=False,
                     cap_isolated_single_faces=False):
    mesh = obj.data
    if mesh.shape_keys or len(mesh.skin_vertices) or any(m.type=='MULTIRES' for m in obj.modifiers):
        raise ValueError('Shape keys / Skin / Multires are not supported')
    if obj.library or obj.override_library or mesh.library or mesh.override_library:
        raise ValueError('Linked / override data is not supported')
    if optimized_metrics is not None and not optimized_metrics:
        raise ValueError('Select at least one optimization metric')
    if repair_coincident and optimized_metrics is not None:
        from .coincident_cap import prepare_coincident_cap
        return prepare_coincident_cap(
            obj, optimized_metrics,
            allow_opposite_multi_face_edges=allow_opposite_multi_face_edges,
            cap_isolated_single_faces=cap_isolated_single_faces)
    plans, rejected = cap_plan(
        mesh, allow_self_intersections or optimized_metrics is not None,
        spatial=optimized_metrics is not None,
        cap_isolated_single_faces=(cap_isolated_single_faces and optimized_metrics is not None))
    report = {'caps':len(plans),'rejected':rejected}
    new_edges=[]
    optimized_rim=set()
    allowed_multi_edges=set()
    if optimized_metrics is not None:
        from .cap_optimizer import optimize_ring
        face_plans=[];details=[];accepted=0;isolated_single_faces=0
        edge_map={tuple(sorted(e.vertices)):e.index for e in mesh.edges}
        edge_face_counts=[0]*len(mesh.edges)
        edge_direction_counts=[[0,0] for _ in mesh.edges]
        for polygon in mesh.polygons:
            loops=list(polygon.loop_indices)
            for offset,loop_id in enumerate(loops):
                loop=mesh.loops[loop_id]
                edge_face_counts[loop.edge_index]+=1
                edge=mesh.edges[loop.edge_index]
                edge_direction_counts[loop.edge_index][
                    0 if loop.vertex_index==min(edge.vertices) else 1]+=1
        polygons_by_vertices={}
        for polygon in mesh.polygons:
            polygons_by_vertices.setdefault(frozenset(polygon.vertices),[]).append(polygon)
        reused_additions={}
        for verts,edges,uv,material in plans:
            index={v:i for i,v in enumerate(verts)}
            spanning = [p for p in polygons_by_vertices.get(frozenset(verts),())
                        if len(p.vertices)==len(verts)]
            isolated_spanning = (len(spanning)==1
                                 and all(edge_face_counts[edge_map[tuple(sorted(pair))]]==1
                                         for pair in spanning[0].edge_keys))
            rim={tuple(sorted((verts[i],verts[(i+1)%len(verts)]))) for i in range(len(verts))}
            forbidden=[]
            for (a,b),edge_id in edge_map.items():
                if a not in index or b not in index or (a,b) in rim:continue
                # An internal cap edge has two new incident triangles.  The
                # opt-in exception permits 1 existing + 2 cap faces only.
                reusable=(edge_face_counts[edge_id]==2
                          and edge_direction_counts[edge_id]==[1,1])
                if not (allow_opposite_multi_face_edges and reusable):
                    forbidden.append((index[a],index[b]))
            existing_faces=[tuple(index[v] for v in p.vertices) for p in mesh.polygons
                            if len(p.vertices)==3 and all(v in index for v in p.vertices)
                            and not (cap_isolated_single_faces and isolated_spanning
                                    and p==spanning[0])]
            try:
                tris,detail=optimize_ring([mesh.vertices[v].co for v in verts],optimized_metrics,forbidden,existing_faces)
            except ValueError as exc:
                report['rejected'].append(f'Edge {edges[0]}: {exc}');continue
            detail['boundary_edge']=edges[0];details.append(detail);accepted+=1
            if cap_isolated_single_faces and isolated_spanning:isolated_single_faces+=1
            optimized_rim.update(edges)
            for tri in tris:
                ids=tuple(verts[i] for i in tri);edge_ids=[]
                for i in range(3):
                    key=tuple(sorted((ids[i],ids[(i+1)%3])))
                    if key not in edge_map:
                        edge_map[key]=len(mesh.edges)+len(new_edges);new_edges.append(key)
                    edge_ids.append(edge_map[key])
                    if edge_map[key] < len(mesh.edges):
                        reused_additions[edge_map[key]]=reused_additions.get(edge_map[key],0)+1
                face_plans.append((ids,edge_ids,{name:[coords[i] for i in tri] for name,coords in uv.items()},material))
        if allow_opposite_multi_face_edges:
            allowed_multi_edges={edge for edge,count in reused_additions.items()
                                 if edge_face_counts[edge]+count==4}
        plans=face_plans
        report.update(caps=accepted,triangles=len(face_plans),optimization=details,
                      near_degenerate_triangles=sum(
                          len(item.get('near_degenerate_triangles',())) for item in details),
                      allowed_opposite_multi_face_edges=len(allowed_multi_edges),
                      capped_isolated_single_faces=isolated_single_faces)
    if not plans: return None,report
    # Packed custom-normal values are relative to the current topology. Save
    # decoded vectors separately so topology changes can be re-encoded safely.
    before = attributes(mesh)
    old_normals = [n.vector.copy() for n in mesh.corner_normals] if mesh.has_custom_normals else None
    result = mesh.copy()
    try:
        if new_edges:
            result.edges.add(len(new_edges))
            for i,verts in enumerate(new_edges):result.edges[len(mesh.edges)+i].vertices=verts
        count = sum(len(p[0]) for p in plans)
        result.loops.add(count)
        result.polygons.add(len(plans))
        offset = len(mesh.loops)
        sharp = set()
        for index,(verts,edges,uv,material) in enumerate(plans):
            face = result.polygons[len(mesh.polygons)+index]
            face.loop_start = offset
            face.use_smooth = False
            face.material_index = material
            for j,(vertex,edge) in enumerate(zip(verts,edges)):
                result.loops[offset+j].vertex_index = vertex
                result.loops[offset+j].edge_index = edge
            for layer in result.uv_layers:
                coords = uv[layer.name] if isinstance(uv,dict) else uv
                for j,coord in enumerate(coords): layer.data[offset+j].uv = coord
            sharp.update(edges if optimized_metrics is None else (e for e in edges if e in optimized_rim))
            offset += len(verts)
        for edge in sharp: result.edges[edge].use_edge_sharp = True
        result.update(calc_edges=False,calc_edges_loose=True)
        if allow_self_intersections and optimized_metrics is None:
            triangulated = bmesh.new()
            try:
                triangulated.from_mesh(result)
                triangulated.faces.ensure_lookup_table()
                caps = list(triangulated.faces)[len(mesh.polygons):]
                # Ear clipping for n-gons; fixed diagonal for quads (Blender
                # exposes a separate quad mode, not an Ear Clip quad mode).
                bmesh.ops.triangulate(triangulated, faces=caps,
                                      quad_method='FIXED', ngon_method='EAR_CLIP')
                triangulated.to_mesh(result)
            finally:
                triangulated.free()
            # Explicitly restore existing attributes, especially packed normals.
            # New cap UVs are interpolated/carried by BMesh triangulation.
            for name,(domain,typ,props,values) in before.items():
                attr=result.attributes.get(name)
                if attr is None:attr=result.attributes.new(name=name,type=typ,domain=domain)
                for i,record in enumerate(values):
                    for prop,value in zip(props,record):setattr(attr.data[i],prop,value)
            for edge in sharp:result.edges[edge].use_edge_sharp=True
            # Triangulation may copy the n-gon's encoded normal onto its
            # triangles. Reset only new corners to automatic per-face normals.
            packed=result.attributes.get('custom_normal')
            if packed is not None and len(packed.data):
                props=[p for p in packed.data[0].bl_rna.properties if p.identifier!='rna_type' and not p.is_readonly]
                for item in list(packed.data)[len(mesh.loops):]:
                    for prop in props:
                        setattr(item,prop.identifier,tuple(0 for _ in range(prop.array_length)) if prop.is_array else 0)
            for face in result.polygons[len(mesh.polygons):]:face.use_smooth=False
            result.update()
            expected_triangles=sum(len(p[0])-2 for p in plans)
            if len(result.polygons)!=len(mesh.polygons)+expected_triangles:
                raise ValueError('Unexpected triangulated cap count')
            if any(len(p.vertices)!=3 for p in result.polygons[len(mesh.polygons):]):
                raise ValueError('Non-triangle cap remained')
            report['triangles']=expected_triangles
        if old_normals is not None:
            desired_normals = [tuple(normal) for normal in old_normals]
            desired_normals.extend(
                [(0.0, 0.0, 0.0)] * (len(result.loops) - len(old_normals)))
            result.normals_split_custom_set(desired_normals)
            result.update()
            # Blender may mark unrelated existing edges sharp while rebuilding
            # custom normals. Only cap rim edges are allowed to change here.
            for i, edge in enumerate(mesh.edges):
                if i not in sharp:
                    result.edges[i].use_edge_sharp = edge.use_edge_sharp
            result.update()
        actual = attributes(result)
        for name,(domain,typ,props,values) in before.items():
            current = actual[name]
            expected = list(values)
            if name == 'sharp_edge':
                expected = [(True,) if i in sharp else value for i,value in enumerate(values)]
            if current[:3] != (domain,typ,props) or current[3][:len(values)] != expected:
                raise ValueError('Existing attribute changed: '+name)
        if old_normals is not None and any(
                not _custom_normal_is_close(result.corner_normals[i].vector, normal)
                for i, normal in enumerate(old_normals)):
            raise ValueError('Existing custom normals changed')
        if any(tuple(v.co)!=tuple(mesh.vertices[i].co) or [(g.group,g.weight) for g in v.groups]!=[(g.group,g.weight) for g in mesh.vertices[i].groups] for i,v in enumerate(result.vertices)):
            raise ValueError('Vertex data changed')
        if any(tuple(p.vertices)!=tuple(mesh.polygons[i].vertices) for i,p in enumerate(result.polygons[:len(mesh.polygons)])):
            raise ValueError('Existing faces changed')
        check=bmesh.new()
        try:
            check.from_mesh(result);check.edges.ensure_lookup_table()
            from .non_manifold_check import is_allowed_opposite_multi_face_edge
            for edge_id,count in reused_additions.items() if optimized_metrics is not None else ():
                edge=check.edges[edge_id]
                if len(edge.link_faces)>2 and not (
                        edge_id in allowed_multi_edges
                        and is_allowed_opposite_multi_face_edge(edge)):
                    raise ValueError('Invalid reused edge sharing after cap')
            if any(not check.edges[i].is_manifold or not check.edges[i].is_contiguous for i in sharp):
                raise ValueError('Cap boundary is not consistently manifold')
            if any(not e.is_manifold or not e.is_contiguous for e in list(check.edges)[len(mesh.edges):]):
                raise ValueError('Invalid internal cap edge')
        finally:check.free()
        for face in result.polygons[len(mesh.polygons):]:
            if face.use_smooth or (optimized_metrics is None and face.area<=0):
                raise ValueError('Invalid cap')
            # Skinny triangles can differ numerically between polygon-normal
            # and corner-normal calculations. Flat shading means one normal
            # across the face, not exact equality between those algorithms.
            flat_normal=result.corner_normals[face.loop_start].vector
            if any((result.corner_normals[i].vector-flat_normal).length>2e-4 for i in face.loop_indices):
                raise ValueError('Cap has varying corner normals')
        return result,report
    except Exception:
        bpy.data.meshes.remove(result)
        raise


class OBJECT_OT_hard_cap_boundaries(bpy.types.Operator):
    bl_idname='object.hard_cap_boundaries'
    bl_label='Hard Cap Boundaries'
    bl_description='Cap simple boundary loops including non-planar loops; sharp rim and flat n-gons'
    bl_options={'REGISTER','UNDO'}
    scope:bpy.props.EnumProperty(name='Target',items=[('SELECTED','Selected Meshes',''),('ACTIVE','Active Mesh','')],default='SELECTED')
    allow_self_intersections:bpy.props.BoolProperty(
        name='Allow Self-Intersections (Ear Clip)', default=False,
        description='Bypass projected crossing rejection and triangulate caps with Ear Clip; intersections may remain')

    @classmethod
    def poll(cls,context):
        return context.mode=='OBJECT' and (any(o.type=='MESH' for o in context.selected_objects)
            or (context.active_object is not None and context.active_object.type=='MESH'))

    def execute(self,context):
        targets=[context.active_object] if self.scope=='ACTIVE' else context.selected_objects
        targets=[o for o in targets if o and o.type=='MESH']
        if not targets:
            self.report({'WARNING'},'No target mesh objects')
            return {'CANCELLED'}
        changed=caps=rejected=failed=0
        for obj in sorted((o for o in targets if o and o.type=='MESH'),key=lambda o:o.name):
            try:
                result,report=prepare_hard_cap(obj,self.allow_self_intersections)
                rejected+=len(report['rejected'])
                for message in report['rejected']:print(f'Hard Cap {obj.name}: {message}')
                if result is not None:
                    try:obj.data=result
                    except Exception:
                        bpy.data.meshes.remove(result);raise
                    result.name+='_HardCap'
                    changed+=1;caps+=report['caps']
            except Exception as exc:
                failed+=1;self.report({'WARNING'},f'{obj.name}: {exc}')
        self.report({'WARNING'} if rejected or failed else {'INFO'},f'{changed} mesh(es); {caps} caps; {rejected} loops skipped; {failed} meshes failed (details in system console)')
        return {'FINISHED'} if changed or not failed else {'CANCELLED'}
