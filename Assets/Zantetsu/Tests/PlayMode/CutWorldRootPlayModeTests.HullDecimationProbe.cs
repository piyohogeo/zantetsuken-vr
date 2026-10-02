using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The 131-point college union brought within the vertex limit (the EditMode replay's input, read from that test's
    /// source): cooked as a real convex collider and cut by the real cook along four planes -- what the fusion does with
    /// the hull it adopts.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const string CollegeReplaySourcePath = "Assets/Zantetsu/Tests/EditMode/PhysicsCut/ConvexHullBuilderCollegeReplayTests.cs";

        /// <summary>The k_points3 array of the EditMode replay test, read from its source (the two assemblies do not reference each other).</summary>
        private static List<float3> CollegePoints3()
        {
            string text = File.ReadAllText(CollegeReplaySourcePath);
            int at = text.IndexOf("k_points3 =", System.StringComparison.Ordinal);
            int end = text.IndexOf("};", at, System.StringComparison.Ordinal);
            var points = new List<float3>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text.Substring(at, end - at), @"new float3\(([-0-9.eE]+)f, ([-0-9.eE]+)f, ([-0-9.eE]+)f\)"))
            {
                points.Add(new float3(float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), float.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }

            return points;
        }

        [UnityTest]
        public IEnumerator HullDecimationProbe_The131PointUnionWithin128_IsCookedAndCutByTheCook()
        {
            List<float3> points = CollegePoints3();
            Assert.That(points.Count, Is.EqualTo(131), "the replay's 131 points");
            CutWorldRoot root = NewHullWorld();
            yield return null;
            HullBrep brep = HullBrep.OfPoints(points, 128, out HullBuildReport report);
            Assert.That(brep, Is.Not.Null, report.reason);
            TestContext.Out.WriteLine("decimated hull: " + brep.VertexCount + " vertices " + brep.FaceCount + " faces, faces removed " + report.decimated + ", expansion " + report.expansion.ToString("E2") + " m");
            Assert.That(brep.VertexCount, Is.LessThanOrEqualTo(128));
            Mesh mesh = brep.MakeColliderMesh("decimated college hull", PhysicsCutCook.DefaultCooking);
            var probe = Track(new GameObject("Decimated hull probe"));
            var collider = probe.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = mesh;
            Assert.That(collider.GeometryHolder.Type, Is.EqualTo(UnityEngine.LowLevelPhysics.GeometryType.ConvexMesh), "PhysX cooked the decimated hull as a convex");
            PhysicsOwnerShape shape = PhysicsOwnerShape.Authored(brep.Bank, new[] { brep.Range }, new List<Mesh> { mesh }, PhysicsShapeSource.External(), float4x4.identity);
            int cuts = 0, failures = 0;
            foreach (float4 plane in new[] { new float4(1f, 0f, 0f, 0f), new float4(0f, 1f, 0f, -8f), new float4(0.6f, 0.8f, 0f, -3f), new float4(0f, 0f, 1f, 0.5f) })
            {
                if (!PhysicsCutClassification.TryClassify(shape, plane, 1e-4f, 10000.0, 128, out PhysicsCutClassification cls) || !cls.SplitsBothSides) { cls?.Dispose(); continue; }
                shape.AcquireForWork();
                ConvexCutOwnerInput input = cls.Input;
                PhysicsCutRequest request = root.Cook.Submit(in input, float4x4.identity);
                cuts++;
                yield return Until(() => request.IsOver, "cut " + cuts);
                if (request.Products == null) { failures++; TestContext.Out.WriteLine("plane " + plane + ": " + request.Outcome + " kernel " + request.KernelStatus + " clip " + (CutStatus)request.cut.kernel.cutStatus); }
                else request.Products.Dispose();
                shape.ReleaseFromWork();
                cls.Dispose();
            }

            TestContext.Out.WriteLine("real cook: cuts " + cuts + ", failures " + failures);
            Assert.That(cuts, Is.GreaterThanOrEqualTo(3), "the planes cross the hull");
            Assert.That(failures, Is.Zero, "the cook cuts the decimated hull");
            collider.sharedMesh = null;
            shape.Dispose();
            brep.Dispose();
            PhysicsCutCook.DestroyMesh(mesh);
            yield return EndWorld(root);
        }
    }
}
