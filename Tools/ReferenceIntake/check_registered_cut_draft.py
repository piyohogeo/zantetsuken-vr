"""Read a registered cut draft in Blender 4.5, without opening its source .blend.

  blender --background --factory-startup --python check_registered_cut_draft.py -- \
      --root <private repository> --asset-sha <sha> [--asset-sha <sha>] --out <private report.json>

Only the report and a disposable private staging directory are written. This is an
offline intake/coordinate check, not Runtime, PhysX, cut DAG or cut-output validation.
No mesh, winding, owner, anchor or binding is repaired.
"""
import argparse
from collections import defaultdict
import hashlib
import json
import math
from pathlib import Path
import shutil
import sys
import tempfile
import traceback

import bpy
from mathutils import Matrix, Vector


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def finite(values):
    return all(math.isfinite(float(value)) for value in values)


def matrix(rows):
    if len(rows) != 4 or any(len(row) != 4 or not finite(row) for row in rows):
        raise ValueError("expected finite 4 by 4 row-major matrix")
    return Matrix(rows)


def bounds(points):
    return [[min(p[axis] for p in points) for axis in range(3)],
            [max(p[axis] for p in points) for axis in range(3)]]


def point_delta(a, b):
    return max(abs(float(x) - float(y)) for x, y in zip(a, b))


def convex_gate(vertices, faces, tolerance):
    """Small explicit closed outward convex-polyhedron check; no construction/repair.

    Face planarity, convexity, opposite paired edges, one vertex fan, connectivity,
    positive volume, and all vertices inside each outward face are checked. Tests
    use a declared distance tolerance. This is not a replacement for product gates.
    """
    result = {"vertices": len(vertices), "faces": len(faces), "limit": 128,
              "distanceTolerance": tolerance, "failures": []}
    errors = result["failures"]
    if not vertices or any(len(p) != 3 or not finite(p) for p in vertices):
        errors.append("empty or non-finite vertices")
        return result
    if len(vertices) > 128:
        errors.append("L128 exceeded")
    points = [Vector(p) for p in vertices]
    edges = defaultdict(list)
    incident = defaultdict(set)
    volume = 0.0
    max_outside = 0.0
    for index, face in enumerate(faces):
        if (len(face) < 3 or len(set(face)) != len(face)
                or any(not isinstance(v, int) or v < 0 or v >= len(points) for v in face)):
            errors.append("invalid face %d" % index)
            continue
        polygon = [points[v] for v in face]
        area = Vector((0.0, 0.0, 0.0))
        origin = polygon[0]
        for a, b in zip(polygon[1:-1], polygon[2:]):
            area += (a - origin).cross(b - origin)
            volume += origin.dot(a.cross(b)) / 6.0
        if not finite(area) or area.length <= tolerance * tolerance:
            errors.append("degenerate face %d" % index)
        else:
            normal = area.normalized()
            distances = [normal.dot(p - origin) for p in points]
            max_outside = max(max_outside, max(distances))
            if max(distances) > tolerance:
                errors.append("non-convex or inward face %d" % index)
            if any(abs(distances[v]) > tolerance for v in face):
                errors.append("non-planar face %d" % index)
            for j, p in enumerate(polygon):
                previous = polygon[j - 1]
                following = polygon[(j + 1) % len(polygon)]
                if (p - previous).cross(following - p).dot(normal) < -tolerance * tolerance:
                    errors.append("non-convex polygon %d" % index)
                    break
        for a, b in zip(face, face[1:] + face[:1]):
            edges[min(a, b), max(a, b)].append((a, b, index))
            incident[a].add(index)
    neighbours = defaultdict(set)
    bad_edges = 0
    for uses in edges.values():
        if len(uses) != 2 or uses[0][:2] != uses[1][:2][::-1]:
            bad_edges += 1
        if len(uses) == 2:
            neighbours[uses[0][2]].add(uses[1][2])
            neighbours[uses[1][2]].add(uses[0][2])
    if bad_edges:
        errors.append("unpaired/inconsistently wound edges: %d" % bad_edges)
    for vertex, face_set in incident.items():
        reached = set()
        todo = [next(iter(face_set))]
        while todo:
            current = todo.pop()
            if current in reached:
                continue
            reached.add(current)
            # Only edges containing this vertex connect its incident-face fan.
            for edge, uses in edges.items():
                if vertex in edge and any(use[2] == current for use in uses):
                    todo.extend(use[2] for use in uses if use[2] not in reached)
        if reached != face_set:
            errors.append("disconnected vertex fan %d" % vertex)
    reached = set()
    todo = [0] if faces else []
    while todo:
        current = todo.pop()
        if current not in reached:
            reached.add(current)
            todo.extend(neighbours[current] - reached)
    if len(reached) != len(faces) or len(incident) != len(points):
        errors.append("disconnected faces or unused vertices")
    if not math.isfinite(volume) or volume <= tolerance ** 3:
        errors.append("non-positive/degenerate signed volume")
    result.update(edges=len(edges), badEdges=bad_edges, signedVolume=volume,
                  maximumOutsideDistance=max_outside, bounds=bounds(points), passed=not errors)
    return result


