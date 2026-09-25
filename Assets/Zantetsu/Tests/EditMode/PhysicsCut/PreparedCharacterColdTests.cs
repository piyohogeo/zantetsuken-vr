using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.PhysicsCut.Tests
{
    public unsafe partial class PreparedCharacterColdTests
    {
        readonly List<IDisposable> owned = new List<IDisposable>();
        readonly List<Object> objects = new List<Object>();
        CutWorldRoot world;
        T Keep<T>(T item) where T : IDisposable { owned.Add(item); return item; }
        T Track<T>(T item) where T : Object { objects.Add(item); return item; }
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj);
        static void Set(object obj,string name,object value) => obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(obj,value);
        [TearDown] public void Cleanup()
        {
            for(int i=owned.Count-1;i>=0;i--)owned[i].Dispose();owned.Clear();
            if(world!=null)Assert.That(world.Shutdown(),Is.True,"cold-only world has no outstanding work");
            for(int i=objects.Count-1;i>=0;i--)if(objects[i]!=null)Object.DestroyImmediate(objects[i]);
            objects.Clear();world=null;
        }
        void NewWorld(bool material=true)
        {
            var profile=Track(ScriptableObject.CreateInstance<CutWorldProfile>());
            var mat=Track(new Material(Shader.Find("Zantetsu/VP Indexed Indirect Unlit")));
            var go=Track(new GameObject("D6H cold world"));go.SetActive(false);world=go.AddComponent<CutWorldRoot>();
            Set(world,"profile",profile);Set(world,"materials",material?new[]{new CutWorldRoot.MaterialBinding{sourceIndex=0,material=mat}}:Array.Empty<CutWorldRoot.MaterialBinding>());
            typeof(CutWorldRoot).GetMethod("Build",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(world,null);
            Assert.That(world.IsReady,Is.True);
        }
        SkinnedMeshRenderer Rig()
        {
            var go=Track(new GameObject("D6H borrowed rig"));var r=go.AddComponent<SkinnedMeshRenderer>();r.quality=SkinQuality.Bone4;
            var mesh=Track(new Mesh{vertices=new[]{Vector3.zero,Vector3.right,Vector3.up,Vector3.forward},
                normals=Enumerable.Repeat(Vector3.up,4).ToArray(),uv=Enumerable.Repeat(new Vector2(.5f,.5f),4).ToArray(),
                triangles=new[]{0,2,1,0,1,3,0,3,2,1,2,3},bindposes=new[]{Matrix4x4.identity},
                boneWeights=Enumerable.Repeat(new BoneWeight{weight0=1},4).ToArray()});
            r.sharedMesh=mesh;r.bones=new[]{go.transform};return r;
        }
        OwnerCutHarness Bank(int count=1)
        {
            var h=Keep(new OwnerCutHarness());for(int i=0;i<count;i++)h.Add(CaseGenerator.Box());h.Build();return h;
        }
        static ConvexBrepRange[] Ranges(OwnerCutHarness h)
        {var rs=new ConvexBrepRange[h.input.convexCount];for(int i=0;i<rs.Length;i++)rs[i]=h.input.convexes[i];return rs;}
        bool Prepare(SkinnedMeshRenderer r,OwnerCutHarness h,VpPhysicsColdPreparation cold,out VpPreparedCharacterCut result)
            =>world.TryPrepareCharacterCut(r,new[]{0,1,2,3},4,h.input.bank,Ranges(h),new[]{r.transform},cold,out result);
        void NoPublication()
        {
            Assert.That(world.Storage.VertexCount+world.Owners.Count+world.Ledger.FragmentCount
                +world.Ledger.OperationCount+world.References.LiveGeometryCount+world.Display.VertexTransfers,Is.Zero);
        }
        static int PreparedMeshes()=>Resources.FindObjectsOfTypeAll<Mesh>().Count(m=>m.name=="Prepared bone-local convex");

        [Test] public void D6H_Cold_OwnsCopies_NoPublication_DisposeDoesNotDestroyBorrowedRig()
        {
            NewWorld();var r=Rig();var mesh=r.sharedMesh;var h=Bank();var warm=Keep(new VpPhysicsColdPreparation());
            var ranges=Ranges(h);var bones=new[]{r.transform};var topo=new[]{0,1,2,3};
            Assert.That(world.TryPrepareCharacterCut(r,topo,4,h.input.bank,ranges,bones,warm,out var handle),Is.True);Keep(handle);
            Assert.That(handle.IsReady&&handle.TryFinishPreparation(),Is.True);NoPublication();
            var input=Field<VpPreparedPhysicsInput>(handle,"physics");var shape=input.ColdPreparationShape();var cooked=shape.MeshOf(0);
            var direct=Field<VpDirectSkinInput>(handle,"direct");var slot=Field<VpLogicalCutDisplay.PreparedRoot>(handle,"slot");
            Assert.That(input.IsPosed,Is.False);Assert.That(input.ColdCookCalls,Is.EqualTo(1));
            Assert.That(slot.IsConsumed,Is.False);Assert.That((long)shape.BankOf(0).vertices,Is.Not.EqualTo((long)h.input.bank.vertices));
            bones[0]=null;topo[0]=99;ranges[0]=default;
            Assert.That(Field<Transform[]>(handle,"convexBones")[0],Is.SameAs(r.transform));
            h.Dispose();Assert.That(shape.MeshOf(0),Is.SameAs(cooked));
            handle.Dispose();handle.Dispose();Assert.That(handle.IsReady||handle.TryFinishPreparation(),Is.False);
            Assert.That(handle.IsDisposed&&direct.IsDisposed&&slot.IsDisposed&&shape.IsFreed,Is.True);
            Assert.That(cooked==null,Is.True);Assert.That(r!=null&&mesh!=null&&r.enabled,Is.True);
            Assert.That(world.IsReady&&warm.IsPrepared,Is.True);NoPublication();
        }
        [Test] public void D6H_Cold_TwoHandles_ShareWarmOnly_NotInputsOrSlots()
        {
            NewWorld();var r=Rig();var h=Bank();var warm=Keep(new VpPhysicsColdPreparation());
            Assert.That(Prepare(r,h,warm,out var a),Is.True);Keep(a);Assert.That(Prepare(r,h,warm,out var b),Is.True);Keep(b);
            var ia=Field<VpPreparedPhysicsInput>(a,"physics");var ib=Field<VpPreparedPhysicsInput>(b,"physics");
            Assert.That(ia,Is.Not.SameAs(ib));Assert.That(ia.ColdPreparationShape().MeshOf(0),Is.Not.SameAs(ib.ColdPreparationShape().MeshOf(0)));
            Assert.That(Field<VpLogicalCutDisplay.PreparedRoot>(a,"slot"),Is.Not.SameAs(Field<VpLogicalCutDisplay.PreparedRoot>(b,"slot")));
            a.Dispose();Assert.That(b.IsReady,Is.True);Assert.That(ib.IsPosed,Is.False);NoPublication();
        }
        [TestCase("world")][TestCase("warm")][TestCase("renderer")][TestCase("bones")][TestCase("count")]
        [TestCase("bone")][TestCase("topology")][TestCase("scale")][TestCase("weights")][TestCase("material")]
        public void D6H_Cold_RefusesUnsupportedBeforePublication(string kind)
        {
            NewWorld(kind!="material");var r=Rig();var h=Bank();var warm=Keep(new VpPhysicsColdPreparation());
            int meshes=PreparedMeshes();var bones=new[]{r.transform};var topo=new[]{0,1,2,3};
            if(kind=="world")Assert.That(world.Shutdown(),Is.True);
            if(kind=="renderer")r=null;if(kind=="bones")bones=null;if(kind=="count")bones=Array.Empty<Transform>();
            if(kind=="bone")bones[0]=null;if(kind=="topology")topo[0]=99;
            if(kind=="scale")r.transform.localScale=Vector3.one*2;if(kind=="weights")r.quality=SkinQuality.Bone2;
            Assert.That(world.TryPrepareCharacterCut(r,topo,4,h.input.bank,Ranges(h),bones,kind=="warm"?null:warm,out var handle),Is.False);
            Assert.That(handle,Is.Null);Assert.That(PreparedMeshes(),Is.EqualTo(meshes));Assert.That(warm.IsPrepared,Is.False);
            if(kind!="world"){NoPublication();Assert.That(Field<int>(world.Display,"_preparedRootCount"),Is.Zero);}
        }
        [Test] public void D6H_Cold_PartialPhysicsFailure_ReleasesAlreadyCookedMeshAndSlot()
        {
            NewWorld();var r=Rig();var h=Bank(2);var warm=Keep(new VpPhysicsColdPreparation());
            int meshes=PreparedMeshes();var ranges=Ranges(h);ranges[1].faceCount=3;
            Assert.Throws<ArgumentException>(()=>world.TryPrepareCharacterCut(r,new[]{0,1,2,3},4,h.input.bank,ranges,
                new[]{r.transform,r.transform},warm,out _));
            Assert.That(PreparedMeshes(),Is.EqualTo(meshes));Assert.That(Field<int>(world.Display,"_preparedRootCount"),Is.Zero);
            Assert.That(warm.IsPrepared,Is.False);NoPublication();
        }
        [Test] public void D6H_Cold_WorldShutdownInvalidatesReadiness_CallerStillDisposesInput()
        {
            NewWorld();var r=Rig();var h=Bank();var warm=Keep(new VpPhysicsColdPreparation());
            Assert.That(Prepare(r,h,warm,out var handle),Is.True);Keep(handle);
            var shape=Field<VpPreparedPhysicsInput>(handle,"physics").ColdPreparationShape();
            Assert.That(world.Shutdown(),Is.True);Assert.That(handle.IsReady||handle.TryFinishPreparation(),Is.False);
            Assert.That(shape.IsFreed,Is.False);handle.Dispose();Assert.That(shape.IsFreed,Is.True);Assert.That(r!=null,Is.True);
        }
    }
}
