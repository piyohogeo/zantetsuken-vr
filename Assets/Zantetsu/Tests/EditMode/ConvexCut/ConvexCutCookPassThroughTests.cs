using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>
    /// Output compatibility check (test-only thin cook connection, not the Phase 4 Mesh / Bake / Actor integration): the
    /// kernel's adopted B-reps are written as vertex-only meshes (probe M0), baked with Physics.BakeMesh and queried
    /// through a MeshCollider with the probe oracle (bounds, ray entry point, ClosestPoint) in the owner-local frame.
    /// </summary>
    public class ConvexCutCookPassThroughTests
    {
        const MeshColliderCookingOptions k_cooking = MeshColliderCookingOptions.CookForFasterSimulation | MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices | MeshColliderCookingOptions.UseFastMidphase;

        sealed class Oracle : IDisposable
        {
            readonly GameObject m_go; readonly MeshCollider m_collider; readonly Mesh m_mesh;
            public struct Result { public bool ok, hit; public double boundsErr, rayErr, closestErr; public double bakeMicroseconds; }

            public Oracle()
            {
                m_go = new GameObject("ConvexCutCookProbe") { hideFlags = HideFlags.HideAndDontSave };
                m_collider = m_go.AddComponent<MeshCollider>(); m_collider.convex = true; m_collider.cookingOptions = k_cooking;
                m_mesh = new Mesh { name = "ConvexCutCookProbeMesh", hideFlags = HideFlags.HideAndDontSave };
            }

            /// Vertex-only (M0) mesh through the writable MeshData path, bake, assign, query.
            public unsafe Result Run(ConvexPoly poly)
            {
                int n = poly.V.Length;
                var mda = Mesh.AllocateWritableMeshData(1);
                var md = mda[0];
                md.SetVertexBufferParams(n, new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3));
                md.SetIndexBufferParams(0, IndexFormat.UInt16);
                var dv = md.GetVertexData<float3>();
                float3 mn = new float3(float.MaxValue), mx = new float3(float.MinValue);
                for (int i = 0; i < n; i++) { float3 p = (float3)poly.V[i]; dv[i] = p; mn = math.min(mn, p); mx = math.max(mx, p); }
                md.subMeshCount = 1;
                md.SetSubMesh(0, new SubMeshDescriptor(0, 0, MeshTopology.Triangles) { bounds = new Bounds((mn + mx) * 0.5f, mx - mn), firstVertex = 0, vertexCount = n },
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
                Mesh.ApplyAndDisposeWritableMeshData(mda, m_mesh, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds);
                m_mesh.bounds = new Bounds((mn + mx) * 0.5f, mx - mn);
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Physics.BakeMesh(m_mesh.GetEntityId(), true, k_cooking);
                double us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
                m_collider.sharedMesh = m_mesh;
                Physics.SyncTransforms();
                poly.Bounds(out var pmn, out var pmx);
                double ext = math.cmax(pmx - pmn);
                double queryTol = math.max(2e-3 * ext, 1e-5);
                var b = m_collider.bounds;
                double boundsErr = math.max(math.cmax(math.abs((double3)(float3)b.min - pmn)), math.cmax(math.abs((double3)(float3)b.max - pmx))) / ext;
                double3 c = poly.VertexCentroid();
                double3 dir = math.normalize(new double3(-0.6, -0.5, -0.3));
                double3 origin = c - dir * (3 * ext);
                double tEnter = double.MinValue;
                double magnitude = ext;
                foreach (var v in poly.V) magnitude = math.max(magnitude, math.cmax(math.abs(v)));
                double resTol = BrepVerifier.FloatResolution * magnitude;
                var planeless = new bool[poly.F.Length];
                for (int f = 0; f < poly.F.Length; f++) planeless[f] = BrepVerifier.CollinearAtResolution(poly, f, resTol);
                for (int f = 0; f < poly.F.Length; f++)
                {
                    if (planeless[f]) continue;   // rounding residue: no plane, and never the entry face of a generic ray
                    poly.FacePlane(f, out var fn, out var d);
                    double denom = math.dot(fn, dir), num = d - math.dot(fn, origin);
                    if (math.abs(denom) < 1e-15) continue;
                    double t = num / denom;
                    if (denom < 0) tEnter = math.max(tEnter, t);
                }
                double3 expectedHit = origin + dir * tEnter;
                bool hit = m_collider.Raycast(new Ray((Vector3)(float3)origin, (Vector3)(float3)dir), out var h, (float)(10 * ext));
                double rayErr = hit ? math.distance((double3)(float3)h.point, expectedHit) / ext : double.PositiveInfinity;
                var cp = (double3)(float3)m_collider.ClosestPoint((Vector3)(float3)origin);
                double outside = 0;
                for (int f = 0; f < poly.F.Length; f++) { if (planeless[f]) continue; poly.FacePlane(f, out var fn, out var d); outside = math.max(outside, math.dot(fn, cp) - d); }
                double closestErr = math.max(outside, math.max(0, math.distance(cp, origin) - math.distance(expectedHit, origin))) / ext;
                m_collider.sharedMesh = null;
                return new Result { ok = boundsErr <= 2e-3 && hit && rayErr * ext <= queryTol && closestErr * ext <= queryTol, hit = hit, boundsErr = boundsErr, rayErr = rayErr, closestErr = closestErr, bakeMicroseconds = us };
            }

            public void Dispose() { UnityEngine.Object.DestroyImmediate(m_mesh); UnityEngine.Object.DestroyImmediate(m_go); }
        }

        [Test]
        public void AdoptedOutputs_CookAndAnswerQueriesLikeTheProbe()
        {
            var owners = new List<(string, OwnerCutHarness)>
            {
                ("single-v64", OwnerFixtures.Compound(1, 1, new[] { 64 }, 100, 1, 0)),
                ("single-v128", OwnerFixtures.Compound(1, 1, new[] { 128 }, 110, 1, 0)),
                ("c4-50pct", OwnerFixtures.Compound(4, 2, OwnerFixtures.Mixed, 130, 1, 0)),
                ("c16-100pct", OwnerFixtures.Compound(16, 16, OwnerFixtures.Mixed, 170, 1, 0)),
                ("reduce-light", OwnerFixtures.ReduceLight(0)),
                ("reduce-heavy", OwnerFixtures.ReduceHeavy(0)),
                ("box-throughEdge", new OwnerCutHarness().Add(CaseGenerator.Box()).Plane((float3)math.normalize(new double3(1, 0, 1)), 0, 1e-5f)),
                ("tetra", new OwnerCutHarness().Add(CaseGenerator.Tetra()).Plane(new float3(0, 1, 0), 0.1f, 1e-5f)),
            };
            var log = new StringBuilder("| owner | meshes | bounds err | ray err | closest err | bake p50 us | fails |\n|---|---|---|---|---|---|---|\n");
            int totalMeshes = 0, totalFails = 0, residueMeshes = 0; double worstBounds = 0, worstRay = 0, worstClosest = 0;
            using (var oracle = new Oracle())
            {
                // warm up the physics cooking path once (first bake in a session carries one-time costs)
                oracle.Run(CaseGenerator.Box());
                foreach (var (name, h) in owners)
                {
                    using (h)
                    {
                        h.Build();
                        var r = h.Execute();
                        Assert.That(r.status, Is.EqualTo(ConvexCutOwnerStatus.Ok), name);
                        int meshes = 0, fails = 0; double wb = 0, wr = 0, wc = 0; var bakes = new List<double>();
                        for (int c = 0; c < h.data.Count; c++)
                        {
                            var o = h.Outcome(c);
                            if (!o.IsSplit) continue;
                            foreach (var range in new[] { o.positive, o.negative })
                            {
                                var poly = h.ReadOutputPoly(in range);
                                var v = BrepVerifier.Verify(poly, new BrepVerifier.Options { relTol = 4e-6, maxVertices = 128, distinctRelTol = 0, resolutionRelTol = BrepVerifier.FloatResolution });
                                if (v.degenerateFaces + v.coincidentVertexPairs > 0) residueMeshes++;
                                var res = oracle.Run(poly);
                                meshes++; bakes.Add(res.bakeMicroseconds);
                                wb = math.max(wb, res.boundsErr); wr = math.max(wr, res.rayErr); wc = math.max(wc, res.closestErr);
                                if (!res.ok) { fails++; TestContext.Out.WriteLine(name + " convex " + c + " V=" + range.vertexCount + ": bounds=" + res.boundsErr.ToString("G3") + " hit=" + res.hit + " ray=" + res.rayErr.ToString("G3") + " closest=" + res.closestErr.ToString("G3")); }
                            }
                        }
                        bakes.Sort();
                        double p50 = bakes.Count > 0 ? bakes[bakes.Count / 2] : 0;
                        log.Append("| ").Append(name).Append(" | ").Append(meshes).Append(" | ").Append(wb.ToString("G2", TestCorpus.Ci)).Append(" | ").Append(wr.ToString("G2", TestCorpus.Ci)).Append(" | ").Append(wc.ToString("G2", TestCorpus.Ci)).Append(" | ").Append(p50.ToString("F0", TestCorpus.Ci)).Append(" | ").Append(fails).Append(" |\n");
                        totalMeshes += meshes; totalFails += fails; worstBounds = math.max(worstBounds, wb); worstRay = math.max(worstRay, wr); worstClosest = math.max(worstClosest, wc);
                    }
                }
            }
            log.Append("\nprobe editor owner-verify (Frame-Raw, same oracle): bounds 6.2e-4, ray 2.8e-4, closest 9.5e-7, oracle failures 0\n");
            log.Append("meshes with rounding-level residue (degenerate face / coincident pair) cooked: ").Append(residueMeshes).Append('\n');
            TestContext.Out.WriteLine(log.ToString());
            Assert.That(totalMeshes, Is.GreaterThanOrEqualTo(40));
            Assert.That(totalFails, Is.EqualTo(0), "cook oracle failures");
            Assert.That(worstBounds, Is.LessThanOrEqualTo(2e-3));
        }

        [Test]
        public void ResidueOutputs_FromExactClipThroughVertex_Cook()
        {
            // outputs with rounding-level residue (the local degeneracy accepted by DESIGN 7.2) must cook and answer queries like clean ones
            int cooked = 0, withResidue = 0, fails = 0, thinCooked = 0, thinDeviations = 0; double thinWorstBounds = 0;
            using (var oracle = new Oracle())
            using (var arena = new SentinelArena(8 << 20))
            {
                foreach (var c in TestCorpus.Corpus)
                {
                    if (c.planeClass != "throughVertex" && c.planeClass != "throughEdge" && c.planeClass != "exactZero") continue;
                    if (TestCorpus.IsDiagnosticFamily(c) || c.V < 16) continue;
                    bool thinPlate = c.family == "thin";   // 1e-3 thickness plates: PhysX cooking tolerances dominate; reported, not gated (the probe cooked no such plates)
                    var data = ConvexBrepData.FromPoly(c.poly);
                    var run = ClipHarness.RunClip(arena, data, (float3)c.n, (float)c.w, (float)c.eps);
                    if (run.status != (int)CutStatus.Ok) continue;
                    foreach (var side in new[] { run.pos, run.neg })
                    {
                        var poly = side.ToPoly();
                        var v = BrepVerifier.Verify(poly, new BrepVerifier.Options { relTol = 4e-6, maxVertices = 100000, distinctRelTol = 0, resolutionRelTol = BrepVerifier.FloatResolution });
                        bool residue = v.degenerateFaces + v.coincidentVertexPairs > 0;
                        if (!residue && withResidue >= cooked / 2) continue;   // keep the sample residue-heavy
                        var res = oracle.Run(poly);
                        if (thinPlate) { thinCooked++; thinWorstBounds = math.max(thinWorstBounds, res.boundsErr); if (!res.ok) thinDeviations++; continue; }
                        cooked++; if (residue) withResidue++;
                        if (!res.ok) { fails++; TestContext.Out.WriteLine(c.id + (residue ? " (residue)" : "") + ": bounds=" + res.boundsErr.ToString("G3") + " hit=" + res.hit + " ray=" + res.rayErr.ToString("G3") + " closest=" + res.closestErr.ToString("G3")); }
                    }
                    if (cooked >= 60) break;
                }
            }
            TestContext.Out.WriteLine("cooked=" + cooked + " withResidue=" + withResidue + " fails=" + fails + "; thin plates (diagnostic): cooked=" + thinCooked + " oracle deviations=" + thinDeviations + " worst bounds err=" + thinWorstBounds.ToString("G3", TestCorpus.Ci));
            Assert.That(withResidue, Is.GreaterThan(0), "no residue outputs found among the through-vertex / through-edge cases");
            Assert.That(fails, Is.EqualTo(0));
        }
    }
}
