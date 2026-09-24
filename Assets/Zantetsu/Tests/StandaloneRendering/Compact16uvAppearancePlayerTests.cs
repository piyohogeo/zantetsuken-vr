using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.MeshCut;
using Object=UnityEngine.Object;
using Debug=UnityEngine.Debug;

namespace Zantetsu.Rendering.StandaloneTests
{
    public class Compact16uvAppearancePlayerTests
    {
        private const int Size=256;
        private readonly List<Object> owned=new List<Object>();
        private T Own<T>(T item) where T:Object { owned.Add(item);return item; }

        [TestCase(0)][TestCase(1)][TestCase(2)]
        public void LicensedMegacity_SourceMeshAndThreeRecuts_MatchIndexed16(int view)
        {
            var source=Resources.Load<Mesh>("AppearanceMigration/MegacitySource");
            var sourceMaterial=Resources.Load<Material>("AppearanceMigration/SourcePalette");
            var bytes=Resources.Load<TextAsset>("Static16Migration/Megacity");
            var normal=Resources.Load<Texture2D>("PaletteAtlas/Normal");
            var debug=Resources.Load<Texture2D>("PaletteAtlas/Debug");
            if(source==null||sourceMaterial==null||bytes==null||normal==null||debug==null)
                Assert.Ignore("Private appearance/intake/atlas resources absent. Prepare all three builders; missing input is not a pass.");
            var previousNormal=VpCutSurfaceAtlas.Normal;var previousDebug=VpCutSurfaceAtlas.Debug;var state=VpCutSurfaceColour.Capture();
            try
            {
                VpCutSurfaceColour.SetDebugEnabled(false);VpCutSurfaceAtlas.Bind(normal,debug);
                var oracle=Own(new Material(sourceMaterial));
                var material=Own(new Material(Resources.Load<Material>("VpAtlasProbe2")));material.SetFloat("_VpUsePaletteAtlas",1);
                var target=Own(new RenderTexture(Size,Size,24,RenderTextureFormat.ARGB32));target.antiAliasing=1;target.Create();
                var camera=Own(new GameObject("Megacity appearance camera")).AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.clear;camera.cullingMask=1<<30;camera.targetTexture=target;
                var referenceObject=Own(new GameObject("Original Mesh oracle"));referenceObject.layer=30;
                var filter=referenceObject.AddComponent<MeshFilter>();var renderer=referenceObject.AddComponent<MeshRenderer>();renderer.sharedMaterial=oracle;
                renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;renderer.enabled=false;
                using var storage=new VpCpuGeometryStorage(65536,262144,64,128,128,Allocator.Persistent);
                using(var stream=new MemoryStream(bytes.bytes,false))
                {
                    Assert.That(VpStatic16File.TryAppendCuttable(stream,storage,out _,out var failure),Is.True,failure);
                    stream.Position=0;Assert.That(VpStatic16File.TryAppendCuttable(stream,storage,out var loaded,out failure),Is.True,failure);
                    Run(loaded);
                }
                void Run(VpStoredGeometry geometry)
                {
                    using var gpu=new VpGpuIndexedGeometryBuffers(65536,262144);
                    using var batch=new VpIndexedIndirectDrawBatch(1,1);
                    var properties=new MaterialPropertyBlock();int totalCapChanged=0;
                    Assert.That(geometry.vertexStart,Is.GreaterThan(0));
                    for(int stage=0;stage<4;stage++)
                    {
                        if(stage>0)
                        {
                            Assert.That(storage.TryGetPublishedExtent(geometry,out _,out _,out var oldBounds),Is.True);
                            int axis=stage-1;float3 n=axis==0?new float3(1,0,0):axis==1?new float3(0,1,0):new float3(0,0,1);
                            Assert.That(VpStorageCutInput.TryAcquire(storage,geometry,out var input),Is.True);
                            VpStorageCutResult cut;
                            using(input)Assert.That(VpStorageCut.TryExecute(storage,input,new float4(n,-oldBounds.center[axis]-.137f*oldBounds.extents[axis]),default,out cut),Is.True);
                            Assert.That(cut.status,Is.EqualTo(VpStorageCutStatus.Ok));Assert.That(cut.kernel.executedManaged,Is.Zero);Assert.That(cut.kernel.openContourCount,Is.Zero);
                            geometry=cut.positive.geometry;
                        }
                        Assert.That(storage.TryGetPublishedExtent(geometry,out int referencedStart,out int referencedCount,out var bounds),Is.True);
                        Assert.That(storage.TryGetIndexState(geometry.indexRange,out _,out int indexStart,out int indexCount),Is.True);
                        var range=new VpGeometryRange(referencedStart,referencedCount,indexStart,indexCount);
                        Assert.That(VpStoredGeometryTransfer.TryUploadCommittedVertices(storage,gpu.VertexBuffer,0,storage.VertexCount,out _),Is.True);
                        Assert.That(VpStoredGeometryTransfer.TryUploadPublishedIndices(storage,gpu.IndexBuffer,geometry.indexRange,out _),Is.True);
                        Assert.That(batch.TryUpload(new[]{new VpIndirectCommand(range,bounds,1)},new[]{Matrix4x4.identity},false),Is.True);
                        // The uncut oracle is the original imported float Mesh. Cut oracles expand the published
                        // output for an independent vertex-fetch/indexing check, not independent cutting math.
                        filter.sharedMesh=stage==0?source:Expand(storage,geometry);
                        // The licensed source has three submeshes. A single material slot omits two of them
                        // in MeshRenderer, whereas the VP range includes all three index runs.
                        renderer.sharedMaterials=Enumerable.Repeat(oracle,filter.sharedMesh.subMeshCount).ToArray();
                        Assert.That(renderer.sharedMaterials.Length,Is.EqualTo(filter.sharedMesh.subMeshCount));
                        Assert.That(filter.sharedMesh.triangles.Length,Is.EqualTo(indexCount));
                        float radius=Mathf.Max(bounds.extents.magnitude,.01f);
                        var direction=view==0?new Vector3(1,.35f,-1):view==1?new Vector3(-1,.4f,1):new Vector3(-.4f,-1,-.5f);
                        camera.transform.position=bounds.center+direction.normalized*radius*4;camera.transform.LookAt(bounds.center);
                        camera.orthographicSize=radius*(view==1?2.4f:1.1f);camera.nearClipPlane=radius*.01f;camera.farClipPlane=radius*10;
                        VpCutSurfaceColour.SetDebugEnabled(false);oracle.SetFloat("_UseSourcePalette",0);
                        var reference=Render(true,$"stage{stage}-view{view}-mesh");
                        var packed=Render(false,$"stage{stage}-view{view}-packed");
                        var diff=Compare(reference,packed);
                        Debug.Log($"Appearance stage={stage} view={view}: covered={diff.covered}, silhouetteDiff={diff.silhouette}, maxRgb={diff.max}, meanRgb={diff.mean:F6}, changed={diff.changed}");
                        Assert.That(diff.covered,Is.GreaterThan(100));Assert.That(diff.silhouette,Is.Zero);Assert.That(diff.max,Is.LessThanOrEqualTo(3),"8-bit colour tolerance for oct8 normal shading; never silhouette tolerance");
                        if(stage==0)
                        {
                            oracle.SetFloat("_UseSourcePalette",1);var originalPalette=Render(true,$"stage0-view{view}-source-palette");oracle.SetFloat("_UseSourcePalette",0);
                            var paletteDiff=Compare(originalPalette,reference);
                            Debug.Log($"Palette appearance view={view}: silhouetteDiff={paletteDiff.silhouette}, maxRgb={paletteDiff.max}, meanRgb={paletteDiff.mean:F6}, changed={paletteDiff.changed}; DXT1 source vs DXT5 modified atlas, recorded separately");
                            Assert.That(paletteDiff.silhouette,Is.Zero);
                        }
                        VpCutSurfaceColour.SetDebugEnabled(true);
                        var debugMesh=Render(true,$"stage{stage}-view{view}-debug-mesh");
                        var debugPacked=Render(false,$"stage{stage}-view{view}-debug-packed");
                        var debugDiff=Compare(debugMesh,debugPacked);
                        Assert.That(debugDiff.silhouette,Is.Zero);Assert.That(debugDiff.max,Is.LessThanOrEqualTo(3));
                        int capChanged=Compare(packed,debugPacked).changed;totalCapChanged+=capChanged;
                        if(stage==0)Assert.That(capChanged,Is.Zero);
                        Debug.Log($"Appearance debug stage={stage} view={view}: maxRgb={debugDiff.max}, capChanged={capChanged}");
                        if(view==0&&stage==3)MeasureUpload(storage,gpu,geometry);
                    }
                    Assert.That(totalCapChanged,Is.GreaterThan(0),"At least one actual cut face must be visible across recuts");
                    Color32[] Render(bool useMesh,string name)
                    {
                        renderer.enabled=useMesh;
                        if(!useMesh)batch.RenderForward(material,properties,gpu,30,0,1,camera);
                        var request=new RenderPipeline.StandardRequest{destination=target};Assert.That(RenderPipeline.SupportsRenderRequest(camera,request),Is.True);
                        RenderPipeline.SubmitRenderRequest(camera,request);renderer.enabled=false;
                        var image=Own(new Texture2D(Size,Size,TextureFormat.RGBA32,false));var active=RenderTexture.active;
                        try{RenderTexture.active=target;image.ReadPixels(new Rect(0,0,Size,Size),0,0);image.Apply();}finally{RenderTexture.active=active;}
                        string output=Environment.GetEnvironmentVariable("VP_APPEARANCE_DIAGNOSTICS");
                        if(!string.IsNullOrEmpty(output)){Directory.CreateDirectory(output);File.WriteAllBytes(Path.Combine(output,name+".png"),image.EncodeToPNG());}
                        return image.GetPixels32();
                    }
                }
            }
            finally
            {
                VpCutSurfaceAtlas.Clear();VpCutSurfaceColour.Restore(state);if(previousNormal!=null&&previousDebug!=null)VpCutSurfaceAtlas.Bind(previousNormal,previousDebug);
                foreach(var item in owned)if(item!=null)Object.DestroyImmediate(item);owned.Clear();
            }
        }

