using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>A probe (2026-09-30): fused hulls of two cut halves moved apart, cut by the real cook -- which clip status each gives, so that the hull builder's output is what the kernel takes.</summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static List<float3> HalvesApart(int trial)
        {
            var pts = new List<float3>();
            const float x = 0.3f;
            foreach (float3 c in k_corners) if (c.x < x) pts.Add(c);
            var section = new[] { new float3(x, -1f, -1f), new float3(x, -1f, 1f), new float3(x, 1f, -1f), new float3(x, 1f, 1f) };
            pts.AddRange(section);
            float4x4 moved = float4x4.TRS(new float3(0.02f * trial, -0.6f - 0.01f * trial, 0.03f * trial), quaternion.EulerXYZ(0.05f * trial, 0.1f * trial, 0.02f * trial), 1f);
            foreach (float3 c in k_corners) if (c.x > x) pts.Add(math.transform(moved, c));
            foreach (float3 c in section) pts.Add(math.transform(moved, c));
            return pts;
        }

        /// <summary>The sixteen vertices a fused hull of two flush halves had when the clip kernel failed its walk (the ten-round test, 2026-09-30).</summary>
        private static readonly float3[] k_flushHalves =
        {
            new float3(-1f, -1f, 1f), new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, -1f, 1f), new float3(1.00000179f, -0.150071219f, -1.00000465f), new float3(1f, -0.149999976f, 1f),
            new float3(-1f, -0.149999976f, 1f), new float3(0.999780834f, 0.150743648f, -1.00023913f), new float3(0.9991587f, 1.001487f, 0.999096f), new float3(0.9997829f, 0.150743484f, 0.9997617f),
            new float3(-1.00084078f, 1.0000174f, 0.999098837f), new float3(-1.0002172f, 0.1507428f, 0.999764562f), new float3(-1.00084245f, 0.998459637f, -1.00090051f), new float3(0.9995152f, 0.1507448f, -1.0005213f),
            new float3(0.999157f, 0.999929249f, -1.00090337f), new float3(-1.00048208f, 0.150742769f, 0.9994814f),
        };

        [UnityTest]
        public IEnumerator HullKernelProbe_FusedHullsOfMovedHalves_AreCutByTheCook()
        {
            CutWorldRoot root = NewHullWorld();
            yield return null;
            int failures = 0, cuts = 0;
            for (int trial = 0; trial < 13; trial++)
            {
                HullBrep brep = HullBrep.OfPoints(trial == 12 ? new List<float3>(k_flushHalves) : HalvesApart(trial), out string reason);
                if (trial == 12) TestContext.Out.WriteLine("flush halves: hull " + (brep != null ? brep.VertexCount + " vertices " + brep.FaceCount + " faces" : "refused: " + reason));
                Assert.That(brep, Is.Not.Null, "trial " + trial + ": " + reason);
                Mesh mesh = brep.MakeColliderMesh("probe hull " + trial, PhysicsCutCook.DefaultCooking);
                PhysicsOwnerShape shape = PhysicsOwnerShape.Authored(brep.Bank, new[] { brep.Range }, new List<Mesh> { mesh }, PhysicsShapeSource.External(), float4x4.identity);
                foreach (float4 plane in new[] { new float4(1f, 0f, 0f, -0.1f), new float4(0f, 1f, 0f, 0.2f), new float4(0.6f, 0.8f, 0f, -0.05f), new float4(0f, 0f, 1f, 0.1f) })
                {
                    if (!PhysicsCutClassification.TryClassify(shape, plane, 1e-4f, 12.0, 128, out PhysicsCutClassification cls) || !cls.SplitsBothSides) { cls?.Dispose(); continue; }
                    shape.AcquireForWork();
                    ConvexCutOwnerInput input = cls.Input;
                    PhysicsCutRequest request = root.Cook.Submit(in input, float4x4.identity);
                    cuts++;
                    yield return Until(() => request.IsOver, "trial " + trial + " cut");
                    if (request.Products == null)
                    {
                        failures++;
                        TestContext.Out.WriteLine("trial " + trial + " plane " + plane + ": " + request.Outcome + " kernel " + request.KernelStatus + " clip " + (CutStatus)request.cut.kernel.cutStatus + " (hull " + brep.VertexCount + " v " + brep.FaceCount + " f)");
                    }
                    else
                    {
                        request.Products.Dispose();
                    }

                    shape.ReleaseFromWork();
                    cls.Dispose();
                }

                shape.Dispose();
                brep.Dispose();
                PhysicsCutCook.DestroyMesh(mesh);
            }

            TestContext.Out.WriteLine("cuts " + cuts + ", kernel failures " + failures);
            Assert.That(failures, Is.Zero, "every fused hull is cut by the kernel");
            yield return EndWorld(root);
        }
    }
}
