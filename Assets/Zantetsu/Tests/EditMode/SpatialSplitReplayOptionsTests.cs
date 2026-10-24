using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using UnityEngine;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The replay's options (GRAPH-CONNECT-6 §2) on synthetic sessions: the tuning list selects the listed samples before any is
    /// read and fails on a missing, a duplicate or another hash; the graph stage needs the span input; the Player's arguments
    /// are read with their refusals; the input and graph settings are in every line. (The graph replay against the engine's
    /// product path is the PlayMode case's, on a captured session with convexes.)
    /// </summary>
    public unsafe class SpatialSplitReplayOptionsTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "zantetsu-split-replay-options-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
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

        // A sample as the copy makes one: one submesh, no anchor, no convex (as SpatialSplitCaptureTests makes them).
        private static SpatialSplitCaptureSample NewSample(SpatialSplitCaptureSink sink, long attempt, uint[] indices, int viewLength, uint spanLo, byte[] span)
        {
            int spanCount = span.Length / VertexBytes;
            long bytes = (3L * sizeof(int)) + ((long)indices.Length * sizeof(uint)) + span.Length;
            Assert.That(sink.TryReserve(bytes, false), Is.True);
            byte* data = (byte*)UnsafeUtility.Malloc(bytes, 16, Allocator.Persistent);
            var w = new SpatialSplitRawWriter(data, bytes);
            w.Int(0);
            w.Int(indices.Length);
            w.Int(7);
            fixed (uint* p = indices) w.Bytes(p, (long)indices.Length * sizeof(uint));
            fixed (byte* p = span) w.Bytes(p, span.Length);
            return new SpatialSplitCaptureSample
            {
                identity = new SpatialSplitCaptureIdentity { world = 1, attempt = attempt, issuedFrame = 40, fragment = 5, rootFragment = 2, splitSource = 0, generation = 1, epoch = 3, objectName = "box" },
                margin = 0.01f, supportEpsilon = 0.004f, anchorEpsilon = 0.005f, vertexLimit = 128, parentMass = 12.5,
                limits = new SpatialSplitSearchLimits(64, 8, 16, 131072), cutConvexes = true, minCutSideTriangles = 50,
                bentSearch = true, bentLimits = SpatialSplitBentLimits.Default, maxConvexVertices = 8,
                geometryToOwner = float4x4.TRS(new float3(1f, 2f, 3f), quaternion.identity, new float3(1f)), shapeToOwner = float4x4.identity,
                submeshCount = 1, anchorCount = -1, indexCount = indices.Length, vertexViewLength = viewLength, convexCount = 0,
                vertexStructBytes = VertexBytes, spanLo = spanLo, spanCount = spanCount, data = data, bytes = bytes,
            };
        }

        private string NewDirectory(string name) => Path.Combine(_root, name);

        /// <summary>A session of three samples (attempts 3, 4 and 5), closed; returns the folder and the samples' hashes by attempt.</summary>
        private string ThreeSamples(out string sessionId, out string[] sha)
        {
            string session = NewDirectory("session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "", null);
            sink.NoteIssued(NewSample(sink, 3, new uint[] { 0, 1, 2 }, 3, 0, Pattern(3 * VertexBytes, 7)));
            sink.NoteIssued(NewSample(sink, 4, new uint[] { 0, 1, 2 }, 3, 0, Pattern(3 * VertexBytes, 8)));
            sink.NoteIssued(NewSample(sink, 5, new uint[] { 0, 1, 2 }, 3, 0, Pattern(3 * VertexBytes, 9)));
            Assert.That(sink.Close(10.0), Is.True);
            sessionId = JsonUtility.FromJson<SpatialSplitSessionIdLine>(File.ReadAllText(Path.Combine(session, "session.json"))).sessionId;
            sha = new string[3];
            for (int i = 0; i < 3; i++) sha[i] = SpatialSplitSample.Read(Path.Combine(session, SpatialSplitCaptureSink.SampleFileName(1, 3 + i))).sha256;
            return session;
        }

        private static string Line(string session, long attempt, string sha) =>
            "{\"session\":\"" + session + "\",\"world\":1,\"attempt\":" + attempt + ",\"sha256\":\"" + sha + "\"}";

        [Test]
        public void TheList_SelectsTheListedSamplesBeforeAnyIsRead_AndFailsOnAMissingADuplicateOrAnotherHash()
        {
            string session = ThreeSamples(out string id, out string[] sha);
            // the list names 3 and 5 of this session, and a line of another session (ignored)
            string list = Path.Combine(_root, "list.jsonl");
            File.WriteAllLines(list, new[] { Line(id, 3, sha[0]), Line("another-session", 4, sha[1]), Line(id, 5, sha[2]) });
            var options = new SpatialSplitReplayOptions { listPath = list };
            string summary = SpatialSplitReplay.RunSession(session, NewDirectory("replay"), options, out bool allSame, out bool noSample);
            TestContext.Out.WriteLine(summary);
            StringAssert.Contains("tuning list for session " + id + ": entries 2, replayed 2, missing 0, duplicate 0, hash differs from the list 0, files left out (not listed) 1; list check passed", summary);
            Assert.That(noSample, Is.False);
            string lines = File.ReadAllText(Path.Combine(NewDirectory("replay"), "replay.jsonl"));
            Assert.That(lines.Split('\n').Count(l => l.Contains("\"attempt\":4,")), Is.Zero, "the unlisted sample was not replayed");
            Assert.That(lines.Split('\n').Count(l => l.Contains("\"attempt\":3,")) + lines.Split('\n').Count(l => l.Contains("\"attempt\":5,")), Is.EqualTo(2));
            StringAssert.Contains("\"inputMode\":\"compact\",\"graphOn\":false", lines);
            StringAssert.Contains("\"input\":\"", lines, "the input check's own field is kept");
            StringAssert.Contains("\"sessionId\":\"" + id + "\",\"sha256\":\"" + sha[0] + "\"", lines, "the session and the hash on the line");
            StringAssert.Contains("\"graph\":null", lines);

            // missing: a listed attempt the folder has not
            File.WriteAllLines(list, new[] { Line(id, 3, sha[0]), Line(id, 9, "00") });
            summary = SpatialSplitReplay.RunSession(session, NewDirectory("replay-missing"), options, out allSame, out _);
            StringAssert.Contains("entries 2, replayed 1, missing 1, duplicate 0, hash differs from the list 0", summary);
            StringAssert.Contains("list check FAILED", summary);
            Assert.That(allSame, Is.False);

            // duplicate: the same attempt listed twice
            File.WriteAllLines(list, new[] { Line(id, 3, sha[0]), Line(id, 3, sha[0]) });
            summary = SpatialSplitReplay.RunSession(session, NewDirectory("replay-duplicate"), options, out allSame, out _);
            StringAssert.Contains("duplicate 1", summary);
            StringAssert.Contains("list check FAILED", summary);

            // another hash: the listed sample's file is not the one the list means
            File.WriteAllLines(list, new[] { Line(id, 3, sha[1]) });
            summary = SpatialSplitReplay.RunSession(session, NewDirectory("replay-hash"), options, out allSame, out _);
            StringAssert.Contains("replayed 0, missing 0, duplicate 0, hash differs from the list 1", summary, "another hash is its own count, not a missing sample");
            StringAssert.Contains("list check FAILED", summary);

            // an unindexed file is left out when a list is given (it is replayed without one)
            string stray = Path.Combine(session, SpatialSplitCaptureSink.SampleFileName(1, 7));
            File.Copy(Path.Combine(session, SpatialSplitCaptureSink.SampleFileName(1, 4)), stray);
            File.WriteAllLines(list, new[] { Line(id, 3, sha[0]) });
            summary = SpatialSplitReplay.RunSession(session, NewDirectory("replay-stray"), options, out _, out _);
            StringAssert.Contains("files left out (not listed) 3", summary);
            StringAssert.Contains("committed and not in the index (found by the folder) 0", summary);
            summary = SpatialSplitReplay.RunSession(session, NewDirectory("replay-stray-nolist"), null, out _, out _);
            StringAssert.Contains("committed and not in the index (found by the folder) 1", summary);
        }

        [Test]
        public void TheGraphStage_NeedsTheSpanInput_AndTheSpanInput_IsSaidInEveryLine()
        {
            string session = ThreeSamples(out _, out _);
            Assert.Throws<ArgumentException>(() => SpatialSplitReplay.RunSession(session, NewDirectory("graph-compact"), new SpatialSplitReplayOptions { graph = true }, out _, out _));
            Assert.That(Directory.Exists(NewDirectory("graph-compact")), Is.False, "refused before anything was made");
            string summary = SpatialSplitReplay.RunSession(session, NewDirectory("span"), new SpatialSplitReplayOptions { spanInput = true }, out _, out _);
            StringAssert.Contains("options: inputMode span, graph off", summary);
            string lines = File.ReadAllText(Path.Combine(NewDirectory("span"), "replay.jsonl"));
            StringAssert.Contains("\"inputMode\":\"span\",\"graphOn\":false", lines);
            StringAssert.Contains("\"graphStages\":{\"entered\":false,\"led\":false", lines);
            // GRAPH-CONNECT-8: the bent setting as requested, saved and run, the stages' marks, the all-path anchors flag, the ids
            StringAssert.Contains("\"bentRequested\":\"saved\",\"bentSaved\":", lines);
            StringAssert.Contains("\"stagesRan\":{\"plane\":false,\"bent\":false,\"graph\":false,\"meshLed\":false}", lines, "the synthetic sample has no convexes and is not rebuilt: no stage ran, and the marks say so (false), not nothing");
            StringAssert.Contains(",\"anchorsDistributed\":", lines);
            StringAssert.Contains(",\"cut\":false,\"crossingConvexes\":0,\"bentMs\":", lines);
            StringAssert.Contains(",\"fragment\":", lines); StringAssert.Contains(",\"rootFragment\":", lines);
            StringAssert.Contains("options: inputMode span, graph off, bent saved", summary);
            StringAssert.Contains("bent stage (the saved setting): effective in ", summary);
        }

        private static bool Read6(string[] arguments, out SpatialSplitReplayOptions options, out string refusal) =>
            SpatialSplitReplayOptions.TryRead(arguments, "-zantetsuSplitReplayInput", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayList", "-zantetsuSplitReplayBent", "-zantetsuSplitReplayOrder", out options, out refusal);

        /// <summary>GRAPH-CONNECT-10: the order argument is current (the default) or graph-first; graph-first needs the graph on the span input
        /// and is refused together with bent off (B and C are different conditions); a missing or unknown value is refused; the Editor's
        /// names read the same; the five-name reader leaves the order current.</summary>
        [Test]
        public void TheOrderOption_IsReadWithItsRefusals_AndGraphFirstIsNotBentOff()
        {
            Assert.That(Read6(new string[0], out SpatialSplitReplayOptions o, out string refusal), Is.True, refusal);
            Assert.That(o.graphFirst, Is.False); Assert.That(o.OrderName, Is.EqualTo("current"));
            Assert.That(Read6(new[] { "-zantetsuSplitReplayOrder", "current" }, out o, out refusal), Is.True, refusal);
            Assert.That(o.graphFirst, Is.False);
            Assert.That(Read6(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayOrder", "graph-first" }, out o, out refusal), Is.True, refusal);
            Assert.That(o.graphFirst, Is.True); Assert.That(o.OrderName, Is.EqualTo("graph-first")); Assert.That(o.bentOff, Is.False);
            Assert.That(Read6(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayOrder", "graph-first" }, out _, out refusal), Is.False, "graph-first without the graph");
            StringAssert.Contains("graph-first needs", refusal);
            Assert.That(Read6(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayBent", "off", "-zantetsuSplitReplayOrder", "graph-first" }, out _, out refusal), Is.False, "B and C together");
            StringAssert.Contains("not together", refusal);
            Assert.That(Read6(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayOrder" }, out _, out refusal), Is.False, "a value is missing");
            StringAssert.Contains("has no value", refusal);
            Assert.That(Read6(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayOrder", "bent-first" }, out _, out refusal), Is.False);
            StringAssert.Contains("current or graph-first", refusal);
            Assert.That(SpatialSplitReplayOptions.TryRead(new[] { "-splitReplayInput", "span", "-splitReplayGraph", "-splitReplayOrder", "graph-first" }, "-splitReplayInput", "-splitReplayGraph", "-splitReplayList", "-splitReplayBent", "-splitReplayOrder", out o, out refusal), Is.True, refusal);
            Assert.That(o.graphFirst, Is.True);
            Assert.That(Read5(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayOrder", "graph-first" }, out o, out refusal), Is.True, refusal);
            Assert.That(o.graphFirst, Is.False, "not read by the five-name reader");
        }

        private static bool Read5(string[] arguments, out SpatialSplitReplayOptions options, out string refusal) =>
            SpatialSplitReplayOptions.TryRead(arguments, "-zantetsuSplitReplayInput", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayList", "-zantetsuSplitReplayBent", out options, out refusal);

        /// <summary>GRAPH-CONNECT-8: the bent argument is saved (the default and the only value without the comparison) or off; off needs
        /// the graph on the span input; a missing or unknown value is refused; the Editor's names read the same; the four-name reader
        /// leaves the bent setting saved.</summary>
        [Test]
        public void TheBentOption_IsReadWithItsRefusals_AndOffNeedsTheGraphOnTheSpanInput()
        {
            Assert.That(Read5(new string[0], out SpatialSplitReplayOptions o, out string refusal), Is.True, refusal);
            Assert.That(o.bentOff, Is.False); Assert.That(o.BentName, Is.EqualTo("saved"));
            Assert.That(Read5(new[] { "-zantetsuSplitReplayBent", "saved" }, out o, out refusal), Is.True, refusal);
            Assert.That(o.bentOff, Is.False);
            Assert.That(Read5(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayBent", "off" }, out o, out refusal), Is.True, refusal);
            Assert.That(o.bentOff, Is.True); Assert.That(o.BentName, Is.EqualTo("off"));
            Assert.That(Read5(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayBent", "off" }, out _, out refusal), Is.False, "off without the graph");
            StringAssert.Contains("off needs", refusal);
            Assert.That(Read5(new[] { "-zantetsuSplitReplayInput", "compact", "-zantetsuSplitReplayBent", "off" }, out _, out refusal), Is.False, "off without the graph and the span input");
            Assert.That(Read5(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayBent" }, out _, out refusal), Is.False, "a value is missing");
            StringAssert.Contains("has no value", refusal);
            Assert.That(Read5(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayBent", "maybe" }, out _, out refusal), Is.False);
            StringAssert.Contains("saved or off", refusal);
            Assert.That(Read5(new[] { "-zantetsuSplitReplayBent", "-zantetsuSplitReplayGraph" }, out _, out refusal), Is.False, "another switch in the value's place");
            // the Editor's names, the same reader
            Assert.That(SpatialSplitReplayOptions.TryRead(new[] { "-splitReplayInput", "span", "-splitReplayGraph", "-splitReplayBent", "off" }, "-splitReplayInput", "-splitReplayGraph", "-splitReplayList", "-splitReplayBent", out o, out refusal), Is.True, refusal);
            Assert.That(o.bentOff, Is.True);
            Assert.That(SpatialSplitReplayOptions.TryRead(new[] { "-splitReplayBent" }, "-splitReplayInput", "-splitReplayGraph", "-splitReplayList", "-splitReplayBent", out _, out refusal), Is.False);
            // the four-name reader (the Player before this unit, the tests): the bent argument is not read, the setting stays saved
            Assert.That(Read(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayBent", "off" }, out o, out refusal), Is.True, refusal);
            Assert.That(o.bentOff, Is.False, "not read by the four-name reader");
        }

        private static bool Read(string[] arguments, out SpatialSplitReplayOptions options, out string refusal) =>
            SpatialSplitReplayOptions.TryRead(arguments, "-zantetsuSplitReplayInput", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayList", out options, out refusal);

        [Test]
        public void ThePlayersOptions_AreReadWithTheirRefusals()
        {
            Assert.That(Read(new string[0], out SpatialSplitReplayOptions o, out string refusal), Is.True, refusal);
            Assert.That(o.spanInput, Is.False); Assert.That(o.graph, Is.False); Assert.That(o.listPath, Is.Null);
            Assert.That(Read(new[] { "-zantetsuSplitReplayInput", "span", "-zantetsuSplitReplayGraph", "-zantetsuSplitReplayList", Path.Combine(_root, "l.jsonl") }, out o, out refusal), Is.True, refusal);
            Assert.That(o.spanInput, Is.True); Assert.That(o.graph, Is.True); Assert.That(o.listPath, Is.EqualTo(Path.Combine(_root, "l.jsonl")));
            Assert.That(Read(new[] { "-zantetsuSplitReplayInput", "compact" }, out o, out refusal), Is.True, refusal);
            Assert.That(o.spanInput, Is.False);
            Assert.That(Read(new[] { "-zantetsuSplitReplayGraph" }, out _, out refusal), Is.False, "the graph needs the span input");
            StringAssert.Contains("span", refusal);
            Assert.That(Read(new[] { "-zantetsuSplitReplayInput", "other" }, out _, out refusal), Is.False);
            StringAssert.Contains("compact or span", refusal);
            Assert.That(Read(new[] { "-zantetsuSplitReplayInput" }, out _, out refusal), Is.False, "a value is missing");
            Assert.That(Read(new[] { "-zantetsuSplitReplayList", "relative\\list.jsonl" }, out _, out refusal), Is.False);
            StringAssert.Contains("absolute", refusal);
            Assert.That(Read(new[] { "-zantetsuSplitReplayList", "-zantetsuSplitReplayGraph" }, out _, out refusal), Is.False, "another switch in the value's place");
        }
    }
}
