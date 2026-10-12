# SPDX-License-Identifier: MIT
"""Run the Apache-2.0 upstream add-on in an isolated Blender process."""
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace

import addon_utils
import bmesh
import bpy

ROOT = Path(__file__).resolve().parent
OUT = ROOT/'generated'
ADDON = ROOT/'addons'/'convex_decomposition.py'
BINARY = ROOT/'vhacd'/'TestVHACD.exe'
REPORT = OUT/'convex_report.json'
report = {'status':'started','source_blend':bpy.data.filepath,
    'requested':{'max_hulls':32,'max_vertices_per_hull':32,'voxel_resolution':1000000},
    'blender_version':bpy.app.version_string,'addon_source':str(ADDON),
    'addon_upstream':'https://github.com/olitheolix/blender-convex-decomposition',
    'addon_license':'Apache-2.0','solver_binary':str(BINARY),
    'adapter':'Execution adapter adds explicit -h 32; upstream add-on source is unchanged.'}

def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def write_report():
    REPORT.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')

def operator_exists(name):
    category, operation = name.split('.')
    try:
        getattr(getattr(bpy.ops,category),operation).get_rna_type()
        return True
    except (AttributeError,RuntimeError,TypeError,KeyError):
        return False

def hull_stats(obj):
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.normal_update()
    coords = [v.co.copy() for v in bm.verts]
    lo = [min(v[a] for v in coords) for a in range(3)]
    hi = [max(v[a] for v in coords) for a in range(3)]
    diag = math.sqrt(sum((b-a)**2 for a,b in zip(lo,hi)))
    epsilon = max(1e-7,diag*1e-6)
    volume = bm.calc_volume(signed=True)
    sign = 1 if volume>=0 else -1
    maximum_plane_violation = max(sign*f.normal.dot(v-f.verts[0].co) for f in bm.faces for v in coords)
    data = {'name':obj.name,'vertices':len(bm.verts),'faces':len(bm.faces),
        'triangles':sum(len(f.verts)-2 for f in bm.faces),
        'boundary_edges':sum(e.is_boundary for e in bm.edges),
        'edges_not_two_faces':sum(len(e.link_faces)!=2 for e in bm.edges),
        'non_contiguous_edges':sum(not e.is_contiguous for e in bm.edges),
        'non_manifold_vertices':sum(not v.is_manifold for v in bm.verts),
        'zero_area_faces':sum(f.calc_area()<=diag*diag*1e-14 for f in bm.faces),
        'euler_characteristic':len(bm.verts)-len(bm.edges)+len(bm.faces),
        'signed_volume':volume,'maximum_plane_violation':maximum_plane_violation,
        'convexity_epsilon':epsilon,'convex':maximum_plane_violation<=epsilon}
    bm.free()
    data['pass'] = (4<=data['vertices']<=32 and data['convex'] and abs(volume)>1e-15
        and data['euler_characteristic']==2
        and not any(data[k] for k in ('boundary_edges','edges_not_two_faces',
            'non_contiguous_edges','non_manifold_vertices','zero_area_faces')))
    return data