def verified_blob(base, entry):
    name = entry["blob"]
    if Path(name).name != name:
        raise ValueError("blob name must not include a directory")
    path = base / "blobs" / name
    if sha256(path) != entry["sha256"] or path.stat().st_size != entry["bytes"]:
        raise ValueError("registered blob hash/length mismatch: " + name)
    return path


def stage_file(directory, entry, source):
    name = entry["file"]
    if Path(name).name != name:
        raise ValueError("staged filename must not include a directory")
    target = directory / name
    if target.exists() and sha256(target) != entry["sha256"]:
        raise ValueError("conflicting staged filenames: " + name)
    shutil.copyfile(source, target)
    return target


def consume_physics(data, plane, epsilon, tolerance):
    if data.get("schemaVersion") != 1 or data.get("coordinateSystem") != "BlenderRH_Zup_m":
        raise ValueError("unsupported cut-physics schema/coordinate system")
    objects = {item["name"]: item for item in
               [data["carrier"]] + data["renderObjects"] + data["rigs"] + data.get("owners", [])}
    rigs = {item["name"]: item for item in data["rigs"]}
    report = {"coordinateSystem": data["coordinateSystem"], "sourceFrame": data["sourceFrame"],
              "sourceUnitScaleMetres": data["sourceUnitScaleMetres"], "convex": [],
              "anchors": [], "failures": [], "testPlaneWorld": plane, "anchorEpsilon": epsilon}
    for hull in data["convex"]:
        attachment = hull["attachment"]
        owner = objects[attachment["owner"]]
        attachment_world = matrix(owner["worldMatrix"])
        if attachment["kind"] == "bone":
            rig = rigs[attachment["owner"]]
            bone = next(b for b in rig["bones"] if b["name"] == attachment["bone"])
            attachment_world = attachment_world @ matrix(bone["poseMatrix"])
        elif attachment["kind"] != "object":
            raise ValueError("unknown attachment kind")
        recorded_attachment = matrix(attachment["attachmentToWorld"])
        assembled = attachment_world @ matrix(attachment["localToAttachment"])
        recorded_world = matrix(hull["worldMatrix"])
        delta = max(abs(assembled[r][c] - recorded_world[r][c])
                    for r in range(4) for c in range(4))
        attachment_delta = max(abs(attachment_world[r][c] - recorded_attachment[r][c])
                               for r in range(4) for c in range(4))
        vertices = [list(assembled @ Vector(point)) for point in hull["vertices"]]
        gate = convex_gate(vertices, hull["polygons"], tolerance)
        gate.update(name=hull["name"], attachmentKind=attachment["kind"],
                    owner=attachment["owner"], bone=attachment.get("bone"),
                    reconstructedWorldMatrixDelta=delta, attachmentMatrixDelta=attachment_delta)
        if delta > tolerance or attachment_delta > tolerance:
            gate["failures"].append("attachment/world transform mismatch")
        gate["passed"] = not gate["failures"]
        report["convex"].append(gate)
    for anchor in data["anchors"]:
        owner_world = matrix(objects[anchor["owner"]]["worldMatrix"])
        position = owner_world @ Vector(anchor["ownerLocalPosition"])
        reference = matrix(anchor["worldMatrix"]).translation
        delta = point_delta(position, reference)
        distance = sum(plane[i] * position[i] for i in range(3)) + plane[3]
        valid = finite(position) and math.isfinite(distance) and delta <= tolerance
        side = ("positive" if distance > epsilon else "negative" if distance < -epsilon
                else "both") if valid else "invalid"
        report["anchors"].append(dict(name=anchor["name"], owner=anchor["owner"],
                                      worldPosition=list(position), ownerMappingDelta=delta,
                                      signedDistance=distance, inheritedBy=side, passed=valid))
    if any(not row["passed"] for row in report["convex"]):
        report["failures"].append("one or more convex/frame checks failed")
    if any(not row["passed"] for row in report["anchors"]):
        report["failures"].append("one or more anchor checks failed")
    report["passed"] = not report["failures"]
    return report


