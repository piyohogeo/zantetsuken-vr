using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.ReferenceIntake
{
    /// <summary>Migration diagnostic for the three pinned representatives, not a generic topology reconstruction tool.</summary>
    public static class Compact16uvProductIntake
    {
        public const string PrivateRoot = "Assets/Licensed/Compact16uvIntake";
        public const string Output = "docs/diagnostics/compact16uv-intake";
        public static void Run()
        {
            var input=JsonUtility.FromJson<Input>(File.ReadAllText(PrivateRoot+"/intake.json"));
            var report=new Report { unity=Application.unityVersion, inputManifestSha256=FileHash(PrivateRoot+"/intake.json"), results=new List<Result>() };
            foreach(var entry in input.assets)
            {
                var result=new Result { family=entry.family, asset=Path.GetFileName(entry.assetPath), sourceSha256=entry.sourceSha256 };
                report.results.Add(result);
                try { Inspect(entry,result); result.passed=true; }
                catch(Exception error) { result.failure=error.Message; Debug.LogWarning(entry.family+": "+error.Message); }
            }
            report.passed=report.results.Count==3 && report.results.All(x=>x.passed);
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output+"/intake.json",JsonUtility.ToJson(report,true));
            AssetDatabase.Refresh();
            if(!report.passed) throw new InvalidOperationException("Representative intake failed; see "+Output+"/intake.json");
            Debug.Log("Compact16uv product intake: 3/3 source contracts verified; Static16 Megacity written; Characters remain skinned.");
        }
        private static void Inspect(Entry entry,Result result)
        {
            if(FileHash(entry.assetPath)!=entry.sourceSha256) throw new Exception("source hash mismatch");
            var root=AssetDatabase.LoadAssetAtPath<GameObject>(entry.assetPath);
            if(root==null) throw new Exception("imported model missing");
            bool character=entry.family.StartsWith("character-");
            var records=new List<Record>();
            if(character) records.AddRange(root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r=>!Excluded(r.name)&&r.sharedMesh!=null)
                .Select(r=>new Record { path=ObjectPath(r.transform,root.transform),mesh=r.sharedMesh,materials=r.sharedMaterials }));
            else records.AddRange(root.GetComponentsInChildren<MeshFilter>(true).Where(r=>!Excluded(r.name)&&r.sharedMesh!=null&&r.GetComponent<MeshRenderer>()!=null)
                .Select(r=>new Record { path=ObjectPath(r.transform,root.transform),mesh=r.sharedMesh,materials=r.GetComponent<MeshRenderer>().sharedMaterials }));
            records=records.OrderBy(r=>r.path,StringComparer.Ordinal).ToList();
            if(records.Count==0) throw new Exception("no selected display mesh");
            // Exact ordered hashes bind the reused, already audited topology map to this import. Vertex count alone
            // is not evidence; no nearest-position map is generated here and no source vertex is welded.
            result.positionHash=Hash(w=>{foreach(var r in records){w.Write(r.path);foreach(var p in r.mesh.vertices){w.Write(p.x);w.Write(p.y);w.Write(p.z);}}});
            result.normalHash=Hash(w=>{foreach(var r in records){w.Write(r.path);foreach(var n in r.mesh.normals){w.Write(n.x);w.Write(n.y);w.Write(n.z);}}});
            result.uvHash=Hash(w=>{foreach(var r in records){w.Write(r.path);foreach(var uv in r.mesh.uv){w.Write(uv.x);w.Write(uv.y);}}});
            result.indexHash=Hash(w=>{foreach(var r in records){w.Write(r.path);w.Write(r.mesh.subMeshCount);for(int s=0;s<r.mesh.subMeshCount;s++){w.Write((int)r.mesh.GetTopology(s));foreach(int i in r.mesh.GetIndices(s,true))w.Write(i);}}});
            result.weightHash=Hash(w=>{foreach(var r in records){w.Write(r.path);using var counts=r.mesh.GetBonesPerVertex();using var weights=r.mesh.GetAllBoneWeights();w.Write(counts.Length);foreach(byte c in counts)w.Write(c);w.Write(weights.Length);foreach(var b in weights){w.Write(b.boneIndex);w.Write(b.weight);}foreach(var m in r.mesh.bindposes)for(int i=0;i<16;i++)w.Write(m[i]);}});
            if(result.positionHash!=entry.currentPositionHash || result.normalHash!=entry.currentNormalHash || result.uvHash!=entry.currentUvHash
                || result.indexHash!=entry.currentIndexTopologyHash || result.weightHash!=entry.currentWeightHash) throw new Exception("ordered import hash mismatch; topology map must not be reused");
            result.renderers=records.Count;
            foreach(var r in records)
            {
                result.vertices+=r.mesh.vertexCount;
                for(int s=0;s<r.mesh.subMeshCount;s++)result.indices+=(int)r.mesh.GetIndexCount(s);
                foreach(var uv in r.mesh.uv){float u=uv.x*256-.5f,v=uv.y*256-.5f;if(!VpRenderVertex.IsSupportedUv(uv)||u<0||u>255||v<0||v>127||u!=Mathf.Round(u)||v!=Mathf.Round(v))throw new Exception("UV not on BottomHalfUV centres");}
                foreach(var material in r.materials)
                {
                    var texture=material==null ? null : material.mainTexture as Texture2D;
                    if(texture==null || FileHash(AssetDatabase.GetAssetPath(texture))!=entry.textureSha256) throw new Exception("palette reference/hash mismatch");
                    result.textureFormat=texture.format.ToString();result.textureMipCount=texture.mipmapCount;
                    result.textureFilter=texture.filterMode.ToString();result.textureWrap=texture.wrapMode.ToString();result.textureAniso=texture.anisoLevel;
                    result.textureSrgb=((TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture))).sRGBTexture;
                }
            }
            if(character){result.disposition="skinned input audited; no Static16 freeze; Bake/current-pose path unchanged";return;}
            Mesh mesh=records.Single(r=>r.mesh.name==entry.objectName).mesh;
            string path=PrivateRoot+"/Resources/Static16Migration/Megacity.bytes";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using(var buffer=new MemoryStream())
            {
                VpStatic16Writer.Write(buffer,mesh,entry.topologyMap,entry.topologyCount,Enumerable.Range(0,mesh.subMeshCount).ToArray());
                using var storage=new VpCpuGeometryStorage(mesh.vertexCount*2,result.indices*2,8,mesh.subMeshCount*2,8,Allocator.Persistent);
                buffer.Position=0;
                if(!VpStatic16File.TryAppendCuttable(buffer,storage,out _,out var failure))throw new Exception("runtime intake: "+failure);
                File.WriteAllBytes(path,buffer.ToArray());result.packedFileBytes=(int)buffer.Length;
            }
            result.packedSha256=FileHash(path);result.vertexPayloadBytes=mesh.vertexCount*16;
            result.disposition="static cuttable geometry packed offline; runtime registration verified; no scene replacement";
        }
        private static bool Excluded(string name)=>new[]{"WGT-","WGTS_rig","UCX_","PHYS_NULL"}.Any(p=>name.StartsWith(p,StringComparison.OrdinalIgnoreCase));
        private static string ObjectPath(Transform t,Transform root){var names=new List<string>();while(t!=null){names.Add(t.name);if(t==root)break;t=t.parent;}names.Reverse();return string.Join("/",names);}
        private static string Hash(Action<BinaryWriter> write){using var stream=new MemoryStream();using(var w=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))write(w);using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-","").ToLowerInvariant();}
        private static string FileHash(string path){using var sha=SHA256.Create();using var stream=File.OpenRead(path);return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        private sealed class Record { public string path;public Mesh mesh;public Material[] materials; }
        [Serializable] private sealed class Input { public Entry[] assets; }
        [Serializable] private sealed class Entry { public string family,assetPath,sourceSha256,textureSha256,currentPositionHash,currentNormalHash,currentUvHash,currentIndexTopologyHash,currentWeightHash,objectName;public int topologyCount;public int[] topologyMap; }
        [Serializable] private sealed class Report { public string unity,inputManifestSha256;public bool passed;public List<Result> results; }
        [Serializable] private sealed class Result { public string family,asset,sourceSha256,positionHash,normalHash,uvHash,indexHash,weightHash,textureFormat,textureFilter,textureWrap,disposition,packedSha256,failure;public int renderers,vertices,indices,textureMipCount,textureAniso,packedFileBytes,vertexPayloadBytes;public bool textureSrgb,passed; }
    }
}
