"""Transactional exact-coincident cap cleanup; never mutate the source object."""
import bpy
import bmesh
from mathutils import Vector
from .boundary_weld import attributes
from .cap_optimizer import optimize_ring


def prepare_coincident_cap(obj, metrics, allow_opposite_multi_face_edges=False,
                           cap_isolated_single_faces=False):
    from .hard_cap import prepare_hard_cap, cap_plan, _custom_normal_is_close
    from .non_manifold_check import (is_allowed_opposite_multi_face_edge,
                                     vertex_only_issue)
    # Keep the established, fully checked path for ordinary boundaries.
    base, report = prepare_hard_cap(
        obj, optimized_metrics=metrics,
        allow_opposite_multi_face_edges=allow_opposite_multi_face_edges,
        cap_isolated_single_faces=cap_isolated_single_faces)
    owned = base is not None
    base = base if owned else obj.data
    bm = None
    result = None
    try:
        plans, _ = cap_plan(base, True, spatial=True, allow_coincident=True,
                            cap_isolated_single_faces=cap_isolated_single_faces)
        plans = [p for p in plans if len({tuple(base.vertices[v].co) for v in p[0]}) < len(p[0])]
        if not plans:return (base if owned else None), report
        report['rejected'] = [r for r in report['rejected'] if 'Coincident boundary vertices;' not in r]
        report.setdefault('optimization', [])
        report.setdefault('triangles', 0)
        report.setdefault('near_degenerate_triangles', 0)
        report['welded_vertices'] = 0
        saved = attributes(base)
        weights = [tuple(sorted((g.group,g.weight) for g in v.groups)) for v in base.vertices]
        normals = [tuple(n.vector) for n in base.corner_normals] if base.has_custom_normals else None
        bm = bmesh.new();bm.from_mesh(base)
        names = {}
        for domain,seq in [('POINT',bm.verts),('EDGE',bm.edges),('FACE',bm.faces),('CORNER',bm.loops)]:
            name = '_bt_cap_source_'+domain
            while seq.layers.int.get(name):name += '_'
            layer = seq.layers.int.new(name);names[domain] = name
            elements = [l for f in bm.faces for l in f.loops] if domain=='CORNER' else seq
            for i,e in enumerate(elements):e[layer] = i
        def compatible(domain,a,b):
            return all(values[a]==values[b] for name,(dom,_,_,values) in saved.items()
                       if dom==domain and not name.startswith('.select_'))
        accepted = 0
        for ids,edge_ids,uv,material in plans:
            trial = bm.copy()
            try:
                vl=trial.verts.layers.int[names['POINT']];el=trial.edges.layers.int[names['EDGE']]
                fl=trial.faces.layers.int[names['FACE']];cl=trial.loops.layers.int[names['CORNER']]
                by_id={v[vl]:v for v in trial.verts}
                ring=[by_id[i] for i in ids];index={v:i for i,v in enumerate(ring)}
                rim={frozenset((ring[i],ring[(i+1)%len(ring)])) for i in range(len(ring))}
                forbidden=[(index[e.verts[0]],index[e.verts[1]]) for e in trial.edges
                           if all(v in index for v in e.verts) and frozenset(e.verts) not in rim
                           and not (allow_opposite_multi_face_edges and (
                               len(e.link_faces)==2 and e.is_contiguous))]
                faces=[tuple(index[v] for v in f.verts) for f in trial.faces if len(f.verts)==3 and all(v in index for v in f.verts)]
                tris,detail=optimize_ring([v.co for v in ring],metrics,forbidden,faces,allow_coincident=True)
                collapsed=detail['collapsed_triangles']
                # Only weld pairs explicitly recorded in selected collapsed triangles.
                parent=list(range(len(ring)))
                def root(i):
                    while parent[i]!=i:i=parent[i]
                    return i
                for tri in collapsed:
                    for a in tri:
                        for b in tri:
                            if a<b and tuple(ring[a].co)==tuple(ring[b].co):parent[root(b)]=root(a)
                target={ring[i]:ring[root(i)] for i in range(len(ring)) if root(i)!=i}
                for a,b in target.items():
                    if not compatible('POINT',a[vl],b[vl]) or weights[a[vl]]!=weights[b[vl]]:
                        conflicts=[name for name,(dom,_,_,values) in saved.items() if dom=='POINT' and values[a[vl]]!=values[b[vl]]]
                        raise ValueError('Conflicting point attributes or vertex weights: '+str(conflicts))
                # Merged old edges may not silently discard different attributes.
                edge_groups={}
                for e in trial.edges:
                    key=frozenset(target.get(v,v) for v in e.verts)
                    if e[el]>=0:
                        if len(key)<2:raise ValueError('Weld would collapse an existing edge')
                        previous=edge_groups.get(key)
                        if previous is not None and not compatible('EDGE',previous,e[el]):
                            raise ValueError('Conflicting edge attributes')
                        edge_groups[key]=e[el]
                old_faces={f:[(l[cl],tuple(l.vert.co)) for l in f.loops] for f in trial.faces}
                old_boundary=sum(e.is_boundary for e in trial.edges)
                old_edges=set(trial.edges)
                created=[]
                for tri in tris:
                    f=trial.faces.new([ring[i] for i in tri]);f[fl]=-1
                    f.smooth=False;f.material_index=material;created.append(f)
                    for l,i in zip(f.loops,tri):
                        l[cl]=-1
                        for name in trial.loops.layers.uv.keys():
                            l[trial.loops.layers.uv[name]].uv=uv[name][i]
                for e in trial.edges:
                    if e not in old_edges:e[el]=-1
                    if frozenset(e.verts) in rim:e.smooth=False
                expected=len(tris)-len(collapsed)
                if target:bmesh.ops.weld_verts(trial,targetmap=target)
                # BMesh removes collapsed triangles during weld; verify precisely.
                if len(trial.faces)!=len(old_faces)+expected:
                    raise ValueError('Unexpected face removal during cleanup')
                for f,corners in old_faces.items():
                    if not f.is_valid or [(l[cl],tuple(l.vert.co)) for l in f.loops]!=corners:
                        raise ValueError('Existing face or corner changed')
                affected={v for v in ring if v.is_valid}
                if any(vertex_only_issue(v,allow_opposite_multi_face_edges) for v in affected):
                    raise ValueError('Weld would leave a bow-tie or open vertex fan')
                if any(not (e.is_manifold and e.is_contiguous) and not (
                        allow_opposite_multi_face_edges and is_allowed_opposite_multi_face_edge(e)
                ) for v in affected for e in v.link_edges):
                    raise ValueError('Invalid edge sharing or orientation after cleanup')
                if sum(e.is_boundary for e in trial.edges)!=old_boundary-len(edge_ids):
                    raise ValueError('Boundary did not close completely')
                new_faces=[f for f in trial.faces if f not in old_faces]
                if any(len(f.verts)!=3 for f in new_faces):
                    raise ValueError('Degenerate cap remained')
                bm.free();bm=trial;trial=None;accepted+=1
                report['caps']+=1;report['triangles']+=expected
                report['near_degenerate_triangles']+=len(
                    detail.get('near_degenerate_triangles',()))
                report['welded_vertices']+=len(target)
                detail.update(boundary_edge=edge_ids[0],removed_triangles=len(collapsed),triangles=expected,
                              scores_before_cleanup=True)
                report['optimization'].append(detail)
            except (ValueError,KeyError) as exc:
                report['rejected'].append(f'Edge {edge_ids[0]}: {exc}')
            finally:
                if trial is not None:trial.free()
        if not accepted:return (base if owned else None),report
        mappings={}
        for domain,seq in [('POINT',bm.verts),('EDGE',bm.edges),('FACE',bm.faces),('CORNER',bm.loops)]:
            layer=seq.layers.int[names[domain]]
            elements=[l for f in bm.faces for l in f.loops] if domain=='CORNER' else seq
            mappings[domain]=[e[layer] for e in elements];seq.layers.int.remove(layer)
        if sorted(i for i in mappings['FACE'] if i>=0)!=list(range(len(base.polygons))):
            raise ValueError('Original face identity lost')
        if sorted(i for i in mappings['CORNER'] if i>=0)!=list(range(len(base.loops))):
            raise ValueError('Original corner identity lost')
        result=base.copy();bm.to_mesh(result);result.update()
        for name,(domain,typ,props,values) in saved.items():
            attr=result.attributes.get(name)
            if attr is None:attr=result.attributes.new(name=name,type=typ,domain=domain)
            for i,source in enumerate(mappings[domain]):
                if source<0:continue
                # Rim sharpness intentionally changes; keep BMesh's result.
                if name=='sharp_edge':continue
                for prop,value in zip(props,values[source]):setattr(attr.data[i],prop,value)
        if normals is not None:
            result.normals_split_custom_set([normals[i] if i>=0 else (0,0,0) for i in mappings['CORNER']])
        result.update()
        for i,source in enumerate(mappings['POINT']):
            v=result.vertices[i]
            if tuple(v.co)!=tuple(base.vertices[source].co) or tuple(sorted((g.group,g.weight) for g in v.groups))!=weights[source]:
                raise ValueError('Vertex position or weights changed')
        actual=attributes(result)
        for name,(domain,typ,props,values) in saved.items():
            if name=='sharp_edge':continue
            if any(actual[name][3][i]!=values[s] for i,s in enumerate(mappings[domain]) if s>=0):
                raise ValueError('Attribute preservation failed: '+name)
        if normals is not None and any(
                not _custom_normal_is_close(
                    result.corner_normals[i].vector, Vector(normals[source]))
                for i, source in enumerate(mappings['CORNER']) if source >= 0):
            raise ValueError('Custom normal preservation failed')
        if any(p.use_smooth for i,p in enumerate(result.polygons) if mappings['FACE'][i]<0):
            raise ValueError('Invalid final cap')
        if owned:bpy.data.meshes.remove(base);owned=False
        return result,report
    except Exception:
        if result is not None:bpy.data.meshes.remove(result)
        if owned:bpy.data.meshes.remove(base)
        raise
    finally:
        if bm is not None:bm.free()
