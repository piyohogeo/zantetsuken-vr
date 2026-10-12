"""Run the Mesh Repair optimized-cap recipe over one canonical asset.

A canonical asset holds every mesh of its source collection, so the recipe
runs per mesh object and the asset passes only when all of them do, following
the prop non-manifold fill probe's Objects[] shape. Identity comes from the
custom properties of whichever object carries them - the rig for a character,
the root mesh for a static asset - never from the blend filename.
"""
import argparse
import json
import math
import os
import sys
import time

import bmesh
import bpy


PROFILE_ID = "ithappy-mesh-repair-optimized-cap-v5"
SOURCE_COLLECTION = "SOURCE"
NON_MANIFOLD_ALL = dict(
    extend=False, use_wire=True, use_boundary=True, use_multi_face=True,
    use_non_contiguous=True, use_verts=True,
)
STAGES = (
    ("Split", "split_bow_tie_vertices", {}),
    ("Welded", "weld_collapsed_boundaries", {}),
    ("IsolatedRemoved", "remove_isolated_vertices", {}),
    ("WiresRemoved", "remove_wire_geometry", {}),
    ("FlapsRemoved", "remove_boundary_flaps", {}),
    ("EnclosedRemoved", "remove_enclosed_multi_face_patches", {}),
    ("CollapsedRemoved", "remove_collapsed_boundary_faces", {}),
    ("CollinearJoined", "join_collinear_boundary_seams", {}),
    ("OptimizedCapped", "optimized_boundary_cap", dict(
        area=True, length=False, shape=False, fold=False,
        repair_coincident=True, allow_opposite_multi_face_edges=True,
        cap_isolated_single_faces=True)),
    ("Triangulated", "preserving_triangulate", dict(
        minimize_four_face_edges=True, simplify_collapsed_quads=True)),
    ("SheetsSplit", "split_balanced_four_face_sheets", {}),
)


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--asset-family", required=True)
    parser.add_argument("--asset-id", required=True)
    parser.add_argument("--addon-parent", required=True)
    parser.add_argument("--addon-version", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--output-blend")
    parser.add_argument("--split-three-face-sheets", action="store_true")
    return parser.parse_args(sys.argv[sys.argv.index("--") + 1:])


def activate(obj):
    if bpy.context.object is not None and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    bpy.ops.object.select_all(action="DESELECT")
    obj.hide_set(False)
    obj.hide_viewport = False
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def select_non_manifold(obj, preset):
    activate(obj)
    bpy.context.tool_settings.mesh_select_mode = (True, False, False)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="DESELECT")
    result = bpy.ops.mesh.select_non_manifold(**preset)
    bm = bmesh.from_edit_mesh(obj.data)
    counts = {
        "OperatorResult": sorted(result),
        "SelectedVertexCount": sum(v.select for v in bm.verts),
        "SelectedEdgeCount": sum(e.select for e in bm.edges),
        "SelectedFaceCount": sum(f.select for f in bm.faces),
    }
    bpy.ops.object.mode_set(mode="OBJECT")
    return counts


def component_count(bm):
    remaining = set(bm.verts)
    count = 0
    while remaining:
        count += 1
        stack = [remaining.pop()]
        while stack:
            vertex = stack.pop()
            for edge in vertex.link_edges:
                other = edge.other_vert(vertex)
                if other in remaining:
                    remaining.remove(other)
                    stack.append(other)
    return count


def balanced_four_face(edge):
    """Mirror the addon's allowed exception: 4 faces traversed exactly 2-to-2."""
    if len(edge.link_faces) != 4:
        return False
    low = min(v.index for v in edge.verts)
    forward = sum(loop.vert.index == low for loop in edge.link_loops)
    return forward == 2 and len(edge.link_loops) == 4


