using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        CutWorldRoot coldWorld;
        VpPhysicsColdPreparation coldWarm;
        readonly List<VpPreparedCharacterCut> coldHandles=new List<VpPreparedCharacterCut>();
        readonly List<Object> coldObjects=new List<Object>();
        T ColdTrack<T>(T obj) where T:Object {coldObjects.Add(obj);return obj;}
        static void ColdSet(object obj,string name,object value)=>obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(obj,value);
        void ColdWorld()
        {
            var profile=ColdTrack(ScriptableObject.CreateInstance<CutWorldProfile>());
            var mat=ColdTrack(new Material(Shader.Find("Zantetsu/VP Indexed Indirect Unlit")));
            var go=ColdTrack(new GameObject("D6H cold world"));go.SetActive(false);coldWorld=go.AddComponent<CutWorldRoot>();
            ColdSet(coldWorld,"profile",profile);ColdSet(coldWorld,"materials",new[]{new CutWorldRoot.MaterialBinding{sourceIndex=0,material=mat}});
            go.SetActive(true);Assert.That(coldWorld.IsReady,Is.True);
        }
        SkinnedMeshRenderer ColdRig()
        {
            var go=ColdTrack(new GameObject("D6H borrowed rig"));var r=go.AddComponent<SkinnedMeshRenderer>();r.quality=SkinQuality.Bone4;
            r.sharedMesh=ColdTrack(new Mesh{vertices=new[]{Vector3.zero,Vector3.right,Vector3.up,Vector3.forward},
                normals=Enumerable.Repeat(Vector3.up,4).ToArray(),uv=Enumerable.Repeat(new Vector2(.5f,.5f),4).ToArray(),
                triangles=new[]{0,2,1,0,1,3,0,3,2,1,2,3},bindposes=new[]{Matrix4x4.identity},
                boneWeights=Enumerable.Repeat(new BoneWeight{weight0=1},4).ToArray()});
            r.bones=new[]{go.transform};return r;
        }
        [UnityTest] public IEnumerator D6H_Cold_PendingBlocksReady_TwoHandlesBecomeReadyAfterLoadingFrame()
        {yield return ColdPending(false);}
        [UnityTest] public IEnumerator D6H_Cold_DisposeWhilePending_DoesNotReleaseNativeColliderMeshEarly()
        {yield return ColdPending(true);}
        IEnumerator ColdPending(bool cancel)
        {
            ColdWorld();var r=ColdRig();var source=NewAuthoredShape(1);coldWarm=new VpPhysicsColdPreparation();
            for(int i=0;i<2;i++)
            {
                Assert.That(coldWorld.TryPrepareCharacterCut(r,new[]{0,1,2,3},4,source.BankOf(0),new[]{source.Convex(0)},
                    new[]{r.transform},coldWarm,out var handle),Is.True);coldHandles.Add(handle);
                Assert.That(handle.IsReady||handle.TryFinishPreparation(),Is.False);
            }
            var roots=Resources.FindObjectsOfTypeAll<GameObject>().Where(g=>g.name.StartsWith("Cold physics preparation ")).ToArray();
            Assert.That(roots.Length,Is.EqualTo(2),"shared preparer, not two per handle");
            Assert.That(roots.All(g=>!g.activeInHierarchy),Is.True);
            var collider=roots.SelectMany(g=>g.GetComponentsInChildren<MeshCollider>(true)).Single();var mesh=collider.sharedMesh;
            Assert.That(coldWorld.Storage.VertexCount+coldWorld.Ledger.FragmentCount+coldWorld.Owners.Count+coldWorld.Display.VertexTransfers,Is.Zero);
            if(cancel)
            {
                foreach(var handle in coldHandles){handle.Dispose();handle.Dispose();}
                Assert.That(mesh!=null,Is.True);Assert.That(coldWarm.IsPrepared,Is.False);
            }
            yield return null;
            Assert.That(roots.All(g=>g==null)&&collider==null,Is.True);
            Assert.That(mesh!=null,Is.True,"bootstrap still owns the independent hold");
            if(cancel)
            {
                Assert.That(coldHandles.All(h=>!h.TryFinishPreparation()),Is.True);
                Assert.That(coldWarm.IsPrepared,Is.False,"disposed handles must not finish the borrowed preparer");
                Assert.That(coldWarm.TryFinish(),Is.True);
            }
            else foreach(var handle in coldHandles)Assert.That(handle.TryFinishPreparation()&&handle.IsReady,Is.True);
            Assert.That(coldWarm.IsPrepared,Is.True);
            yield return null;
            Assert.That(mesh==null,Is.EqualTo(cancel));Assert.That(r!=null&&r.sharedMesh!=null&&r.enabled,Is.True);
        }
        [UnityTearDown] public IEnumerator ColdCleanup()
        {
            if(coldWorld==null&&coldWarm==null&&coldHandles.Count==0&&coldObjects.Count==0)yield break;
            foreach(var handle in coldHandles)handle.Dispose();coldHandles.Clear();
            yield return null;
            if(coldWarm!=null){Assert.That(coldWarm.TryFinish(),Is.True);coldWarm.Dispose();coldWarm=null;}
            yield return null;
            if(coldWorld!=null)
            {
                for(int i=0;i<120&&!coldWorld.Shutdown();i++)yield return null;
                Assert.That(coldWorld.IsReleased,Is.True,"do not destroy resources while workers still hold them");
            }
            for(int i=coldObjects.Count-1;i>=0;i--)if(coldObjects[i]!=null)Object.Destroy(coldObjects[i]);
            coldObjects.Clear();coldWorld=null;
            yield return null;
        }
    }
}
