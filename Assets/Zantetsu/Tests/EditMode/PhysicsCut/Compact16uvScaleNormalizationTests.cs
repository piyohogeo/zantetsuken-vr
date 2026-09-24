using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    public class Compact16uvScaleNormalizationTests
    {
        [TestCase("character-casual")] [TestCase("character-professional")]
        public void FixedScale_LoadPreparation_PreservesAnimatedWorldGeometry(string family)
        {
            const string root = "Assets/Licensed/Compact16uvConvexRepair/";
            if (!File.Exists(root+"intake.json")) Assert.Ignore("Private repaired intake missing.");
            var entry = JsonUtility.FromJson<Manifest>(File.ReadAllText(root+"intake.json")).assets.Single(e => e.family==family);
            var parent = new GameObject("Fixed scale preparation comparison");
            using var cache=new VpFixedScaleSkinCache();
            VpFixedScaleSkinInput input=null;
            var beforeBake = new Mesh(); var afterBake = new Mesh();
            try
            {
                var instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(entry.assetPath),parent.transform);
                foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) animator.enabled=false;
                var original = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(s => s.sharedMesh!=null && s.sharedMesh.name==entry.objectName);
                var hierarchy = instance.GetComponentsInChildren<Transform>(true);
                var initialMatrices = hierarchy.Select(t => t.localToWorldMatrix).ToArray();
                var bindPositions = original.sharedMesh.vertices;
                var originalBindposes = original.sharedMesh.bindposes;
                input=cache.Prepare(original,parent.transform);
                var prepared=input.Renderer;
                float scale=input.FixedScale;
                var preparedMesh=prepared.sharedMesh;
                Assert.That(scale, Is.EqualTo(family=="character-professional" ? .01f : 1f).Within(1e-6f));
                CollectionAssert.AreEqual(initialMatrices,hierarchy.Select(t => t.localToWorldMatrix),"rig and UCX hierarchy untouched at preparation");
                CollectionAssert.AreEqual(original.bones,prepared.bones);
                Assert.That(prepared.rootBone,Is.SameAs(original.rootBone));
                CollectionAssert.AreEqual(original.sharedMesh.uv,preparedMesh.uv);
                CollectionAssert.AreEqual(original.sharedMesh.normals,preparedMesh.normals);
                CollectionAssert.AreEqual(original.sharedMesh.tangents,preparedMesh.tangents);
                using (var a=original.sharedMesh.GetAllBoneWeights()) using (var b=preparedMesh.GetAllBoneWeights()) CollectionAssert.AreEqual(a.ToArray(),b.ToArray());
                for (int s=0;s<original.sharedMesh.subMeshCount;s++) CollectionAssert.AreEqual(original.sharedMesh.GetIndices(s),preparedMesh.GetIndices(s));
                var rotations=original.bones.Select(b=>b.localRotation).ToArray();
                var positions=original.bones.Select(b=>b.localPosition).ToArray();
                var rootScale=original.rootBone.localScale;
                float maxWorld=0,maxNormal=0;
                for (int frame=0;frame<300;frame++)
                {
                    float phase=frame*.031f;
                    for (int i=0;i<original.bones.Length;i++)
                    {
                        original.bones[i].localRotation=rotations[i]*Quaternion.Euler(0,Mathf.Sin(phase+i*.1f)*2,Mathf.Cos(phase+i*.2f)*2);
                        original.bones[i].localPosition=positions[i]+new Vector3(Mathf.Sin(phase+i)*.0001f,0,0);
                    }
                    original.rootBone.localScale=Vector3.Scale(rootScale,frame<100 ? Vector3.one : frame<200 ? Vector3.one*1.05f : new Vector3(1.05f,.95f,1.1f));
                    parent.transform.SetPositionAndRotation(new Vector3(2,0,3),Quaternion.Euler(0,frame*.1f,0));
                    original.BakeMesh(beforeBake,true); input.BakeCurrentPose(afterBake);
                    var a=beforeBake.vertices; var b=afterBake.vertices;
                    var an=beforeBake.normals; var bn=afterBake.normals;
                    Matrix4x4 normalA=original.transform.worldToLocalMatrix.transpose;
                    Matrix4x4 normalB=prepared.transform.worldToLocalMatrix.transpose;
                    for (int v=0;v<a.Length;v++)
                    {
                        maxWorld=Mathf.Max(maxWorld,Vector3.Distance(original.transform.TransformPoint(a[v]),prepared.transform.TransformPoint(b[v])));
                        maxNormal=Mathf.Max(maxNormal,Vector3.Distance(normalA.MultiplyVector(an[v]).normalized,normalB.MultiplyVector(bn[v]).normalized));
                    }
                    Assert.That(Vector3.Distance(prepared.transform.lossyScale,Vector3.one),Is.LessThan(1e-5));
                }
                Assert.That(maxWorld,Is.LessThan(2e-5f));
                Assert.That(maxNormal,Is.LessThan(2e-5f));
                CollectionAssert.AreEqual(bindPositions,original.sharedMesh.vertices,"original asset mesh never mutated");
                CollectionAssert.AreEqual(originalBindposes,original.sharedMesh.bindposes);
                TestContext.WriteLine($"SCALE_PREP family={family} scale={scale:R} frames=300 maxWorldError={maxWorld:R} maxWorldNormalVectorError={maxNormal:R} unitRenderer=True rigAndUcxUntouched=True");
            }
            finally
            {
                input?.Dispose();
                UnityEngine.Object.DestroyImmediate(parent);
                UnityEngine.Object.DestroyImmediate(beforeBake); UnityEngine.Object.DestroyImmediate(afterBake);
            }
        }
        [TestCase("character-casual")] [TestCase("character-professional")]
        public void FixedScale_ImportedClip_PreservesWorldGeometry(string family)
        {
            const string root="Assets/Licensed/Compact16uvConvexRepair/";
            if (!File.Exists(root+"intake.json")) Assert.Ignore("Private repaired intake missing.");
            var entry=JsonUtility.FromJson<Manifest>(File.ReadAllText(root+"intake.json")).assets.Single(e=>e.family==family);
            var clip=AssetDatabase.LoadAllAssetsAtPath(entry.assetPath).OfType<AnimationClip>()
                .First(c=>!c.name.StartsWith("__preview__") && c.length>0 && AnimationUtility.GetCurveBindings(c).Length>0);
            var bindings=AnimationUtility.GetCurveBindings(clip);
            int varying=bindings.Count(b=>AnimationUtility.GetEditorCurve(clip,b).keys.Select(k=>k.value).Distinct().Count()>1);
            var parent=new GameObject("Imported clip scale input");
            var a=new Mesh(); var b=new Mesh();
            using var cache=new VpFixedScaleSkinCache();
            VpFixedScaleSkinInput input=null;
            try
            {
                var instance=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(entry.assetPath),parent.transform);
                foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) animator.enabled=false;
                var source=instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r=>r.sharedMesh!=null && r.sharedMesh.name==entry.objectName);
                input=cache.Prepare(source,parent.transform);
                float error=0,motion=0,normalError=0;
                Vector3[] first=null;
                for (int frame=0;frame<120;frame++)
                {
                    clip.SampleAnimation(instance,clip.length*frame/119);
                    source.BakeMesh(a,true); input.BakeCurrentPose(b);
                    var av=a.vertices; var bv=b.vertices;
                    var an=a.normals; var bn=b.normals;
                    var na=source.transform.worldToLocalMatrix.transpose;
                    var nb=input.Renderer.transform.worldToLocalMatrix.transpose;
                    var world=av.Select(p=>source.transform.TransformPoint(p)).ToArray();
                    if (first==null) first=world;
                    for (int i=0;i<world.Length;i++)
                    {
                        error=Mathf.Max(error,Vector3.Distance(world[i],input.Renderer.transform.TransformPoint(bv[i])));
                        motion=Mathf.Max(motion,Vector3.Distance(first[i],world[i]));
                        normalError=Mathf.Max(normalError,Vector3.Distance(na.MultiplyVector(an[i]).normalized,nb.MultiplyVector(bn[i]).normalized));
                    }
                }
                Assert.That(error,Is.LessThan(2e-5f)); Assert.That(normalError,Is.LessThan(2e-5f));
                TestContext.WriteLine($"IMPORTED_CLIP family={family} clip={clip.name} length={clip.length:R} curves={bindings.Length} varyingCurves={varying} samples=120 maxWorldError={error:R} maxWorldNormalError={normalError:R} maxWorldMotion={motion:R}");
            }
            finally
            {
                input?.Dispose(); UnityEngine.Object.DestroyImmediate(parent);
                UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b);
            }
        }
        [Serializable] sealed class Manifest { public Entry[] assets; }
        [Serializable] sealed class Entry { public string family,assetPath,objectName; }
    }
}