def boundary_loop_sizes(bm):
    """Loop lengths matter: Optimized Cap skips loops above its vertex limit."""
    remaining = {e for e in bm.edges if e.is_boundary}
    sizes = []
    while remaining:
        seed = remaining.pop()
        group = {seed}
        stack = [seed]
        while stack:
            edge = stack.pop()
            for vertex in edge.verts:
                for other in vertex.link_edges:
                    if other in remaining:
                        remaining.remove(other)
                        group.add(other)
                        stack.append(other)
        sizes.append(len({v for e in group for v in e.verts}))
    return sorted(sizes, reverse=True)


def health(obj, addon):
    mesh = obj.data
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.verts.ensure_lookup_table()
    bm.verts.index_update()
    lo = [min((v.co[a] for v in bm.verts), default=0.0) for a in range(3)]
    hi = [max((v.co[a] for v in bm.verts), default=0.0) for a in range(3)]
    diag = math.sqrt(sum((hi[a] - lo[a]) ** 2 for a in range(3)))
    eps = max(1e-6, diag * 1e-6)
    boundary = sum(e.is_boundary for e in bm.edges)
    wire = sum(e.is_wire for e in bm.edges)
    over = [e for e in bm.edges if len(e.link_faces) > 2]
    balanced = sum(balanced_four_face(e) for e in over)
    noncontiguous = sum(len(e.link_faces) == 2 and not e.is_contiguous for e in bm.edges)
    loose = sum(not v.link_edges and not v.link_faces for v in bm.verts)
    degenerate = sum(f.calc_area() <= eps * eps for f in bm.faces)
    finite = all(math.isfinite(c) for v in bm.verts for c in v.co)
    components = component_count(bm)
    signed_volume = bm.calc_volume(signed=True) if bm.faces else 0.0
    loops = boundary_loop_sizes(bm)
    triangles_only = all(len(f.verts) == 3 for f in bm.faces)
    bm.free()
    selection = select_non_manifold(obj, NON_MANIFOLD_ALL)
    bowties = addon.mesh_plan(mesh)["split_vertices"]
    # Upstream DESIGN.md 6.3 accepts zero-area triangles in closed, manifold,
    # winding-consistent geometry, so a degenerate face no longer blocks; the
    # count stays recorded and StrictClosedVolumeCandidate keeps the old rule.
    auxiliary = not any((boundary, wire, len(over), noncontiguous, loose)) and finite
    closed = auxiliary and selection["SelectedVertexCount"] == 0 and abs(signed_volume) > eps ** 3
    strict_closed = closed and degenerate == 0
    return {
        "Vertices": len(mesh.vertices), "Edges": len(mesh.edges),
        "Polygons": len(mesh.polygons),
        "Triangles": sum(max(0, len(p.vertices) - 2) for p in mesh.polygons),
        "AllFacesTriangles": triangles_only,
        "ConnectedComponents": components, "BoundsMin": lo, "BoundsMax": hi,
        "BoundaryEdges": boundary, "BoundaryLoopCount": len(loops),
        "LargestBoundaryLoopVertices": loops[0] if loops else 0,
        "WireEdges": wire, "MultipleFaceEdges": len(over),
        "BalancedFourFaceEdges": balanced,
        "UnbalancedMultipleFaceEdges": len(over) - balanced,
        "NonContiguousEdges": noncontiguous,
        "LooseVertices": loose, "DegenerateFaces": degenerate,
        "FinitePositions": finite, "SignedVolume": signed_volume,
        "BowTieVertexCount": bowties, "NonManifoldSelect": selection,
        "AuxiliaryTopologyGatePass": auxiliary, "ClosedVolumeCandidate": closed,
        "StrictClosedVolumeCandidate": strict_closed,
    }


