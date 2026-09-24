using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.Rendering.StandaloneTests
{
    public class Static16IntakePlayerTests
    {
        [Test]
        public void LicensedMegacity_OfflineBytes_RegisterAtNonzeroBase_CutRecut_AndGpuRoundTrip()
        {
            var asset=Resources.Load<TextAsset>("Static16Migration/Megacity");
            if(asset==null) Assert.Ignore("Private licensed fixture absent. Run Prepare-Compact16uv-Intake.ps1 and Compact16uvProductIntake.Run; this is not a pass.");
            byte[] bytes=asset.bytes;
            Resources.UnloadAsset(asset); // No runtime source Mesh or retained TextAsset cache.
            using var storage=new VpCpuGeometryStorage(65536,262144,64,128,128,Allocator.Persistent);
            VpStoredGeometry geometry;
            using(var input=new MemoryStream(bytes,false))
            {
                Assert.That(VpStatic16File.TryAppendCuttable(input,storage,out _,out var failure),Is.True,failure);
                input.Position=0;
                Assert.That(VpStatic16File.TryAppendCuttable(input,storage,out geometry,out failure),Is.True,failure);
            }
            int vertexBase=geometry.vertexStart;
            Assert.That(vertexBase,Is.GreaterThan(0));
            using(var reader=new BinaryReader(new MemoryStream(bytes,false)))
            {
                reader.BaseStream.Position=VpStatic16File.HeaderBytes;
                for(int i=0;i<geometry.vertexCount;i++)
                {
                    var vertex=storage.Vertices[vertexBase+i];
                    Assert.That(vertex.position,Is.EqualTo(new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle())));
                    Assert.That(vertex.normalX,Is.EqualTo(reader.ReadSByte()));Assert.That(vertex.normalY,Is.EqualTo(reader.ReadSByte()));
                    Assert.That(vertex.u,Is.EqualTo(reader.ReadByte()));Assert.That(vertex.v,Is.EqualTo(reader.ReadByte()));
                }
            }
            bytes=null;
            using var gpu=new GraphicsBuffer(GraphicsBuffer.Target.Structured,65536,16);
            for(int axis=0;axis<3;axis++)
            {
                Assert.That(storage.TryGetPublishedExtent(geometry,out _,out _,out Bounds bounds),Is.True);
                float3 normal=axis==0 ? new float3(1,0,0) : axis==1 ? new float3(0,1,0) : new float3(0,0,1);
                float offset=bounds.center[axis]+.137f*bounds.extents[axis];
                var before=storage.Vertices.ToArray();
                Assert.That(VpStorageCutInput.TryAcquire(storage,geometry,out var cutInput),Is.True);
                VpStorageCutResult cut;
                using(cutInput)Assert.That(VpStorageCut.TryExecute(storage,cutInput,new float4(normal,-offset),default,out cut),Is.True);
                Assert.That(cut.status,Is.EqualTo(VpStorageCutStatus.Ok));
                Assert.That(cut.kernel.executedManaged,Is.Zero,"Burst Direct Call, not managed fallback");
                Assert.That(cut.kernel.newVertexCount,Is.GreaterThan(0));
                Assert.That(cut.kernel.openContourCount,Is.Zero);
                for(int i=0;i<before.Length;i++)Assert.That(storage.Vertices[i],Is.EqualTo(before[i]));
                Assert.That(VpStoredGeometryTransfer.TryUploadCommittedVertices(storage,gpu,vertexBase,storage.VertexCount-vertexBase,out int written),Is.True);
                var readback=new VpRenderVertex[written];gpu.GetData(readback,0,vertexBase,written);
                for(int i=0;i<written;i++)Assert.That(readback[i],Is.EqualTo(storage.Vertices[vertexBase+i]));
                int cap=0;for(int i=before.Length;i<storage.VertexCount;i++)if(storage.Vertices[i].u==247&&storage.Vertices[i].v==247)cap++;
                Assert.That(cap,Is.GreaterThan(0));
                geometry=cut.positive.geometry;
                Debug.Log($"Static16 Megacity axis={axis}: input={before.Length}, new={cut.kernel.newVertexCount}, cap={cap}, gpuVertices={written}, Burst=1");
            }
        }
    }
}
