"""Pure topology planning; no Blender dependency and no geometry changes."""
from collections import defaultdict


def plan_split(vertex_count, edges, faces):
    """faces: sequence of (vertex_indices, edge_indices), in corner order.

    Only edges incident to the vertex connect incident faces. An edge with
    three or more faces connects ALL those faces; edge non-manifoldness is
    deliberately not repaired. Wire edges stay attached to the original vertex.
    """
    incident = defaultdict(list)
    edge_faces = [[] for _ in edges]
    for fi, (verts, face_edges) in enumerate(faces):
        if len(verts) < 3 or len(set(verts)) != len(verts):
            raise ValueError("Degenerate face with fewer than 3 distinct vertices")
        if len(verts) != len(face_edges):
            raise ValueError("Invalid corner/edge count")
        for j, (v, ei) in enumerate(zip(verts, face_edges)):
            if not 0 <= v < vertex_count or not 0 <= ei < len(edges):
                raise ValueError("Invalid vertex/edge index")
            if set(edges[ei]) != {v, verts[(j + 1) % len(verts)]}:
                raise ValueError("Face edge does not match its corners")
            incident[v].append(fi)
            edge_faces[ei].append(fi)

    vertex_edges = defaultdict(list)
    for ei, (a, b) in enumerate(edges):
        if a == b or not (0 <= a < vertex_count and 0 <= b < vertex_count):
            raise ValueError("Invalid or zero-length topological edge")
        vertex_edges[a].append(ei)
        vertex_edges[b].append(ei)

    assignments = {}
    sources = []
    split_vertices = 0
    for v, face_ids in incident.items():
        parent = {fi: fi for fi in face_ids}

        def root(fi):
            while parent[fi] != fi:
                parent[fi] = parent[parent[fi]]
                fi = parent[fi]
            return fi

        for ei in vertex_edges[v]:
            linked = edge_faces[ei]
            if linked:
                for fi in linked[1:]:
                    parent[root(fi)] = root(linked[0])
        components = defaultdict(list)
        for fi in face_ids:
            components[root(fi)].append(fi)
        if len(components) < 2:
            continue
        split_vertices += 1
        # Deterministic: lowest face index keeps the existing vertex.
        for ci, component in enumerate(sorted(components.values(), key=min)):
            target = v
            if ci:
                target = vertex_count + len(sources)
                sources.append(v)
            for fi in component:
                assignments[v, fi] = target

    new_corners = [
        assignments.get((v, fi), v)
        for fi, (verts, _) in enumerate(faces) for v in verts
    ]
    new_edges = []
    for ei, edge in enumerate(edges):
        linked = edge_faces[ei]
        new_edges.append(tuple(
            assignments.get((v, linked[0]), v) if linked else v for v in edge
        ))
    return {
        "sources": sources,
        "corners": new_corners,
        "edges": new_edges,
        "split_vertices": split_vertices,
    }
