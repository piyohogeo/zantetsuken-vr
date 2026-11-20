using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The saved piece and its replay (<see cref="ConvexTrimCapture"/>, <see cref="ConvexTrimCaptureReplay"/>, 2026-11-20):
    /// a piece at rest on a convex that reaches below what is drawn of it is saved with what it collides with and with
    /// what the game measured under it, read back from its file, and replayed in a physics scene of its own -- as saved it
    /// stays where the game had it, trimmed it comes down to what is drawn. A collider the piece does not collide with
    /// (the Player's layer) is counted and left out; a support whose mesh cannot be read is saved as the rays found it.
    /// And the replay of a folder of pieces saved during play (<see cref="TrimCapturesVariable"/>), which is how those are
    /// evaluated; ignored unless the folder is given.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        /// <summary>The environment variable that names a folder of saved pieces (float-*.json, in it or below it) to replay; without it that case is ignored.</summary>
        internal const string TrimCapturesVariable = "ZANTETSU_CONVEX_TRIM_CAPTURES";

        // The bar cut in two over the floor, the side of larger x held by its anchor, the other let go and asleep on its
        // convex's underside: half a metre of air under what is drawn of it.
        private IEnumerator ABarAsleepOnItsExcessConvex(CutWorldRoot root, LogicalFragmentId[] sides)
        {
            SplitOpen(root);
            var at = new Vector3(0f, TrimFloorTop + 0.05f + k_barHalf.y + BarExcessBelow, 0f);
            LogicalFragmentId bar = AddBarOnAFatConvex(root, at, new[] { new float3(0.8f, 0f, 0f) });
            yield return CutTheBarAcross(root, bar, 0.6f, sides);
            Assert.That(root.Owners.TryGet(sides[1], out PhysicsFragmentOwner free), Is.True);
            Assert.That(root.Owners.TryGet(sides[0], out PhysicsFragmentOwner held), Is.True);
            Assert.That(held.FixedByAnchors && held.Body.isKinematic, Is.True, "the side with the anchor is fixed");
            free.Body.useGravity = true;
            free.Body.WakeUp();
            yield return Until(() => free.Body.IsSleeping() && LowestConvexY(free) < TrimFloorTop + 0.03f, "the free side rests on the floor and sleeps");
        }

        [UnityTest]
        public IEnumerator ConvexTrimCapture_APieceRestingOnItsExcessConvex_IsSavedWithWhatItCollidesWith_AndReplayed_AsSavedItStays_TrimmedItComesDown()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            AddTrimFloor();

            // Something of the Player's layer in the air under what is drawn: the piece does not collide with it. Saved as a
            // support it would hold the replayed piece up, and measured against it would make the gap a tenth of a metre.
            int playerLayer = LayerMask.NameToLayer("Player");
            Assert.That(playerLayer, Is.GreaterThanOrEqualTo(0));
            Assert.That(Physics.GetIgnoreLayerCollision(playerLayer, 0), Is.True, "the Player's layer touches nothing in this project");
            GameObject player = Track(new GameObject("Player Part"));
            player.layer = playerLayer;
            var playerBox = player.AddComponent<BoxCollider>();
            playerBox.center = new Vector3(-0.2f, TrimFloorTop + 0.25f, 0f);
            playerBox.size = new Vector3(0.4f, 0.3f, 0.4f);

            var sides = new LogicalFragmentId[2];
            yield return ABarAsleepOnItsExcessConvex(root, sides);

            ConvexTrimCapture taken = ConvexTrimCapture.Take(root, sides[1], "a test", 0.5f, out string refusal);
            Assert.That(taken, Is.Not.Null, refusal);
            Assert.That(taken.convexes.Length, Is.EqualTo(1));
            Assert.That(taken.convexes[0].vertices.Length, Is.EqualTo(8));
            Assert.That(taken.meshIndices.Length % 3, Is.Zero);
            Assert.That(taken.meshIndices.Max(), Is.EqualTo(taken.meshPositions.Length - 1), "the mesh is the vertices its indices name, numbered again");
            Assert.That(taken.sleeping, Is.True);
            Assert.That(taken.Resting, Is.True);
            Assert.That(taken.productPass, Is.EqualTo(nameof(ConvexTrimPass.Candidate)), "the product's trim would look at it");
            Assert.That(taken.supportsNear, Is.EqualTo(3), "the floor, the other side and the Player's part");
            Assert.That(taken.supportsNotColliding, Is.EqualTo(1));
            Assert.That(taken.notColliding.Single(), Does.StartWith("Player Part (layer " + playerLayer + ")"));
            Assert.That(taken.supportsDropped, Is.Zero);
            Assert.That(taken.supports.Length, Is.EqualTo(2), "what it does not collide with is not saved");
            Assert.That(taken.supports.All(s => s.exact && s.name != "Player Part"), Is.True);
            Assert.That(taken.supports.Count(s => s.kind == "box" && !s.hasBody), Is.EqualTo(1), "the floor");
            Assert.That(taken.supports.Count(s => s.kind == "convex" && s.fragment == sides[0].value && s.kinematic), Is.EqualTo(1), "the other side, held, as a fragment's convex");
            Assert.That(taken.supports[0].distance, Is.LessThanOrEqualTo(taken.supports[1].distance), "the nearest first");
            Assert.That(taken.measured, Is.True);
            Assert.That(taken.measuredClearance, Is.EqualTo(BarExcessBelow).Within(0.03f), "the game's scene measured the gap to the floor, past the Player's part");
            Assert.That(taken.measuredAgainst, Is.EqualTo("Floor"));
            Assert.That(taken.measuredAgainstSupport, Is.GreaterThanOrEqualTo(0));
            Assert.That(taken.supports[taken.measuredAgainstSupport].kind, Is.EqualTo("box"));
            Assert.That(taken.gravity, Is.EqualTo(Physics.gravity));

            string folder = Path.Combine(Application.temporaryCachePath, "convex-trim-capture-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = ConvexTrimCaptureReplay.Write(taken, folder, 1);
                ConvexTrimCapture read = ConvexTrimCapture.FromJson(File.ReadAllText(path));
                Vector3 gravity = Physics.gravity;
                ConvexTrimReplayResult result = ConvexTrimCaptureReplay.Run(read, Path.GetFileName(path), CutConvexTrimSettings.Default, 128);
                TestContext.Out.WriteLine(result.Describe());
                Assert.That(Physics.gravity, Is.EqualTo(gravity), "the replay puts the gravity back");
                Assert.That(result.large && result.free && result.resting, Is.True);
                Assert.That(result.tier, Is.EqualTo("A"), string.Join("; ", result.reasons));
                Assert.That(result.supportsExactFixed, Is.EqualTo(2));
                Assert.That(result.supportsAsBounds + result.supportsApproximate + result.supportsMoving + result.supportsAsleep + result.supportsDropped, Is.Zero);
                Assert.That(result.measuredAgainstSaved, Is.True);
                Assert.That(result.savedClearance, Is.EqualTo(taken.measuredClearance).Within(1e-3f), "the replay's scene measures at the saved pose what the game measured");
                Assert.That(result.asSaved.slept, Is.True);
                Assert.That(result.asSaved.clearance, Is.EqualTo(BarExcessBelow).Within(0.03f), "as saved it floats still");
                Assert.That(result.asSaved.moved, Is.LessThan(0.02f), "and stays where it was saved");
                Assert.That(result.offReproduced, Is.True, "the replay stands as the game stood");
                Assert.That(result.asSaved.reachDownAtSaved, Is.EqualTo(BarExcessBelow).Within(1e-3f));
                Assert.That(result.verticesBeforeInAll, Is.EqualTo(8));
                Assert.That(result.verticesAfterInAll, Is.EqualTo(8));
                Assert.That(result.trimsInAll, Is.EqualTo(1));
                Assert.That(result.reach26Before, Is.EqualTo(BarExcessBelow).Within(1e-3f));
                Assert.That(result.reach26After, Is.LessThan(1e-3f));
                Assert.That(result.trimmed, Is.Not.Null);
                Assert.That(result.trimmed.slept, Is.True);
                Assert.That(result.trimmed.reachDownAtSaved, Is.LessThan(1e-3f));
                Assert.That(result.trimmed.clearance, Is.EqualTo(0f).Within(0.03f), "trimmed, it rests on what is drawn");
                Assert.That(result.trimCameDown, Is.EqualTo(BarExcessBelow).Within(0.03f), "having come down by what was cut off");
                Assert.That(result.trimSank, Is.False);

                // and the folder's replay, as the pieces saved during play are evaluated
                var all = new List<ConvexTrimReplayResult>();
                yield return ReplayTrimCaptures(folder, CutConvexTrimSettings.Default, all);
                Assert.That(all.Count, Is.EqualTo(1));
                Assert.That(all[0].trimCameDown, Is.EqualTo(BarExcessBelow).Within(0.03f));
                string text = File.ReadAllText(Path.Combine(folder, "replay.txt"));
                Assert.That(text, Does.Contain("tier A").And.Contain("stood as the game stood for 1; of them trimmed 1: lower by more than 5 mm 1, sank 0"));
                Assert.That(File.ReadAllText(Path.Combine(folder, "replay.json")), Does.Contain("\"trimCameDown\""));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }

            yield return null;
            yield return EndWorld(root);
        }

        /// <summary>
        /// A support whose mesh cannot be read (a Release Player's scenery) is saved as rays from above found it under the
        /// piece -- a stand-in, said to be one -- and the replay on it still stands as the game stood.
        /// </summary>
        [UnityTest]
        public IEnumerator ConvexTrimCapture_AFloorWhoseMeshCannotBeRead_IsSavedAsTheRaysFoundIt_AStandIn_AndTheReplayStandsAsTheGameStood()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            GameObject floor = Track(new GameObject("Mesh Floor"));
            var mesh = new Mesh { name = "Mesh Floor", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[] { new Vector3(-40f, TrimFloorTop, -40f), new Vector3(-40f, TrimFloorTop, 40f), new Vector3(40f, TrimFloorTop, 40f), new Vector3(40f, TrimFloorTop, -40f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            floor.AddComponent<MeshCollider>().sharedMesh = mesh;
            try
            {
                var sides = new LogicalFragmentId[2];
                yield return ABarAsleepOnItsExcessConvex(root, sides);
                ConvexTrimCapture.TestMeshesUnreadable = true;   // the Editor keeps every mesh readable
                ConvexTrimCapture taken = ConvexTrimCapture.Take(root, sides[1], "a test", 0.5f, out string refusal);
                ConvexTrimCapture.TestMeshesUnreadable = false;
                Assert.That(taken, Is.Not.Null, refusal);
                Assert.That(taken.supports.Single(s => s.fragment == sides[0].value).kind, Is.EqualTo("convex"), "a fragment's convex is read from its B-rep where its Mesh cannot be");
                Assert.That(taken.supports.Single(s => s.fragment == sides[0].value).exact, Is.True);
                ConvexTrimCaptureSupport saved = taken.supports.Single(s => s.name == "Mesh Floor");
                Assert.That(saved.kind, Is.EqualTo("heightfield"));
                Assert.That(saved.exact, Is.False);
                Assert.That(saved.substitution, Does.Contain("cannot be read"));
                Assert.That(saved.heights.All(h => Mathf.Abs(h - TrimFloorTop) < 1e-3f), Is.True, "every ray found the floor");
                Assert.That(taken.measuredAgainst, Is.EqualTo("Mesh Floor"));
                Assert.That(taken.measuredClearance, Is.EqualTo(BarExcessBelow).Within(0.03f));

                ConvexTrimReplayResult result = ConvexTrimCaptureReplay.Run(ConvexTrimCapture.FromJson(taken.ToJson()), "a floor that cannot be read", CutConvexTrimSettings.Default, 128);
                TestContext.Out.WriteLine(result.Describe());
                Assert.That(result.tier, Is.EqualTo("B"), string.Join("; ", result.reasons));
                Assert.That(result.supportsApproximate, Is.EqualTo(1));
                Assert.That(result.supportsAsBounds, Is.Zero);
                Assert.That(result.offReproduced, Is.True);
                Assert.That(result.trimCameDown, Is.EqualTo(BarExcessBelow).Within(0.03f));
                Assert.That(result.trimSank, Is.False);
            }
            finally
            {
                ConvexTrimCapture.TestMeshesUnreadable = false;
                UnityEngine.Object.Destroy(mesh);
            }

            yield return null;
            yield return EndWorld(root);
        }

        /// <summary>
        /// With the trim on, a saved piece carries the trim's own account of it -- where it stands, how its attempt ended,
        /// the vertices and the reach before and after -- so that a float still there can be told apart: the trim never
        /// came to it, or it came and the shape still reaches. And the trim's records are written beside the pieces.
        /// </summary>
        [UnityTest]
        public IEnumerator ConvexTrimCapture_WithTheTrimOn_ThePieceIsSavedWithWhatTheTrimMadeOfIt()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            AddTrimFloor();
            var sides = new LogicalFragmentId[2];
            yield return ABarAsleepOnItsExcessConvex(root, sides);

            ConvexTrimCapture off = ConvexTrimCapture.Take(root, sides[1], "the trim off", 0.5f, out string refusal);
            Assert.That(off, Is.Not.Null, refusal);
            Assert.That(off.trimEnabled, Is.False);
            Assert.That(off.trimState, Is.EqualTo(nameof(ConvexTrimPass.Candidate)));
            Assert.That(off.trimHasRecord, Is.False);

            CutConvexTrim trim = TrimOn(root);
            bool room = false;
            trim.SplitLeavesRoom = () => room;
            string logFolder = Path.Combine(Application.temporaryCachePath, "convex-trim-log-" + Guid.NewGuid().ToString("N"));
            var log = new ConvexTrimRecordLog(trim, logFolder);
            for (int i = 0; i < 5; i++) yield return null;
            ConvexTrimCapture waiting = ConvexTrimCapture.Take(root, sides[1], "the trim on, no turn yet", 0.5f, out refusal);
            Assert.That(waiting.trimEnabled, Is.True);
            Assert.That(waiting.trimState, Is.EqualTo("Queued"), "the split keeps the frames: the fragment waits for its look");
            Assert.That(ConvexTrimCaptureReplay.Run(waiting, "waiting", CutConvexTrimSettings.Default, 128).gameTrimKind, Is.EqualTo("no turn yet"));

            room = true;
            Assert.That(root.Owners.TryGet(sides[1], out PhysicsFragmentOwner free), Is.True);
            yield return Until(() => trim.Published == 2 && trim.IsIdle && free.Body.IsSleeping(), "both sides were trimmed and the free one rests again. " + trim.Describe());
            ConvexTrimCapture taken = ConvexTrimCapture.Take(root, sides[1], "the trim on, published", 0.5f, out refusal);
            Assert.That(taken, Is.Not.Null, refusal);
            Assert.That(taken.trimState, Is.EqualTo(nameof(ConvexTrimPass.Done)));
            Assert.That(taken.trimMarkedEnd, Is.EqualTo(nameof(ConvexTrimEnd.Published)));
            Assert.That(taken.trimHasRecord, Is.True);
            Assert.That(taken.trimEnd, Is.EqualTo(nameof(ConvexTrimEnd.Published)));
            Assert.That(taken.trimVerticesBefore, Is.EqualTo(8));
            Assert.That(taken.trimVerticesAfter, Is.EqualTo(8));
            Assert.That(taken.trimTrims, Is.EqualTo(1));
            Assert.That(taken.trimReachBefore, Is.EqualTo(BarExcessBelow).Within(1e-3f));
            Assert.That(taken.trimReachAfter, Is.LessThan(1e-3f));
            Assert.That(taken.trimEndedFrame, Is.LessThanOrEqualTo(taken.frame));
            Assert.That(taken.measuredClearance, Is.EqualTo(0f).Within(0.03f), "trimmed in the game, it rests on what is drawn");
            ConvexTrimReplayResult result = ConvexTrimCaptureReplay.Run(ConvexTrimCapture.FromJson(taken.ToJson()), "published", CutConvexTrimSettings.Default, 128);
            TestContext.Out.WriteLine(result.Describe());
            Assert.That(result.gameTrimOn, Is.True);
            Assert.That(result.gameTrimKind, Is.EqualTo("published"));
            Assert.That(result.trimsInAll, Is.Zero, "nothing is left to trim of a shape the game trimmed");
            Assert.That(result.reach26Before, Is.LessThan(1e-3f));

            // the trim's times of a frame and of the wait for a worker were taken
            Assert.That(trim.Cost(ConvexTrimCostKind.MainFrame).Count, Is.GreaterThan(0));
            Assert.That(trim.Cost(ConvexTrimCostKind.WorkerWait).Count, Is.EqualTo(4), "a search and a bake a side");
            Assert.That(trim.MostFrameStages, Is.Not.EqualTo(CutConvexTrim.FrameStages.None));
            Assert.That(trim.MostFrameMainSeconds, Is.GreaterThanOrEqualTo(trim.MostMainStepSeconds));

            // every attempt went to the log as it ended, whatever the trim itself still keeps
            try
            {
                Assert.That(log.Lines, Is.EqualTo(2));
                log.WriteTallies();
                string[] logged = File.ReadAllLines(log.Path);
                Assert.That(logged[0], Is.EqualTo(ConvexTrimCaptureReplay.TrimRecordHeads));
                Assert.That(logged[1], Does.StartWith("# a world's trim").And.Contain("every attempt follows"));
                Assert.That(logged.Length, Is.EqualTo(2 + 2));
                Assert.That(logged.Skip(2).Select(l => l.Split('\t')[2]), Is.All.EqualTo("Published"));
                Assert.That(File.ReadAllText(Path.Combine(logFolder, "trim-tallies.txt")), Does.Contain("attempts in trim-records.txt 2").And.Contain("published 2"));
                log.Dispose();
                var later = new ConvexTrimRecordLog(trim, logFolder);   // a log made later takes what the trim still keeps, after the first's lines
                later.Dispose();
                Assert.That(File.ReadAllLines(log.Path).Length, Is.EqualTo(4 + 1 + 2));
            }
            finally
            {
                log.Dispose();
                if (Directory.Exists(logFolder)) Directory.Delete(logFolder, true);
            }

            string folder = Path.Combine(Application.temporaryCachePath, "convex-trim-records-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = ConvexTrimCaptureReplay.WriteTrimRecords(root, folder);
                string[] lines = File.ReadAllLines(path);
                Assert.That(lines.Length, Is.EqualTo(2 + 2), "the tallies, the heads, and an attempt a side");
                Assert.That(lines.Count(l => l.Split('\t').Length > 3 && l.Split('\t')[1] == sides[1].value.ToString(CultureInfo.InvariantCulture) && l.Split('\t')[2] == "Published"), Is.EqualTo(1));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }

            yield return EndWorld(root);
        }

        /// <summary>
        /// The evaluation of pieces saved during play: every float-*.json under the folder is replayed -- as saved and
        /// trimmed -- and what came of each is written beside them (replay.json, replay.txt) and to the test's output.
        /// </summary>
        [UnityTest, Timeout(3600000)]
        public IEnumerator ConvexTrimCaptureReplay_OfAFolderOfPiecesSavedDuringPlay()
        {
            string folder = Environment.GetEnvironmentVariable(TrimCapturesVariable);
            if (string.IsNullOrEmpty(folder)) Assert.Ignore("an evaluation: run with " + TrimCapturesVariable + " = a folder of saved pieces");
            Assert.That(Directory.Exists(folder), Is.True, folder);
            CutConvexTrimSettings settings = CutConvexTrimSettings.Default;
            string overhang = Environment.GetEnvironmentVariable(TrimEvalOverhangVariable);
            if (!string.IsNullOrEmpty(overhang)) settings.minOverhangMeters = float.Parse(overhang, CultureInfo.InvariantCulture);
            var all = new List<ConvexTrimReplayResult>();
            yield return ReplayTrimCaptures(folder, settings, all);
            Assert.That(all.Count, Is.GreaterThan(0), "no piece of the folder could be replayed; see replay.txt in " + folder);
        }

        // Replays every float-*.json under the folder, one a frame, and writes replay.txt (the table, then a line a piece) and replay.json beside them.
        private static IEnumerator ReplayTrimCaptures(string folder, CutConvexTrimSettings settings, List<ConvexTrimReplayResult> results)
        {
            string[] files = Directory.GetFiles(folder, "float-*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.That(files.Length, Is.GreaterThan(0), "no float-*.json under " + folder);
            var lines = new StringBuilder();
            var all = new List<string>();
            foreach (string file in files)
            {
                string name = file.Substring(folder.Length).TrimStart('\\', '/');
                ConvexTrimReplayResult result = null;
                try
                {
                    result = ConvexTrimCaptureReplay.Run(ConvexTrimCapture.FromJson(File.ReadAllText(file)), name, settings, 128);
                    lines.AppendLine(result.Describe());
                    all.Add(JsonUtility.ToJson(result));
                }
                catch (Exception e)
                {
                    lines.AppendLine(name + ": NOT REPLAYED: " + e.GetType().Name + ": " + e.Message);
                }

                if (result != null) results.Add(result);
                yield return null;   // the replay's scene is unloaded between two pieces
            }

            string text = "reach threshold " + settings.minOverhangMeters.ToString("R", CultureInfo.InvariantCulture) + " m, size " + settings.minSizeMeters.ToString("R", CultureInfo.InvariantCulture)
                + " m; " + files.Length + " files, replayed " + results.Count + Environment.NewLine + ConvexTrimCaptureReplay.Summarise(results) + Environment.NewLine + lines;
            File.WriteAllText(Path.Combine(folder, "replay.txt"), text);
            File.WriteAllText(Path.Combine(folder, "replay.json"), "[" + string.Join(",\n", all) + "]");
            TestContext.Out.WriteLine(text);
        }
    }
}
