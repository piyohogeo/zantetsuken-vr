"""Bow-Tie Vertex Split, Blender 4.5 LTS. MIT."""
import bpy
from bpy.props import EnumProperty
from .topology import plan_split
from .boundary_weld import OBJECT_OT_weld_collapsed_boundaries
from .wire_cleanup import OBJECT_OT_remove_wire_geometry
from .isolated_vertex_cleanup import OBJECT_OT_remove_isolated_vertices
from .boundary_flap_cleanup import OBJECT_OT_remove_boundary_flaps
from .enclosed_multi_face_cleanup import OBJECT_OT_remove_enclosed_multi_face_patches
from .collapsed_boundary_cleanup import OBJECT_OT_remove_collapsed_boundary_faces
from .collinear_boundary_join import OBJECT_OT_join_collinear_boundary_seams
from .hard_cap import OBJECT_OT_hard_cap_boundaries
from .optimized_cap import OBJECT_OT_optimized_cap, METRIC_UI
from .non_manifold_check import OBJECT_OT_check_non_manifold, CATEGORY_UI
from .balanced_four_face_split import OBJECT_OT_split_balanced_four_face_sheets
from .three_face_split import OBJECT_OT_split_three_face_sheets
from .preserving_triangulate import OBJECT_OT_preserving_triangulate


def _freeze(value):
    if isinstance(value, (str, bytes, int, float, bool)):
        return value
    return tuple(_freeze(part) for part in value)


def mesh_plan(mesh):
    return plan_split(
        len(mesh.vertices),
        [tuple(e.vertices) for e in mesh.edges],
        [(tuple(p.vertices), tuple(mesh.loops[i].edge_index for i in p.loop_indices))
         for p in mesh.polygons],
    )


def _point_values(mesh, sources):
    """Snapshot before vertices.add invalidates RNA array element references."""
    result = []
    for attr in mesh.attributes:
        if attr.domain != 'POINT':
            continue
        # Legacy/imported meshes can contain valid attribute definitions with
        # no stored elements.  There is nothing to copy from such a layer;
        # Mesh.vertices.add initializes any new entries with Blender defaults.
        if not len(attr.data):
            continue
        # Introspect the concrete RNA value type, including vector/color/int2.
        props = [p for p in attr.data[0].bl_rna.properties
                 if p.identifier != 'rna_type' and not p.is_readonly]
        # Color and color_srgb alias the same storage; writing both introduces
        # a lossy color-space round trip. Byte colors are stored in sRGB.
        if attr.data_type in {'FLOAT_COLOR', 'BYTE_COLOR'}:
            canonical = 'color' if attr.data_type == 'FLOAT_COLOR' else 'color_srgb'
            props = [p for p in props if p.identifier == canonical]
        if not props:
            raise ValueError("Cannot safely copy point attribute: " + attr.name)
        values = []
        for source in sources:
            record = {}
            for prop in props:
                value = getattr(attr.data[source], prop.identifier)
                record[prop.identifier] = _freeze(value) if prop.is_array else value
            values.append(record)
        result.append((attr.name, values))
    return result


