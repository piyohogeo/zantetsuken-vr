using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.MeshCut.Tests
{
    public class VpPreparedRootDisplayTests
    {
        readonly List<IDisposable> owned = new List<IDisposable>();
        readonly List<Object> objects = new List<Object>();
        int frame = 1;
        Action duringFrame;
        T Keep<T>(T x) where T : IDisposable { owned.Add(x); return x; }
        T Track<T>(T x) where T : Object { objects.Add(x); return x; }
        [TearDown] public void Cleanup()
        {
            duringFrame = null;
            for (int i=owned.Count-1;i>=0;i--) owned[i].Dispose(); owned.Clear();
            for (int i=objects.Count-1;i>=0;i--) if(objects[i]!=null) Object.DestroyImmediate(objects[i]); objects.Clear();
            frame=1;
        }
        VpDirectSkinInput Input()
        {
            var go=Track(new GameObject("Prepared root synthetic rig"));
            var renderer=go.AddComponent<SkinnedMeshRenderer>(); renderer.quality=SkinQuality.Bone4;
            var mesh=Track(new Mesh {vertices=new[]{Vector3.zero,Vector3.right,Vector3.up,Vector3.forward},
                normals=Enumerable.Repeat(Vector3.up,4).ToArray(), uv=Enumerable.Repeat(new Vector2(.5f,.5f),4).ToArray(),
                triangles=new[]{0,2,1,0,1,3,0,3,2,1,2,3},bindposes=new[]{Matrix4x4.identity},
                boneWeights=Enumerable.Repeat(new BoneWeight{weight0=1},4).ToArray()});
            renderer.sharedMesh=mesh; renderer.bones=new[]{go.transform}; renderer.rootBone=go.transform;
            Assert.That(VpDirectSkinInput.TryCreate(renderer,new[]{0,1,2,3},4,out var input),Is.True);
            return Keep(input);
        }
        VpCpuGeometryStorage Storage()=>Keep(new VpCpuGeometryStorage(128,384,32,32,32,Allocator.Persistent));
        sealed class World
        {
            internal VpCpuGeometryStorage storage;
            internal VpGeometryReferenceTable table;
            internal LogicalCutLedger ledger;
            internal VpLogicalCutDisplay display;
            internal Dictionary<int,Material> materials;
        }
        World NewWorld(int slots=16,int instanceCapacity=16,bool material=true)
        {
            var w=new World {storage=Storage(),ledger=new LogicalCutLedger(new LogicalCutIncompleteBudget(8))};
            w.table=new VpGeometryReferenceTable(w.storage,slots,slots);
            w.materials=new Dictionary<int,Material>();
            if(material)w.materials.Add(0,Track(new Material(Shader.Find("Zantetsu/VP Indexed Indirect Unlit"))));
            Assert.That(VpLogicalCutDisplay.TryCreate(w.storage,w.table,w.ledger,w.materials,null,null,16,instanceCapacity,
                VpDisplayTestCapacities.Branches,VpDisplayTestCapacities.Candidates,VpDisplayTestCapacities.ChainDepth,
                VpStencilTestSettings.Create(),()=>{duringFrame?.Invoke();return frame;},out w.display),Is.True);
            Keep(w.display);return w;
        }
        VpLogicalCutDisplay.PreparedRoot Slot(World w,VpDirectSkinInput input)
        {Assert.That(w.display.TryPrepareRoot(input,out var slot),Is.True);return Keep(slot);}
        static VpDirectSkinOutput Append(VpDirectSkinInput input,VpCpuGeometryStorage storage)
        {Assert.That(input.TryAppendForDisplay(storage,out var output),Is.True);return output;}
        static bool Show(World w,VpLogicalCutDisplay.PreparedRoot slot,VpDirectSkinOutput output,LogicalFragmentId id)
            =>w.display.TryShowPreparedRoot(slot,output,id,Matrix4x4.identity,Matrix4x4.identity);

        [Test] public void D4_ShownCapacity_TwoRoots_NoGrowth_NoLogicalOrGpuReservation()
        {
            var w=NewWorld(); var a=Input();var b=Input();
            w.display.PrepareShownCapacity(8); int capacity=D4ColdPreparationTests.Capacity(w.display,"_shown");
            var sa=Slot(w,a);var sb=Slot(w,b);w.display.PrepareShownCapacity(0);
            Assert.That(w.ledger.FragmentCount+w.ledger.Revision+w.table.LiveGeometryCount+w.storage.VertexCount,Is.Zero);
            Assert.Throws<ArgumentOutOfRangeException>(()=>w.display.PrepareShownCapacity(-1));
            Assert.That(Show(w,sa,Append(a,w.storage),w.ledger.AddFragment()),Is.True);
            Assert.That(Show(w,sb,Append(b,w.storage),w.ledger.AddFragment()),Is.True);
            Assert.That(D4ColdPreparationTests.Capacity(w.display,"_shown"),Is.EqualTo(capacity));
            Assert.That(w.table.LiveGeometryCount,Is.EqualTo(2));
            w.display.Dispose();Assert.Throws<ObjectDisposedException>(()=>w.display.PrepareShownCapacity(8));
        }

        [Test] public void ColdSlots_AreDistinct_DoNotReserveIdsStorageReferencesOrBudget()
        {
            var a=Input();var b=Input();var w=NewWorld();int vertices=w.storage.VertexCount;
            var sa=Slot(w,a);var sb=Slot(w,b);
            Assert.That(sa.entry,Is.Not.SameAs(sb.entry)); Assert.That(sa.entry.commands,Is.Not.SameAs(sb.entry.commands));
            Assert.That(sa.entry.ranges,Is.Not.SameAs(sb.entry.ranges)); Assert.That(sa.entry.instances.Capacity,Is.GreaterThanOrEqualTo(1));
            Assert.That(w.storage.VertexCount,Is.EqualTo(vertices)); Assert.That(w.table.LiveGeometryCount,Is.Zero);
            Assert.That(w.ledger.FragmentCount,Is.Zero);Assert.That(w.ledger.Revision,Is.Zero);
            Assert.That(w.ledger.Budget.IncompleteCutOperationCount,Is.Zero);
        }
        [Test] public void PreparedCommandMatchesGeneral_NonzeroVertexAndIndexOffset_NoColdArraysReplaced()
        {
            var input=Input();var w=NewWorld();Append(input,w.storage);var slot=Slot(w,input);var entry=slot.entry;
            var output=Append(input,w.storage);var id=w.ledger.AddFragment();var arrays=entry.commands;
            Assert.That(w.storage.TryGetIndexState(output.Geometry.indexRange,out _,out int start,out _),Is.True);
            Assert.That(start,Is.GreaterThan(0));Assert.That(output.Geometry.vertexStart,Is.GreaterThan(0));
            Assert.That(VpStoredGeometryDraw.TryBuildCommands(w.storage,output.Geometry,start,out var general,out var materials,out var bounds),Is.True);
            Assert.That(Show(w,slot,output,id),Is.True);
            Assert.That(entry.commands,Is.SameAs(arrays));Assert.That(entry.commands[0],Is.EqualTo(general[0]));
            Assert.That(entry.ranges[0],Is.EqualTo(general[0].range));Assert.That(entry.localBounds,Is.EqualTo(bounds));
            Assert.That(entry.commandMaterials[0],Is.SameAs(w.materials[materials[0]]));
            Assert.That(slot.IsConsumed&&slot.IsCommitted,Is.True);Assert.That(slot.entry,Is.Null);
            slot.Dispose();Assert.That(w.table.LiveGeometryCount,Is.EqualTo(1));
        }
        [Test] public void TwoProducers_RegisterIndependently_NoAmbientArm()
        {
            var a=Input();var b=Input();var w=NewWorld();var sa=Slot(w,a);var sb=Slot(w,b);
            var oa=Append(a,w.storage);var ob=Append(b,w.storage);var ia=w.ledger.AddFragment();var ib=w.ledger.AddFragment();
            Assert.That(Show(w,sa,ob,ia),Is.False);Assert.That(sa.IsConsumed,Is.False);
            Assert.That(Show(w,sb,ob,ib),Is.True);Assert.That(Show(w,sa,oa,ia),Is.True);
            Assert.That(w.table.LiveGeometryCount,Is.EqualTo(2));Assert.That(w.display.VertexTransfers,Is.EqualTo(2));
        }
        [TestCase("default")][TestCase("storage")][TestCase("display")][TestCase("disposed-slot")]
        public void InvalidReceiptOrSlot_IsRefusedBeforeTransfer(string kind)
        {
            var input=Input();var w=NewWorld();var slot=Slot(w,input);var output=Append(input,w.storage);
            if(kind=="default")output=default;
            if(kind=="storage")output=Append(input,Storage());
            if(kind=="display")slot=Slot(NewWorld(),input);
            if(kind=="disposed-slot")slot.Dispose();
            Assert.That(Show(w,slot,output,w.ledger.AddFragment()),Is.False);
            Assert.That(w.display.VertexTransfers,Is.Zero);Assert.That(w.table.LiveGeometryCount,Is.Zero);
        }
        [Test] public void RetiredThenReusedDescriptor_RejectsOldReceipt_AcceptsFreshOutput()
        {
            var input=Input();var w=NewWorld();var slot=Slot(w,input);var old=Append(input,w.storage);
            Assert.That(w.storage.TryRetireIndices(old.Geometry.indexRange),Is.True);var fresh=Append(input,w.storage);
            var id=w.ledger.AddFragment();Assert.That(Show(w,slot,old,id),Is.False);Assert.That(slot.IsConsumed,Is.False);
            Assert.That(Show(w,slot,fresh,id),Is.True);
        }
        [TestCase("fragment")][TestCase("frame")][TestCase("placement")]
        public void EarlyRefusal_LeavesSlotReady_ForExplicitLaterAttempt(string reason)
        {
            var input=Input();var w=NewWorld();var slot=Slot(w,input);var output=Append(input,w.storage);var id=w.ledger.AddFragment();
            bool result;
            if(reason=="frame") {Assert.That(w.display.TryBeginFrame(),Is.True);result=Show(w,slot,output,id);frame++;}
            else if(reason=="placement")result=w.display.TryShowPreparedRoot(slot,output,id,Matrix4x4.identity,Matrix4x4.Scale(Vector3.one*2));
            else result=Show(w,slot,output,default);
            Assert.That(result,Is.False);Assert.That(slot.IsConsumed,Is.False);Assert.That(w.display.VertexTransfers,Is.Zero);
            Assert.That(Show(w,slot,output,id),Is.True);
        }
        [Test] public void ReferenceTableRefusal_ConsumesSlotButDoesNotRetireCallerGeometry()
        {
            var input=Input();var w=NewWorld(slots:1);var first=Append(input,w.storage);var second=Append(input,w.storage);
            var slot=Slot(w,input);Assert.That(w.table.TryRegisterGeometryWithDisplayInstance(first.Geometry,out var reference,out var instance),Is.True);
            Assert.That(Show(w,slot,second,w.ledger.AddFragment()),Is.False);
            Assert.That(slot.IsConsumed,Is.True);Assert.That(slot.IsCommitted,Is.False);
            Assert.That(w.storage.TryGetIndexState(second.Geometry.indexRange,out var state,out _,out _),Is.True);Assert.That(state,Is.EqualTo(VpIndexRangeState.Published));
            Assert.That(w.table.TryRetireDisplayInstance(instance),Is.True);Assert.That(w.table.TryRetireGeometry(reference),Is.True);
            Assert.That(Show(w,slot,second,w.ledger.AddFragment()),Is.False);
            Assert.That(w.display.TryShow(w.ledger.AddFragment(),second.Geometry,Matrix4x4.identity),Is.True,"general path remains usable");
        }
        [Test] public void CapacityRefusal_IsEarly_NotAReservation()
        {
            var input=Input();var w=NewWorld(instanceCapacity:2);var a=Slot(w,input);var b=Slot(w,input);
            Assert.That(Show(w,a,Append(input,w.storage),w.ledger.AddFragment()),Is.True);
            Assert.That(Show(w,b,Append(input,w.storage),w.ledger.AddFragment()),Is.False);Assert.That(b.IsConsumed,Is.False);
        }
        [Test] public void DisposeUnusedMiddleSlot_AndDisplay_ReleasesRemainingColdEntries()
        {
            var input=Input();var w=NewWorld();var a=Slot(w,input);var b=Slot(w,input);var c=Slot(w,input);
            b.Dispose();Assert.That(b.entry,Is.Null);Assert.That(a.IsDisposed||c.IsDisposed,Is.False);
            w.display.Dispose();Assert.That(a.IsDisposed&&c.IsDisposed,Is.True);Assert.That(a.entry,Is.Null);Assert.That(c.entry,Is.Null);
            Assert.That(w.table.LiveGeometryCount,Is.Zero);
        }
        [Test] public void MaterialResolvedCold_MissingRefused_DestroyedRefused_BindingFrozen()
        {
            var input=Input();var missing=NewWorld(material:false);Assert.That(missing.display.TryPrepareRoot(input,out _),Is.False);
            var w=NewWorld();var slot=Slot(w,input);var captured=slot.entry.commandMaterials[0];w.materials.Clear();
            Assert.That(Show(w,slot,Append(input,w.storage),w.ledger.AddFragment()),Is.True,"frozen cold binding, no dictionary lookup");
            var other=NewWorld();var dead=Slot(other,input);Object.DestroyImmediate(other.materials[0]);
            Assert.That(Show(other,dead,Append(input,other.storage),other.ledger.AddFragment()),Is.False);Assert.That(dead.IsConsumed,Is.False);
        }
        [Test] public void ProducerDisposal_RefusesNewPreparation_ButPublishedReceiptRemainsValid()
        {
            var input=Input();var w=NewWorld();var slot=Slot(w,input);var output=Append(input,w.storage);input.Dispose();
            Assert.That(w.display.TryPrepareRoot(input,out _),Is.False);Assert.That(Show(w,slot,output,w.ledger.AddFragment()),Is.True);
        }
        [Test] public void ConsumedSlot_CannotRegisterTwice_AndDoesNotAffectGeneralRegistration()
        {
            var input=Input();var w=NewWorld();var slot=Slot(w,input);var first=Append(input,w.storage);
            Assert.That(Show(w,slot,first,w.ledger.AddFragment()),Is.True);var second=Append(input,w.storage);
            Assert.That(Show(w,slot,second,w.ledger.AddFragment()),Is.False);
            Assert.That(w.display.TryShow(w.ledger.AddFragment(),second.Geometry,Matrix4x4.identity),Is.True);
        }
        [Test] public void FrameCallbackDisposesSlot_NoUseAfterDispose()
        {
            var input=Input();var w=NewWorld();var slot=Slot(w,input);var output=Append(input,w.storage);
            duringFrame=()=>slot.Dispose();Assert.That(Show(w,slot,output,w.ledger.AddFragment()),Is.False);
            Assert.That(w.display.VertexTransfers,Is.Zero);
        }
        [Test] public void UploadException_ConsumesSlot_BreaksDisplay_NoReferenceRegistration()
        {
            var input=Input();var w=NewWorld();var slot=Slot(w,input);var output=Append(input,w.storage);
            var buffers=(VpGpuIndexedGeometryBuffers)typeof(VpLogicalCutDisplay).GetField("_buffers",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(w.display);
            buffers.VertexBuffer.Dispose();
            Assert.Catch<Exception>(()=>Show(w,slot,output,w.ledger.AddFragment()));
            Assert.That(slot.IsConsumed,Is.True);Assert.That(slot.IsCommitted,Is.False);Assert.That(w.table.LiveGeometryCount,Is.Zero);
            Assert.Throws<InvalidOperationException>(()=>w.display.TryShow(w.ledger.AddFragment(),output.Geometry,Matrix4x4.identity));
        }
    }
}
