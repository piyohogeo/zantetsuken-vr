using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The capture of the additional split's issued inputs (DESIGN 7.9.8), its own parts on synthetic samples: a sample
    /// written and read back bit for bit, with only the vertices its indices name; every bound stopping the capture
    /// with its reason and nothing thinned out; a write that fails; the session's disk bound; partial and cut-short
    /// files, which are never read as samples; a sample without a result line, which is 結果未記録 and not NotFound;
    /// and the replay's check finding a triangle lost, doubled, short of the margin or on the wrong side.
    /// (The live split, its search and the replay of what it found are the PlayMode cases'.)
    /// </summary>
    public unsafe class SpatialSplitCaptureTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "zantetsu-split-capture-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            // This case's own folder, made by SetUp.
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static readonly int VertexBytes = UnsafeUtility.SizeOf<VpRenderVertex>();

        private static byte[] Pattern(int bytes, int seed)
        {
            var random = new System.Random(seed);
            var data = new byte[bytes];
            random.NextBytes(data);
            return data;
        }

        // A sample as the copy makes one: one submesh, no anchor, no convex; the indices as given and the span of vertices.
        private static SpatialSplitCaptureSample NewSample(SpatialSplitCaptureSink sink, long attempt, uint[] indices, int viewLength, uint spanLo, byte[] span)
        {
            int spanCount = span.Length / VertexBytes;
            long bytes = (3L * sizeof(int)) + ((long)indices.Length * sizeof(uint)) + span.Length;
            if (!sink.TryReserve(bytes, false))
            {
                return null;
            }

            byte* data = (byte*)UnsafeUtility.Malloc(bytes, 16, Allocator.Persistent);
            var w = new SpatialSplitRawWriter(data, bytes);
            w.Int(0);
            w.Int(indices.Length);
            w.Int(7);
            fixed (uint* p = indices) w.Bytes(p, (long)indices.Length * sizeof(uint));
            fixed (byte* p = span) w.Bytes(p, span.Length);
            return new SpatialSplitCaptureSample
            {
                identity = new SpatialSplitCaptureIdentity { world = 1, attempt = attempt, issuedFrame = 40, fragment = 5, rootFragment = 2, splitSource = 0, generation = 1, epoch = 3, objectName = "box \"one\"" },
                margin = 0.01f, supportEpsilon = 0.004f, anchorEpsilon = 0.005f, vertexLimit = 128, parentMass = 12.5,
                limits = new SpatialSplitSearchLimits(64, 8, 16, 131072), cutConvexes = true, minCutSideTriangles = 50,
                bentSearch = true, bentLimits = SpatialSplitBentLimits.Default, maxConvexVertices = 8,
                geometryToOwner = float4x4.TRS(new float3(1f, 2f, 3f), quaternion.identity, new float3(1f)), shapeToOwner = float4x4.identity,
                submeshCount = 1, anchorCount = -1, indexCount = indices.Length, vertexViewLength = viewLength, convexCount = 0,
                vertexStructBytes = VertexBytes, spanLo = spanLo, spanCount = spanCount, data = data, bytes = bytes,
            };
        }

        private string NewDirectory(string name) => Path.Combine(_root, name);

        [Test]
        public void ASample_IsWrittenAndReadBackBitForBit_WithOnlyTheVerticesItsIndicesName()
        {
            string directory = NewDirectory("round");
            var sink = new SpatialSplitCaptureSink(directory, SpatialSplitCaptureLimits.Default, "test code", new[] { "-zantetsuSomething 1" });
            // Vertices 12..15 copied; 14 is named by no index, and 99999 is past the storage's view (kept as an index, no vertex).
            uint[] indices = { 12, 15, 12, 99999, 13, 15 };
            byte[] span = Pattern(4 * VertexBytes, 1);
            sink.NoteIssued(NewSample(sink, 7, indices, 20, 12, span));
            Assert.That(sink.Close(10.0), Is.True, "the writer ended");
            Assert.That(sink.Saved, Is.EqualTo(1));
            Assert.That(sink.Lost + sink.NotCapturedInAll + sink.Returned, Is.Zero);
            Assert.That(sink.QueuedItems, Is.Zero, "everything given back");
            Assert.That(sink.QueuedBytes, Is.Zero);

            string file = Path.Combine(directory, SpatialSplitCaptureSink.SampleFileName(1, 7));
            SpatialSplitSample sample = SpatialSplitSample.Read(file);
            Assert.That(sample.indices, Is.EqualTo(indices), "the indices as issued, in their order");
            Assert.That(sample.vertexIds, Is.EqualTo(new uint[] { 12, 13, 15 }), "the vertices named, ascending; 14 is not kept");
            byte[] expected = span.Take(2 * VertexBytes).Concat(span.Skip(3 * VertexBytes).Take(VertexBytes)).ToArray();
            Assert.That(sample.vertexBytes, Is.EqualTo(expected), "each kept vertex as its bytes were");
            Assert.That(sample.vertexViewLength, Is.EqualTo(20));
            Assert.That(sample.submeshOffsets, Is.EqualTo(new[] { 0 }));
            Assert.That(sample.submeshCounts, Is.EqualTo(new[] { 6 }));
            Assert.That(sample.materials, Is.EqualTo(new[] { 7 }));
            Assert.That(sample.anchors, Is.Null, "no anchor: none, not an empty set");
            Assert.That(sample.convexes, Is.Empty);
            Assert.That(sample.world == 1 && sample.attempt == 7 && sample.issuedFrame == 40 && sample.fragment == 5 && sample.rootFragment == 2
                && sample.generation == 1 && sample.epoch == 3, Is.True, "who it is");
            Assert.That(sample.objectName, Is.EqualTo("box \"one\""));
            Assert.That(sample.codeVersion, Is.EqualTo("test code"));
            Assert.That(sample.sessionId, Is.EqualTo(sink.sessionId));
            Assert.That(BitConverter.SingleToInt32Bits(sample.margin), Is.EqualTo(BitConverter.SingleToInt32Bits(0.01f)));
            Assert.That(sample.parentMass, Is.EqualTo(12.5));
            Assert.That(sample.limits.maxTriangles == 131072 && sample.limits.meshBins == SpatialSplitSearchLimits.DefaultMeshBins, Is.True);
            Assert.That(sample.cutConvexes && sample.bentSearch && sample.bentLimits.Equals(SpatialSplitBentLimits.Default), Is.True);
            Assert.That(sample.geometryToOwner.Equals(float4x4.TRS(new float3(1f, 2f, 3f), quaternion.identity, new float3(1f))), Is.True);

            string[] index = File.ReadAllLines(Path.Combine(directory, "samples.jsonl"));
            Assert.That(index, Has.Length.EqualTo(1));
            SpatialSplitSampleLine line = JsonUtility.FromJson<SpatialSplitSampleLine>(index[0]);
            Assert.That(line.file, Is.EqualTo(Path.GetFileName(file)));
            Assert.That(line.sha256, Is.EqualTo(sample.sha256), "the index's hash is of the content read back");
            Assert.That(line.rawBytes, Is.EqualTo(sample.rawBytes));
            Assert.That(File.Exists(Path.Combine(directory, "session.json")) && File.Exists(Path.Combine(directory, "summary.json")), Is.True);
            Assert.That(Directory.GetFiles(directory, "*.partial"), Is.Empty, "no file of the writer's left half done");
            StringAssert.Contains("notCapturedByDesign", File.ReadAllText(Path.Combine(directory, "session.json")));
        }

        [Test]
        public void TheLaunchsArgument_MakesASessionInANewFolder_AndWithoutIt_NothingIsMade()
        {
            Assert.That(SpatialSplitCaptureSession.FromArguments(new[] { "x.exe", "-zantetsuPhysicsStep", "fixed" }), Is.Null, "off without its argument");
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("needs an absolute directory"));
            Assert.That(SpatialSplitCaptureSession.FromArguments(new[] { "x.exe", SpatialSplitCaptureSession.Argument, "relative" }), Is.Null, "a relative folder is refused");
            SpatialSplitCaptureSink sink = SpatialSplitCaptureSession.FromArguments(new[]
            {
                "x.exe", SpatialSplitCaptureSession.Argument, _root, SpatialSplitCaptureSession.CodeArgument, "abc1234", "-zantetsuPhysicsStep", "fixed",
            });
            Assert.That(sink, Is.Not.Null);
            try
            {
                Assert.That(Path.GetDirectoryName(sink.directory), Is.EqualTo(_root), "a new folder under the one named");
                StringAssert.StartsWith("split-capture-", Path.GetFileName(sink.directory));
                StringAssert.Contains("code abc1234", sink.codeVersion);
            }
            finally
            {
                Assert.That(sink.Close(10.0), Is.True);
            }

            string session = File.ReadAllText(Path.Combine(sink.directory, "session.json"));
            StringAssert.Contains("-zantetsuPhysicsStep fixed", session, "the launch's switches are noted");
            StringAssert.Contains(SpatialSplitCaptureSession.Argument + " <directory>", session);
            StringAssert.DoesNotContain(_root.Replace("\\", "\\\\"), session, "and not where it writes");
        }

        [Test]
        public void ASampleOverItsBound_StopsTheCapture_WithItsReason_AndLaterInputsAreCountedNotCaptured()
        {
            var sink = new SpatialSplitCaptureSink(NewDirectory("cap"), new SpatialSplitCaptureLimits(4, 1 << 20, 100, 1 << 30), "", null);
            Assert.That(sink.TryReserve(200, false), Is.False);
            sink.NoteIssued(null);
            Assert.That(sink.StopReason, Is.EqualTo(SpatialSplitCaptureRefusal.SampleOverCap));
            Assert.That(sink.IsCapturing, Is.False);
            Assert.That(sink.NotCaptured(SpatialSplitCaptureRefusal.SampleOverCap), Is.EqualTo(1));
            Assert.That(sink.TryReserve(50, false), Is.False, "nothing thinned out: stopped is stopped");
            sink.NoteIssued(null);
            Assert.That(sink.NotCaptured(SpatialSplitCaptureRefusal.Stopped), Is.EqualTo(1));
            Assert.That(sink.Issued, Is.EqualTo(2));
            Assert.That(sink.TryReserve(50, true), Is.True, "a result of a sample already taken is still let through");
            sink.Unreserve(50);
            Assert.That(sink.Close(10.0), Is.True);
            StringAssert.Contains("SampleOverCap", File.ReadAllText(Path.Combine(sink.directory, "summary.json")));
        }

        [Test]
        public void AFullQueue_StopsTheCapture_AndACopyGivenBack_IsCountedAndFreed()
        {
            var sink = new SpatialSplitCaptureSink(NewDirectory("queue"), new SpatialSplitCaptureLimits(1, 1 << 20, 1 << 20, 1 << 30), "", null);
            SpatialSplitCaptureSample first = NewSample(sink, 1, new uint[] { 0, 1, 2 }, 3, 0, Pattern(3 * VertexBytes, 2));
            Assert.That(first, Is.Not.Null);
            Assert.That(NewSample(sink, 2, new uint[] { 0, 1, 2 }, 3, 0, Pattern(3 * VertexBytes, 3)), Is.Null, "the item being held counts");
            Assert.That(sink.StopReason, Is.EqualTo(SpatialSplitCaptureRefusal.QueueItems));

            // The work was not taken: the copy goes back, and the attempt is not counted as issued.
            sink.Return(first);
            Assert.That(sink.Returned, Is.EqualTo(1));
            Assert.That(sink.Issued, Is.Zero);
            Assert.That(sink.QueuedItems + sink.QueuedBytes, Is.Zero);
            Assert.That(sink.Close(10.0), Is.True);
            Assert.That(sink.Saved, Is.Zero);
        }

        [Test]
        public void AWriteThatFails_StopsTheCapture_KeepsWhatWasCommitted_AndLeavesNoHalfFile()
        {
            string directory = NewDirectory("fail");
            var sink = new SpatialSplitCaptureSink(directory, SpatialSplitCaptureLimits.Default, "", null) { FailWriteAtSampleForTest = 1 };
            sink.NoteIssued(NewSample(sink, 1, new uint[] { 0, 1, 2 }, 3, 0, Pattern(3 * VertexBytes, 4)));
            sink.NoteIssued(NewSample(sink, 2, new uint[] { 0, 1, 2 }, 3, 0, Pattern(3 * VertexBytes, 5)));
            Assert.That(sink.Close(10.0), Is.True);
            Assert.That(sink.Saved, Is.EqualTo(1));
            Assert.That(sink.Lost, Is.EqualTo(1));
            Assert.That(sink.InFlight, Is.Zero);
            Assert.That(sink.StopReason, Is.EqualTo(SpatialSplitCaptureRefusal.WriteFailed));
            Assert.That(Directory.GetFiles(directory, "*.zss.gz").Select(Path.GetFileName), Is.EqualTo(new[] { SpatialSplitCaptureSink.SampleFileName(1, 1) }));
            Assert.That(Directory.GetFiles(directory, "*.partial"), Is.Empty);
            Assert.That(File.ReadAllLines(Path.Combine(directory, "samples.jsonl")), Has.Length.EqualTo(1));
            Assert.That(sink.QueuedItems + sink.QueuedBytes, Is.Zero);
        }

        [Test]
        public void TheSessionsDiskBound_StopsTheCapture_BeforeASampleWouldPassIt()
        {
            string directory = NewDirectory("disk");
            var sink = new SpatialSplitCaptureSink(directory, new SpatialSplitCaptureLimits(16, 1 << 24, 1 << 24, 4096), "", null);
            const int vertices = 512;   // random bytes: some 8 KiB that do not compress
            var indices = new uint[vertices];
            for (int i = 0; i < vertices; i++) indices[i] = (uint)i;
            sink.NoteIssued(NewSample(sink, 1, indices, vertices, 0, Pattern(vertices * VertexBytes, 6)));
            Assert.That(sink.Close(10.0), Is.True);
            Assert.That(sink.StopReason, Is.EqualTo(SpatialSplitCaptureRefusal.SessionDisk));
            Assert.That(sink.Saved, Is.Zero);
            Assert.That(sink.Lost, Is.EqualTo(1));
            Assert.That(Directory.GetFiles(directory, "*.zss.gz"), Is.Empty);
            Assert.That(Directory.GetFiles(directory, "*.partial"), Is.Empty);
            Assert.That(sink.DiskBytes, Is.LessThanOrEqualTo(4096));
        }

        [Test]
        public void TheReplay_ReadsOnlyListedSamples_TellsACutShortOne_AndASampleWithoutAResultIsNotRecorded()
        {
            // A session of one sample and no result line.
            string session = NewDirectory("session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "", null);
            sink.NoteIssued(NewSample(sink, 3, new uint[] { 0, 1, 2 }, 3, 0, Pattern(3 * VertexBytes, 7)));
            Assert.That(sink.Close(10.0), Is.True);
            File.WriteAllText(Path.Combine(session, "w001-a0000009.zss.gz.0123.partial"), "half a file");
            string summary = SpatialSplitReplay.RunSession(session, NewDirectory("replay"), out bool allSame);
            TestContext.Out.WriteLine(summary);
            StringAssert.Contains("partial files (not samples) 1", summary);
            StringAssert.Contains("結果未記録 1", summary, "no result line: not recorded, and never NotFound");
            StringAssert.Contains("結果未記録 by reason: no result line 1", summary);
            Assert.That(allSame, Is.False, "a sample with no convex cannot be made a shape again: the replay says so");
            string replayed = File.ReadAllText(Path.Combine(NewDirectory("replay"), "replay.jsonl"));
            StringAssert.Contains("結果未記録", replayed);
            StringAssert.DoesNotContain("NotFound", replayed);
            Assert.Throws<IOException>(() => SpatialSplitReplay.RunSession(session, NewDirectory("replay"), out _), "the replay's output is a new folder");

            // The same sample cut short, listed: unreadable, not a sample.
            string cut = NewDirectory("cut");
            Directory.CreateDirectory(cut);
            File.Copy(Path.Combine(session, "samples.jsonl"), Path.Combine(cut, "samples.jsonl"));
            byte[] whole = File.ReadAllBytes(Path.Combine(session, SpatialSplitCaptureSink.SampleFileName(1, 3)));
            File.WriteAllBytes(Path.Combine(cut, SpatialSplitCaptureSink.SampleFileName(1, 3)), whole.Take(whole.Length / 2).ToArray());
            Assert.That(() => SpatialSplitSample.Read(Path.Combine(cut, SpatialSplitCaptureSink.SampleFileName(1, 3))), Throws.Exception, "a sample cut short is not read as one");
            string cutSummary = SpatialSplitReplay.RunSession(cut, NewDirectory("cut-replay"), out bool cutSame);
            StringAssert.Contains("unreadable 1", cutSummary);
            Assert.That(cutSame, Is.False);
        }

        [Test]
        public void ThePlayersReplayRequest_OnceNamed_IsAReplay_AndAnIncompleteOneIsRefused_NeverTheGame()
        {
            string s = Zantetsu.Sandbox.SpatialSplitReplayPlayer.Argument, o = Zantetsu.Sandbox.SpatialSplitReplayPlayer.OutArgument;
            string session = Path.Combine(_root, "session"), output = Path.Combine(_root, "out");
            bool Read(string[] a, out string refusal) => Zantetsu.Sandbox.SpatialSplitReplayPlayer.TryReadRequest(a, out _, out _, out refusal);

            Assert.That(Read(new[] { "x.exe", "-zantetsuPhysicsStep", "fixed" }, out string none), Is.False, "neither named: the game as ever");
            Assert.That(none, Is.Null);

            Assert.That(Zantetsu.Sandbox.SpatialSplitReplayPlayer.TryReadRequest(new[] { "x.exe", s, session, o, output }, out string gotSession, out string gotOutput, out string ok), Is.True);
            Assert.That(ok, Is.Null, "a complete request runs");
            Assert.That(gotSession == session && gotOutput == output, Is.True);

            foreach (string[] incomplete in new[]
            {
                new[] { "x.exe", s },                                   // the session's value missing at the end
                new[] { "x.exe", s, "-force-d3d12", o, output },        // another switch in its place
                new[] { "x.exe", s, session },                          // no output named
                new[] { "x.exe", s, session, o },                       // the output's value missing
                new[] { "x.exe", o, output },                           // only the output named
                new[] { "x.exe", s, "relative", o, output },            // not absolute
            })
            {
                Assert.That(Read(incomplete, out string refusal), Is.True, "named: a replay request, never the game: " + string.Join(" ", incomplete));
                Assert.That(refusal, Is.Not.Null.And.Not.Empty, "and refused, to end with 1: " + string.Join(" ", incomplete));
            }
        }

        [Test]
        public void AnEmptyFolder_OrASessionOfNoSample_IsNoSample_NeverTheSame()
        {
            string empty = NewDirectory("empty");
            Directory.CreateDirectory(empty);
            string summary = SpatialSplitReplay.RunSession(empty, NewDirectory("empty-replay"), out bool allSame, out bool noSample);
            TestContext.Out.WriteLine(summary);
            Assert.That(allSame, Is.False, "an empty folder is no evidence of anything");
            Assert.That(noSample, Is.True);
            StringAssert.Contains("NO SAMPLE", summary);

            // A session that captured nothing: its index and results are there, and empty.
            string session = NewDirectory("no-sample");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "", null);
            Assert.That(sink.Close(10.0), Is.True);
            Assert.That(File.Exists(Path.Combine(session, "samples.jsonl")), Is.True, "the layout: an empty index");
            string none = SpatialSplitReplay.RunSession(session, NewDirectory("no-sample-replay"), out bool noneSame, out bool noneEmpty);
            TestContext.Out.WriteLine(none);
            Assert.That(noneSame, Is.False);
            Assert.That(noneEmpty, Is.True);
            StringAssert.Contains("NO SAMPLE", none);
            StringAssert.Contains("every sample compared with its live result and the same, every check passed: NO", none);
        }

        [Test]
        public void TheOutputsCopy_ReservesItsWholeRoom_NotWhatItWrote_UntilItIsGivenBack_AndIsRefusedPastTheBound()
        {
            // A search that found nothing over many indices: little is written, and the whole room of a sharing out is taken.
            const int indices = 30000;
            var attempt = new CutSpatialSplit.Attempt
            {
                indexCount = indices, submeshCount = 1, convexCount = 0, ran = true,
                limits = new SpatialSplitSearchLimits(64, 8, 16, 131072),
            };
            attempt.search.outcome = SpatialSplitSearchOutcome.NotFound;
            var sink = new SpatialSplitCaptureSink(NewDirectory("result-room"), SpatialSplitCaptureLimits.Default, "", null);
            try
            {
                long room = SpatialSplitCanonical.MaxBytes(attempt) + SpatialSplitCaptureResult.Overhead;
                Assert.That(room, Is.GreaterThan(indices * 4L), "the layout: the room of a sharing out of every index");
                SpatialSplitCaptureResult result = SpatialSplitCaptureCopy.TakeResult(sink, attempt, out string refused);
                Assert.That(result, Is.Not.Null, refused);
                Assert.That(result.dataBytes, Is.LessThan(room / 10), "what was written of it is small");
                Assert.That(sink.QueuedBytes, Is.EqualTo(room), "the whole room is counted while the result is held");
                Assert.That(sink.QueuedItems, Is.EqualTo(1));
                sink.LoseResult(result);
                Assert.That(sink.QueuedBytes + sink.QueuedItems, Is.Zero, "and given back with it");
                Assert.That(sink.ResultsLost, Is.EqualTo(1), "a result given up is counted as not recorded");
            }
            finally
            {
                Assert.That(sink.Close(10.0), Is.True);
            }

            var small = new SpatialSplitCaptureSink(NewDirectory("result-refused"), new SpatialSplitCaptureLimits(4, 64 * 1024, 1024, 1 << 30), "", null);
            try
            {
                Assert.That(SpatialSplitCaptureCopy.TakeResult(small, attempt, out string why), Is.Null, "past the queue's bytes");
                StringAssert.Contains("no room", why);
                Assert.That(small.QueuedBytes + small.QueuedItems, Is.Zero, "nothing held for a refused copy");
                Assert.That(small.StopReason, Is.EqualTo(SpatialSplitCaptureRefusal.QueueBytes), "and the capture stopped at the bound");
            }
            finally
            {
                Assert.That(small.Close(10.0), Is.True);
            }
        }

        [Test]
        public void ASampleCommittedWhoseIndexLineFails_IsCountedOnce_AndTheReplayFindsItByTheFolder()
        {
            string directory = NewDirectory("index-fail");
            var sink = new SpatialSplitCaptureSink(directory, SpatialSplitCaptureLimits.Default, "", null) { FailIndexAtSampleForTest = 0 };
            sink.NoteIssued(NewSample(sink, 4, new uint[] { 0, 1, 2 }, 3, 0, Pattern(3 * VertexBytes, 8)));
            Assert.That(sink.Close(10.0), Is.True);
            Assert.That(sink.Unindexed, Is.EqualTo(1), "committed without its line");
            Assert.That(sink.Saved + sink.Lost, Is.Zero, "and counted nowhere else");
            Assert.That(sink.InFlight, Is.Zero);
            Assert.That(sink.StopReason, Is.EqualTo(SpatialSplitCaptureRefusal.WriteFailed));
            Assert.That(File.Exists(Path.Combine(directory, SpatialSplitCaptureSink.SampleFileName(1, 4))), Is.True, "the file is there, whole");
            Assert.That(SpatialSplitSample.Read(Path.Combine(directory, SpatialSplitCaptureSink.SampleFileName(1, 4))).indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
            string summary = SpatialSplitReplay.RunSession(directory, NewDirectory("index-fail-replay"), out bool allSame);
            TestContext.Out.WriteLine(summary);
            StringAssert.Contains("samples listed 0, committed and not in the index (found by the folder) 1", summary);
            StringAssert.Contains("replayed 1", summary);
            Assert.That(allSame, Is.False);
        }

        [Test]
        public void AnIndexWhoseLastLineIsCutShort_StillGivesEveryLineBeforeIt_AndTheSampleOfThatLine()
        {
            string directory = NewDirectory("cut-line");
            var sink = new SpatialSplitCaptureSink(directory, SpatialSplitCaptureLimits.Default, "", null);
            sink.NoteIssued(NewSample(sink, 1, new uint[] { 0, 1, 2 }, 3, 0, Pattern(3 * VertexBytes, 9)));
            sink.NoteIssued(NewSample(sink, 2, new uint[] { 0, 1, 2 }, 3, 0, Pattern(3 * VertexBytes, 10)));
            Assert.That(sink.Close(10.0), Is.True);
            string index = Path.Combine(directory, "samples.jsonl");
            string[] lines = File.ReadAllLines(index);
            Assert.That(lines, Has.Length.EqualTo(2), "the layout");
            File.WriteAllText(index, lines[0] + "\n" + lines[1].Substring(0, lines[1].Length / 2));   // as a process that ended while writing it
            string summary = SpatialSplitReplay.RunSession(directory, NewDirectory("cut-line-replay"), out bool allSame);
            TestContext.Out.WriteLine(summary);
            StringAssert.Contains("samples listed 1, committed and not in the index (found by the folder) 1", summary);
            StringAssert.Contains("lines not read (cut short or broken): index 1, results 0", summary);
            StringAssert.Contains("replayed 2", summary, "both samples are replayed");
            Assert.That(allSame, Is.False);
        }

        [Test]
        public void TheBentCheck_FindsAFoldThroughATrianglesInside_AndADistanceShortOfTheMargin()
        {
            // A ridge: h rises from 0 at u = 0 to 1 at u = 1 and falls to 0 at u = 2, flat beyond.
            var ridge = new SpatialSplitBentSurface { height = new float3(0f, 1f, 0f), along = new float3(1f, 0f, 0f), knotCount = 3 };
            ridge.knotU[0] = 0f; ridge.knotH[0] = 0f;
            ridge.knotU[1] = 1f; ridge.knotH[1] = 1f;
            ridge.knotU[2] = 2f; ridge.knotH[2] = 0f;
            var check = new SpatialSplitReplay.BentCheck(in ridge);
            var points = new System.Collections.Generic.List<float2>();

            // Every vertex above, and the edge from (0, 0.5) to (2, 0.5) passes under the ridge's top: the fold is inside.
            points.AddRange(new[] { new float2(0f, 0.5f), new float2(2f, 0.5f), new float2(1f, 3f) });
            Assert.That(points.TrueForAll(p => p.y > check.HeightAt(p.x)), Is.True, "the layout: every vertex is above");
            Assert.That(check.Verdict(points, true, 0.01f), Is.EqualTo(1), "not wholly above");

            // Wholly above, but its lowest corner within 0.05 of the flat end: short of a margin of 0.1, enough for 0.01.
            points.Clear();
            points.AddRange(new[] { new float2(-3f, 0.05f), new float2(-2f, 0.05f), new float2(-2.5f, 1f) });
            Assert.That(check.Verdict(points, true, 0.1f), Is.EqualTo(2), "nearer than the margin");
            Assert.That(check.Verdict(points, true, 0.01f), Is.Zero, "the margin kept");

            // Its vertices some 0.74 from the ridge, yet its lower edge passes 0.55 over the top knot: short of 0.6.
            points.Clear();
            points.AddRange(new[] { new float2(0.5f, 1.55f), new float2(1.5f, 1.55f), new float2(1f, 2.5f) });
            Assert.That(check.Verdict(points, true, 0.6f), Is.EqualTo(2), "the knot's distance to the polygon counts, not only the vertices'");
            Assert.That(check.Verdict(points, true, 0.5f), Is.Zero);
            Assert.That(check.Verdict(points, false, 0.01f), Is.EqualTo(1), "and below it is not");

            // A part wholly below the ridge, far from it.
            points.Clear();
            points.AddRange(new[] { new float2(0.5f, -2f), new float2(1.5f, -2f), new float2(1f, -1.5f) });
            Assert.That(check.Verdict(points, false, 0.1f), Is.Zero);
        }

        // A box's B-rep: corner i at (bit 0 ? hi.x : lo.x, bit 1 ? hi.y : lo.y, bit 2 ? hi.z : lo.z), six quads, twelve edges.
        private static readonly int[][] BoxFaces =
        {
            new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 },
        };

        // Shape of boxes, a convex each, made the product's way (PhysicsOwnerShape.Authored); one mesh holds them all.
        private static PhysicsOwnerShape BoxesShape(float3[] lows, float3[] highs, out Mesh mesh)
        {
            var vertices = new System.Collections.Generic.List<float3>();
            var offsets = new System.Collections.Generic.List<int>();
            var faceIndices = new System.Collections.Generic.List<int>();
            var faceEdges = new System.Collections.Generic.List<int>();
            var edges = new System.Collections.Generic.List<Zantetsu.ConvexCut.BrepEdge>();
            var ranges = new Zantetsu.ConvexCut.ConvexBrepRange[lows.Length];
            var lo = new float3(float.PositiveInfinity);
            var hi = new float3(float.NegativeInfinity);
            for (int c = 0; c < lows.Length; c++)
            {
                ranges[c] = new Zantetsu.ConvexCut.ConvexBrepRange
                {
                    vertexBase = vertices.Count, vertexCount = 8, faceBase = offsets.Count, faceCount = 6,
                    faceIndexBase = faceIndices.Count, faceIndexCount = 24, edgeBase = edges.Count, edgeCount = 12, maxFaceLoop = 4,
                };
                for (int i = 0; i < 8; i++)
                {
                    var p = new float3((i & 1) != 0 ? highs[c].x : lows[c].x, (i & 2) != 0 ? highs[c].y : lows[c].y, (i & 4) != 0 ? highs[c].z : lows[c].z);
                    vertices.Add(p);
                    lo = math.min(lo, p);
                    hi = math.max(hi, p);
                }

                var local = new System.Collections.Generic.Dictionary<(int, int), int>();
                var localEdges = new System.Collections.Generic.List<Zantetsu.ConvexCut.BrepEdge>();
                for (int f = 0; f < 6; f++)
                {
                    offsets.Add(4 * f);
                    for (int k = 0; k < 4; k++)
                    {
                        int a = BoxFaces[f][k], b = BoxFaces[f][(k + 1) % 4];
                        var key = (math.min(a, b), math.max(a, b));
                        if (!local.TryGetValue(key, out int e))
                        {
                            e = localEdges.Count;
                            local[key] = e;
                            localEdges.Add(new Zantetsu.ConvexCut.BrepEdge { v0 = key.Item1, v1 = key.Item2, f0 = -1, f1 = -1 });
                        }

                        Zantetsu.ConvexCut.BrepEdge edge = localEdges[e];
                        if (a < b) edge.f0 = f; else edge.f1 = f;
                        localEdges[e] = edge;
                        faceIndices.Add(a);
                        faceEdges.Add(e);
                    }
                }

                offsets.Add(24);
                edges.AddRange(localEdges);
            }

            mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, vertices = new Vector3[] { lo, hi } };
            mesh.RecalculateBounds();
            var v = new NativeArray<float3>(vertices.ToArray(), Allocator.Persistent);
            var o = new NativeArray<int>(offsets.ToArray(), Allocator.Persistent);
            var fi = new NativeArray<int>(faceIndices.ToArray(), Allocator.Persistent);
            var fe = new NativeArray<int>(faceEdges.ToArray(), Allocator.Persistent);
            var ed = new NativeArray<Zantetsu.ConvexCut.BrepEdge>(edges.ToArray(), Allocator.Persistent);
            try
            {
                var bank = new Zantetsu.ConvexCut.ConvexBrepBank
                {
                    vertices = (float3*)v.GetUnsafePtr(), faceOffsets = (int*)o.GetUnsafePtr(), faceIndices = (int*)fi.GetUnsafePtr(),
                    faceEdges = (int*)fe.GetUnsafePtr(), edges = (Zantetsu.ConvexCut.BrepEdge*)ed.GetUnsafePtr(),
                };
                var meshes = new Mesh[lows.Length];
                for (int c = 0; c < meshes.Length; c++) meshes[c] = mesh;
                return PhysicsOwnerShape.Authored(bank, ranges, meshes, PhysicsShapeSource.External(), float4x4.identity);
            }
            finally
            {
                v.Dispose();
                o.Dispose();
                fi.Dispose();
                fe.Dispose();
                ed.Dispose();
            }
        }

        private enum Kind { MeshLed, WholeConvexPlane, Bent }

        // The replay's check of one sharing out by the plane y = 0 (or the flat bent surface h = 0): two triangles above,
        // two below, and two convexes the classification puts Positive (the first) and Negative (the second), the first
        // reaching down to -positiveReach and the second up to +negativeReach. Support epsilon 1e-4, margin 2e-4.
        private static SpatialSplitReplayResult CheckConvexes(Kind kind, float positiveReach, float negativeReach)
        {
            const float epsilon = 1e-4f;
            var vertices = new NativeArray<VpRenderVertex>(6, Allocator.Persistent);
            for (int i = 0; i < 6; i++) vertices[i] = new VpRenderVertex { position = new Vector3(i, i < 3 ? 1f : -1f, 0f) };
            var indices = new NativeArray<uint>(new uint[] { 0, 1, 2, 3, 4, 5 }, Allocator.Persistent);
            PhysicsOwnerShape shape = BoxesShape(
                new[] { new float3(0f, -positiveReach, 0f), new float3(2f, -1f, 0f) },
                new[] { new float3(1f, 1f, 1f), new float3(3f, negativeReach, 1f) }, out Mesh mesh);
            var attempt = new CutSpatialSplit.Attempt
            {
                indexCount = 6, submeshCount = 1, convexCount = 2, margin = 2f * epsilon, supportEpsilon = epsilon,
                limits = new SpatialSplitSearchLimits(64, 8, 16, 131072), shapeToOwner = float4x4.identity, geometryToOwner = float4x4.identity,
                vertices = (VpRenderVertex*)vertices.GetUnsafePtr(), vertexViewLength = 6, indices = (uint*)indices.GetUnsafePtr(),
            };
            try
            {
                attempt.TakeBlock();
                attempt.submeshOffsets[0] = 0;
                attempt.submeshCounts[0] = 6;
                for (int c = 0; c < 2; c++)
                {
                    attempt.banks[c] = shape.BankOf(c);
                    attempt.ranges[c] = shape.Convex(c);
                }

                attempt.search.outcome = SpatialSplitSearchOutcome.Found;
                attempt.search.planeGeometry = attempt.search.planeShape = attempt.search.planeOwner = new float4(0f, 1f, 0f, 0f);
                attempt.search.meshLed = kind == Kind.MeshLed;
                if (kind == Kind.Bent)
                {
                    var surface = new SpatialSplitBentSurface { height = new float3(0f, 1f, 0f), along = new float3(1f, 0f, 0f), knotCount = 2 };
                    surface.knotU[0] = -10f; surface.knotH[0] = 0f;
                    surface.knotU[1] = 10f; surface.knotH[1] = 0f;
                    attempt.search.bent = true;
                    attempt.search.bentSurface = surface;
                }

                attempt.distributed = true;
                attempt.distribution.positiveIndexCount = 3;
                attempt.distribution.negativeIndexCount = 3;
                attempt.outSubmeshOffsets[0] = 0;
                attempt.outSubmeshOffsets[1] = 0;
                attempt.outSubmeshCounts[0] = 3;
                attempt.outSubmeshCounts[1] = 3;
                for (int i = 0; i < 6; i++) attempt.output[i] = (uint)i;
                byte* sides = stackalloc byte[2];
                sides[0] = (byte)Zantetsu.ConvexCut.ConvexSide.Positive;
                sides[1] = (byte)Zantetsu.ConvexCut.ConvexSide.Negative;
                Assert.That(PhysicsCutClassification.TryClassifyWithSides(
                    shape, sides, 2, 10.0, 128, PhysicsCutBlocks.Take<byte>(PhysicsCutClassification.RequiredBlockBytes(shape)), out PhysicsCutClassification classification), Is.True,
                    "the layout: the product's classification with the sides given");
                attempt.classification = classification;
                var result = new SpatialSplitReplayResult();
                SpatialSplitReplay.Check(attempt, result);
                Assert.That(result.checkedSharing, Is.True);
                Assert.That(result.trianglesMissing + result.trianglesExtra + result.triangleMarginShort + result.triangleWrongSide, Is.Zero, "the layout: the triangles are as they should be");
                return result;
            }
            finally
            {
                attempt.Free(null);
                shape.Dispose();
                UnityEngine.Object.DestroyImmediate(mesh);
                vertices.Dispose();
                indices.Dispose();
            }
        }

        [Test]
        public void TheReplaysConvexCheck_OfAMeshLedPlane_AllowsAReachAcrossUpToTheSupportEpsilon_OnEitherSide_AndNoFurther()
        {
            const float epsilon = 1e-4f;
            foreach (bool positiveSide in new[] { true, false })
            {
                string side = positiveSide ? "a Positive convex reaching below" : "a Negative convex reaching above";
                SpatialSplitReplayResult R(float reach) => positiveSide ? CheckConvexes(Kind.MeshLed, reach, -0.5f) : CheckConvexes(Kind.MeshLed, -0.5f, reach);

                SpatialSplitReplayResult under = R(0.5f * epsilon);
                Assert.That(under.convexWrongSide, Is.Zero, side + " by half the epsilon: no support of the other side (DESIGN 7.6)");
                Assert.That(under.convexAcrossWithinEpsilon, Is.EqualTo(1), "and counted");
                Assert.That(under.convexMostAcross, Is.EqualTo(0.5f * epsilon), "with how far");

                SpatialSplitReplayResult at = R(epsilon);
                Assert.That(at.convexWrongSide, Is.Zero, side + " by the epsilon exactly: still no support (only beyond it is)");
                Assert.That(at.convexAcrossWithinEpsilon, Is.EqualTo(1));
                Assert.That(at.convexMostAcross, Is.EqualTo(epsilon));

                SpatialSplitReplayResult over = R(1.5f * epsilon);
                Assert.That(over.convexWrongSide, Is.EqualTo(1), side + " beyond the epsilon: on the wrong side");
                Assert.That(over.convexAcrossWithinEpsilon, Is.Zero);
                Assert.That(over.convexMostAcross, Is.EqualTo(1.5f * epsilon));

                SpatialSplitReplayResult clear = positiveSide ? CheckConvexes(Kind.MeshLed, -0.5f, -0.5f) : CheckConvexes(Kind.MeshLed, -0.5f, -0.5f);
                Assert.That(clear.convexWrongSide + clear.convexAcrossWithinEpsilon, Is.Zero, "clear of the plane: nothing");
                Assert.That(clear.convexMostAcross, Is.Zero);
            }
        }

        [Test]
        public void TheReplaysConvexCheck_OfAWholeConvexPlane_OrABentSurface_StaysStrict_WithinTheSupportEpsilonToo()
        {
            const float epsilon = 1e-4f;
            foreach (Kind kind in new[] { Kind.WholeConvexPlane, Kind.Bent })
            {
                Assert.That(CheckConvexes(kind, 0.5f * epsilon, -0.5f).convexWrongSide, Is.EqualTo(1), kind + ": a Positive convex reaching below by half the epsilon is on the wrong side");
                Assert.That(CheckConvexes(kind, -0.5f, 0.5f * epsilon).convexWrongSide, Is.EqualTo(1), kind + ": a Negative convex reaching above by half the epsilon too");
                SpatialSplitReplayResult clear = CheckConvexes(kind, -0.5f, -0.5f);
                Assert.That(clear.convexWrongSide + clear.convexMarginShort, Is.Zero, kind + ": clear of it by more than the margin: nothing");
                Assert.That(clear.convexAcrossWithinEpsilon, Is.Zero, kind + ": the epsilon's allowance is the mesh-led plane's only");
            }

            // A whole-convex plane keeps the margin too: a convex clear of it by less than the margin is short of it.
            Assert.That(CheckConvexes(Kind.WholeConvexPlane, -1.5f * epsilon, -0.5f).convexMarginShort, Is.EqualTo(1));
        }

        [Test]
        public void TheReplaysCheck_FindsATriangleLostOrDoubled_AVertexShortOfTheMargin_AndOneOnTheWrongSide()
        {
            var vertices = new NativeArray<VpRenderVertex>(6, Allocator.Persistent);
            var indices = new NativeArray<uint>(new uint[] { 0, 1, 2, 3, 4, 5 }, Allocator.Persistent);
            var attempt = new CutSpatialSplit.Attempt
            {
                indexCount = 6, submeshCount = 1, convexCount = 0, margin = 0.1f,
                limits = new SpatialSplitSearchLimits(64, 8, 16, 131072),
                vertices = (VpRenderVertex*)vertices.GetUnsafePtr(), vertexViewLength = 6, indices = (uint*)indices.GetUnsafePtr(),
            };
            try
            {
                attempt.TakeBlock();
                attempt.submeshOffsets[0] = 0;
                attempt.submeshCounts[0] = 6;
                attempt.search.outcome = SpatialSplitSearchOutcome.Found;
                attempt.search.planeGeometry = new float4(0f, 1f, 0f, 0f);
                attempt.distributed = true;
                attempt.distribution.positiveIndexCount = 3;
                attempt.distribution.negativeIndexCount = 3;
                attempt.outSubmeshOffsets[0] = 0;
                attempt.outSubmeshOffsets[1] = 0;
                attempt.outSubmeshCounts[0] = 3;
                attempt.outSubmeshCounts[1] = 3;

                void Lay(float y0)
                {
                    for (int i = 0; i < 6; i++)
                    {
                        vertices[i] = new VpRenderVertex { position = new Vector3(i, i < 3 ? 1f : -1f, 0f) };
                    }

                    vertices[0] = new VpRenderVertex { position = new Vector3(0f, y0, 0f) };
                    for (int i = 0; i < 6; i++) attempt.output[i] = (uint)i;
                }

                SpatialSplitReplayResult Checked()
                {
                    var result = new SpatialSplitReplayResult();
                    SpatialSplitReplay.Check(attempt, result);
                    return result;
                }

                Lay(1f);
                SpatialSplitReplayResult good = Checked();
                Assert.That(good.checkedSharing && good.ChecksPass, Is.True, "as shared out: every check holds");

                Lay(1f);
                attempt.output[3] = 0;
                attempt.output[4] = 1;
                attempt.output[5] = 2;
                SpatialSplitReplayResult doubled = Checked();
                Assert.That(doubled.trianglesMissing, Is.EqualTo(1), "the negative triangle is lost");
                Assert.That(doubled.trianglesExtra, Is.EqualTo(1), "and the positive one written twice");

                Lay(0.05f);
                SpatialSplitReplayResult near = Checked();
                Assert.That(near.triangleMarginShort, Is.EqualTo(1), "a vertex within the margin");
                Assert.That(near.triangleWrongSide, Is.Zero);

                Lay(-0.5f);
                SpatialSplitReplayResult wrong = Checked();
                Assert.That(wrong.triangleWrongSide, Is.EqualTo(1), "a vertex of a positive triangle below the plane");
            }
            finally
            {
                attempt.Free(null);
                vertices.Dispose();
                indices.Dispose();
            }
        }
    }
}