def repair_one(source_obj, addon, collection):
    """Run the whole recipe on a working copy and report per-stage health."""
    work = source_obj.copy()
    work.data = source_obj.data.copy()
    work.name = source_obj.name + ".repair"
    collection.objects.link(work)

    original_materials = [s.material.name if s.material else None for s in work.material_slots]
    original_parent = work.parent
    original_armatures = [m.object for m in work.modifiers if m.type == "ARMATURE"]
    original_groups = [g.name for g in work.vertex_groups]
    original_uv_layers = [layer.name for layer in work.data.uv_layers]

    original = health(work, addon)
    measurements = {"Original": original}
    operators = {}
    timings = {}
    pre_cap = None
    for key, operator_name, kwargs in STAGES:
        activate(work)
        if key == "OptimizedCapped":
            pre_cap = (len(work.data.vertices), len(work.data.polygons))
        started = time.perf_counter()
        operators[key] = sorted(getattr(bpy.ops.object, operator_name)(scope="SELECTED", **kwargs))
        timings[key] = time.perf_counter() - started
        measurements[key] = health(work, addon)
    final = measurements["SheetsSplit"]

    capped = measurements["OptimizedCapped"]
    order = ["Original"] + [key for key, _, _ in STAGES]
    pre_cap_stage = order[order.index("OptimizedCapped") - 1]
    cap_validation = {
        "PreCapStage": pre_cap_stage,
        "PreCapBoundaryLoopCount": measurements[pre_cap_stage]["BoundaryLoopCount"],
        "PreCapLargestLoopVertices": measurements[pre_cap_stage]["LargestBoundaryLoopVertices"],
        "AddedFaces": capped["Polygons"] - pre_cap[1],
        "VertexCountDelta": capped["Vertices"] - pre_cap[0],
        "MergedVertices": capped["Vertices"] != pre_cap[0],
        "CapSeconds": timings["OptimizedCapped"],
    }
    preservation = {
        "BoundsMaximumAbsoluteErrorM": max(
            abs(original[key][axis] - final[key][axis])
            for key in ("BoundsMin", "BoundsMax") for axis in range(3)
        ),
        "MaterialSlotsPreserved":
            [s.material.name if s.material else None for s in work.material_slots] == original_materials,
        "ParentPreserved": work.parent == original_parent,
        "ArmatureTargetsPreserved":
            [m.object for m in work.modifiers if m.type == "ARMATURE"] == original_armatures,
        "VertexGroupsPreserved": [g.name for g in work.vertex_groups] == original_groups,
        "UVLayersPreserved": [layer.name for layer in work.data.uv_layers] == original_uv_layers,
        "UnweightedVertexCount": sum(not v.groups for v in work.data.vertices),
    }
    preservation_pass = (
        preservation["BoundsMaximumAbsoluteErrorM"] <= 1e-6
        and all(v for k, v in preservation.items() if isinstance(v, bool))
    )
    row = {
        "ObjectName": source_obj.name,
        "MeshName": source_obj.data.name,
        "StageOrder": ["Original"] + [key for key, _, _ in STAGES],
        "StageParameters": {key: kwargs for key, _, kwargs in STAGES},
        "Measurements": measurements,
        "Operators": operators,
        "StageSeconds": timings,
        "CapValidation": cap_validation,
        "Preservation": preservation,
        "PreservationGatePass": preservation_pass,
        "ClosedVolumeCandidate": final["ClosedVolumeCandidate"],
        "StrictClosedVolumeCandidate": final["StrictClosedVolumeCandidate"],
        "RecipeHealthGatePass": final["ClosedVolumeCandidate"] and preservation_pass,
        "StrictRecipeHealthGatePass": final["StrictClosedVolumeCandidate"] and preservation_pass,
    }
    return row, work


