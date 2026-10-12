import bpy
import time
from .hard_cap import prepare_hard_cap

METRIC_UI=(('area','AREA','Total Area'),('length','LENGTH','Diagonal Length'),
           ('shape','SHAPE','Triangle Slenderness'),('fold','FOLD','Internal Fold Angle'))


class OBJECT_OT_optimized_cap(bpy.types.Operator):
    bl_idname='object.optimized_boundary_cap'
    bl_label='Optimized 3D Boundary Cap'
    bl_description='Optimize a 3D triangular patch directly; sharp rim, flat triangles; intersections allowed'
    bl_options={'REGISTER','UNDO'}
    scope:bpy.props.EnumProperty(name='Target',items=[('SELECTED','Selected Meshes',''),('ACTIVE','Active Mesh','')],default='SELECTED')
    area:bpy.props.BoolProperty(name='Total Area',default=True)
    length:bpy.props.BoolProperty(name='Diagonal Length',default=False)
    shape:bpy.props.BoolProperty(name='Triangle Slenderness',default=False)
    fold:bpy.props.BoolProperty(name='Internal Fold Angle (slower)',default=False)
    repair_coincident:bpy.props.BoolProperty(name='Repair Coincident Boundary Vertices',default=False,
        description='Allow exact-coincident collapsed cap triangles, weld their vertices, and reject unsafe loop repairs')
    allow_opposite_multi_face_edges:bpy.props.BoolProperty(
        name='Allow Opposite 4-Face Edges', default=False,
        description='Allow only 4-face edges whose traversal directions are balanced 2-to-2')
    cap_isolated_single_faces:bpy.props.BoolProperty(
        name='Cap Isolated Single Faces', default=False,
        description='Add an opposite-facing triangulated cap only when a boundary loop is exactly one edge-isolated face')

    @classmethod
    def poll(cls,context):
        return context.mode=='OBJECT' and (any(o.type=='MESH' for o in context.selected_objects)
            or (context.active_object is not None and context.active_object.type=='MESH'))

    def execute(self,context):
        metrics=[code for key,code,_ in METRIC_UI if getattr(self,key)]
        if not metrics:
            self.report({'WARNING'},'Select at least one metric');return {'CANCELLED'}
        targets=[context.active_object] if self.scope=='ACTIVE' else context.selected_objects
        targets=sorted((o for o in targets if o and o.type=='MESH'),key=lambda o:o.name)
        if not targets:
            self.report({'WARNING'},'No target mesh objects');return {'CANCELLED'}
        start=time.perf_counter();caps=triangles=skipped=failed=allowed_multi=isolated=fallback=0;lines=[]
        for obj in targets:
            try:
                result,report=prepare_hard_cap(
                    obj, optimized_metrics=metrics,
                    repair_coincident=self.repair_coincident,
                    allow_opposite_multi_face_edges=self.allow_opposite_multi_face_edges,
                    cap_isolated_single_faces=self.cap_isolated_single_faces)
                skipped+=len(report['rejected'])
                for warning in report['rejected']:print(f'Optimized Cap {obj.name}: {warning}')
                if result is not None:
                    try:obj.data=result
                    except Exception:
                        bpy.data.meshes.remove(result);raise
                    result.name+='_OptimizedCap'
                    caps+=report['caps'];triangles+=report['triangles']
                    allowed_multi+=report.get('allowed_opposite_multi_face_edges',0)
                    isolated+=report.get('capped_isolated_single_faces',0)
                    fallback+=report.get('near_degenerate_triangles',0)
                    for item in report['optimization']:
                        s=item['scores']
                        line=(f"{obj.name} / edge {item['boundary_edge']}: "
                              f"A {s['AREA']:.5g} L {s['LENGTH']:.5g} "
                              f"S {s['SHAPE']:.5g} F {s['FOLD']:.5g}")
                        if item.get('scores_before_cleanup'):line+=' (pre-cleanup)'
                        if item.get('near_degenerate_triangles'):
                            line+=f" / fallback {len(item['near_degenerate_triangles'])}"
                        lines.append(line);print('Optimized Cap:',item)
            except Exception as exc:
                failed+=1;self.report({'WARNING'},f'{obj.name}: {exc}')
        elapsed=time.perf_counter()-start
        summary=f'{caps} caps / {triangles} triangles / {elapsed:.2f}s / {skipped} loops skipped / {failed} meshes failed'
        if fallback:summary+=f' / {fallback} near-degenerate fallback triangles'
        if self.allow_opposite_multi_face_edges:summary+=f' / {allowed_multi} opposite 4-face edges allowed'
        if self.cap_isolated_single_faces:summary+=f' / {isolated} isolated single faces back-capped'
        context.scene.btw_optimizer_result='\n'.join([summary,'Metrics: '+', '.join(metrics)]+lines)
        self.report({'WARNING'} if skipped or failed else {'INFO'},summary)
        return {'FINISHED'} if caps or not failed else {'CANCELLED'}
