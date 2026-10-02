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
    /// The records the coexistence check reads (TL, 2026-09-30). The display's collection counts are stamped with the
    /// frame that ran them: a frame whose stamped counts have a structure build is exactly a frame whose own
    /// "Zantetsu.Snapshot.Collect" marker ran (that stage runs only after a build), read the way the check reads them --
    /// the marker's last value and the counts of the frame before, in the next frame. A hit's display children are told
    /// apart by what became of them: with a shape, cut again since, empty, not done yet, and a Completed operation's child
    /// without its geometry (a mismatch).
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator HullKinematic_TheDisplaysCountsFallOnTheFrameThatRanThem_AndAHitsChildrenAreToldApart()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            using var collect = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Snapshot.Collect");
            yield return null;
            yield return null;
            Assert.That(collect.Valid, Is.True, "the Collect stage's marker is recorded");

            int framesRead = 0, framesBuilt = 0, framesCollected = 0, both = 0, unknown = 0, growthFrames = 0;
            long validations = 0, placementOnly = 0;
            var mismatched = new List<string>();
            float[] cuts = { 0f, -0.5f, 0.5f };
            for (int i = 0; i < cuts.Length; i++)
            {
                Evaluate(detector, Upright(i + 1, cuts[i]), i + 1);
                float until = Time.realtimeSinceStartup + 30f;
                int settledFrames = 0;
                while (Time.realtimeSinceStartup < until && settledFrames < 5)
                {
                    yield return null;
                    int frame = Time.frameCount - 1;   // the frame just ended: its marker's last value and its stamped counts
                    long marker = collect.LastValue;
                    if (!root.Display.TryGetFrameCounts(frame, out VpLogicalCutDisplay.FrameCounts counts)) { unknown++; continue; }
                    framesRead++;
                    if (counts.structureBuilds > 0) framesBuilt++;
                    if (marker > 0) framesCollected++;
                    if (counts.structureBuilds > 0 && marker > 0) both++;
                    if (counts.roomGrowths > 0 || counts.snapshotRegrowths > 0) growthFrames++;
                    if ((counts.structureBuilds > 0) != (marker > 0)) mismatched.Add("frame " + frame + ": builds " + counts.structureBuilds + ", Collect " + marker + " ns");
                    // The validations' parts of the same frame: as many structural validations as the frame's counter says.
                    long partsStructural = counts.validate != null ? counts.validate.structural : 0;
                    if (partsStructural != counts.structureValidations) mismatched.Add("frame " + frame + ": structural validations " + counts.structureValidations + ", their parts " + partsStructural);
                    if (counts.validate != null) { validations += counts.validate.structural; placementOnly += counts.validate.placementOnly; }
                    settledFrames = h.GroupCuts == i + 1 && h.IsSettled ? settledFrames + 1 : 0;
                }

                Assert.That(h.GroupCuts == i + 1 && h.IsSettled, Is.True, "cut " + (i + 1) + " settled");
            }

            TestContext.Out.WriteLine("frames read " + framesRead + " (unknown " + unknown + "): with a structure build " + framesBuilt + ", with the Collect marker " + framesCollected + ", both " + both + "; frames with a room growth " + growthFrames
                + "; validations stamped: structural " + validations + ", placement-only " + placementOnly + (mismatched.Count > 0 ? "; not matching: " + string.Join("; ", mismatched) : ""));
            Assert.That(unknown, Is.EqualTo(0), "every frame's counts known, read in the next frame");
            Assert.That(framesBuilt, Is.GreaterThan(0), "the cuts made structure builds");
            Assert.That(mismatched, Is.Empty, "a frame's stamped builds and its own Collect marker agree");

            // The first hit's children were both cut again (x = -0.5 on its negative child, x = 0.5 on its positive one);
            // the later hits' children stand with their shapes; nothing is pending, mismatched or missing.
            var all = new BuildingHullFusion.ChildCounts();
            for (int i = 0; i < h.Hits.Count; i++)
            {
                BuildingHullFusion.ChildCounts c = h.CountChildren(h.Hits[i]);
                TestContext.Out.WriteLine("hit " + i + " (" + h.Hits[i].displayOperations.Count + " operations): empty " + c.empty + ", with a shape " + c.withShape + ", cut again " + c.cutAgain + ", retired " + c.retired + ", not done " + c.pending + ", mismatch " + c.mismatch + ", unknown " + c.unknown);
                Assert.That(c.empty + c.withShape + c.cutAgain + c.retired + c.pending + c.mismatch + c.unknown, Is.EqualTo(2 * h.Hits[i].displayOperations.Count), "hit " + i + ": every child counted once");
                all.cutAgain += c.cutAgain; all.pending += c.pending; all.mismatch += c.mismatch; all.unknown += c.unknown;
            }

            Assert.That(h.CountChildren(h.Hits[0]).cutAgain, Is.EqualTo(2), "the first hit's two children were cut again");
            Assert.That(h.CountChildren(h.Hits[h.Hits.Count - 1]).cutAgain, Is.EqualTo(0), "the last hit's children stand");
            Assert.That(all.pending + all.mismatch + all.unknown, Is.EqualTo(0), "nothing pending, mismatched or missing once settled");
            AssertOneEach(root, 1, "at the end");
            yield return EndWorld(root);
        }
        /// <summary>
        /// **The GPU copy starts at 256 MiB each and a cut does not grow it** (TL, 2026-10-01): a world made with the
        /// profile's defaults reads back 16,777,216 vertices of 16 bytes and 67,108,864 indices of 4 bytes (512 MiB), and
        /// after a cut and its commits neither buffer has grown and nothing awaits release.
        /// </summary>
        [UnityTest]
        public IEnumerator HullKinematic_TheGpuCopy_Is256MiBEach_AndACutDoesNotGrowIt()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;
            VpLogicalCutDisplay d = root.Display;
            TestContext.Out.WriteLine("profile " + root.Profile.GpuVertexInitialCapacity + " / " + root.Profile.GpuIndexInitialCapacity + "; display: vertices " + d.GpuVertexCapacity + " x " + d.GpuVertexStride + " B, indices " + d.GpuIndexCapacity + " x " + d.GpuIndexStride
                + " B, " + (d.GpuVertexBytes + d.GpuIndexBytes) + " B, made in " + (d.GpuCreationSeconds * 1000).ToString("F3") + " ms");
            Assert.That(d.GpuVertexCapacity, Is.EqualTo(16777216));
            Assert.That(d.GpuIndexCapacity, Is.EqualTo(67108864));
            Assert.That(d.GpuVertexBytes + d.GpuIndexBytes, Is.EqualTo(512L << 20), "512 MiB in all");
            Evaluate(detector, Upright(1, 0f), 1);
            yield return UntilKinematic(root, 1, () => h.GroupCuts == 1 && h.IsSettled, 30f, "the cut settled", snapshots, snapshot);
            TestContext.Out.WriteLine("after the cut: grown " + d.GpuGrowthCount + " (vertex " + d.GpuVertexGrowthCount + ", index " + d.GpuIndexGrowthCount + "), awaiting release " + d.GpuRetiredNow + ", the range used at most " + d.GpuVertexHighWater + " vertices, " + d.GpuIndexHighWater + " indices; commits " + d.CommitCalls);
            Assert.That(d.CommitCalls, Is.GreaterThan(0), "the cut committed");
            Assert.That(d.GpuGrowthCount + d.GpuVertexGrowthCount + d.GpuIndexGrowthCount + d.GpuMaxRetired, Is.EqualTo(0), "nothing grown, nothing to release");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A commit makes each side a new reflected set; the body's is not changed; the snapshot takes the sets by their
        /// own lookups.** A building is cut, and one of its children cut again: the root's set (nothing reflected) stays as
        /// it was, each child's holds its parent's boundaries and its own cut on its side, the grandchildren one more; the
        /// structure builds index no set again and take every one by its lookup, and hold none past a build.
        /// </summary>
        [UnityTest]
        public IEnumerator HullKinematic_ACommitMakesNewReflectedSets_AndTheSnapshotTakesThemByTheirLookups()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out LogicalFragmentId fragment);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;
            Assert.That(root.Display.TryGetReflectedForTest(fragment, out VpReflectedSet rootSet), Is.True, "the root is shown");
            Assert.That(rootSet.Count, Is.EqualTo(0));
            long builtBefore = root.Display.AdoptedSnapshot.ReflectedIndexesBuilt;
            Evaluate(detector, Upright(1, 0f), 1);
            yield return UntilKinematic(root, 1, () => h.GroupCuts == 1 && h.IsSettled, 30f, "the first cut settled", snapshots, snapshot);
            Assert.That(root.Ledger.TryGetOperation(h.Hits[0].displayOperations[0], out LogicalCutOperation first), Is.True);
            Assert.That(root.Display.TryGetReflectedForTest(first.positive, out VpReflectedSet plus), Is.True, "the positive child shown");
            Assert.That(root.Display.TryGetReflectedForTest(first.negative, out VpReflectedSet minus), Is.True, "the negative child shown");
            var facePlus = new VpClipBoundary(new VpCapFace(root.Ledger, first.id), 1f);
            var faceMinus = new VpClipBoundary(new VpCapFace(root.Ledger, first.id), -1f);
            Assert.That(rootSet.Count, Is.EqualTo(0), "the root's set was not changed");
            Assert.That(plus.Count == 1 && plus.Contains(facePlus) && !plus.Contains(faceMinus), Is.True, "the positive child reflects its own side of the cut");
            Assert.That(minus.Count == 1 && minus.Contains(faceMinus) && !minus.Contains(facePlus), Is.True, "the negative child its own");
            Evaluate(detector, Upright(2, 0.5f), 2);
            yield return UntilKinematic(root, 1, () => h.GroupCuts == 2 && h.IsSettled, 30f, "the second cut settled", snapshots, snapshot);
            Assert.That(root.Ledger.TryGetOperation(h.Hits[1].displayOperations[0], out LogicalCutOperation second), Is.True);
            Assert.That(second.source, Is.EqualTo(first.positive), "the positive child cut again");
            Assert.That(root.Display.TryGetReflectedForTest(second.positive, out VpReflectedSet grand), Is.True);
            Assert.That(grand.Count == 2 && grand.Contains(facePlus), Is.True, "a grandchild reflects its parent's boundary and its own");
            Assert.That(plus.Count, Is.EqualTo(1), "its parent's set was not changed");
            yield return Steps(3);
            VpMultiCutSnapshot adopted = root.Display.AdoptedSnapshot;
            TestContext.Out.WriteLine("display: reflected sets made " + root.Display.ReflectedSetsMade + " (" + root.Display.ReflectedSetEntries + " boundaries, " + (root.Display.ReflectedSetSeconds * 1000).ToString("F3") + " ms), held " + root.Display.ReflectedEntriesHeld
                + "; commits " + root.Display.CommitCalls + " (longest " + (root.Display.MaxCommitSeconds * 1000).ToString("F3") + " ms); the adopted snapshot: indexes built " + adopted.ReflectedIndexesBuilt + ", reused " + adopted.ReflectedIndexesReused);
            Assert.That(adopted.ReflectedIndexesBuilt, Is.EqualTo(0), "no display set indexed again by a structure build (" + builtBefore + " on the snapshot adopted at the start)");
            Assert.That(adopted.ReflectedIndexesReused, Is.GreaterThan(0), "the sets taken by their lookups");
            Assert.That(adopted.HoldsReflectedForTest, Is.False, "none held past a build");
            AssertOneEach(root, 1, "at the end");
            yield return EndWorld(root);
        }
    }
}
