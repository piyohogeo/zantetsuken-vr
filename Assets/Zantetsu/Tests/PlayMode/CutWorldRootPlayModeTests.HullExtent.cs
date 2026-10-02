using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The display classification decided by a committed geometry's recorded extent when the whole box lies certainly on
    /// one side (TL, 2026-10-01), against the reading of every member's indices (the reference): the same answer for every
    /// member of a building cut and re-cut (children with their caps), standing moved and turned, against planes far on
    /// either side, through the members, and at the boundary (the plane at a member's extreme vertex, offset by less and
    /// more than the tolerance); an uncommitted member and a refused lease wait either way. The cost of the two over the
    /// same members and planes -- mostly one-sided, mostly crossed -- is written out with the reads made, not judged.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private struct Classified
        {
            public bool known, positive, negative, byExtent;
            public override string ToString() => known ? (positive && negative ? "crossed" : positive ? "+" : negative ? "-" : "on no side") : "waits";
        }

        private static Classified ClassifyOnce(BuildingHullFusion h, HullGroup.DisplayMember m, float4 planeWorld, bool extentOff)
        {
            var c = new Classified();
            c.known = h.ClassifyForTest(m.fragment, PlaneInMember(m, planeWorld), false, extentOff, out c.positive, out c.negative, out c.byExtent);
            return c;
        }

        [UnityTest]
        public IEnumerator HullKinematic_TheExtentDecidesOnlyWhatTheIndicesWould_AndReadsLess()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            float3 anchor = new float3(-0.5f, -0.9f, 0f);
            HullGroup straight = AddHullBuilding(root, Vector3.zero, new[] { anchor }, 12.0, out _);
            HullGroup posed = AddHullBuildingPosed(root, new Vector3(30f, 0.5f, 5f), Quaternion.Euler(0f, 30f, 10f), new[] { anchor }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;

            // Cuts and re-cuts: upright, level and diagonal, each through both buildings' current members.
            Vector3 op = posed.Members[0].root.transform.position;
            var planes = new (Vector3 n, float at)[] { (Vector3.right, 0f), (Vector3.up, 0.3f), (new Vector3(1f, 1f, 0f).normalized, 0.1f), (Vector3.right, 0.45f), (Vector3.forward, -0.2f) };
            long slash = 1;
            foreach ((Vector3 n, float at) in planes)
            {
                foreach (Vector3 origin in new[] { Vector3.zero, op })
                {
                    Vector3 along = Vector3.Cross(n, Mathf.Abs(n.z) > 0.9f ? Vector3.up : Vector3.forward).normalized;
                    Vector3 span = Vector3.Cross(n, along).normalized;
                    Evaluate(detector, Wide(slash, n, origin + n * at, along, span), slash);
                    slash++;
                    int cuts = h.GroupCuts;
                    yield return UntilKinematic(root, 2, () => h.IsSettled, 30f, "cut " + (slash - 1) + " settled", snapshots, snapshot);
                }
            }

            var members = new List<HullGroup.DisplayMember>();
            foreach (HullGroup g in new[] { straight, posed }) foreach (HullGroup.DisplayMember m in g.Members) if (root.Geometry.TryGetGeometry(m.fragment, out _)) members.Add(m);
            TestContext.Out.WriteLine("members committed " + members.Count + " (cuts published " + h.GroupCuts + ", display cuts " + h.DisplayCuts + ")");
            Assert.That(members.Count, Is.GreaterThanOrEqualTo(12), "a building cut and re-cut into many members");

            // The planes: far on either side, through each member, and at each member's extreme vertex offset around the tolerance.
            float eps = root.Profile.SupportEpsilon;
            var normals = new[] { Vector3.right, Vector3.up, Vector3.forward, new Vector3(1f, 1f, 0f).normalized, new Vector3(-0.3f, 0.8f, 0.52f).normalized };
            var far = new List<(HullGroup.DisplayMember m, float4 p)>();
            var through = new List<(HullGroup.DisplayMember m, float4 p)>();
            var boundary = new List<(HullGroup.DisplayMember m, float4 p, float d)>();
            foreach (HullGroup.DisplayMember m in members)
            {
                foreach (Vector3 n in normals)
                {
                    IndependentSides(root, m.fragment, new float4(n, 0f), out float lo, out float hi, out _, out _);
                    far.Add((m, new float4(n, -(lo - 3f))));   // everything 3 m above
                    far.Add((m, new float4(n, -(hi + 3f))));   // everything 3 m below
                    through.Add((m, new float4(n, -0.5f * (lo + hi))));
                    foreach (float d in new[] { 2f * eps, 1.01f * eps, eps, 0.5f * eps, 0f, -0.5f * eps, -2f * eps, 1e-4f, 5e-4f, 2e-3f })
                    {
                        boundary.Add((m, new float4(n, -lo + d), d));   // the lowest vertex d above the plane
                        boundary.Add((m, new float4(n, -hi - d), d));   // the highest vertex d below it
                    }
                }
            }

            int compared = 0, byExtent = 0;
            var mismatches = new List<string>();
            void Compare(HullGroup.DisplayMember m, float4 p, string what)
            {
                Classified reference = ClassifyOnce(h, m, p, true), extent = ClassifyOnce(h, m, p, false);
                compared++;
                if (extent.byExtent) byExtent++;
                Assert.That(reference.byExtent, Is.False, "the reference reads the indices");
                if (reference.known != extent.known || reference.positive != extent.positive || reference.negative != extent.negative)
                    mismatches.Add(what + " member " + m.fragment.value + " plane " + p + ": indices " + reference + ", extent " + extent);
            }

            foreach ((HullGroup.DisplayMember m, float4 p) in far) Compare(m, p, "far");
            int farByExtent = byExtent;
            foreach ((HullGroup.DisplayMember m, float4 p) in through) Compare(m, p, "through");
            int throughByExtent = byExtent - farByExtent;
            foreach ((HullGroup.DisplayMember m, float4 p, float d) in boundary) Compare(m, p, "boundary " + d.ToString("E2"));
            int boundaryByExtent = byExtent - farByExtent - throughByExtent;
            TestContext.Out.WriteLine("compared " + compared + " (far " + far.Count + ", through " + through.Count + ", at the boundary " + boundary.Count + "), decided by the extent: far " + farByExtent + ", through " + throughByExtent + ", at the boundary " + boundaryByExtent + " (tolerance " + eps + " m)"
                + (mismatches.Count > 0 ? "; MISMATCHES: " + string.Join(" | ", mismatches.GetRange(0, Mathf.Min(10, mismatches.Count))) : ""));
            Assert.That(mismatches, Is.Empty, "the extent's answer is the indices' answer, member by member, plane by plane");
            Assert.That(farByExtent, Is.EqualTo(far.Count), "every far plane decided by the extent alone");
            Assert.That(throughByExtent, Is.EqualTo(0), "no plane through a member decided by its extent");

            // An uncommitted member (no geometry yet) and a refused lease wait either way.
            LogicalFragmentId bare = root.Ledger.AddFragment();
            foreach (bool off in new[] { true, false })
            {
                Assert.That(h.ClassifyForTest(bare, new float4(1f, 0f, 0f, 0f), false, off, out _, out _, out bool bx), Is.False, "no geometry committed: it waits (" + (off ? "indices" : "extent") + ")");
                Assert.That(bx, Is.False);
            }

            h.refuseIndexLeaseForTest = true;
            int refused = h.DisplayIndexLeasesRefused;
            foreach (bool off in new[] { true, false })
            {
                Assert.That(h.ClassifyForTest(members[0].fragment, PlaneInMember(members[0], far[0].p), false, off, out _, out _, out _), Is.False, "a refused lease: it waits (" + (off ? "indices" : "extent") + ")");
            }

            Assert.That(h.DisplayIndexLeasesRefused - refused, Is.EqualTo(2), "counted as refused either way");
            h.refuseIndexLeaseForTest = false;

            // The cost over the same members and planes, mostly one-sided and mostly crossed, and the reads each made.
            foreach ((string label, List<(HullGroup.DisplayMember m, float4 p)> set) in new[] { ("mostly one-sided (far)", far), ("mostly crossed (through)", through) })
            {
                foreach (bool off in new[] { true, false })
                {
                    long reads0 = h.DisplayIndicesRead, tests0 = h.DisplayVertexTests, lengths0 = h.DisplayIndexRangeLengths;
                    int extent0 = h.DisplayMembersByExtent, scanned0 = h.DisplayMembersReadByIndices;
                    double best = double.MaxValue;
                    for (int repeat = 0; repeat < 5; repeat++)
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        foreach ((HullGroup.DisplayMember m, float4 p) in set) h.ClassifyForTest(m.fragment, PlaneInMember(m, p), false, off, out _, out _, out _);
                        best = System.Math.Min(best, watch.Elapsed.TotalMilliseconds);
                    }

                    TestContext.Out.WriteLine(label + ", " + (off ? "indices (reference)" : "extent first") + ": " + set.Count + " classifications, best of 5 " + best.ToString("F3") + " ms; over the 5: by the extent " + (h.DisplayMembersByExtent - extent0)
                        + ", read by indices " + (h.DisplayMembersReadByIndices - scanned0) + ", indices read " + (h.DisplayIndicesRead - reads0) + ", vertices tested " + (h.DisplayVertexTests - tests0) + ", index range lengths " + (h.DisplayIndexRangeLengths - lengths0));
                }
            }

            AssertOneEach(root, 2, "at the end");
            yield return EndWorld(root);
        }
    }
}