def main():
    global STAGES, PROFILE_ID
    options = parse_args()
    if options.split_three_face_sheets:
        STAGES = STAGES[:8] + (("ThreeFaceSheetsSplit", "split_three_face_sheets", {}),) + STAGES[8:]
        PROFILE_ID = "ithappy-mesh-repair-optimized-cap-v6"
    started = time.perf_counter()
    # Capture the source before any save_as_mainfile moves bpy.data.filepath.
    source_blend_path = os.path.abspath(bpy.data.filepath)
    sys.path.insert(0, os.path.abspath(options.addon_parent))
    import bow_tie_vertex_split as addon
    addon.register()

    carrier = next(
        (o for o in bpy.data.objects
         if o.get("zpp_asset_id") == options.asset_id
         and o.get("zpp_asset_family") == options.asset_family),
        None,
    )
    if carrier is None:
        raise RuntimeError(
            f"No rig tagged {options.asset_family}--{options.asset_id} in {bpy.data.filepath}")
    source = bpy.data.collections.get(SOURCE_COLLECTION)
    if source is None:
        raise RuntimeError("Canonical has no SOURCE collection")
    # SOURCE holds the character meshes; the WGT-* bone shapes sit in RIG_SUPPORT.
    source_objects = sorted(
        (o for o in source.all_objects if o.type == "MESH"), key=lambda o: o.name)
    if not source_objects:
        raise RuntimeError("SOURCE collection holds no mesh")

    collection = bpy.data.collections.new("MESH_REPAIR_PROBE")
    bpy.context.scene.collection.children.link(collection)
    rows = []
    works = []
    for source_obj in source_objects:
        row, work = repair_one(source_obj, addon, collection)
        rows.append(row)
        works.append((source_obj, work))

    asset_pass = all(row["RecipeHealthGatePass"] for row in rows)
    output_blend = None
    if options.output_blend and asset_pass:
        # Replace only the tested meshes; the canonical hierarchy is untouched.
        for source_obj, work in works:
            source_obj.data = work.data
            source_obj.data.name = source_obj.name + "_MeshRepair"
            bpy.data.objects.remove(work, do_unlink=True)
        carrier["zpp_mesh_repair_profile"] = PROFILE_ID
        carrier["zpp_mesh_repair_addon_version"] = options.addon_version
        output_blend = os.path.abspath(options.output_blend)
        os.makedirs(os.path.dirname(output_blend), exist_ok=True)
        bpy.ops.wm.save_as_mainfile(filepath=output_blend, compress=True)

    report = {
        "SchemaVersion": 1, "ProfileId": PROFILE_ID,
        "AddonVersion": options.addon_version, "BlenderVersion": bpy.app.version_string,
        "AssetFamily": options.asset_family, "AssetId": options.asset_id,
        "SourceBlendPath": source_blend_path,
        "Recipe": [
            "Split Selected Meshes", "Weld Selected Meshes", "Remove Isolated",
            "Remove Wires", "Remove Flaps", "Remove Enclosed",
            "Remove Collapsed", "Join Collinear",
            *(["Split 3-Face Sheets"] if options.split_three_face_sheets else []),
            "Optimized Cap (Total Area, Repair Coincident ON, Allow Opposite 4-Face Edges ON,"
            " Cap Isolated Single Faces ON)",
            "Preserving Triangulate (Minimize New 4-Face Edges, Simplify Collapsed Quads)",
            "Split Balanced 4-Face Sheets",
        ],
        "MeshObjectCount": len(rows),
        "OriginalClosedVolumeCandidate":
            all(r["Measurements"]["Original"]["ClosedVolumeCandidate"] for r in rows),
        "RepairedClosedVolumeCandidate": all(r["ClosedVolumeCandidate"] for r in rows),
        "PreservationGatePass": all(r["PreservationGatePass"] for r in rows),
        "RecipeHealthGatePass": asset_pass,
        "StrictRecipeHealthGatePass": all(r["StrictRecipeHealthGatePass"] for r in rows),
        "OutputBlendPath": output_blend,
        "ElapsedSeconds": time.perf_counter() - started,
        "Objects": rows,
    }
    path = os.path.abspath(options.output)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(f"MESH_REPAIR={options.asset_family}:{options.asset_id}"
          f":meshes={len(rows)}:pass={asset_pass}")


main()