def evaluated_bounds(obj):
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    try:
        return bounds([evaluated.matrix_world @ vertex.co for vertex in mesh.vertices])
    finally:
        evaluated.to_mesh_clear()


def weighted_bones(obj, rig):
    bone_names = set(rig.data.bones.keys())
    groups = {group.index: group.name for group in obj.vertex_groups if group.name in bone_names}
    return sorted({groups[weight.group] for vertex in obj.data.vertices for weight in vertex.groups
                   if weight.group in groups and weight.weight > 0})


def inspect_fbx(path, stage, data, textures, tolerance):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    result = bpy.ops.import_scene.fbx(filepath=str(path), use_image_search=False,
                                    use_custom_normals=True, automatic_bone_orientation=False)
    if "FINISHED" not in result:
        raise RuntimeError("registered FBX import failed")
    bpy.context.view_layer.update()
    report = {"objects": [], "rigs": [], "convexAttachments": [], "textures": [], "failures": [],
              "absoluteToleranceMetres": tolerance,
              "relativeBoundsTolerance": 1e-5, "readSourceBlend": False}
    expected_names = {item["name"] for item in data["renderObjects"]}
    imported_names = {obj.name for obj in bpy.data.objects if obj.type == "MESH"}
    if expected_names != imported_names:
        report["failures"].append("visual mesh names differ")
    report["missingMeshes"] = sorted(expected_names - imported_names)
    report["unexpectedMeshes"] = sorted(imported_names - expected_names)
    for expected in data["renderObjects"]:
        obj = bpy.data.objects.get(expected["name"])
        if obj is None or obj.type != "MESH":
            continue
        row = dict(name=obj.name, vertices=len(obj.data.vertices), polygons=len(obj.data.polygons),
                   worldBounds=evaluated_bounds(obj), failures=[])
        expected_bounds = [expected["posedWorldBounds"]["min"], expected["posedWorldBounds"]["max"]]
        extent = max(expected_bounds[1][i] - expected_bounds[0][i] for i in range(3))
        allowed = tolerance + abs(extent) * 1e-5
        delta = max(point_delta(a, b) for a, b in zip(row["worldBounds"], expected_bounds))
        row.update(expectedPosedWorldBounds=expected_bounds, boundsMaximumDelta=delta,
                   boundsTolerance=allowed)
        if delta > allowed:
            row["failures"].append("posed world bounds/scale differ")
        bindings = []
        for modifier in obj.modifiers:
            if modifier.type == "ARMATURE" and modifier.object is not None:
                bindings.append(dict(object=modifier.object.name,
                                     weightedBones=weighted_bones(obj, modifier.object)))
        row["armatureBindings"] = bindings
        if sorted(bindings, key=lambda b: b["object"]) != sorted(
                expected["armatureBindings"], key=lambda b: b["object"]):
            row["failures"].append("armature/weighted bone bindings differ")
        row["passed"] = not row["failures"]
        report["objects"].append(row)
    for expected in data["rigs"]:
        obj = bpy.data.objects.get(expected["name"])
        row = dict(name=expected["name"], failures=[], bones=[])
        if obj is None or obj.type != "ARMATURE":
            row["failures"].append("missing armature")
        else:
            names = {bone["name"] for bone in expected["bones"]}
            if names != set(obj.data.bones.keys()):
                row["failures"].append("bone names differ")
            rig_world = matrix(expected["worldMatrix"])
            for reference in expected["bones"]:
                bone = obj.data.bones.get(reference["name"])
                if bone is None:
                    continue
                expected_head = (rig_world @ matrix(reference["restMatrix"])).translation
                actual_head = (obj.matrix_world @ bone.matrix_local).translation
                delta = point_delta(actual_head, expected_head)
                parent = bone.parent.name if bone.parent else None
                row["bones"].append(dict(name=bone.name, parent=parent,
                                         restHeadWorldDelta=delta))
                if parent != reference["parent"] or delta > tolerance:
                    row["failures"].append("bone parent/rest head differs: " + bone.name)
        row["passed"] = not row["failures"]
        report["rigs"].append(row)
    # Use the imported owners, not the sidecar's duplicate owner matrices, to
    # check that a consumer can actually attach each convex to this FBX.
    for hull in data["convex"]:
        attachment = hull["attachment"]
        owner = bpy.data.objects.get(attachment["owner"])
        row = dict(name=hull["name"], owner=attachment["owner"],
                   attachmentKind=attachment["kind"], bone=attachment.get("bone"),
                   pointToleranceMetres=tolerance, failures=[])
        if owner is None:
            row["failures"].append("attachment owner missing from imported FBX")
        else:
            imported_attachment = owner.matrix_world
            if attachment["kind"] == "bone":
                bone = (owner.pose.bones.get(attachment["bone"])
                        if owner.type == "ARMATURE" else None)
                if bone is None:
                    row["failures"].append("attachment bone missing from imported FBX")
                else:
                    imported_attachment = imported_attachment @ bone.matrix
            elif attachment["kind"] != "object":
                row["failures"].append("unknown attachment kind")
            if not row["failures"]:
                actual = imported_attachment @ matrix(attachment["localToAttachment"])
                reference = matrix(hull["worldMatrix"])
                distances = [(actual @ Vector(v) - reference @ Vector(v)).length
                             for v in hull["vertices"]]
                maximum = max(distances, default=math.inf)
                row["maximumPointDeltaMetres"] = maximum if math.isfinite(maximum) else None
                if not finite(distances) or maximum > tolerance:
                    row["failures"].append("convex placement differs on imported FBX owner")
        row["passed"] = not row["failures"]
        report["convexAttachments"].append(row)
    registered = {entry["file"]: entry for entry in textures}
    used = set()
    for image in bpy.data.images:
        if image.source != "FILE":
            continue
        image_path = Path(bpy.path.abspath(image.filepath)).resolve()
        entry = registered.get(image_path.name)
        ok = (entry is not None and image_path.parent == stage.resolve()
              and image_path.is_file() and sha256(image_path) == entry["sha256"])
        report["textures"].append(dict(name=image.name, file=image_path.name,
                                       resolvedFromPrivateStage=ok))
        if ok:
            used.add(image_path.name)
        else:
            report["failures"].append("texture unresolved or outside registered stage: " + image.name)
    report["registeredTexturesNotReferenced"] = sorted(set(registered) - used)
    if report["registeredTexturesNotReferenced"]:
        report["failures"].append("registered textures not referenced by imported FBX")
    if any(not row["passed"] for row in
           report["objects"] + report["rigs"] + report["convexAttachments"]):
        report["failures"].append("one or more visual transform/binding checks failed")
    report["passed"] = not report["failures"]
    return report


