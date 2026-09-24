using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace Zantetsu.Rendering.StandaloneTests
{
    public class PaletteAtlasPlayerTests
    {
        private readonly List<Object> owned=new List<Object>();
        private T Own<T>(T value) where T:Object{owned.Add(value);return value;}
        [TestCase(0,0)][TestCase(0,1)][TestCase(0,2)]
        [TestCase(1,0)][TestCase(1,1)][TestCase(1,2)]
        [TestCase(2,0)][TestCase(2,1)][TestCase(2,2)]
        [TestCase(3,0)][TestCase(3,1)][TestCase(3,2)]
        public void FourForwardPaths_SwitchCapsWithoutSurfaceOrGeometryChanges(int path,int view)
        {
            var normal=Resources.Load<Texture2D>("PaletteAtlas/Normal");var debug=Resources.Load<Texture2D>("PaletteAtlas/Debug");
            if(normal==null||debug==null)Assert.Ignore("Private atlas pair absent: run Compact16uvAtlasBuild.Run. Not a pass.");
            var oldNormal=VpCutSurfaceAtlas.Normal;var oldDebug=VpCutSurfaceAtlas.Debug;var state=VpCutSurfaceColour.Capture();
            try
            {
                VpCutSurfaceColour.SetDebugEnabled(false);VpCutSurfaceAtlas.Bind(normal,debug);
                Material material=Own(new Material(Resources.Load<Material>("VpAtlasProbe"+path)));
                material.SetFloat("_VpUsePaletteAtlas",1);
                var shadow=Resources.Load<Material>("VpAtlasProbe"+(path==1?4:5));
                var target=Own(new RenderTexture(128,128,24,RenderTextureFormat.ARGB32));target.antiAliasing=1;target.Create();
                var camera=Own(new GameObject("Atlas probe camera")).AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;
                camera.orthographicSize=view==1 ? 2.4f : 1.2f;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                camera.nearClipPlane=.1f;camera.farClipPlane=20;camera.targetTexture=target;
                camera.transform.position=view==2 ? new Vector3(2,0,-5):new Vector3(0,0,-5);camera.transform.LookAt(Vector3.zero);
                using var pool=new VpCpuGeometryPool(16,24,Allocator.Persistent);
                Assert.That(pool.TryAppend(Quad(-.55f,false),out var surface),Is.True);
                Assert.That(pool.TryAppend(Quad(.55f,true),out var cap),Is.True);
                using var indexed=new VpGpuIndexedGeometryBuffers(16,24);Assert.That(indexed.TryUpload(pool),Is.True);
                using var direct=new VpGpuGeometryBuffers(16,24);Assert.That(direct.TryUpload(pool),Is.True);
                var before=new VpRenderVertex[8];(path<2 ? direct.VertexBuffer:indexed.VertexBuffer).GetData(before,0,0,8);
                using var batch=new VpIndexedIndirectDrawBatch(2,2);
                using var stage2=new VpIndirectDrawBatch(2,2);
                var bounds=new Bounds(Vector3.zero,Vector3.one*4);
                var commands=new[]{new VpIndirectCommand(surface,bounds,1),new VpIndirectCommand(cap,bounds,1)};
                var transforms=new[]{Matrix4x4.identity,Matrix4x4.identity};
                Assert.That(batch.TryUpload(commands,transforms,false),Is.True);Assert.That(stage2.TryUpload(commands,transforms,false),Is.True);
                using var visible=new GraphicsBuffer(GraphicsBuffer.Target.Structured,2,4);visible.SetData(new uint[]{0,1});
                var properties=new MaterialPropertyBlock();properties.SetBuffer("_VpVisibleInstances",visible);
                Color32[] Render(string label)
                {
                    if(path==0){VpDirectDraw.Render(material,properties,direct,surface,Matrix4x4.identity,bounds,0,camera);VpDirectDraw.Render(material,properties,direct,cap,Matrix4x4.identity,bounds,0,camera);}
                    else if(path==1)stage2.Render(material,shadow,properties,direct,0,camera);
                    else batch.RenderForward(material,properties,indexed,0,0,2,camera);
                    var request=new RenderPipeline.StandardRequest{destination=target};Assert.That(RenderPipeline.SupportsRenderRequest(camera,request),Is.True);
                    RenderPipeline.SubmitRenderRequest(camera,request);
                    var previous=RenderTexture.active;RenderTexture.active=target;
                    var readback=Own(new Texture2D(128,128,TextureFormat.RGBA32,false));readback.ReadPixels(new Rect(0,0,128,128),0,0);readback.Apply();RenderTexture.active=previous;
                    string output=Environment.GetEnvironmentVariable("VP_ATLAS_DIAGNOSTICS");if(!string.IsNullOrEmpty(output)){Directory.CreateDirectory(output);File.WriteAllBytes(Path.Combine(output,$"path{path}-view{view}-{label}.png"),readback.EncodeToPNG());}
                    return readback.GetPixels32();
                }
                var first=Render("normal");VpCutSurfaceColour.SetDebugEnabled(true);var second=Render("debug");
                int surfaceDiff=0,green=0,surfacePixels=0;
                for(int i=0;i<first.Length;i++)
                {
                    if(i%128<64){if(!first[i].Equals(second[i]))surfaceDiff++;if(first[i].r+first[i].g+first[i].b>45)surfacePixels++;}
                    else if(second[i].g>second[i].r+30&&second[i].g>second[i].b+30)green++;
                }
                Assert.That(surfacePixels,Is.GreaterThan(30));Assert.That(green,Is.GreaterThan(30));Assert.That(surfaceDiff,Is.Zero);
                material.SetColor("_BaseColor",new Color(.2f,.5f,.9f));material.SetTextureScale("_BaseMap",new Vector2(.73f,.61f));material.SetTextureOffset("_BaseMap",new Vector2(.11f,.03f));
                var changed=Render("debug-st-tint");int capDiff=0,surfaceChanged=0;
                for(int i=0;i<changed.Length;i++){if(i%128>=64){if(!second[i].Equals(changed[i]))capDiff++;}else if(!second[i].Equals(changed[i]))surfaceChanged++;}
                Assert.That(capDiff,Is.Zero);Assert.That(surfaceChanged,Is.GreaterThan(20));
                var gpu=new VpRenderVertex[8];(path<2 ? direct.VertexBuffer:indexed.VertexBuffer).GetData(gpu,0,0,8);
                Assert.That(gpu,Is.EqualTo(before),"switch/ST/tint wrote no geometry");
                Assert.That(Shader.GetGlobalTexture("_VpPaletteSurface"),Is.SameAs(normal));Assert.That(Shader.GetGlobalTexture("_VpPaletteCaps"),Is.SameAs(debug));
                Debug.Log($"Atlas path={path} view={view}: surfacePixels={surfacePixels}, surfaceDiff={surfaceDiff}, green={green}, capSTTintDiff={capDiff}, surfaceSTTintChanged={surfaceChanged}, vertexFieldsEqual=1");
            }
            finally
            {
                VpCutSurfaceAtlas.Clear();VpCutSurfaceColour.Restore(state);if(oldNormal!=null&&oldDebug!=null)VpCutSurfaceAtlas.Bind(oldNormal,oldDebug);
                foreach(var value in owned)if(value!=null)Object.DestroyImmediate(value);owned.Clear();
            }
        }
        [Test]
        public void StencilProvisional_UsesRedSlot_SwitchesWithoutUpload_AndStillNeedsPositiveStencil()
        {
            var normal=Resources.Load<Texture2D>("PaletteAtlas/Normal");var debug=Resources.Load<Texture2D>("PaletteAtlas/Debug");
            if(normal==null||debug==null)Assert.Ignore("Private atlas absent; not a pass.");
            var oldNormal=VpCutSurfaceAtlas.Normal;var oldDebug=VpCutSurfaceAtlas.Debug;var state=VpCutSurfaceColour.Capture();
            try
            {
                VpCutSurfaceColour.SetDebugEnabled(false);VpCutSurfaceAtlas.Bind(normal,debug);
                var target=Own(new RenderTexture(128,128,0,UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm){depthStencilFormat=VpStencilAttachment.EightBitStencilFormat,antiAliasing=1});target.Create();
                var camera=Own(new GameObject("Atlas stencil camera")).AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;camera.orthographicSize=1;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.targetTexture=target;
                camera.transform.position=new Vector3(0,0,-5);camera.nearClipPlane=.1f;camera.farClipPlane=20;
                var mesh=Own(new Mesh());
                mesh.vertices=new[]{new Vector3(-.5f,-.5f,-.5f),new Vector3(.5f,-.5f,-.5f),new Vector3(.5f,.5f,-.5f),new Vector3(-.5f,.5f,-.5f),new Vector3(-.5f,-.5f,.5f),new Vector3(.5f,-.5f,.5f),new Vector3(.5f,.5f,.5f),new Vector3(-.5f,.5f,.5f)};
                mesh.normals=Enumerable.Repeat(Vector3.back,8).ToArray();mesh.uv=Enumerable.Repeat(new Vector2(.25f,.25f),8).ToArray();
                int[][] faces={new[]{0,3,2,1},new[]{4,5,6,7},new[]{0,1,5,4},new[]{3,7,6,2},new[]{0,4,7,3},new[]{1,2,6,5}};
                mesh.triangles=faces.SelectMany(f=>new[]{f[0],f[1],f[2],f[0],f[2],f[3]}).ToArray();
                using var pool=new VpCpuGeometryPool(16,64,Allocator.Persistent);Assert.That(pool.TryAppend(mesh,out var range),Is.True);
                using var gpu=new VpGpuIndexedGeometryBuffers(16,64);Assert.That(gpu.TryUpload(pool),Is.True);
                using var batch=new VpStencilCapBatch(1,1,1,4,6);
                Assert.That(VpStencilCapMaterials.TryCreate(1,out var materials),Is.True);
                using(materials)
                {
                    materials.Cap(0).SetFloat("_VpUsePaletteAtlas",1);
                    var commands=new[]{new VpIndirectCommand(range,new Bounds(Vector3.zero,Vector3.one*2),1)};
                    var transforms=new[]{Matrix4x4.identity};var clips=new[]{VpInstanceClip.Keep(new Unity.Mathematics.float4(0,0,1,0),1)};
                    Vector3[] vertices={new(-.7f,-.7f,0),new(-.7f,.7f,0),new(.7f,.7f,0),new(.7f,-.7f,0)};int[] indices={0,1,2,0,2,3};
                    Assert.That(batch.TryUpload(commands,transforms,clips,vertices,4,indices,6,new[]{new VpStencilCapColor(0,1,0,6,Color.blue)},1),Is.True);
                    int uploads=batch.Uploads,writes=batch.BufferWrites;
                    Color32[] Render(string label)
                    {
                        batch.Render(materials,gpu,0,camera);
                        var request=new RenderPipeline.StandardRequest{destination=target};Assert.That(RenderPipeline.SupportsRenderRequest(camera,request),Is.True);RenderPipeline.SubmitRenderRequest(camera,request);
                        var previous=RenderTexture.active;RenderTexture.active=target;var image=Own(new Texture2D(128,128,TextureFormat.RGBA32,false));image.ReadPixels(new Rect(0,0,128,128),0,0);image.Apply();RenderTexture.active=previous;
                        string output=Environment.GetEnvironmentVariable("VP_ATLAS_DIAGNOSTICS");if(!string.IsNullOrEmpty(output)){Directory.CreateDirectory(output);File.WriteAllBytes(Path.Combine(output,"stencil-"+label+".png"),image.EncodeToPNG());}
                        return image.GetPixels32();
                    }
                    var first=Render("normal");VpCutSurfaceColour.SetDebugEnabled(true);var second=Render("debug");
                    int red=0,grey=0;for(int i=0;i<first.Length;i++){if(first[i].r+first[i].g+first[i].b>30&&Math.Abs(first[i].r-first[i].b)<12)grey++;if(second[i].r>150&&second[i].g<50&&second[i].b<50)red++;}
                    Assert.That(red,Is.GreaterThan(500));Assert.That(grey,Is.EqualTo(red));
                    Assert.That(batch.Uploads,Is.EqualTo(uploads));Assert.That(batch.BufferWrites,Is.EqualTo(writes));
                    Assert.That(batch.TryUpload(commands,transforms,clips,vertices,4,indices,6,new[]{new VpStencilCapColor(0,0,0,6,Color.blue)},1),Is.True);
                    Assert.That(Render("no-volume").Count(p=>p.r>150&&p.g<50&&p.b<50),Is.Zero);
                    Debug.Log($"Atlas stencil: grey={grey}, red={red}, switchUploadDelta=0, switchBufferWriteDelta=0, noVolumeRed=0");
                }
            }
            finally
            {
                VpCutSurfaceAtlas.Clear();VpCutSurfaceColour.Restore(state);if(oldNormal!=null&&oldDebug!=null)VpCutSurfaceAtlas.Bind(oldNormal,oldDebug);
                foreach(var value in owned)if(value!=null)Object.DestroyImmediate(value);owned.Clear();
            }
        }
        private Mesh Quad(float x,bool cap)
        {
            var mesh=Own(new Mesh());mesh.vertices=new[]{new Vector3(x-.35f,-.6f,0),new Vector3(x-.35f,.6f,0),new Vector3(x+.35f,.6f,0),new Vector3(x+.35f,-.6f,0)};
            mesh.normals=Enumerable.Repeat(Vector3.back,4).ToArray();mesh.triangles=new[]{0,1,2,0,2,3};
            mesh.uv=cap ? Enumerable.Repeat(new Vector2(247.5f/256,247.5f/256),4).ToArray()
                : new[]{new Vector2(16.5f/256,31.5f/256),new Vector2(16.5f/256,111.5f/256),new Vector2(240.5f/256,111.5f/256),new Vector2(240.5f/256,31.5f/256)};
            return mesh;
        }
    }
}
