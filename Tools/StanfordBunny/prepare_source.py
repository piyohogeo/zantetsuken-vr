# SPDX-License-Identifier: MIT
import os
import bpy

outdir = os.path.dirname(os.path.abspath(__file__))
source_path = os.path.join(outdir,'source','bun_zipper.ply')
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.wm.ply_import(filepath=source_path)
obj = next(o for o in bpy.context.selected_objects if o.type == 'MESH')
obj.name = 'Stanford_Bunny_Official'
obj.data.name = 'Stanford_Bunny_Official_source'
obj['zpp_asset_family'] = 'Stanford'
obj['zpp_asset_id'] = 'Bunny'
obj['source_url'] = 'https://graphics.stanford.edu/pub/3Dscanrep/bunny.tar.gz'
obj['source_file'] = source_path
source = bpy.data.collections.new('SOURCE')
bpy.context.scene.collection.children.link(source)
for collection in tuple(obj.users_collection):
    collection.objects.unlink(obj)
source.objects.link(obj)
bpy.context.view_layer.objects.active = obj
bpy.context.scene.unit_settings.system = 'METRIC'
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type == 'VIEW_3D':
            area.spaces.active.region_3d.view_distance = 0.32
            area.spaces.active.clip_start = 0.001
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(outdir,'generated','source.blend'),compress=True)
print('PREPARED_OFFICIAL_BUNNY vertices='+str(len(obj.data.vertices))+' triangles='+str(len(obj.data.polygons)))