def check_asset(root, asset_sha, report_parent, args):
    if len(asset_sha) != 64 or any(c not in "0123456789abcdef" for c in asset_sha):
        raise ValueError("asset SHA must be 64 lower-case hex digits")
    base = root / "Working" / "Phase0.21"
    manifest = read_json(base / "registry" / "manifests" / (asset_sha + ".json"))
    if (manifest["assetSha"] != asset_sha or
            hashlib.sha256(manifest["canonical"].encode("utf-8")).hexdigest() != asset_sha):
        raise ValueError("asset/canonical identity mismatch")
    inputs = [entry for entry in manifest.get("inputs", [])
              if entry["role"] == "cut-physics" and entry["target"] == "scene"]
    if len(inputs) != 1:
        raise ValueError("expected exactly one explicit cut-physics/scene input")
    entries = [manifest["fbx"]] + manifest["textures"] + manifest.get("inputs", [])
    paths = {entry["blob"]: verified_blob(base, entry) for entry in entries}
    data = read_json(paths[inputs[0]["blob"]])
    normal_length = math.sqrt(sum(value * value for value in args.plane[:3]))
    plane = [value / normal_length for value in args.plane]
    physics = consume_physics(data, plane, args.epsilon, args.tolerance)
    # This context only removes the unique directory it creates, never registry blobs.
    with tempfile.TemporaryDirectory(prefix="cut-draft-check-", dir=report_parent) as temporary:
        stage = Path(temporary)
        fbx = stage_file(stage, manifest["fbx"], paths[manifest["fbx"]["blob"]])
        for entry in manifest["textures"]:
            stage_file(stage, entry, paths[entry["blob"]])
        visual = inspect_fbx(fbx, stage, data, manifest["textures"], args.tolerance)
    return dict(assetSha=asset_sha, name=manifest["name"], verifiedBlobCount=len(paths),
                physics=physics, visual=visual, passed=physics["passed"] and visual["passed"])


