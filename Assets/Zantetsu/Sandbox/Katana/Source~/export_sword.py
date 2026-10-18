# Exports the drawn sword of aikuchi_original.blend for Unity: the three meshes of the collection "Sword" as one FBX,
# and the textures the .blend carries as PNG files. The .blend is opened and read; it is not saved.
#
#   blender --background aikuchi_original.blend --python export_sword.py -- <output folder>
#
# Written with Blender 4.5. In the .blend: metres, the origin at the grip's centre, +Y toward the tip, X across the
# blade's width, Z through its thickness. The FBX is written for Unity's axes with the transforms baked into the
# meshes (no object rotation or scale to undo there). The metallic map Unity's Lit shader reads (metallic in R,
# smoothness in A) is made here from the blade's roughness image: metallic 0.98, smoothness = 1 - roughness.
import os
import sys

import bpy
import numpy as np

out = sys.argv[sys.argv.index('--') + 1]
os.makedirs(out, exist_ok=True)

sword = bpy.data.collections['Sword']
names = sorted(o.name for o in sword.objects if o.type == 'MESH')
assert names == ['Blade_75cm', 'Grip_26cm_Wood', 'Habaki_Hidden_When_Sheathed'], names
for o in bpy.data.objects:
    o.select_set(False)
triangles = 0
for name in names:
    o = bpy.data.objects[name]
    o.hide_set(False)
    o.select_set(True)
    o.data.calc_loop_triangles()
    triangles += len(o.data.loop_triangles)
bpy.context.view_layer.objects.active = bpy.data.objects[names[0]]
bpy.ops.export_scene.fbx(
    filepath=os.path.join(out, 'aikuchi_sword.fbx'), use_selection=True, object_types={'MESH'},
    apply_unit_scale=True, apply_scale_options='FBX_SCALE_UNITS', bake_space_transform=True,
    axis_forward='-Z', axis_up='Y', use_mesh_modifiers=True, mesh_smooth_type='FACE', use_tspace=False,
    add_leaf_bones=False, bake_anim=False, path_mode='STRIP', embed_textures=False)   # texture names only, no folders
print('EXPORT fbx: objects %s, triangles %d' % (names, triangles))


def save(image_name, file_name):
    image = bpy.data.images[image_name]
    path = os.path.join(out, file_name)
    image.save(filepath=path, save_copy=True)
    print('EXPORT image %s -> %s %s' % (image_name, file_name, tuple(image.size)))


save('Blade_Steel_BaseColor', 'aikuchi_blade_basecolor.png')
save('Blade_Micro_Brush_Normal', 'aikuchi_blade_normal.png')
save('Warm_Wood_BaseColor', 'aikuchi_wood_basecolor.png')

roughness = bpy.data.images['Blade_Brushed_Roughness']
width, height = roughness.size
pixels = np.empty(width * height * 4, dtype=np.float32)
roughness.pixels.foreach_get(pixels)
pixels = pixels.reshape(height, width, 4)
packed = np.empty_like(pixels)
packed[..., 0:3] = 0.98
packed[..., 3] = 1.0 - pixels[..., 0]
mask = bpy.data.images.new('aikuchi_blade_metallic_smoothness', width, height, alpha=True, is_data=True)
mask.pixels.foreach_set(packed.ravel())
mask.file_format = 'PNG'
mask.filepath_raw = os.path.join(out, 'aikuchi_blade_metallic_smoothness.png')
mask.save()
print('EXPORT image metallic 0.98 / smoothness %.3f..%.3f (mean %.3f) -> aikuchi_blade_metallic_smoothness.png' % (
    packed[..., 3].min(), packed[..., 3].max(), packed[..., 3].mean()))
