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
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The capture of the additional split's issued inputs and their offline replay (DESIGN 7.9.8), through the
    /// product's own world: every attempt issued -- a plane, no plane at all, a bent partition, a mesh-led plane that
    /// cuts a convex, and what the walk tries after them -- is saved, reads back as it was issued (the indices, the
    /// vertices they name, the convexes, bit for bit), and replayed by the attempt's own numbers comes to the same
    /// answer as it did live. An attempt still waiting, or still out, when the world ends is recorded as such, and its
    /// sample is never taken for a search that found nothing.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static string NewCaptureFolder() => Path.Combine(Path.GetTempPath(), "zantetsu-split-capture-" + Guid.NewGuid().ToString("N"));

        private sealed class IssuedInput
        {
            internal uint[] indices;
            internal Dictionary<uint, byte[]> vertices;
            internal List<(float3[] v, int[] fo, int[] fi, int[] fe, BrepEdge[] e)> convexes;
            internal float4x4 shapeToOwner;
        }

        // What a fragment's split would be issued with now: its indices, the bytes of every vertex they name, its convexes.
        private static IssuedInput InputOf(CutWorldRoot root, LogicalFragmentId fragment)
        {
            Assert.That(root.Geometry.TryGetGeometry(fragment, out VpStoredGeometry geometry), Is.True);
            Assert.That(root.Owners.TryGet(fragment, out PhysicsFragmentOwner owner), Is.True);
            Assert.That(root.Storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True);
            var input = new IssuedInput { indices = view.ToArray(), vertices = new Dictionary<uint, byte[]>(), convexes = new List<(float3[], int[], int[], int[], BrepEdge[])>() };
            Assert.That(root.Storage.TryReleaseIndexReadLease(lease), Is.True);
            NativeArray<VpRenderVertex>.ReadOnly all = root.Storage.Vertices;
            int size = UnsafeUtility.SizeOf<VpRenderVertex>();
            var pool = (byte*)all.GetUnsafeReadOnlyPtr();
            foreach (uint v in input.indices)
            {
                if (v >= all.Length || input.vertices.ContainsKey(v)) continue;
                var bytes = new byte[size];
                fixed (byte* to = bytes) UnsafeUtility.MemCpy(to, pool + ((long)v * size), size);
                input.vertices[v] = bytes;
            }

            PhysicsOwnerShape shape = owner.Shape;
            input.shapeToOwner = shape.LocalToOwner;
            for (int c = 0; c < shape.ConvexCount; c++)
            {
                ConvexBrepRange r = shape.Convex(c);
                ConvexBrepBank b = shape.BankOf(c);
                input.convexes.Add((
                    Copy(b.vertices + r.vertexBase, r.vertexCount), Copy(b.faceOffsets + r.faceBase, r.faceCount + 1),
                    Copy(b.faceIndices + r.faceIndexBase, r.faceIndexCount), Copy(b.faceEdges + r.faceIndexBase, r.faceIndexCount),
                    Copy(b.edges + r.edgeBase, r.edgeCount)));
            }

            return input;
        }

        private static T[] Copy<T>(T* from, int count) where T : unmanaged
        {
            var values = new T[count];
            for (int i = 0; i < count; i++) values[i] = from[i];
            return values;
        }

        private static void AssertReadsBackAsIssued(SpatialSplitSample sample, IssuedInput input, string what)
        {
            Assert.That(sample.indices, Is.EqualTo(input.indices), what + ": the indices as issued, in their order");
            Assert.That(sample.vertexIds, Is.EqualTo(input.vertices.Keys.OrderBy(v => v).ToArray()), what + ": the vertices they name, and only those");
            int size = sample.vertexStructBytes;
            for (int k = 0; k < sample.vertexIds.Length; k++)
            {
                Assert.That(sample.vertexBytes.Skip(k * size).Take(size).ToArray(), Is.EqualTo(input.vertices[sample.vertexIds[k]]), what + ": vertex " + sample.vertexIds[k] + " bit for bit");
            }

            Assert.That(sample.convexes.Length, Is.EqualTo(input.convexes.Count), what + ": the convexes");
            for (int c = 0; c < sample.convexes.Length; c++)
            {
                SpatialSplitSample.Convex x = sample.convexes[c];
                (float3[] v, int[] fo, int[] fi, int[] fe, BrepEdge[] e) = input.convexes[c];
                Assert.That(x.vertices, Is.EqualTo(v), what + ": convex " + c + "'s vertices, in order");
                Assert.That(x.faceOffsets, Is.EqualTo(fo));
                Assert.That(x.faceIndices, Is.EqualTo(fi));
                Assert.That(x.faceEdges, Is.EqualTo(fe));
                Assert.That(x.edges.Select(d => (d.v0, d.v1, d.f0, d.f1)), Is.EqualTo(e.Select(d => (d.v0, d.v1, d.f0, d.f1))), what + ": convex " + c + "'s edges");
            }

            Assert.That(sample.shapeToOwner.Equals(input.shapeToOwner), Is.True, what + ": the shape's frame");
        }

        private static List<T> Lines<T>(string path) => File.ReadAllLines(path).Where(l => l.Trim().Length > 0).Select(JsonUtility.FromJson<T>).ToList();

        /// <summary>
        /// **Every issued input is saved, reads back as it was issued, and replays to the same answer.** Four lower
        /// sides -- a body with a hand (a plane), the same with a mesh bridge (nothing), a part inside a U (a bent
        /// partition) and a bar across the gap (a mesh-led plane that cuts it) -- and whatever the walk tries after
        /// them, with the plane, the bent and the mesh-led searches on. Each attempt's sample has the indices, the
        /// vertices they name and the convexes as they were when it was issued; each ends with a result line; and the
        /// replay, which runs the attempt's own numbers on the sample, comes to the same search, plane, sharing out,
        /// anchors, sides and kernel, and passes its checks. A partial file beside them is not taken for a sample.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCapture_EveryIssuedInput_IsSaved_ReadsBackAsItWasIssued_AndReplaysToTheSameAnswer()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.Settings = SplitOnWithBent(cutConvexes: true, minCutSideTriangles: 12);
            string folder = NewCaptureFolder();
            string session = Path.Combine(folder, "session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "PlayMode case", null);
            split.Capture = sink;
            try
            {
                LogicalFragmentId withHand = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
                LogicalFragmentId bridged = AddBodyWithAHand(root, new Vector3(0f, 0f, 12f), meshBridge: true);
                LogicalFragmentId u = AddUWithAPartInside(root, new Vector3(0f, 0f, 24f));
                LogicalFragmentId across = AddBodyWithAHand(root, new Vector3(0f, 0f, 36f), convexAcross: true);
                yield return null;
                var sides = new LogicalFragmentId[4][];
                for (int i = 0; i < 4; i++) sides[i] = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, withHand, sides[0]);
                yield return CutAtTheWaist(root, bridged, sides[1]);
                yield return CutAtTheWaist(root, u, sides[2], lowerConvexes: 5);
                yield return CutAtTheWaist(root, across, sides[3], lowerConvexes: 3);
                Assert.That(split.Issued, Is.Zero, "the layout: nothing issued while the budget was closed");
                string[] names = { "the body with a hand", "the bridged body", "the U with a part", "the bar across" };
                var inputs = new IssuedInput[4];
                for (int i = 0; i < 4; i++) inputs[i] = InputOf(root, sides[i][1]);

                remaining[0] = 1.0;
                yield return Until(() => split.Published >= 3 && split.IsIdle && !split.HasAttempt, "the four were tried, three published, and the walk stands idle");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.Describe());
                Assert.That(split.PublishedBent, Is.EqualTo(1), "the U's part by a bent partition");
                Assert.That(split.PublishedWithCut, Is.EqualTo(1), "the bar by a mesh-led plane");
                Assert.That(split.EndedAs(SpatialSplitEnd.NotFound), Is.GreaterThanOrEqualTo(1), "the bridged body, at least");
                Assert.That(split.CaptureCopyBytes, Is.GreaterThan(0));
                Assert.That(split.MaxCaptureCopySeconds, Is.GreaterThan(0.0));
                TestContext.Out.WriteLine("capture: copies " + (split.CaptureCopySeconds * 1000.0).ToString("F3") + " ms in all (most " + (split.MaxCaptureCopySeconds * 1000.0).ToString("F3")
                    + " ms, " + split.MaxCaptureCopyBytes + " bytes), outputs " + (split.CaptureResultSeconds * 1000.0).ToString("F3") + " ms; issue " + (split.IssueSeconds * 1000.0).ToString("F3") + " ms without them");

                Assert.That(sink.Close(20.0), Is.True, "the writer wrote everything and ended");
                TestContext.Out.WriteLine(sink.Describe());
                Assert.That(sink.Issued, Is.EqualTo(split.Issued), "every attempt issued was seen");
                Assert.That(sink.Accepted, Is.EqualTo(split.Issued), "and copied");
                Assert.That(sink.Saved, Is.EqualTo(split.Issued), "and saved");
                Assert.That(sink.Lost + sink.NotCapturedInAll + sink.Returned + sink.ResultsLost, Is.Zero);
                Assert.That(sink.ResultsWritten, Is.EqualTo(split.Issued), "a result for each");
                Assert.That(sink.StopReason, Is.EqualTo(SpatialSplitCaptureRefusal.None));

                List<SpatialSplitSampleLine> samples = Lines<SpatialSplitSampleLine>(Path.Combine(session, "samples.jsonl"));
                List<SpatialSplitResultLine> results = Lines<SpatialSplitResultLine>(Path.Combine(session, "results.jsonl"));
                Assert.That(samples, Has.Count.EqualTo(split.Issued));
                Assert.That(results, Has.Count.EqualTo(split.Issued));
                string[] ends = { "Published", "NotFound", "Published", "Published" };
                for (int i = 0; i < 4; i++)
                {
                    SpatialSplitSampleLine line = samples.Single(l => l.fragment == sides[i][1].value);
                    SpatialSplitSample sample = SpatialSplitSample.Read(Path.Combine(session, line.file));
                    Assert.That(sample.sha256, Is.EqualTo(line.sha256), names[i] + ": the hash the index has");
                    AssertReadsBackAsIssued(sample, inputs[i], names[i]);
                    SpatialSplitResultLine result = results.Single(r => r.world == line.world && r.attempt == line.attempt);
                    TestContext.Out.WriteLine(names[i] + ": " + result.end + ", " + result.outcome + (result.bent ? " bent" : "") + (result.meshLed ? " mesh-led" : "") + (result.cut ? " cut" : "")
                        + ", worker " + result.workerMs.ToString("F3") + " ms, copy " + result.copyMs.ToString("F3") + " ms of " + result.copyBytes + " bytes");
                    Assert.That(result.end, Is.EqualTo(ends[i]), names[i] + ": how it ended");
                    Assert.That(result.searchObserved, Is.True, names[i] + ": its outputs were read");
                }

                SpatialSplitResultLine uResult = results.Single(r => r.attempt == samples.Single(l => l.fragment == sides[2][1].value).attempt);
                Assert.That(uResult.bent, Is.True);
                Assert.That(uResult.bentKnotCount, Is.GreaterThanOrEqualTo(2), "the bent surface adopted is kept, not only its hash");
                Assert.That(uResult.bentKnotU.Length == uResult.bentKnotCount && uResult.bentKnotH.Length == uResult.bentKnotCount, Is.True);
                Assert.That(uResult.bentHeight.Length == 3 && uResult.bentAlong.Length == 3, Is.True);
                Assert.That(uResult.bentKnotU.All(v => float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float _)), Is.True);
                SpatialSplitResultLine acrossResult = results.Single(r => r.attempt == samples.Single(l => l.fragment == sides[3][1].value).attempt);
                Assert.That(acrossResult.meshLed && acrossResult.cut, Is.True);
                Assert.That(acrossResult.applied, Is.True, "the stages after the numbers as they stood at its end: the meshes made");
                Assert.That(acrossResult.meshes, Is.EqualTo(2));
                Assert.That(acrossResult.hullsChecked, Is.EqualTo(2), "and both hulls read");
                Assert.That(acrossResult.bakeMs, Is.GreaterThan(0.0), "and the bake's time");
                Assert.That(acrossResult.planeGeometry.Length == 4 && acrossResult.planeShape.Length == 4, Is.True, "the plane adopted, in both frames");

                string summary = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "replay"), out bool allSame);
                TestContext.Out.WriteLine(summary);
                Assert.That(allSame, Is.True, "every sample replayed to the same answer and passed the checks:\n" + summary);
                StringAssert.Contains("same " + split.Issued + " (", summary);
                StringAssert.Contains("differs 0; not compared, 結果未記録 0", summary);
                StringAssert.Contains("compared with the live result " + split.Issued + ":", summary);
                StringAssert.Contains("(bent)", summary);
                StringAssert.Contains("(mesh-led)", summary);
                StringAssert.Contains("(plane)", summary);
                StringAssert.Contains("NotFound", summary);

                // A file a writer left half done beside them is not a sample.
                File.WriteAllText(Path.Combine(session, SpatialSplitCaptureSink.SampleFileName(9, 9) + ".0123.partial"), "half a file");
                string again = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "replay-2"), out bool againSame);
                StringAssert.Contains("partial files (not samples) 1", again);
                Assert.That(againSame, Is.True, "and changes nothing of the replay");
                Assert.That(root.GeometryFaults, Is.Zero);
                Assert.That(root.TerminationRequested, Is.False);
                split.Settings = default;
                yield return EndWorld(root);
            }
            finally
            {
                sink.Close(5.0);
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// **An attempt still out when the world ends is recorded as such.** Its work ran, and its collection is held
        /// back by the destination until the world ends: the sample is saved and the result says how it ended -- an
        /// end of the world's, never NotFound. Where the numbers ran and were read the replay comes to the same; where
        /// they were never read it is 結果未記録.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCapture_AnAttemptStillOutAtTheWorldsEnd_IsRecordedAsSuch()
        {
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(2))) : null,
                null);
            yield return WorldEndCase(root, () => background.HoldEverything = true, () => background.HoldingCount > 0, "its work finished and is held back from the collection");
        }

        /// <summary>
        /// **An attempt still waiting for its destination when the world ends is recorded as such.** Issued and
        /// queued, never begun: the sample is saved, the result says how it ended and that nothing ran.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCapture_AnAttemptNeverBegunAtTheWorldsEnd_IsRecordedAsSuch()
        {
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(2))) : null,
                null);
            yield return WorldEndCase(root, () => background.RefuseNew = true, () => true, "the destination takes nothing");
        }

        /// <summary>
        /// **The application quitting with an attempt still out hands its result before the capture closes.** In a Player
        /// the session's quitting comes before the worlds' destruction, and a world with work out is not released at all:
        /// the result of the attempt still on its worker is handed at the quitting -- an end of AppQuit, its outputs never
        /// read -- the writer writes it with the summary and ends, and the world ending after it hands nothing more.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCapture_TheApplicationQuitting_WithAnAttemptOut_HandsItsResultBeforeTheWriterCloses()
        {
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(2))) : null,
                null);
            yield return QuitCase(root, () => background.HoldEverything = true, split => background.HoldingCount > 0, false);
        }

        /// <summary>
        /// **The application quitting with an attempt collected and not yet published hands its result with its outputs.**
        /// The numbers came back and were read; the publication waits for the budget. At the quitting its result is handed
        /// as it stands, and the replay comes to the same answer.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCapture_TheApplicationQuitting_WithAnAttemptCollected_HandsItsResultWithItsOutputs()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return QuitCase(root, () => { }, split => split.AttemptIsCollected, true);
        }

        private IEnumerator QuitCase(CutWorldRoot root, Action hold, Func<CutSpatialSplit, bool> held, bool collected)
        {
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            string folder = NewCaptureFolder();
            string session = Path.Combine(folder, "session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "PlayMode case", null);
            split.Capture = sink;
            try
            {
                LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides);
                hold();
                remaining[0] = 1.0;
                yield return Until(() => split.HasAttempt, "an attempt was issued");
                remaining[0] = 0.0;
                yield return Until(() => held(split), collected ? "its work came back and waits for the budget" : "its work is held back from the collection");
                Assert.That(split.HasAttempt && split.AttemptIsCollected == collected, Is.True, "the layout");

                // As the session does at Application.quitting, before any world is destroyed.
                Assert.That(sink.Quit(20.0), Is.True, "the writer wrote what it was handed, then the summary, and ended");
                TestContext.Out.WriteLine(sink.Describe());
                Assert.That(sink.IsCapturing, Is.False);
                Assert.That(sink.Saved, Is.EqualTo(1));
                Assert.That(sink.ResultsWritten, Is.EqualTo(1), "the result was handed before the writer closed");
                Assert.That(sink.ResultsLost, Is.Zero);
                Assert.That(sink.QueuedItems + sink.QueuedBytes, Is.Zero, "nothing held");
                Assert.That(File.Exists(Path.Combine(session, "summary.json")), Is.True);
                List<SpatialSplitResultLine> results = Lines<SpatialSplitResultLine>(Path.Combine(session, "results.jsonl"));
                Assert.That(results, Has.Count.EqualTo(1));
                SpatialSplitResultLine result = results[0];
                TestContext.Out.WriteLine("ended " + result.end + " (" + result.detail + "), outputs read " + result.searchObserved);
                Assert.That(result.end, Is.EqualTo("AppQuit"));
                Assert.That(result.searchObserved, Is.EqualTo(collected), collected ? "its outputs were read and kept" : "its outputs were never read");

                string summary = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "replay"), out bool allSame);
                TestContext.Out.WriteLine(summary);
                Assert.That(allSame, Is.EqualTo(collected), summary);
                if (!collected)
                {
                    StringAssert.Contains("結果未記録 1", summary);
                    StringAssert.Contains("結果未記録 by reason: AppQuit before the numbers were read 1", summary, "kept apart from a copy's failure and from a difference");
                    StringAssert.Contains("saving's failures: unreadable 0, hash differs 0, committed without an index line 0, lines not read 0", summary);
                }

                // The world goes after it, as the objects are destroyed: the attempt handed its result, and hands nothing more.
                yield return EndWorld(root);
                Assert.That(sink.ResultsLost, Is.Zero, "no late result refused by the closed writer");
                Assert.That(Lines<SpatialSplitResultLine>(Path.Combine(session, "results.jsonl")), Has.Count.EqualTo(1));
            }
            finally
            {
                sink.Close(5.0);
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private IEnumerator WorldEndCase(CutWorldRoot root, Action hold, Func<bool> held, string heldWhat)
        {
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            string folder = NewCaptureFolder();
            string session = Path.Combine(folder, "session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "PlayMode case", null);
            split.Capture = sink;
            try
            {
                LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides);
                hold();
                remaining[0] = 1.0;
                yield return Until(() => split.HasAttempt, "an attempt was issued");
                remaining[0] = 0.0;
                yield return Until(held, heldWhat);
                Assert.That(split.AttemptIsCollected, Is.False, "the layout: not collected");
                yield return EndWorld(root);
                Assert.That(split.HasAttempt, Is.False);
                Assert.That(split.Published, Is.Zero);

                Assert.That(sink.Close(20.0), Is.True);
                TestContext.Out.WriteLine(sink.Describe());
                Assert.That(sink.Issued, Is.EqualTo(1));
                Assert.That(sink.Saved, Is.EqualTo(1), "the sample of an attempt that never came back is saved all the same");
                List<SpatialSplitResultLine> results = Lines<SpatialSplitResultLine>(Path.Combine(session, "results.jsonl"));
                Assert.That(results, Has.Count.EqualTo(1), "and its end is recorded");
                SpatialSplitResultLine result = results[0];
                TestContext.Out.WriteLine("ended " + result.end + " (" + result.detail + "), outputs read " + result.searchObserved + ", ran " + result.ran + ", cancelled " + result.cancelled);
                Assert.That(new[] { "WorldEnded", "Abandoned", "NotRun" }.Contains(result.end), Is.True, "an end of the world's, never a search's: " + result.end);
                Assert.That(result.end, Is.Not.EqualTo("NotFound"));
                Assert.That(result.searchObserved, Is.EqualTo(result.ran && !result.cancelled), "the outputs are kept where, and only where, the numbers ran");
                string summary = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "replay"), out bool allSame);
                TestContext.Out.WriteLine(summary);
                StringAssert.Contains("replay errors 0", summary, "the sample itself replays");
                if (result.searchObserved)
                {
                    Assert.That(allSame, Is.True, "the numbers ran before the world ended: the replay comes to what they came to\n" + summary);
                }
                else
                {
                    StringAssert.Contains("結果未記録 1", summary, "the live outputs were never read: not recorded, whatever the replay finds");
                    StringAssert.Contains("結果未記録 by reason: the numbers did not run or were not read (" + result.end + ") 1", summary);
                    Assert.That(allSame, Is.False, "a sample replayed with nothing to compare it with is not the same as it ran");
                }
            }
            finally
            {
                sink.Close(5.0);
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