def prepare_mesh(obj, plan):
    """Build on an unlinked object copy; original remains untouched on failure."""
    original = obj.data
    sources = plan['sources']
    if not sources:
        return None
    if original.shape_keys:
        raise ValueError("Shape keys are not supported; mesh was not changed")
    if any(m.type == 'MULTIRES' for m in obj.modifiers):
        raise ValueError("Multires is not supported; mesh was not changed")
    if obj.library or obj.override_library or original.library or original.override_library:
        raise ValueError("Linked/override data is not supported; make a local copy first")

    point_values = _point_values(original, sources)
    weights = [[(g.group, g.weight) for g in original.vertices[i].groups]
               for i in sources]
    skin_values = [(layer.name, [
        (tuple(layer.data[i].radius), layer.data[i].use_root,
         layer.data[i].use_loose) for i in sources
    ]) for layer in original.skin_vertices]
    normals = ([tuple(n.vector) for n in original.corner_normals]
               if original.has_custom_normals else None)
    staged_obj = None
    result = None
    try:
        result = original.copy()
        staged_obj = obj.copy()
        staged_obj.data = result
        base = len(result.vertices)
        result.vertices.add(len(sources))
        for name, records in point_values:
            attr = result.attributes[name]
            for offset, record in enumerate(records):
                for prop, value in record.items():
                    setattr(attr.data[base + offset], prop, value)
        for offset, groups in enumerate(weights):
            for group_index, weight in groups:
                staged_obj.vertex_groups[group_index].add([base + offset], weight, 'REPLACE')
        for name, records in skin_values:
            layer = result.skin_vertices[name]
            for offset, (radius, use_root, use_loose) in enumerate(records):
                entry = layer.data[base + offset]
                entry.radius = radius
                entry.use_root = use_root
                entry.use_loose = use_loose
        result.loops.foreach_set('vertex_index', plan['corners'])
        result.edges.foreach_set('vertices', [v for edge in plan['edges'] for v in edge])
        # Never recalculate edges: keeping indices also keeps all edge attributes.
        result.update(calc_edges=False, calc_edges_loose=True)
        if normals is not None:
            result.normals_split_custom_set(normals)
            result.update()
        if mesh_plan(result)['sources']:
            raise RuntimeError("Postcondition failed: disconnected face fans remain")
        bpy.data.objects.remove(staged_obj)
        staged_obj = None
        return result
    except Exception:
        if staged_obj is not None:
            bpy.data.objects.remove(staged_obj)
        if result is not None and result.users == 0:
            bpy.data.meshes.remove(result)
        raise


class OBJECT_OT_split_bow_tie_vertices(bpy.types.Operator):
    bl_idname = 'object.split_bow_tie_vertices'
    bl_label = 'Split Bow-Tie Vertices'
    bl_description = 'Split vertex-only connections; do not close boundaries or split shared edges'
    bl_options = {'REGISTER', 'UNDO'}

    scope: EnumProperty(
        name='Target',
        items=[('SELECTED', 'Selected Meshes', 'Process all selected mesh objects'),
               ('ACTIVE', 'Active Mesh', 'Process only the active mesh object')],
        default='SELECTED',
    )

    @classmethod
    def poll(cls, context):
        return context.mode == 'OBJECT' and (
            any(o.type == 'MESH' for o in context.selected_objects)
            or (context.active_object is not None and context.active_object.type == 'MESH'))

    def execute(self, context):
        targets = ([context.active_object] if self.scope == 'ACTIVE'
                   else list(context.selected_objects))
        targets = sorted((o for o in targets if o and o.type == 'MESH'), key=lambda o: o.name)
        if not targets:
            self.report({'WARNING'}, 'No target mesh objects')
            return {'CANCELLED'}
        repaired = added = changed = skipped = 0
        for obj in targets:
            try:
                plan = mesh_plan(obj.data)
                result = prepare_mesh(obj, plan)
                if result is not None:
                    # Single-user replacement protects unselected linked duplicates.
                    old_mesh = obj.data
                    try:
                        obj.data = result
                    except Exception:
                        bpy.data.meshes.remove(result)
                        raise
                    result.name = old_mesh.name + '_BowTieSplit'
                    changed += 1
                    repaired += plan['split_vertices']
                    added += len(plan['sources'])
            except Exception as exc:
                skipped += 1
                self.report({'WARNING'}, f'{obj.name}: {exc}')
        message = (f'{changed} mesh(es) changed; {repaired} vertices split; '
                   f'{added} vertices added; {skipped} skipped')
        self.report({'WARNING'} if skipped else {'INFO'}, message)
        return {'FINISHED'} if changed or not skipped else {'CANCELLED'}


