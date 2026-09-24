using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Zantetsu.PhysicsCut.Tests
{
    // Read-time representation probe, not an importer hook or a product runtime entry.
    // Keep the rig/animation hierarchy untouched; a new unit-scale renderer shares its bones.
    internal static class CharacterFixedScalePreparation
    {
        internal static SkinnedMeshRenderer Create(SkinnedMeshRenderer source, Transform unitParent, out float scale)
        {
            Vector3 s = source.transform.lossyScale;
            scale = s.x;
            if (!(scale > 0) || !float.IsFinite(scale) || Mathf.Abs(s.y-scale)>scale*1e-5f || Mathf.Abs(s.z-scale)>scale*1e-5f)
                throw new InvalidOperationException("Only fixed positive uniform scale is supported.");
            if ((unitParent.lossyScale-Vector3.one).magnitude>1e-5f || source.sharedMesh.blendShapeCount != 0)
                throw new InvalidOperationException("Prepare under a unit-scale parent, without blendshapes.");
            Matrix4x4 expected = Matrix4x4.TRS(source.transform.position,source.transform.rotation,Vector3.one*scale);
            for (int i=0;i<16;i++) if (Mathf.Abs(expected[i]-source.transform.localToWorldMatrix[i])>1e-5f)
                throw new InvalidOperationException("Sheared source frame is not supported.");
            var mesh = UnityEngine.Object.Instantiate(source.sharedMesh);
            mesh.name = source.sharedMesh.name + "-FixedScalePrepared";
            float fixedScale = scale;
            mesh.vertices = source.sharedMesh.vertices.Select(p => p*fixedScale).ToArray();
            Matrix4x4 inverseScale = Matrix4x4.Scale(Vector3.one/scale);
            mesh.bindposes = source.sharedMesh.bindposes.Select(b => b*inverseScale).ToArray();
            mesh.bounds = new Bounds(source.sharedMesh.bounds.center*scale,source.sharedMesh.bounds.size*scale);
            var go = new GameObject(source.name+"-UnitScaleRenderer");
            go.transform.SetParent(unitParent,false);
            go.transform.SetPositionAndRotation(source.transform.position,source.transform.rotation);
            go.transform.localScale = Vector3.one;
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = source.bones;
            renderer.rootBone = source.rootBone;
            renderer.sharedMaterials = source.sharedMaterials;
            renderer.quality = source.quality;
            renderer.updateWhenOffscreen = source.updateWhenOffscreen;
            renderer.localBounds = new Bounds(source.localBounds.center*scale,source.localBounds.size*scale);
            renderer.enabled = false; // Diagnostic snapshots only; don't draw a duplicate.
            return renderer;
        }
    }

    public class Compact16uvScaleNormalizationTests
    {
        [TestCase("character-casual")] [TestCase("character-professional")]
        public void FixedScale_LoadPreparation_PreservesAnimatedWorldGeometry(string family)
        {
            const string root = "Assets/Licensed/Compact16uvConvexRepair/";
            if (!File.Exists(root+"intake.json")) Assert.Ignore("Private repaired intake missing.");
            var entry = JsonUtility.FromJson<Manifest>(File.ReadAllText(root+"intake.json")).assets.Single(e => e.family==family);
            var parent = new GameObject("Fixed scale preparation comparison");
            Mesh preparedMesh=null;
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
                var prepared = CharacterFixedScalePreparation.Create(original,parent.transform,out float scale);
                preparedMesh=prepared.sharedMesh;
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
                    original.BakeMesh(beforeBake,true); prepared.BakeMesh(afterBake,true);
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
                UnityEngine.Object.DestroyImmediate(parent);
                if (preparedMesh!=null) UnityEngine.Object.DestroyImmediate(preparedMesh);
                UnityEngine.Object.DestroyImmediate(beforeBake); UnityEngine.Object.DestroyImmediate(afterBake);
            }
        }
        [Serializable] sealed class Manifest { public Entry[] assets; }
        [Serializable] sealed class Entry { public string family,assetPath,objectName; }
    }
}