def main():
    start = time.perf_counter()
    # Detect collisions before importing or enabling the requested add-on.
    collision = []
    if 'convex_decomposition' in sys.modules:
        collision.append('convex_decomposition is already imported')
    if 'convex_decomposition' in bpy.context.preferences.addons:
        collision.append('convex_decomposition is already enabled')
    for name in ('ConvDecompProperties','ConvDecompPropertiesVHACD','ConvDecompPropertiesCoACD'):
        if hasattr(bpy.types.Scene,name): collision.append('Scene.'+name+' already registered')
    for name in ('opr.convex_decomposition_run','opr.convex_decomposition_select',
                 'opr.convex_decomposition_unreal_export','opr.convex_decomposition_godot_export',
                 'opr.convex_decomposition_split_by_face'):
        if operator_exists(name): collision.append(name+' already registered')
    if collision:
        report.update(status='stopped_conflict',collisions=collision)
        write_report()
        raise RuntimeError('STOP: add-on collision: '+'; '.join(collision))
    report['conflict_check']='No existing ConvDecomp module, preferences, properties, or operators in isolated process.'
    report['addon_sha256']=digest(ADDON)
    report['solver_sha256']=digest(BINARY)
    write_report()
    sys.path.insert(0,str(ADDON.parent))
    module=addon_utils.enable('convex_decomposition',default_set=True,persistent=False)
    if module is None: raise RuntimeError('STOP: requested add-on could not be registered')
    if Path(module.__file__).resolve()!=ADDON:
        report['status']='stopped_conflict'
        raise RuntimeError('STOP: a different convex_decomposition module was loaded')
    report['registered_addon_file']=module.__file__
    report['registered_addon_version']=list(module.bl_info['version'])
    prefs=bpy.context.preferences.addons['convex_decomposition'].preferences
    prefs.vhacd_binary=str(BINARY)
    props=bpy.context.scene.ConvDecompProperties
    props.solver='VHACD'
    props.hull_collection_name='Bunny_VHACD_Convex_32'
    props.tmp_hull_prefix='_BunnyVHACD_tmp_'
    vhacd=bpy.context.scene.ConvDecompPropertiesVHACD
    vhacd.i_voxel_resolution=1000000
    vhacd.i_max_hull_vert_count=32
    report['effective_addon_settings']={name:getattr(vhacd,name) for name in module.ConvexDecompositionPropertiesVHACD.__annotations__}
    solver_root=OUT/'solver_runs'
    solver_root.mkdir(exist_ok=True)
    tempfile.tempdir=str(solver_root)
    real_run=subprocess.run
    def execute_solver(cmd,**kwargs):
        if Path(cmd[0]).resolve()!=BINARY.resolve(): raise RuntimeError('Unexpected solver executable')
        command=[str(part) for part in cmd]+['-h','32']
        for flag,value in (('-h','32'),('-v','32'),('-r','1000000')):
            assert command[command.index(flag)+1]==value
        report['solver_command']=command
        report['solver_directory']=str(kwargs.get('cwd'))
        write_report()
        outcome=real_run(command,**kwargs)
        report['solver_exit_code']=outcome.returncode
        write_report()
        if outcome.returncode: raise RuntimeError('V-HACD failed with exit code '+str(outcome.returncode))
        result_file=Path(kwargs['cwd'])/'decomp.obj'
        raw=result_file.read_bytes()
        try:
            raw.decode('utf-8')
            report['solver_obj_encoding']='UTF-8'
        except UnicodeDecodeError:
            # Native Windows V-HACD puts its input path in an OBJ comment using
            # the Windows code page. Preserve all text and numbers as UTF-8 for
            # the upstream add-on's Path.read_text() call.
            result_file.write_text(raw.decode('cp932'),encoding='utf-8')
            report['solver_obj_encoding']='CP932 converted losslessly to UTF-8 before import'
        write_report()
        return outcome
    # Only redirect the upstream add-on's subprocess call, not any other module.
    module.subprocess=SimpleNamespace(run=execute_solver)
    source=bpy.data.objects['Stanford_Bunny_Official']
    bpy.ops.object.select_all(action='DESELECT')
    source.select_set(True)
    bpy.context.view_layer.objects.active=source
    before=triangles_by_positions(source.data)
    result=bpy.ops.opr.convex_decomposition_run()
    report['operator_result']=sorted(result)
    if 'FINISHED' not in result: raise RuntimeError('Add-on operator did not finish')
    hulls=sorted((obj for obj in bpy.data.objects if obj.type=='MESH' and obj.parent==source
        and obj.name.startswith('UCX_')),key=lambda obj:obj.name)
    report['hull_count']=len(hulls)
    report['hulls']=[hull_stats(obj) for obj in hulls]
    report['source_geometry_preserved']=before==triangles_by_positions(source.data)
    report['total_hull_vertices']=sum(row['vertices'] for row in report['hulls'])
    report['total_hull_triangles']=sum(row['triangles'] for row in report['hulls'])
    report['max_actual_hull_vertices']=max((row['vertices'] for row in report['hulls']),default=0)
    report['generation_seconds']=time.perf_counter()-start
    report['validation_pass']=0<len(hulls)<=32 and all(row['pass'] for row in report['hulls']) and report['source_geometry_preserved']
    if not report['validation_pass']:
        report['status']='validation_failed'
        write_report()
        raise RuntimeError('Convex validation failed')
    for obj in hulls:
        obj.display_type='WIRE'
        obj.hide_render=True
        obj['convex_source']=source.name
        obj['vhacd_max_hulls']=32
        obj['vhacd_max_vertices']=32
        obj['vhacd_voxel_resolution']=1000000
    source['convex_generator']='olitheolix Apache-2.0 add-on + V-HACD 4.1'
    source['convex_hull_count']=len(hulls)
    blend=OUT/'convex.blend'
    bpy.ops.object.select_all(action='DESELECT')
    source.select_set(True)
    bpy.context.view_layer.objects.active=source
    bpy.ops.wm.save_as_mainfile(filepath=str(blend),compress=True)
    report.update(status='success',output_blend=str(blend))
    shutil.copy2(BINARY.parent/'LICENSE.txt',OUT/'V-HACD_LICENSE.txt')
    write_report()
    print('BUNNY_CONVEX_SUCCESS='+json.dumps({key:report[key] for key in ('hull_count','total_hull_vertices','total_hull_triangles','max_actual_hull_vertices','generation_seconds','output_blend')}),flush=True)

def triangles_by_positions(mesh):
    result=[]
    for face in mesh.polygons:
        coords=tuple(tuple(mesh.vertices[i].co) for i in face.vertices)
        result.append(min(coords[i:]+coords[:i] for i in range(len(coords))))
    return sorted(result)

try:
    main()
except Exception as error:
    report['status']=report['status'] if report['status'] in ('stopped_conflict','validation_failed') else 'failed'
    report['error']=repr(error)
    write_report()
    raise
