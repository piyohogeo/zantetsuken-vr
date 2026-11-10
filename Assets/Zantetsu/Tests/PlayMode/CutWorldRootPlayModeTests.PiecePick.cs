using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// NPC-PIECE-PICK-1: the collection's copy of a live fragment (the main thread's part, with a world): an NPC's body picked as it stands
    /// is read under an issue's conditions and written whole; the same fragment after its cut -- no longer a current target -- is a pick that
    /// fails and says so; its lower side picked is whole again, and the decision over what was saved of it is the one over the live parts.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static NpcBoneSplitRecord LiveDecide(NpcBoneLineage lineage, NpcBonePiece piece, PhysicsOwnerShape shape, List<(uint, uint, uint)> triangles, out byte[] sides)
        {
            var flat = new uint[triangles.Count * 3];
            for (int k = 0; k < triangles.Count; k++) { flat[3 * k] = triangles[k].Item1; flat[3 * k + 1] = triangles[k].Item2; flat[3 * k + 2] = triangles[k].Item3; }
            int c = shape.ConvexCount;
            var convexBones = new int[c];
            for (int i = 0; i < c; i++) convexBones[i] = shape.ConvexBone(i);
            var native = new NativeArray<uint>(flat, Allocator.Temp);
            var bytes = new NativeArray<byte>(c + triangles.Count, Allocator.Temp);
            try
            {
                var input = new NpcBoneSplitSearch.Input
                {
                    indices = (uint*)native.GetUnsafePtr(), indexCount = flat.Length, convexCount = c, convexBones = convexBones,
                    lineage = new NpcBoneSplitSearch.Lineage
                    {
                        nodeCount = lineage.NodeCount, parent = lineage.Parent, present = lineage.Present, baseBones = lineage.Vertices.Pointer,
                        baseStart = lineage.BaseStart, baseCount = lineage.BaseCount, chunks = lineage.Chunks, localCount = lineage.LocalCount,
                    },
                    jointKept = piece.JointKept, segmentLo = piece.Lo, segmentHi = piece.Hi, boneSides = new byte[2 * lineage.NodeCount],
                    insufficient = lineage.Insufficient, maxTriangles = 131072, maxOriginSteps = NpcBoneSplitSearch.DefaultMaxOriginSteps,
                };
                var record = default(NpcBoneSplitRecord);
                NpcBoneSplitSearch.Decide(in input, (byte*)bytes.GetUnsafePtr(), bytes.Length, ref record);
                sides = bytes.ToArray();
                return record;
            }
            finally
            {
                native.Dispose();
                bytes.Dispose();
            }
        }

        private static string DescribePick(in NpcBoneSplitRecord r) =>
            r.verdict + " (" + r.reason + "): bones held " + r.pieceBones + ", clusters " + r.clusters + ", components " + r.meshComponents + ", groups " + r.groups + " (" + r.fullGroups
            + " full, " + r.unboundGroups + " mesh alone), removed weights " + r.removedWeights + ", referenced " + r.referencedVertices + " of " + r.lineageVertices + ", steps " + r.originSteps
            + ", convexes +" + r.positiveConvexes + " -" + r.negativeConvexes + ", triangles +" + r.positiveTriangles + " -" + r.negativeTriangles;

        /// <summary>
        /// **A live NPC piece is picked whole; a fragment that is no longer current is a pick that fails; the lower side after the cut is
        /// picked whole, and its saved decision is the live one.** The NPC body with a hand of the bone split cases, picked as it stands
        /// (pick 1: the whole body, one lineage, no cut yet), then cut at the waist: the body's fragment is not a current target any more
        /// (pick 2 fails, said), its lower side is (pick 3). Read back, picks 1 and 3 are whole -- every file with its listed hash, the sample's
        /// vertices of the saved lineage -- and the decision over pick 3's saved parts is the decision over the live lower side, side by side.
        /// The line's hit point comes back in the owner's and the geometry's frames (the identity here: the same point).
        /// </summary>
        [UnityTest]
        public IEnumerator PiecePick_ALivePieceIsPickedWhole_AGoneOneFails_AndTheLowerSidesSavedDecisionIsTheLiveOne()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, handLowX: 0.8f);
            yield return null;
            Assert.That(root.NpcBones.Knows(body), Is.True, "the layout: an NPC's");

            string folder = Path.Combine(Path.GetTempPath(), "zantetsu-piece-pick-playmode-" + Guid.NewGuid().ToString("N"));
            var sink = new SpatialSplitCaptureSink(folder, SpatialSplitCaptureLimits.Default, "playmode test", new[] { "-zantetsuPieceCollect <directory>" });
            var models = new Dictionary<NpcVertexBones, (string file, string hash)>();
            var told = new List<(int pick, bool ok, string why)>();
            try
            {
                // Pick 1: the whole body as it stands.
                var aim = new NpcPiecePickCopy.Aim { origin = new float3(0f, 0.5f, -3f), direction = new float3(0f, 0f, 1f), hitWorld = new float3(0.1f, 0.2f, -0.5f), hitDistance = 2.5f, hitCollider = "test collider" };
                bool picked = NpcPiecePickCopy.TryPick(root, split, sink, 1, "playmode test", body, in aim, models, (ok, why) => { lock (told) told.Add((1, ok, why)); }, out NpcPiecePickSample first, out string failure);
                Assert.That(picked, Is.True, failure);
                Assert.That(first.fragment == body.value && first.convexCount == 2 && first.generation == 0 && first.weightsKept == false, Is.True, "who it is (a table of entries keeps no weights)");
                Assert.That(first.hitOwner.Equals(aim.hitWorld) && first.hitGeometry.Equals(aim.hitWorld), Is.True, "the frames are the identity here");
                Assert.That(first.hasNote, Is.False, "not attempted, not made by a split: no note");
                Assert.That(models.Count, Is.EqualTo(1), "the model's table written once");

                // The cut at the waist: the body's fragment is not a current target any more.
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides);
                LogicalFragmentId lower = sides[1];
                Assert.That(root.Ledger.IsCurrentTarget(body), Is.False, "the layout: the source is replaced by its sides");
                picked = NpcPiecePickCopy.TryPick(root, split, sink, 2, "playmode test", body, in aim, models, (ok, why) => { lock (told) told.Add((2, ok, why)); }, out NpcPiecePickSample _, out failure);
                Assert.That(picked, Is.False, "a fragment that went is no pick");
                StringAssert.Contains("not a current target", failure);
                NpcPiecePickCopy.HandFailure(sink, split.CaptureWorld, 2, failure, body.value, null);

                // Pick 3: the lower side (the NPC's: the trunk's lower part and the hand, two convexes).
                Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment lowerRecord) && root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner) && root.Geometry.TryGetGeometry(lower, out VpStoredGeometry lowerGeometry), Is.True);
                root.NpcBones.TryGet(lower, out lowerRecord);
                root.Owners.TryGet(lower, out lowerOwner);
                root.Geometry.TryGetGeometry(lower, out lowerGeometry);
                picked = NpcPiecePickCopy.TryPick(root, split, sink, 3, "playmode test", lower, in aim, models, (ok, why) => { lock (told) told.Add((3, ok, why)); }, out NpcPiecePickSample third, out failure);
                Assert.That(picked, Is.True, failure);
                Assert.That(third.fragment == lower.value && third.generation == 1 && third.rootFragment == body.value && third.convexCount == lowerOwner.Shape.ConvexCount, Is.True, "the lower side, a generation down");
                Assert.That(third.geometry.vertexStart == lowerGeometry.vertexStart && third.geometry.vertexCount == lowerGeometry.vertexCount, Is.True, "its geometry's identity");
                Assert.That(third.hasNote && !third.noteAttempted && third.noteMadeBy.Length == 0 || !third.hasNote, Is.True, "no attempt of the lower side yet (the split is closed)");
                Assert.That(models.Count, Is.EqualTo(1), "the same model's table");
                List<(uint, uint, uint)> lowerTriangles = TrianglesOf(root, lowerGeometry);
                NpcBoneSplitRecord live = LiveDecide(lowerRecord.Lineage, lowerRecord.Piece, lowerOwner.Shape, lowerTriangles, out byte[] liveSides);
                TestContext.Out.WriteLine("PIECE PICK live lower side: " + DescribePick(in live));

                Assert.That(sink.Close(10.0), Is.True, "the writer ended");
                lock (told)
                {
                    Assert.That(told.Count(t => t.pick == 1 && t.ok), Is.EqualTo(NpcPiecePickCopy.ItemsPerPick), "pick 1's three items written: " + string.Join("; ", told.Select(t => t.pick + ":" + t.ok + ":" + t.why)));
                    Assert.That(told.Count(t => t.pick == 3 && t.ok), Is.EqualTo(NpcPiecePickCopy.ItemsPerPick), "pick 3's three items written");
                    Assert.That(told.Any(t => !t.ok), Is.False);
                }

                Assert.That(sink.Saved, Is.EqualTo(2), "two samples");
                Assert.That(sink.Lost + sink.Returned + sink.NotCapturedInAll, Is.Zero);
                List<NpcPiecePickSet.Pick> picks = NpcPiecePickSet.Read(folder);
                string description = NpcPiecePickSet.Describe(picks);
                TestContext.Out.WriteLine("PIECE PICK read back: " + description);
                Assert.That(picks, Has.Count.EqualTo(3));
                Assert.That(picks[0].IsWhole, Is.True, string.Join("; ", picks[0].problems));
                Assert.That(picks[1].failure, Is.Not.Null.And.Contains("not a current target"));
                Assert.That(picks[2].IsWhole, Is.True, string.Join("; ", picks[2].problems));
                Assert.That(picks[2].bones.chunkStarts.Length, Is.EqualTo(1), "the waist cut's new vertices, with their origins");
                Assert.That(picks[2].sample.indices.Length, Is.EqualTo(lowerTriangles.Count * 3));
                Assert.That(picks[2].model.vertexCount, Is.EqualTo(lowerRecord.Lineage.BaseCount));
                NpcBoneSplitRecord saved = NpcPiecePickSet.Decide(picks[2], out byte[] savedSides, out byte[] _);
                TestContext.Out.WriteLine("PIECE PICK saved lower side: " + DescribePick(in saved));
                Assert.That(DescribePick(in saved), Is.EqualTo(DescribePick(in live)), "the decision over the saved parts is the live one");
                Assert.That(savedSides, Is.EqualTo(liveSides), "every convex and triangle on the same side");
                Assert.That(live.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable), "the layout: the hand apart from the trunk below the waist");
                Assert.That(File.Exists(Path.Combine(folder, "summary.json")) && File.Exists(Path.Combine(folder, "picks.jsonl")) && File.Exists(Path.Combine(folder, "models.jsonl")), Is.True);
                Assert.That(Directory.GetFiles(folder, "*.partial"), Is.Empty);
            }
            finally
            {
                sink.Close(0.0);
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