class VIEW3D_PT_bow_tie_split(bpy.types.Panel):
    bl_label = 'Bow-Tie Vertex Split'
    bl_idname = 'VIEW3D_PT_bow_tie_split'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'Mesh Repair'

    def draw(self, context):
        layout = self.layout
        layout.label(text='Vertex-only connections')
        layout.label(text='Split: boundaries stay open', icon='INFO')
        if context.mode != 'OBJECT':
            layout.label(text='Switch to Object Mode', icon='ERROR')
        col = layout.column(align=True)
        col.operator(OBJECT_OT_split_bow_tie_vertices.bl_idname,
                     text='Split Selected Meshes').scope = 'SELECTED'
        col.operator(OBJECT_OT_split_bow_tie_vertices.bl_idname,
                     text='Split Active Mesh').scope = 'ACTIVE'
        layout.separator()
        layout.label(text='Non-Manifold Check')
        layout.label(text='Blender categories / read-only', icon='INFO')
        for key,label in CATEGORY_UI:
            layout.prop(context.scene,'btw_check_'+key,text=label)
        layout.prop(context.scene,'btw_check_allow_opposite_multi')
        col=layout.column(align=True)
        for scope,label in [('SELECTED','Check Non-Manifold - Selected'),
                            ('ACTIVE','Check Non-Manifold - Active')]:
            op=col.operator(OBJECT_OT_check_non_manifold.bl_idname,text=label)
            op.scope=scope
            for key,_ in CATEGORY_UI:setattr(op,key,getattr(context.scene,'btw_check_'+key))
            op.allow_opposite_multi_face_edges=context.scene.btw_check_allow_opposite_multi
        if context.scene.btw_non_manifold_result:
            box=layout.box()
            for line in context.scene.btw_non_manifold_result.splitlines()[:7]:box.label(text=line)
        layout.separator()
        layout.label(text='3-Face Sheet Split')
        layout.label(text='Preview Cap / apply split only', icon='INFO')
        layout.label(text='Adds boundaries; Optimize Cap next')
        col=layout.column(align=True)
        col.operator(OBJECT_OT_split_three_face_sheets.bl_idname,
                     text='Split 3-Face Sheets - Selected').scope='SELECTED'
        col.operator(OBJECT_OT_split_three_face_sheets.bl_idname,
                     text='Split 3-Face Sheets - Active').scope='ACTIVE'
        if context.scene.btw_three_face_result:
            box=layout.box()
            for line in context.scene.btw_three_face_result.splitlines()[:5]:box.label(text=line)
        layout.separator()
        layout.label(text='Balanced 4-Face Sheet Split')
        layout.label(text='2:2 directions / no new boundaries', icon='INFO')
        col=layout.column(align=True)
        col.operator(OBJECT_OT_split_balanced_four_face_sheets.bl_idname,
                     text='Split 4-Face Sheets - Selected').scope='SELECTED'
        col.operator(OBJECT_OT_split_balanced_four_face_sheets.bl_idname,
                     text='Split 4-Face Sheets - Active').scope='ACTIVE'
        layout.separator()
        layout.label(text='Collapsed Boundary (before F)')
        layout.label(text='Exact match / 4 edges only', icon='INFO')
        col = layout.column(align=True)
        col.operator(OBJECT_OT_weld_collapsed_boundaries.bl_idname,
                     text='Weld Selected Meshes').scope = 'SELECTED'
        col.operator(OBJECT_OT_weld_collapsed_boundaries.bl_idname,
                     text='Weld Active Mesh').scope = 'ACTIVE'
        layout.separator()
        layout.label(text='Collapsed Boundary Face Cleanup')
        layout.label(text='Exact zero-area ribbons / before Cap', icon='INFO')
        col = layout.column(align=True)
        col.operator(OBJECT_OT_remove_collapsed_boundary_faces.bl_idname,
                     text='Remove Collapsed - Selected').scope = 'SELECTED'
        col.operator(OBJECT_OT_remove_collapsed_boundary_faces.bl_idname,
                     text='Remove Collapsed - Active').scope = 'ACTIVE'
        layout.separator()
        layout.label(text='Isolated Vertex Cleanup')
        layout.label(text='Vertices with no edges / faces', icon='INFO')
        col=layout.column(align=True)
        col.operator(OBJECT_OT_remove_isolated_vertices.bl_idname,
                     text='Remove Isolated - Selected').scope='SELECTED'
        col.operator(OBJECT_OT_remove_isolated_vertices.bl_idname,
                     text='Remove Isolated - Active').scope='ACTIVE'
        layout.separator()
        layout.label(text='Wire Cleanup')
        layout.label(text='All face-less edges in target', icon='INFO')
        col = layout.column(align=True)
        col.operator(OBJECT_OT_remove_wire_geometry.bl_idname,
                     text='Remove Wires - Selected').scope = 'SELECTED'
        col.operator(OBJECT_OT_remove_wire_geometry.bl_idname,
                     text='Remove Wires - Active').scope = 'ACTIVE'
        layout.separator()
        layout.label(text='Boundary Flap Cleanup')
        layout.label(text='Minimum deletion at multi-face edges', icon='INFO')
        col=layout.column(align=True)
        col.operator(OBJECT_OT_remove_boundary_flaps.bl_idname,
                     text='Remove Flaps - Selected').scope='SELECTED'
        col.operator(OBJECT_OT_remove_boundary_flaps.bl_idname,
                     text='Remove Flaps - Active').scope='ACTIVE'
        layout.separator()
        layout.label(text='Enclosed Multi-Face Cleanup')
        layout.label(text='Closed 3-face loops / no new boundaries', icon='INFO')
        col=layout.column(align=True)
        col.operator(OBJECT_OT_remove_enclosed_multi_face_patches.bl_idname,
                     text='Remove Enclosed - Selected').scope='SELECTED'
        col.operator(OBJECT_OT_remove_enclosed_multi_face_patches.bl_idname,
                     text='Remove Enclosed - Active').scope='ACTIVE'
        layout.separator()
        layout.label(text='Collinear Boundary Seam Join')
        layout.label(text='Straight doubled-back loops / before Cap', icon='INFO')
        col=layout.column(align=True)
        col.operator(OBJECT_OT_join_collinear_boundary_seams.bl_idname,
                     text='Join Collinear - Selected').scope='SELECTED'
        col.operator(OBJECT_OT_join_collinear_boundary_seams.bl_idname,
                     text='Join Collinear - Active').scope='ACTIVE'
        layout.separator()
        layout.label(text='Hard Cap (including non-planar)')
        layout.prop(context.scene, 'btw_allow_self_intersections')
        col = layout.column(align=True)
        for scope,label in [('SELECTED','Hard Cap - Selected'),('ACTIVE','Hard Cap - Active')]:
            op=col.operator(OBJECT_OT_hard_cap_boundaries.bl_idname,text=label)
            op.scope=scope
            op.allow_self_intersections=context.scene.btw_allow_self_intersections
        layout.separator()
        layout.label(text='Optimized 3D Cap')
        layout.label(text='Up to 256 vertices / Fold: 80', icon='INFO')
        for key,code,label in METRIC_UI:
            layout.prop(context.scene,'btw_opt_'+key,text=label)
        layout.label(text='Compare: Undo, change metrics, run again')
        layout.prop(context.scene,'btw_repair_coincident')
        layout.prop(context.scene,'btw_cap_allow_opposite_multi')
        layout.prop(context.scene,'btw_cap_isolated_single_faces')
        col=layout.column(align=True)
        for scope,label in [('SELECTED','Optimize Cap - Selected'),('ACTIVE','Optimize Cap - Active')]:
            op=col.operator(OBJECT_OT_optimized_cap.bl_idname,text=label)
            op.scope=scope
            for key,_,_ in METRIC_UI:setattr(op,key,getattr(context.scene,'btw_opt_'+key))
            op.repair_coincident=context.scene.btw_repair_coincident
            op.allow_opposite_multi_face_edges=context.scene.btw_cap_allow_opposite_multi
            op.cap_isolated_single_faces=context.scene.btw_cap_isolated_single_faces
        if context.scene.btw_optimizer_result:
            box=layout.box()
            for line in context.scene.btw_optimizer_result.splitlines()[:7]:box.label(text=line)
        layout.separator()
        layout.label(text='Preserving Triangulate')
        layout.label(text='Keeps coincident opposite faces', icon='INFO')
        layout.prop(context.scene, 'btw_triangulate_minimize_four_face')
        layout.prop(context.scene, 'btw_triangulate_simplify_collapsed_quads')
        col=layout.column(align=True)
        for scope,label in [('SELECTED','Triangulate Preserving - Selected'),
                            ('ACTIVE','Triangulate Preserving - Active')]:
            op=col.operator(OBJECT_OT_preserving_triangulate.bl_idname,text=label)
            op.scope=scope
            op.minimize_four_face_edges=context.scene.btw_triangulate_minimize_four_face
            op.simplify_collapsed_quads=context.scene.btw_triangulate_simplify_collapsed_quads


