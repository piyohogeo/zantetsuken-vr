# SPDX-License-Identifier: MIT
"""Export the explicit Apache add-on hierarchy; never infer a private asset tree."""
import hashlib
import json
from pathlib import Path
import sys
import bpy
from mathutils import Vector

def vec(v):
    return dict(zip(('x','y','z'), v))

root = Path(__file__).resolve().parents[2]
output = root / 'Assets/NonCommercialDemo/StanfordBunny/StanfordBunny.json'
source = bpy.data.objects['Stanford_Bunny_Official']
assert source.type == 'MESH' and source in bpy.data.collections['SOURCE'].objects[:]
hulls = sorted((o for o in bpy.data.objects if o.type == 'MESH' and o.parent == source and o.name.startswith('UCX_')), key=lambda o:o.name)
assert len(hulls) == 32, 'Expected exactly 32 direct UCX children'
collection = bpy.data.collections['Bunny_VHACD_Convex_32']
assert set(hulls) == set(collection.objects), 'Unexpected convex collection member'
points = [source.matrix_world @ v.co for v in source.data.vertices]
origin = Vector(((min(p.x for p in points)+max(p.x for p in points))/2, min(p.y for p in points), (min(p.z for p in points)+max(p.z for p in points))/2))
# Official PLY is Y-up. Reflect Z for Unity and reverse winding; scale BOTH meshes to 4x.
def position(p):
    p = (p-origin)*4
    return vec((p.x,p.y,-p.z))
def normal(n):
    return vec((n.x,n.y,-n.z))
indices = []
for p in source.data.polygons:
    assert len(p.vertices) == 3
    indices.extend(reversed(p.vertices))
data = dict(schemaVersion=1, topologyCount=len(points), name='Stanford Bunny (noncommercial)',
    source='https://graphics.stanford.edu/data/3Dscanrep/',
    sha256=hashlib.sha256((root/'Tools/StanfordBunny/source/bun_zipper.ply').read_bytes()).hexdigest(),
    isBuilding=False, isCuttable=True, anchors=[],
    render=[dict(position=position(p), normal=normal(source.matrix_world.to_3x3() @ v.normal), uv=dict(x=0,y=0)) for p,v in zip(points,source.data.vertices)],
    indices=indices, topology=list(range(len(points))), hulls=[])
for h in hulls:
    assert 4 <= len(h.data.vertices) <= 32
    offsets = [0]
    faces = []
    for p in h.data.polygons:
        faces.extend(reversed(p.vertices))
        offsets.append(len(faces))
    data['hulls'].append(dict(vertices=[position(h.matrix_world @ v.co) for v in h.data.vertices], faceOffsets=offsets, faceIndices=faces))
assert len(indices) == 69664*3
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(data,separators=(',',':')),encoding='utf-8')
(output.parent/'export-manifest.json').write_text(json.dumps(dict(
    schemaVersion=1, sourceSha256=data['sha256'], inputBlendSha256=hashlib.sha256(Path(bpy.data.filepath).read_bytes()).hexdigest(),
    exportSha256=hashlib.sha256(output.read_bytes()).hexdigest(), vertices=len(points), triangles=len(indices)//3,
    maxHulls=32,maxVerticesPerHull=32,voxelResolution=1000000, hullNames=[h.name for h in hulls],
    coordinates='source Y-up, world matrices baked, Z reflected and face winding reversed, same origin and 4x scale for display and hulls',
    license='Stanford custom noncommercial terms; not MIT'),indent=2),encoding='utf-8')
print('BUNNY UNITY EXPORT',len(points),len(indices)//3,len(hulls))
