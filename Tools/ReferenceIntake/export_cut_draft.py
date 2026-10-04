"""Export SOURCE render FBX and explicit convex/anchor inputs for Phase 0.21.

Run Blender with --background --factory-startup --disable-autoexec --python
this_file.py -- --sources <Working directory> [<another directory> ...]
--out <private staging directory>. Original blends are opened with scripts
disabled and never saved. This exports drafts, not a physics or topology Gate.

FBX retains armatures/weights (no pose flattening). Physics JSON matrices are
row-major, acting on column vectors; vertices and translations are metres in
Blender right-handed Z-up axes. FBX uses its separate Unity-facing axis/unit
conversion. Consumers must convert the sidecar explicitly, not assume FBX axes.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import sys
import traceback

import bpy


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2,
                                    allow_nan=False) + "\n", encoding="utf-8")


def vector(value, units):
    return [float(v) * units for v in value]


def matrix(value, units):
    result = [[float(v) for v in row] for row in value]
    for row in range(3):
        result[row][3] *= units
    return result


def plain(value):
    if isinstance(value, (str, int, float, bool)) or value is None:
        return value
    if hasattr(value, "to_dict"):
        return {k: plain(v) for k, v in value.to_dict().items()}
    if hasattr(value, "to_list"):
        return [plain(v) for v in value.to_list()]
    raise TypeError("Unsupported input property: " + type(value).__name__)


def properties(obj):
    prefixes = ("zpp_", "skinned_convex_", "convex_decomposition_",
                "megacity_ground_anchor_")
    names = {"representative_bone", "raw_hull_vertices",
             "threshold_edge_points", "max_inward_gap_cm", "ztk_building"}
    return {k: plain(obj[k]) for k in sorted(obj.keys())
            if k.startswith(prefixes) or k in names}


def object_record(obj, units):
    return dict(name=obj.name, type=obj.type,
                parent=obj.parent.name if obj.parent else None,
                parentType=obj.parent_type, parentBone=obj.parent_bone,
                localMatrix=matrix(obj.matrix_local, units),
                worldMatrix=matrix(obj.matrix_world, units),
                properties=properties(obj))


def render_record(obj, units):
    def bounds(mesh, transform):
        points = [transform @ v.co for v in mesh.vertices]
        return dict(min=[min(p[i] for p in points) * units for i in range(3)],
                    max=[max(p[i] for p in points) * units for i in range(3)])

    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    posed = evaluated.to_mesh()
    try:
        posed_bounds = bounds(posed, evaluated.matrix_world)
    finally:
        evaluated.to_mesh_clear()
    weighted = {obj.vertex_groups[g.group].name for v in obj.data.vertices
                for g in v.groups if g.weight > 0}
    return dict(**object_record(obj, units), vertices=len(obj.data.vertices),
                polygons=len(obj.data.polygons),
                restWorldBounds=bounds(obj.data, obj.matrix_world),
                posedWorldBounds=posed_bounds,
                armatureBindings=[dict(object=m.object.name,
                                       weightedBones=sorted(weighted.intersection(m.object.data.bones.keys())))
                                  for m in obj.modifiers if m.type == "ARMATURE" and m.object],
                modifiers=[dict(name=m.name, type=m.type, viewport=m.show_viewport,
                                render=m.show_render) for m in obj.modifiers])


def physics_record(render, rigs, units):
    owners = [o for o in bpy.data.objects if o.get("zpp_asset_id")]
    if len(owners) != 1:
        raise ValueError("Expected one explicitly tagged asset carrier")
    carrier = owners[0]
    hulls = sorted((o for o in bpy.data.objects if o.type == "MESH" and
                   any(c.get("skinned_convex_profile") or c.name == "convex hulls"
                       for c in o.users_collection)), key=lambda o: o.name)
    convex, attachment_owners = [], set()
    for hull in hulls:
        bone = hull.get("representative_bone")
        if bone:
            null = hull.parent
            rig = null.parent if null else None
            if (null is None or null.type != "EMPTY" or rig not in rigs or
                    null.parent_type != "BONE" or null.parent_bone != bone or
                    bone not in rig.pose.bones):
                raise ValueError("Broken explicit bone attachment: " + hull.name)
            attachment = rig.matrix_world @ rig.pose.bones[bone].matrix
            binding = dict(kind="bone", owner=rig.name, bone=bone,
                           parent=object_record(null, units))
        else:
            owner = hull.parent
            if owner is None:
                raise ValueError("Unattached convex: " + hull.name)
            attachment = owner.matrix_world
            attachment_owners.add(owner)
            binding = dict(kind="object", owner=owner.name)
        binding["attachmentToWorld"] = matrix(attachment, units)
        binding["localToAttachment"] = matrix(
            attachment.inverted() @ hull.matrix_world, units)
        convex.append(dict(**object_record(hull, units), attachment=binding,
                           vertices=[vector(v.co, units) for v in hull.data.vertices],
                           polygons=[list(p.vertices) for p in hull.data.polygons]))
    anchors = []
    for obj in sorted(bpy.data.objects, key=lambda o: o.name):
        if obj.get("megacity_ground_anchor_role") != "anchor":
            continue
        if obj.parent is None:
            raise ValueError("Unattached explicit anchor: " + obj.name)
        anchors.append(dict(**object_record(obj, units), owner=obj.parent.name,
                            ownerLocalPosition=vector(obj.parent.matrix_world.inverted()
                                                      @ obj.matrix_world.translation, units)))
        attachment_owners.add(obj.parent)
    rig_records = []
    for rig in rigs:
        bones = [dict(name=b.name, parent=b.parent.name if b.parent else None,
                      restMatrix=matrix(b.matrix_local, units),
                      poseMatrix=matrix(rig.pose.bones[b.name].matrix, units),
                      deform=b.use_deform) for b in rig.data.bones]
        rig_records.append(dict(**object_record(rig, units), bones=bones,
                                posePosition=rig.data.pose_position))
    return dict(schemaVersion=1, coordinateSystem="BlenderRH_Zup_m",
                sourceUnitScaleMetres=units, sourceFrame=bpy.context.scene.frame_current,
                carrier=object_record(carrier, units),
                renderObjects=[render_record(o, units) for o in render],
                owners=[object_record(o, units) for o in sorted(attachment_owners, key=lambda o: o.name)],
                rigs=rig_records, convex=convex, anchors=anchors)


def material_images(render):
    images, visited = {}, set()

    def visit(tree):
        if tree is None or tree.as_pointer() in visited:
            return
        visited.add(tree.as_pointer())
        for node in tree.nodes:
            if node.type == "TEX_IMAGE" and node.image is not None:
                images[node.image.name] = node.image
            elif node.type == "GROUP":
                visit(node.node_tree)

    for obj in render:
        for material in obj.data.materials:
            if material is not None and material.use_nodes:
                visit(material.node_tree)
    return [images[name] for name in sorted(images)]


def textures_to_disk(render, output):
    rows = []
    for index, image in enumerate(material_images(render)):
        if image.source != "FILE":
            raise ValueError("Unsupported texture source: " + image.name)
        original = image.filepath
        name = re.sub(r"[^A-Za-z0-9_.-]", "_", image.name)
        extension = Path(original).suffix or Path(name).suffix
        if not extension:
            raise ValueError("Texture has no explicit format: " + image.name)
        target = output / ("tex_%02d_%s%s" % (index, Path(name).stem, extension))
        if image.packed_file is not None:
            target.write_bytes(bytes(image.packed_file.data))
        else:
            source = Path(bpy.path.abspath(original))
            if not source.is_file():
                raise FileNotFoundError("Missing texture: " + image.name)
            shutil.copyfile(source, target)
        image.filepath = str(target)
        rows.append(dict(image=image.name, file=target.name, sha256=digest(target),
                         bytes=target.stat().st_size))
    return rows


def export_one(source, root):
    before = digest(source)
    output = root / source.stem
    if output.exists():
        raise FileExistsError("Use a fresh staging directory; will not overwrite " + output.name)
    output.mkdir()
    bpy.ops.wm.open_mainfile(filepath=str(source), load_ui=False, use_scripts=False)
    bpy.context.view_layer.update()
    collection = bpy.data.collections.get("SOURCE")
    if collection is None:
        raise ValueError("No SOURCE collection")
    render = sorted((o for o in collection.all_objects if o.type == "MESH"), key=lambda o: o.name)
    if not render or any(not o.data.polygons for o in render):
        raise ValueError("Missing or empty SOURCE render mesh")
    rigs = set(o for o in collection.all_objects if o.type == "ARMATURE")
    rigs.update(m.object for o in render for m in o.modifiers
                if m.type == "ARMATURE" and m.object is not None)
    rigs = sorted(rigs, key=lambda o: o.name)
    units = bpy.context.scene.unit_settings.scale_length
    physics = physics_record(render, rigs, units)
    textures = textures_to_disk(render, output)
    selected = set(render + rigs)
    for obj in tuple(selected):
        parent = obj.parent
        while parent is not None:
            if parent not in selected and parent.type != "EMPTY":
                raise ValueError("Unselected non-empty source parent: " + parent.name)
            selected.add(parent)
            parent = parent.parent
    for obj in bpy.context.view_layer.objects:
        obj.select_set(False)
    for obj in selected:
        obj.hide_set(False)
        obj.hide_viewport = False
        obj.select_set(True)
    bpy.context.view_layer.objects.active = render[0]
    options = dict(use_selection=True, object_types={"MESH", "ARMATURE", "EMPTY"},
                   use_mesh_modifiers=False, mesh_smooth_type="OFF", use_tspace=True,
                   add_leaf_bones=False, use_armature_deform_only=False, bake_anim=False,
                   use_custom_props=False, path_mode="STRIP", embed_textures=False,
                   apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS",
                   global_scale=1.0, axis_forward="-Z", axis_up="Y", bake_space_transform=False)
    fbx = output / (source.stem + ".fbx")
    result = bpy.ops.export_scene.fbx(filepath=str(fbx), **options)
    if "FINISHED" not in result or not fbx.is_file():
        raise RuntimeError("FBX export did not finish")
    sidecar = output / (source.stem + ".physics.json")
    write_json(sidecar, physics)
    write_json(output / "inputs.json", [dict(role="cut-physics", target="scene", path=sidecar.name)])
    after = digest(source)
    if after != before:
        raise RuntimeError("Source blend changed during export")
    manifest = dict(schemaVersion=1, complete=True, blenderVersion=bpy.app.version_string,
                    sourceBlend=source.name, sourceBlendSha256=before,
                    sourceBlendSha256After=after, sourceUnchanged=True,
                    fbx=fbx.name, inputs="inputs.json", textures=textures,
                    statistics=dict(renderObjects=len(render), rigs=len(rigs),
                                    convex=len(physics["convex"]), anchors=len(physics["anchors"])),
                    exporter={k: sorted(v) if isinstance(v, set) else v for k, v in options.items()},
                    files=[dict(file=p.name, bytes=p.stat().st_size, sha256=digest(p))
                           for p in sorted(output.iterdir()) if p.is_file()])
    write_json(output / "export.json", manifest)
    return manifest["statistics"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sources", nargs="+", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--only", nargs="+", help="Optional exact .blend file stems for a pilot subset")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    roots = [Path(p).resolve(strict=True) for p in args.sources]
    sources = sorted(p for root in roots for p in root.glob("*.blend") if p.is_file())
    if args.only:
        requested = set(args.only)
        sources = [p for p in sources if p.stem in requested]
        if {p.stem for p in sources} != requested:
            raise ValueError("Some --only stems were not found")
    if not sources or len({p.stem.casefold() for p in sources}) != len(sources):
        raise ValueError("No blend inputs or duplicate asset file stems")
    output = Path(args.out).resolve()
    if any(output == p or p in output.parents for p in roots):
        raise ValueError("Output must not be inside an input directory")
    output.mkdir(parents=True, exist_ok=True)
    failures = 0
    with (output / "batch.jsonl").open("x", encoding="utf-8") as log:
        for number, source in enumerate(sources, 1):
            row = dict(number=number, total=len(sources), sourceBlend=source.name)
            print("EXPORT START %d/%d %s" % (number, len(sources), source.name), flush=True)
            try:
                row.update(status="complete", statistics=export_one(source, output))
            except Exception as error:
                failures += 1
                row.update(status="failed", error=str(error))
                traceback.print_exc()
            log.write(json.dumps(row, ensure_ascii=False) + "\n")
            log.flush()
            print("EXPORT RESULT " + json.dumps(row, ensure_ascii=False), flush=True)
    if failures:
        raise RuntimeError("%d asset exports failed; see batch.jsonl" % failures)


if __name__ == "__main__":
    main()
