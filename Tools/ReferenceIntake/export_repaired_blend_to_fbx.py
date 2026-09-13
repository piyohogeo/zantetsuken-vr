"""Export the mesh objects of a repaired .blend as one FBX plus the textures it needs (DESIGN 10.2.3).

Runs inside the fixed Blender:

  blender.exe --background --factory-startup <file.blend> --python export_repaired_blend_to_fbx.py -- \
      --out <directory> --name <fbx stem> [--collection SOURCE]

The source file is never modified: transforms are applied and the export happens on the in-memory copy,
nothing is saved back. Only MESH objects of the named collection (default SOURCE, falling back to every
mesh object) are exported, without modifiers (the base mesh the repair recipe validated), with per-corner
split normals, UVs and tangents so the FBX carries the render layer, in the Unity axis convention with the
axis conversion baked into the mesh data and the unit scale applied to the FBX scale (mesh data stays in
metres). Textures are written next to the FBX by this script (path_mode STRIP: the FBX names them by file name). A JSON manifest next to the
FBX records the Blender version, the objects and the exporter options used.
"""
import argparse
import hashlib
import json
import os
import sys

import bpy


def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--name", required=True)
    p.add_argument("--collection", default="SOURCE")
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    return p.parse_args(argv)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    args = parse_args()
    os.makedirs(args.out, exist_ok=True)
    src = bpy.data.collections.get(args.collection)
    objs = [o for o in (src.all_objects if src is not None else bpy.data.objects) if o.type == "MESH"]
    objs = [o for o in objs if len(o.data.polygons) > 0]
    if not objs:
        print("EXPORT FAILED: no mesh object to export", flush=True)
        sys.exit(2)

    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.hide_set(False)
        o.hide_viewport = False
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    # Bake object transforms into the (in-memory) mesh data so the FBX models carry identity transforms.
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    # Textures the exported materials use. Packed images are written into the output directory (the source
    # .blend is not saved, so it keeps its packed copy); the exporter then references them by relative name.
    images = {}
    for o in objs:
        for m in o.data.materials:
            if m is None or not m.use_nodes or m.node_tree is None:
                continue
            for n in m.node_tree.nodes:
                if n.type == "TEX_IMAGE" and n.image is not None and n.image.name not in images:
                    images[n.image.name] = n.image
    textures = []
    for name, img in images.items():
        base = os.path.basename(img.filepath_raw) if img.filepath_raw else name
        if not os.path.splitext(base)[1]:
            base += ".png"
        target = os.path.join(args.out, base)
        if img.packed_file is not None:
            img.filepath_raw = target
            img.save()
        elif os.path.isfile(bpy.path.abspath(img.filepath)):
            import shutil
            shutil.copyfile(bpy.path.abspath(img.filepath), target)
        else:
            textures.append(dict(image=name, file=None, note="image file not found and not packed"))
            continue
        textures.append(dict(image=name, file=base))

    fbx_path = os.path.join(args.out, args.name + ".fbx")
    options = dict(
        filepath=fbx_path,
        use_selection=True,
        object_types={"MESH"},
        use_mesh_modifiers=False,
        mesh_smooth_type="OFF",
        use_tspace=True,
        use_triangles=False,
        add_leaf_bones=False,
        bake_anim=False,
        use_custom_props=False,
        path_mode="STRIP",
        embed_textures=False,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        global_scale=1.0,
        axis_forward="-Z",
        axis_up="Y",
        bake_space_transform=True,
    )
    bpy.ops.export_scene.fbx(**options)

    objects = []
    for o in objs:
        me = o.data
        objects.append(dict(
            name=o.name, meshData=me.name, vertices=len(me.vertices), polygons=len(me.polygons),
            nonTrianglePolygons=sum(1 for p in me.polygons if len(p.vertices) != 3),
            materials=[m.name if m else None for m in me.materials],
            uvLayers=[u.name for u in me.uv_layers],
            modifiersNotApplied=[m.type for m in o.modifiers],
        ))
    files = []
    for f in sorted(os.listdir(args.out)):
        path = os.path.join(args.out, f)
        if os.path.isfile(path) and not f.endswith(".json"):
            files.append(dict(file=f, bytes=os.path.getsize(path), sha256=sha256(path)))
    manifest = dict(
        blenderVersion=bpy.app.version_string,
        sourceBlend=os.path.basename(bpy.data.filepath),
        sourceBlendSha256=sha256(bpy.data.filepath),
        collection=args.collection if src is not None else None,
        objects=objects,
        textures=textures,
        exporter={k: (sorted(v) if isinstance(v, set) else v) for k, v in options.items() if k != "filepath"},
        files=files,
    )
    with open(os.path.join(args.out, args.name + ".export.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1, ensure_ascii=False)
    print("EXPORT DONE %s objects=%d files=%d" % (fbx_path, len(objects), len(files)), flush=True)


main()