def main():
    if bpy.app.version[:2] != (4, 5):
        raise RuntimeError("this intake check uses the fixed Blender 4.5 importer")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True)
    parser.add_argument("--asset-sha", action="append", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--plane", nargs=4, type=float, required=True, metavar=("NX", "NY", "NZ", "D"))
    parser.add_argument("--epsilon", type=float, default=1e-5)
    parser.add_argument("--tolerance", type=float, default=1e-5)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    if (not finite(args.plane) or sum(value * value for value in args.plane[:3]) <= 0
            or not math.isfinite(args.epsilon) or args.epsilon < 0
            or not math.isfinite(args.tolerance) or args.tolerance <= 0):
        raise ValueError("invalid plane, epsilon or tolerance")
    output = Path(args.out).resolve()
    repository = Path(__file__).resolve().parents[2]
    if output == repository or repository in output.parents:
        raise ValueError("asset-specific reports must stay outside the code repository")
    if output.exists():
        raise FileExistsError("use a new report path; existing evidence is not overwritten")
    output.parent.mkdir(parents=True, exist_ok=True)
    report = dict(blenderVersion=bpy.app.version_string, assets=[],
                  scope="registered draft intake only; not Runtime, PhysX, DAG, rendering or cut-output validation")
    for asset_sha in args.asset_sha:
        print("CHECK START " + asset_sha, flush=True)
        try:
            result = check_asset(Path(args.root).resolve(strict=True), asset_sha, output.parent, args)
        except Exception as error:
            result = dict(assetSha=asset_sha, passed=False, error=str(error))
            traceback.print_exc()
        report["assets"].append(result)
        print("CHECK RESULT %s passed=%s" % (asset_sha, result["passed"]), flush=True)
    report["passed"] = all(item["passed"] for item in report["assets"])
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    if not report["passed"]:
        raise RuntimeError("registered draft checks failed; see private report")


if __name__ == "__main__":
    main()
