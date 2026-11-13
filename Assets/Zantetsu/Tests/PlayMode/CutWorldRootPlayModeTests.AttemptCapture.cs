using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// NPC-ATTEMPT-CAPTURE-1: the capture of an NPC's additional-split attempt as the play issues it (the split capture with the bones and
    /// the NPC-only filter): its sample, its bones' sidecar, its geometry's own topology table and its live result line, all named by the
    /// attempt -- read back, of one attempt, fragment and epoch, the table what the storage holds, and the decision over what was saved
    /// the live one. A table that could not be copied is a failure counted and told with its reason, and the attempt goes on.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static List<SpatialSplitBoneResultLine> LiveLines(string folder)
        {
            var lines = new List<SpatialSplitBoneResultLine>();
            foreach (string raw in File.ReadAllLines(Path.Combine(folder, "bone-results.jsonl")))
            {
                if (raw.Trim().Length > 0) lines.Add(JsonUtility.FromJson<SpatialSplitBoneResultLine>(raw));
            }

            return lines;
        }

        [UnityTest]
        public IEnumerator SplitBone_Capture_AnNpcsAttemptIsSavedWithItsTopology_ReadBackItDecidesAsLive_AndATableNotCopiedIsTold()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, handLowX: 0.8f, trunkBottomBone: 1);
            LogicalFragmentId second = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 12f), true, handLowX: 0.8f, trunkBottomBone: 1);
            yield return null;
            string folder = Path.Combine(Path.GetTempPath(), "zantetsu-attempt-capture-playmode-" + Guid.NewGuid().ToString("N"));
            var sink = new SpatialSplitCaptureSink(folder, SpatialSplitCaptureLimits.Default, "playmode test", new[] { "-zantetsuSplitCapture <directory> -zantetsuSplitCaptureNpcOnly" });
            // (the bones' capture without the NPC-only filter: this world's bodies carry the bones' records, not the piece lifetime's lineage mark the filter reads)
            SpatialSplitCaptureSession.SetBonesForTests(true, false);
            split.Capture = sink;
            try
            {
                // 1. The first body cut at the waist: its lower side is attempted, placed by its bones, and captured whole.
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides);
                LogicalFragmentId lower = sides[1];
                Assert.That(root.Geometry.TryGetGeometry(lower, out VpStoredGeometry lowerGeometry), Is.True);
                List<(uint, uint, uint)> lowerTriangles = TrianglesOf(root, lowerGeometry);
                var expected = new SortedDictionary<uint, int>();
                NativeArray<int>.ReadOnly storageTopology = root.Storage.TopologyOfVertex;
                foreach ((uint a, uint b, uint c) triangle in lowerTriangles)
                {
                    expected[triangle.a] = storageTopology[(int)triangle.a];
                    expected[triangle.b] = storageTopology[(int)triangle.b];
                    expected[triangle.c] = storageTopology[(int)triangle.c];
                }

                Assert.That(root.Storage.TryGetVertexBlocks(lowerGeometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out int topologyCount), Is.True);
                int blockCount = blocks.Length;
                Assert.That(blockCount, Is.GreaterThanOrEqualTo(2), "the layout: the base's vertex block and the cut's");
                remaining[0] = 1.0;
                yield return Until(() => !split.HasAttempt && split.BoneSearches >= 1, "the lower side was decided");
                remaining[0] = 0.0;
                NpcBoneSplitRecord live = split.LastBone;
                Assert.That(live.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable), DescribeBoneRecord(live));
                Assert.That(split.CaptureTopologies, Is.GreaterThanOrEqualTo(1), "its topology table was handed to the writer");
                Assert.That(split.CaptureTopologyFailures + split.CaptureBoneFailures, Is.EqualTo(0));
                long searchesBefore = split.BoneSearches, tablesBefore = split.CaptureTopologies;

                // 2. The second body's lower side with the table's copy refused: counted, told, and the attempt decided all the same.
                CutSpatialSplit.TestFailCaptureTopology = true;
                LogAssert.ignoreFailingMessages = true;   // the failure is logged as an error, once an attempt (a child may be attempted too)
                var secondSides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, second, secondSides);
                remaining[0] = 1.0;
                yield return Until(() => !split.HasAttempt && split.BoneSearches >= searchesBefore + 1, "the second lower side was decided");
                remaining[0] = 0.0;
                CutSpatialSplit.TestFailCaptureTopology = false;
                LogAssert.ignoreFailingMessages = false;
                long refusals = split.CaptureTopologyFailures;
                Assert.That(refusals, Is.GreaterThanOrEqualTo(1), "a table not copied is counted");
                Assert.That(split.CaptureTopologies, Is.EqualTo(tablesBefore));
                StringAssert.Contains("refused for a test", split.LastCaptureFailure);
                StringAssert.Contains("not handed " + refusals, split.DescribeBone());

                split.Capture = null;
                Assert.That(sink.Close(10.0), Is.True, "the writer ended");

                // 3. Read back.
                List<SpatialSplitBoneResultLine> lines = LiveLines(folder);
                SpatialSplitBoneResultLine first = lines.First(l => l.fragment == lower.value);
                SpatialSplitBoneResultLine refused = lines.First(l => l.fragment == secondSides[1].value);
                Assert.That(first.end == "Published" && first.path == "bone" && !first.fellBack && first.topologyCaptured, Is.True, "the live line: " + first.end + ", " + first.path);
                Assert.That(refused.topologyCaptured, Is.False, "the refused one's line says its table is missing");
                long world = split.CaptureWorld;
                SpatialSplitSample sample = SpatialSplitSample.Read(Path.Combine(folder, SpatialSplitCaptureSink.SampleFileName(world, first.attempt)));
                SpatialSplitBoneSample bones = SpatialSplitBoneSample.Read(Path.Combine(folder, SpatialSplitBoneSample.FileName(world, first.attempt)));
                NpcPieceTopologySample table = NpcPieceTopologySample.Read(Path.Combine(folder, NpcPieceTopologySample.FileName(world, first.attempt)));
                Assert.That(table.Check(), Is.Null);
                Assert.That(sample.world == world && bones.world == world && table.world == world && sample.attempt == first.attempt && bones.attempt == first.attempt && table.pick == first.attempt, Is.True, "one attempt");
                Assert.That(sample.fragment == lower.value && bones.fragment == lower.value && table.fragment == lower.value && first.fragment == lower.value, Is.True, "one fragment");
                Assert.That(sample.epoch == bones.epoch && sample.epoch == table.epoch && sample.epoch == first.epoch, Is.True, "one epoch");
                Assert.That(sample.sessionId == sink.sessionId && bones.sessionId == sink.sessionId && table.sessionId == sink.sessionId, Is.True, "one session");
                Assert.That(sample.generation == 1 && sample.rootFragment == body.value && sample.splitSource == 0, Is.True, "after the first cut, not made by a split");
                Assert.That(table.geometry.vertexStart == lowerGeometry.vertexStart && table.geometry.vertexCount == lowerGeometry.vertexCount, Is.True, "its geometry's identity");
                Assert.That(table.topologyVertexCount == topologyCount && table.blockStarts.Length == blockCount, Is.True, "the geometry's blocks and topology vertex count");
                Assert.That(table.vertexIds, Is.EqualTo(expected.Keys.ToArray()), "every vertex the triangles name, once, ascending");
                Assert.That(table.topologyIds, Is.EqualTo(expected.Values.ToArray()), "each with the topology vertex the storage holds");
                Assert.That(sample.vertexIds.Length, Is.EqualTo(table.vertexIds.Length), "the sample names the same vertices");

                // the index lines: each file listed once with its hash
                foreach ((string index, string file, string hash) in new[]
                {
                    ("samples.jsonl", SpatialSplitCaptureSink.SampleFileName(world, first.attempt), sample.sha256), ("bones.jsonl", SpatialSplitBoneSample.FileName(world, first.attempt), bones.sha256),
                    (NpcPieceTopologySample.Index, NpcPieceTopologySample.FileName(world, first.attempt), table.sha256),
                })
                {
                    string[] listed = File.ReadAllLines(Path.Combine(folder, index)).Where(l => l.Contains("\"" + file + "\"")).ToArray();
                    Assert.That(listed.Length == 1 && listed[0].Contains(hash), Is.True, index + " lists " + file + " once with its hash");
                }

                // the decision over what was saved is the live one
                var pick = new NpcPiecePickSet.Pick { number = (int)first.attempt, sample = sample, bones = bones, topology = table };
                NpcBoneSplitRecord saved = NpcPiecePickSet.Decide(pick, out byte[] savedSides, out byte[] _);
                string savedHash = NpcPiecePickSet.SidesHash(savedSides, bones.convexBones.Length, sample.indices.Length / 3);
                Assert.That(saved.verdict.ToString(), Is.EqualTo(first.verdict));
                Assert.That(savedHash, Is.EqualTo(first.sidesHash), "the same triangles on the same sides");
                Assert.That(saved.clusters == first.clusters && saved.groups == first.groups && saved.fullGroups == first.fullGroups && saved.meshComponents == first.meshComponents
                    && saved.positiveTriangles == first.positiveTriangles && saved.negativeTriangles == first.negativeTriangles && saved.positiveConvexes == first.positiveConvexes
                    && saved.connectionsKept == first.connectionsKept && saved.connectionsDropped == first.connectionsDropped && saved.connectionsProtected == first.connectionsProtected
                    && saved.topologyVertices == first.topologyVertices && saved.seamVertices == first.seamVertices, Is.True, "the same counts");
                Assert.That(saved.seamVertices, Is.GreaterThan(0), "the layout: attribute seams, joined by the saved table");

                // the refused one: no table, no index line of it, and its failure told
                Assert.That(File.Exists(Path.Combine(folder, NpcPieceTopologySample.FileName(world, refused.attempt))), Is.False);
                string[] failures = File.ReadAllLines(Path.Combine(folder, "capture-failures.jsonl")).Where(l => l.Trim().Length > 0).ToArray();
                Assert.That(failures.Length, Is.EqualTo((int)refusals), "one line a failure");
                string told = failures.First(l => l.Contains("\"attempt\":" + refused.attempt + ","));
                StringAssert.Contains("\"what\":\"topology\"", told);
                StringAssert.Contains("refused for a test", told);
                Assert.That(File.Exists(Path.Combine(folder, SpatialSplitCaptureSink.SampleFileName(world, refused.attempt))), Is.True, "its sample and its bones are there all the same");
                Assert.That(File.Exists(Path.Combine(folder, SpatialSplitBoneSample.FileName(world, refused.attempt))), Is.True);
            }
            finally
            {
                CutSpatialSplit.TestFailCaptureTopology = false;
                LogAssert.ignoreFailingMessages = false;
                SpatialSplitCaptureSession.SetBonesForTests(false, false);
                if (split.Capture != null) split.Capture = null;
                sink.Close(1.0);
                try { Directory.Delete(folder, true); } catch (Exception) { }
            }
        }
    }
}
