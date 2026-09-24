using System;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.Core.Tests
{
    public class VpFixedScaleSkinInputTests
    {
        GameObject parent;
        Mesh mesh;
        VpFixedScaleSkinCache cache;
        [SetUp] public void SetUp()
        {
            parent=new GameObject("unit parent");
            mesh=new Mesh { vertices=new[]{Vector3.zero,Vector3.right,Vector3.up},
                normals=new[]{Vector3.forward,Vector3.forward,Vector3.forward},
                triangles=new[]{0,1,2}, bindposes=new[]{Matrix4x4.identity},
                boneWeights=new[]{Weight(),Weight(),Weight()} };
            cache=new VpFixedScaleSkinCache();
        }
        static BoneWeight Weight() => new BoneWeight { boneIndex0=0,weight0=1 };
        SkinnedMeshRenderer Source(float scale=.01f)
        {
            var go=new GameObject("source"); go.transform.SetParent(parent.transform,false);
            go.transform.localScale=Vector3.one*scale;
            var r=go.AddComponent<SkinnedMeshRenderer>(); r.sharedMesh=mesh;
            r.bones=new[]{go.transform}; r.rootBone=go.transform;
            return r;
        }
        [TearDown] public void TearDown()
        {
            cache.Dispose(); UnityEngine.Object.DestroyImmediate(parent); UnityEngine.Object.DestroyImmediate(mesh);
        }

        [Test] public void SharedMesh_IndependentRig_ExplicitLifetime_RestoreVisibility()
        {
            var a=Source(); var b=Source(); b.enabled=false;
            Mesh prepared;
            using (var ia=cache.Prepare(a,parent.transform))
            using (var ib=cache.Prepare(b,parent.transform))
            {
                prepared=ia.Renderer.sharedMesh;
                Assert.That(ib.Renderer.sharedMesh,Is.SameAs(prepared));
                Assert.That(ia.Renderer.bones[0],Is.Not.SameAs(ib.Renderer.bones[0]));
                Assert.That(a.enabled,Is.False); Assert.That(b.enabled,Is.False);
                Assert.That(ia.Renderer.enabled,Is.True); Assert.That(ib.Renderer.enabled,Is.False);
                Assert.That(cache.PreparedMeshBuildCount,Is.EqualTo(1));
                Assert.That(cache.ActiveInputCount,Is.EqualTo(2));
                Assert.Throws<InvalidOperationException>(()=>cache.Dispose());
                Assert.Throws<InvalidOperationException>(()=>cache.Prepare(a,parent.transform));
            }
            Assert.That(a.enabled,Is.True); Assert.That(b.enabled,Is.False);
            Assert.That(cache.ActiveInputCount,Is.Zero);
            using (var again=cache.Prepare(a,parent.transform)) Assert.That(again.Renderer.sharedMesh,Is.SameAs(prepared));
            cache.Dispose(); Assert.That(prepared==null,Is.True); Assert.That(mesh!=null,Is.True);
            Assert.Throws<ObjectDisposedException>(()=>cache.Prepare(a,parent.transform));
        }

        [Test] public void UnitScale_BorrowsSource_AndDifferentScaleGetsSeparateMesh()
        {
            using var unit=cache.Prepare(Source(1),parent.transform);
            using var small=cache.Prepare(Source(.01f),parent.transform);
            using var other=cache.Prepare(Source(.02f),parent.transform);
            Assert.That(unit.Renderer.sharedMesh,Is.SameAs(mesh));
            Assert.That(small.Renderer.sharedMesh,Is.Not.SameAs(other.Renderer.sharedMesh));
            Assert.That(cache.CachedMeshCount,Is.EqualTo(3));
            Assert.That(cache.PreparedMeshBuildCount,Is.EqualTo(2));
            Assert.That(small.PrepareBindPoint(Vector3.right),Is.EqualTo(Vector3.right*.01f));
        }

        [TestCase(-.01f,.01f,.01f)] [TestCase(.01f,.02f,.01f)] [TestCase(0,0,0)]
        public void UnsupportedScale_RejectsBeforeSourceSwitch(float x,float y,float z)
        {
            var source=Source(); source.transform.localScale=new Vector3(x,y,z);
            Assert.Throws<ArgumentException>(()=>cache.Prepare(source,parent.transform));
            Assert.That(source.enabled,Is.True); Assert.That(cache.CachedMeshCount,Is.Zero);
        }

        [Test] public void Blendshape_RejectsBeforeSourceSwitch()
        {
            var source=Source(); mesh.AddBlendShapeFrame("test",100,new Vector3[3],new Vector3[3],new Vector3[3]);
            Assert.Throws<ArgumentException>(()=>cache.Prepare(source,parent.transform));
            Assert.That(source.enabled,Is.True); Assert.That(cache.CachedMeshCount,Is.Zero);
        }

        [Test] public void Bake_RejectsChangedParentScale_AndAssetDestination()
        {
            var source=Source(); using var input=cache.Prepare(source,parent.transform);
            var bake=new Mesh();
            try
            {
                Assert.Throws<ArgumentException>(()=>input.BakeCurrentPose(mesh));
                Assert.Throws<ArgumentException>(()=>input.BakeCurrentPose(input.Renderer.sharedMesh));
                parent.transform.localScale=new Vector3(1.3f,.8f,1.1f);
                Assert.Throws<ArgumentException>(()=>input.BakeCurrentPose(bake));
                parent.transform.localScale=Vector3.one;
                input.BakeCurrentPose(bake); Assert.That(bake.vertexCount,Is.EqualTo(3));
                input.Dispose(); input.Dispose();
                Assert.Throws<ObjectDisposedException>(()=>input.BakeCurrentPose(bake));
            }
            finally { UnityEngine.Object.DestroyImmediate(bake); }
        }

        [Test] public void RendererSettingsAndPropertyBlock_Preserved()
        {
            var source=Source(); source.gameObject.layer=17; source.renderingLayerMask=37;
            source.sortingOrder=4; source.receiveShadows=false; source.updateWhenOffscreen=true;
            var block=new MaterialPropertyBlock(); block.SetFloat("_PreparationTest",7); source.SetPropertyBlock(block);
            using var input=cache.Prepare(source,parent.transform);
            Assert.That(input.Renderer.gameObject.layer,Is.EqualTo(17));
            Assert.That(input.Renderer.renderingLayerMask,Is.EqualTo(37));
            Assert.That(input.Renderer.sortingOrder,Is.EqualTo(4));
            Assert.That(input.Renderer.receiveShadows,Is.False); Assert.That(input.Renderer.updateWhenOffscreen,Is.True);
            block.Clear(); input.Renderer.GetPropertyBlock(block); Assert.That(block.GetFloat("_PreparationTest"),Is.EqualTo(7));
        }
    }
}