        private Mesh Expand(VpCpuGeometryStorage storage,VpStoredGeometry geometry)
        {
            var mesh=Own(new Mesh{indexFormat=IndexFormat.UInt32});var vertices=storage.Vertices;
            var positions=new Vector3[vertices.Length];var normals=new Vector3[vertices.Length];var uvs=new Vector2[vertices.Length];
            for(int i=0;i<vertices.Length;i++){positions[i]=vertices[i].position;normals[i]=vertices[i].normal;uvs[i]=vertices[i].uv0;}
            mesh.vertices=positions;mesh.normals=normals;mesh.uv=uvs;
            Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange,out var lease,out var indices),Is.True);
            try{mesh.triangles=indices.ToArray().Select(x=>(int)x).ToArray();}finally{storage.TryReleaseIndexReadLease(lease);}
            return mesh;
        }
        private static (int covered,int silhouette,int max,double mean,int changed) Compare(Color32[] a,Color32[] b)
        {
            int covered=0,silhouette=0,max=0,changed=0;long sum=0;
            for(int i=0;i<a.Length;i++)
            {
                if((a[i].a>127)!=(b[i].a>127))silhouette++;
                if(a[i].a<=127&&b[i].a<=127)continue;covered++;
                int r=Math.Abs(a[i].r-b[i].r),g=Math.Abs(a[i].g-b[i].g),bl=Math.Abs(a[i].b-b[i].b);
                max=Math.Max(max,Math.Max(r,Math.Max(g,bl)));sum+=r+g+bl;if(r+g+bl>0)changed++;
            }
            return(covered,silhouette,max,covered==0?0:(double)sum/(covered*3),changed);
        }
        private static void MeasureUpload(VpCpuGeometryStorage storage,VpGpuIndexedGeometryBuffers gpu,VpStoredGeometry geometry)
        {
            // Deliberately isolated CPU SetData boundary: no rendering/readback/file IO in the timing window.
            bool Upload()=>VpStoredGeometryTransfer.TryUploadCommittedVertices(storage,gpu.VertexBuffer,0,storage.VertexCount,out _)
                &&VpStoredGeometryTransfer.TryUploadPublishedIndices(storage,gpu.IndexBuffer,geometry.indexRange,out _);
            for(int i=0;i<10;i++)Assert.That(Upload(),Is.True);
            var samples=new double[101];long allocated=GC.GetAllocatedBytesForCurrentThread();bool success=true;
            for(int i=0;i<samples.Length;i++){long start=Stopwatch.GetTimestamp();success&=Upload();samples[i]=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;}
            allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;Assert.That(success,Is.True);Array.Sort(samples);
            storage.TryGetIndexState(geometry.indexRange,out _,out _,out int indices);
            Debug.Log($"Appearance isolated upload: samples=101, medianMs={samples[50]:F6}, p95Ms={samples[95]:F6}, managedBytes={allocated}, vertices={storage.VertexCount}, vertexBytes={storage.VertexCount*16}, indexBytes={indices*4}, capacityVertexBytes={storage.VertexCapacity*16}; not total Main, GPU time, residency or 32B speed comparison");
        }
    }
}