_CLASSES = (OBJECT_OT_split_bow_tie_vertices, OBJECT_OT_weld_collapsed_boundaries,
            OBJECT_OT_remove_collapsed_boundary_faces,
            OBJECT_OT_remove_isolated_vertices, OBJECT_OT_remove_wire_geometry,
            OBJECT_OT_remove_boundary_flaps,
            OBJECT_OT_remove_enclosed_multi_face_patches,
            OBJECT_OT_join_collinear_boundary_seams,
            OBJECT_OT_hard_cap_boundaries,
            OBJECT_OT_optimized_cap, OBJECT_OT_check_non_manifold,
            OBJECT_OT_split_three_face_sheets,
            OBJECT_OT_split_balanced_four_face_sheets,
            OBJECT_OT_preserving_triangulate,
            VIEW3D_PT_bow_tie_split)


def register():
    bpy.types.Scene.btw_three_face_result=bpy.props.StringProperty(
        name='Last 3-Face Sheet Split',default='')
    bpy.types.Scene.btw_triangulate_simplify_collapsed_quads=bpy.props.BoolProperty(
        name='Simplify Collapsed Quads',default=False,
        description='Simplify exact collapsed quads, avoid zero-area n-gon triangles where possible, and report any remaining')
    bpy.types.Scene.btw_triangulate_minimize_four_face=bpy.props.BoolProperty(
        name='Minimize New 4-Face Edges (Best Effort)',default=False,
        description='Prefer quad/n-gon splits avoiding duplicate triangles and new 4-face edges; retry local splits on normal errors without relaxing tolerance')
    bpy.types.Scene.btw_repair_coincident=bpy.props.BoolProperty(name='Repair Coincident Boundary Vertices',default=False)
    bpy.types.Scene.btw_cap_allow_opposite_multi=bpy.props.BoolProperty(
        name='Allow Opposite 4-Face Edges',default=False,
        description='Allow only 4-face edges whose traversal directions are balanced 2-to-2')
    bpy.types.Scene.btw_cap_isolated_single_faces=bpy.props.BoolProperty(
        name='Cap Isolated Single Faces',default=False,
        description='Back-cap only boundary loops that are already exactly one edge-isolated face')
    bpy.types.Scene.btw_check_allow_opposite_multi=bpy.props.BoolProperty(
        name='Allow Opposite 4-Face Edges',default=False,
        description='Ignore only 4-face edges whose traversal directions are balanced 2-to-2')
    for key,label in CATEGORY_UI:
        setattr(bpy.types.Scene,'btw_check_'+key,bpy.props.BoolProperty(name=label,default=True))
    bpy.types.Scene.btw_non_manifold_result=bpy.props.StringProperty(name='Last Non-Manifold Check',default='')
    for key,_,label in METRIC_UI:
        setattr(bpy.types.Scene,'btw_opt_'+key,bpy.props.BoolProperty(name=label,default=key=='area'))
    bpy.types.Scene.btw_optimizer_result=bpy.props.StringProperty(name='Last Optimization',default='')
    bpy.types.Scene.btw_allow_self_intersections = bpy.props.BoolProperty(
        name='Allow Self-Intersections (Ear Clip)', default=False,
        description='Allow crossing caps and triangulate; does not repair intersections')
    for cls in _CLASSES:
        bpy.utils.register_class(cls)


def unregister():
    del bpy.types.Scene.btw_three_face_result
    del bpy.types.Scene.btw_triangulate_simplify_collapsed_quads
    del bpy.types.Scene.btw_triangulate_minimize_four_face
    del bpy.types.Scene.btw_repair_coincident
    del bpy.types.Scene.btw_cap_allow_opposite_multi
    del bpy.types.Scene.btw_cap_isolated_single_faces
    del bpy.types.Scene.btw_check_allow_opposite_multi
    for key,_ in CATEGORY_UI:delattr(bpy.types.Scene,'btw_check_'+key)
    del bpy.types.Scene.btw_non_manifold_result
    for cls in reversed(_CLASSES):
        bpy.utils.unregister_class(cls)
    del bpy.types.Scene.btw_allow_self_intersections
    for key,_,_ in METRIC_UI:delattr(bpy.types.Scene,'btw_opt_'+key)
    del bpy.types.Scene.btw_optimizer_result
