using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The convex trim on the walk city's tree_012 through the product's own path (2026-11-20): the level cut at 6 m of the
    /// preliminary calculation, and the evaluation that cuts the tree at several places, lays each free piece on the floor
    /// in several poses and measures the gap under what is drawn -- once with the trim off and once with it on, each in a
    /// world of its own, written as numbers to a folder (<see cref="TrimEvalVariable"/>). Ignored where the licensed input
    /// is absent; the evaluation is ignored unless its folder is given.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        /// <summary>The environment variable that names the evaluation's output folder (absolute); without it the evaluation's two cases are ignored.</summary>
        internal const string TrimEvalVariable = "ZANTETSU_CONVEX_TRIM_EVAL";

        private static void SplitOff(CutWorldRoot root)
        {
            CutSpatialSplitSettings off = root.SpatialSplit.Settings;
            off.enabled = false;
            root.SpatialSplit.Settings = off;
        }

        /// <summary>
        /// **tree_012 cut level at 6 m, through the product's cut and the trim.** With the additional split off -- so that the
        /// side above stays one fragment, as the numbers alone have it -- the side above is trimmed from 159 vertices to 168
        /// (40 to 37, 54 as it was, 26 to 30, 39 to 47), its largest reach beyond its mesh from 0.413 m to under 0.1 m; the
        /// side below, which holds the anchor, is trimmed too and stays fixed where it stood. The trimmed top is then cut
        /// again, and its children do not reach beyond their meshes by the threshold either.
        /// </summary>
        [UnityTest]
        public IEnumerator ConvexTrim_Tree012CutLevelAtSixMetres_IsTrimmedAsTheNumbersAloneHaveIt_TheAnchoredSideStays_AndTheTopIsCutAgain()
        {
            if (!File.Exists(CompoundTreeInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + CompoundTreeInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CompoundTreeInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            SplitOpen(root);
            SplitOff(root);
            GameObject instance = TrackActor(new GameObject("tree_012 instance"));
            instance.transform.position = new Vector3(0f, -1f, 0f);
            PlacedCuttableRegistration made = PlacedCuttableRegistration.Register(root, data, instance.transform, new Renderer[0], new Collider[0], 50f, false);
            _actors.Add(made.Actor);
            CutConvexTrim trim = TrimOn(root);
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, made.Fragment, new float4(0f, 1f, 0f, -6f), sides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            Assert.That(root.Owners.TryGet(sides[0], out PhysicsFragmentOwner upper), Is.True);
            Assert.That(root.Owners.TryGet(sides[1], out PhysicsFragmentOwner lower), Is.True);
            Assert.That(lower.FixedByAnchors && lower.Body.isKinematic, Is.True, "the side below holds the anchor");
            Assert.That(upper.FixedByAnchors || upper.Body.isKinematic, Is.False, "the side above is free");
            upper.Body.isKinematic = true;   // held where it was cut: this case is about the shapes, not the fall
            Vector3 lowerAt = lower.Root.transform.position;
            yield return Until(() => trim.IsIdle && RecordOf(trim, sides[0], ConvexTrimEnd.Published) != null && RecordOf(trim, sides[1], ConvexTrimEnd.Published) != null,
                "both sides were trimmed");
            ConvexTrimRecord top = RecordOf(trim, sides[0], ConvexTrimEnd.Published), stump = RecordOf(trim, sides[1], ConvexTrimEnd.Published);
            string perConvex = string.Join(", ", Enumerable.Range(0, top.convexes).Select(c => top.verticesBefore[c] + "->" + top.verticesAfter[c] + " (" + top.trims[c] + ")"));
            TestContext.Out.WriteLine("the product's path, the side above: vertices " + top.verticesBeforeInAll + " -> " + top.verticesAfterInAll + ", reach "
                + top.overhangBefore.ToString("F4") + " -> " + top.overhangAfter.ToString("F4") + " m, trims " + top.trimsInAll + "; per convex: " + perConvex);
            TestContext.Out.WriteLine("the side below: vertices " + stump.verticesBeforeInAll + " -> " + stump.verticesAfterInAll + ", reach "
                + stump.overhangBefore.ToString("F4") + " -> " + stump.overhangAfter.ToString("F4") + " m, trims " + stump.trimsInAll);
            TestContext.Out.WriteLine(trim.Describe());

            // The numbers alone (ConvexTrimSearchTests, and the preliminary calculation): the same through the product's cut.
            Assert.That(top.verticesBeforeInAll, Is.EqualTo(159), "the product's cut leaves the side above the same convexes as the numbers alone");
            Assert.That(top.overhangBefore, Is.EqualTo(0.413f).Within(0.0005f));
            Assert.That(top.verticesAfterInAll, Is.EqualTo(168));
            Assert.That(top.trimsInAll, Is.EqualTo(7));
            Assert.That(top.verticesBefore.OrderBy(v => v).ToArray(), Is.EqualTo(new[] { 26, 39, 40, 54 }));
            Assert.That(top.verticesAfter.OrderBy(v => v).ToArray(), Is.EqualTo(new[] { 30, 37, 47, 54 }));
            Assert.That(top.overhangAfter, Is.LessThan(0.1f));
            Assert.That(stump.verticesBeforeInAll, Is.EqualTo(339));
            Assert.That(stump.verticesAfterInAll, Is.EqualTo(347));
            Assert.That(stump.overhangAfter, Is.LessThan(0.1f));

            // What the owners are now is what the records say, and what the solver touches is what the owners are.
            Assert.That(Enumerable.Range(0, upper.Shape.ConvexCount).Sum(c => upper.Shape.Convex(c).vertexCount), Is.EqualTo(168));
            AssertCollidersAreTheShapes(upper, "the side above");
            AssertCollidersAreTheShapes(lower, "the side below");
            for (int k = 0; k < ConvexTrimSearch.Directions; k++)
            {
                Assert.That(ReachBeyondMesh(root, sides[0], upper, ConvexTrimSearch.Direction(k)), Is.LessThan(0.1f), "the side above, direction " + k);
                Assert.That(ReachBeyondMesh(root, sides[1], lower, ConvexTrimSearch.Direction(k)), Is.LessThan(0.1f), "the side below, direction " + k);
            }

            Assert.That(lower.FixedByAnchors && lower.Body.isKinematic, Is.True, "the side below is fixed still");
            Assert.That(lower.Root.transform.position, Is.EqualTo(lowerAt), "where it stood");

            // The trimmed top, cut again at 6.8 m of the tree's frame.
            var again = new LogicalFragmentId[2];
            yield return CutInTwo(root, sides[0], new float4(0f, 1f, 0f, -6.8f), again);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the second cut's record is over");
            yield return Until(() => trim.IsIdle && root.Geometry.TryGetGeometry(again[0], out _) && root.Geometry.TryGetGeometry(again[1], out _), "the children's geometry is committed and they were looked at");
            foreach (LogicalFragmentId child in again)
            {
                Assert.That(root.Owners.TryGet(child, out PhysicsFragmentOwner owner) && owner.Body != null, Is.True);
                AssertCollidersAreTheShapes(owner, "a child of the trimmed top");
                for (int k = 0; k < ConvexTrimSearch.Directions; k++)
                {
                    Assert.That(ReachBeyondMesh(root, child, owner, ConvexTrimSearch.Direction(k)), Is.LessThan(0.1f), "a child of the trimmed top, direction " + k);
                }
            }

            Assert.That(root.GeometryFaults, Is.Zero);
            yield return EndWorld(root);
        }

        // ---- the evaluation --------------------------------------------------------------------------------------------

        [Serializable]
        private sealed class TrimEvalPose
        {
            public string name;
            public Quaternion start;
            public bool slept;
            public Vector3 restPosition;
            public Quaternion restRotation;

            /// <summary>The lowest point of what is drawn above the floor at rest, and how far the convexes reach below what is drawn there (straight down), metres.</summary>
            public float gap, reachDownAtRest;

            /// <summary>With the trim on: the reach straight down of this piece's convexes at the pose the same piece rested in with the trim off (the saved pose), and that pose's own reach and gap.</summary>
            public bool hasOff;
            public float offReachDown, offGap, reachDownAtOffRest;

            /// <summary>With the trim on: put at that saved pose and let go -- how far what is drawn came down, the gap it then rested with, and whether it slept.</summary>
            public float dropFell, dropGap;
            public bool dropSlept;
        }

        [Serializable]
        private sealed class TrimEvalPiece
        {
            public string key;
            public long fragment;
            public bool fixedByAnchors, laid;
            public int convexes, vertices, meshPoints;
            public int[] convexVertices;
            public Vector3 extents;

            /// <summary>The largest reach beyond the mesh over the 26 directions, metres, of the shape as the piece has it at the end.</summary>
            public float reach26;
            public List<TrimEvalPose> poses = new List<TrimEvalPose>();
        }

        [Serializable]
        private sealed class TrimEvalRecord
        {
            public long fragment;
            public string end, note;
            public int convexes, verticesBefore, verticesAfter, candidatesBefore, clipsTried, trims, trimmedConvexes;
            public int[] convexBefore, convexAfter, convexTrims, left;
            public float reachBefore, reachAfter;
            public Vector3 extents;
            public double searchMs, clipMs, bakeMs, issueMs, collectMs, meshMs, hullMs, publishMs;
            public int frames;
        }

        [Serializable]
        private sealed class TrimEvalCase
        {
            public string name;
            public int cuts, cutsAsked;
            public int framesToQuiet, splitPublished, freePieces, piecesNotLaid;
            public bool quiet;
            public List<TrimEvalPiece> pieces = new List<TrimEvalPiece>();
            public List<TrimEvalRecord> records = new List<TrimEvalRecord>();
        }

        [Serializable]
        private sealed class TrimEvalCost
        {
            public string name;
            public int count;
            public double medianMs, maxMs, totalMs;
        }

        [Serializable]
        private sealed class TrimEvalFile
        {
            public bool trimOn;
            public string input, trim, split, splitSeveral, splitTimeline;
            public float minSize, minOverhang;
            public List<TrimEvalCost> costs = new List<TrimEvalCost>();
            public List<TrimEvalCase> cases = new List<TrimEvalCase>();
        }

        // The cuts of a case, in the tree's own frame: the first of the tree itself, each later one of every free piece it
        // crosses (the pieces are held where they were cut, so their frames are the tree's).
        private static readonly (string name, float4[] planes)[] k_trimEvalCuts =
        {
            ("level 4.0 m", new[] { new float4(0f, 1f, 0f, -4f) }),
            ("level 5.0 m", new[] { new float4(0f, 1f, 0f, -5f) }),
            ("level 6.0 m", new[] { new float4(0f, 1f, 0f, -6f) }),
            ("level 7.0 m", new[] { new float4(0f, 1f, 0f, -7f) }),
            ("slanted 30 deg through 5.5 m", new[] { new float4(0.5f, 0.8660254f, 0f, -0.8660254f * 5.5f) }),
            ("level 6.0 m, then upright x = 0", new[] { new float4(0f, 1f, 0f, -6f), new float4(1f, 0f, 0f, 0f) }),
            ("level 5.0 m, then upright x = 0.5, then upright z = -0.5", new[] { new float4(0f, 1f, 0f, -5f), new float4(1f, 0f, 0f, -0.5f), new float4(0f, 0f, 1f, 0.5f) }),
            ("level 4.0 m, then level 6.0 m, then upright x = -0.5", new[] { new float4(0f, 1f, 0f, -4f), new float4(0f, 1f, 0f, -6f), new float4(1f, 0f, 0f, 0.5f) }),
            ("level 5.0 m, then two slanted", new[] { new float4(0f, 1f, 0f, -5f), new float4(0.70710678f, 0.70710678f, 0f, -0.70710678f * 6f), new float4(0f, 0.70710678f, 0.70710678f, -0.70710678f * 6.5f) }),
        };

        /// <summary>The environment variable that gives the evaluation's reach threshold (metres) in place of the default 0.1.</summary>
        internal const string TrimEvalOverhangVariable = "ZANTETSU_CONVEX_TRIM_EVAL_OVERHANG";

        // The poses a piece is laid in: each of the trim's 26 directions of the piece's own frame turned straight down
        // (the one with -y down is the piece as it was cut).
        private static (string name, Quaternion turn)[] TrimEvalPoses()
        {
            var poses = new (string, Quaternion)[ConvexTrimSearch.Directions];
            for (int k = 0; k < poses.Length; k++)
            {
                float3 d = ConvexTrimSearch.Direction(k);
                poses[k] = ("direction " + k.ToString("00", CultureInfo.InvariantCulture) + " (" + math.sign(d.x).ToString("+0;-0;0", CultureInfo.InvariantCulture) + " "
                    + math.sign(d.y).ToString("+0;-0;0", CultureInfo.InvariantCulture) + " " + math.sign(d.z).ToString("+0;-0;0", CultureInfo.InvariantCulture) + ") down",
                    Quaternion.FromToRotation((Vector3)d, Vector3.down));
            }

            return poses;
        }

        private const float TrimEvalStartHeight = 0.6f;
        private const double TrimEvalSettleSeconds = 10.0;

        [UnityTest, Timeout(3600000)]
        public IEnumerator ConvexTrimEvaluation_Tree012_TrimOff()
        {
            yield return RunTrimEvaluation(false);
        }

        [UnityTest, Timeout(3600000)]
        public IEnumerator ConvexTrimEvaluation_Tree012_TrimOn()
        {
            yield return RunTrimEvaluation(true);
        }

        private static string TrimEvalPath(string folder, bool trimOn) => Path.Combine(folder, trimOn ? "trim-on.json" : "trim-off.json");

        private static List<LogicalFragmentId> TrimEvalPiecesOf(CutWorldRoot root, LogicalFragmentId tree)
        {
            var all = new List<LogicalFragmentId>();
            root.Owners.CopyFragmentsTo(all);
            return all.Where(f => root.Ledger.IsCurrentTarget(f) && root.Ledger.TryGetRoot(f, out LogicalFragmentId r) && r == tree
                && root.Owners.TryGet(f, out PhysicsFragmentOwner o) && !o.IsWithdrawn && o.Body != null && o.Shape != null).ToList();
        }

        private static string PieceKey(List<float3> meshInOwner)
        {
            float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
            foreach (float3 p in meshInOwner) { lo = math.min(lo, p); hi = math.max(hi, p); }
            return meshInOwner.Count.ToString(CultureInfo.InvariantCulture) + "|" + lo.x.ToString("F3", CultureInfo.InvariantCulture) + "," + lo.y.ToString("F3", CultureInfo.InvariantCulture) + "," + lo.z.ToString("F3", CultureInfo.InvariantCulture)
                + "|" + hi.x.ToString("F3", CultureInfo.InvariantCulture) + "," + hi.y.ToString("F3", CultureInfo.InvariantCulture) + "," + hi.z.ToString("F3", CultureInfo.InvariantCulture);
        }

        private static List<float3> MeshInOwner(CutWorldRoot root, LogicalFragmentId fragment, PhysicsFragmentOwner owner)
        {
            float4x4 toOwner = (float4x4)owner.GeometryLocalToOwner.Value;
            return MeshPointsOf(root, fragment).Select(p => math.transform(toOwner, p)).ToList();
        }

        private static void Hold(PhysicsFragmentOwner owner)
        {
            if (owner.Body == null || owner.Body.isKinematic) return;
            owner.Body.linearVelocity = Vector3.zero;
            owner.Body.angularVelocity = Vector3.zero;
            owner.Body.isKinematic = true;
        }

        private static void LetGoAt(LogicalFragmentId fragment, PhysicsFragmentOwner owner, Vector3 position, Quaternion rotation)
        {
            owner.Body.isKinematic = false;
            owner.Body.useGravity = true;
            owner.Root.transform.SetPositionAndRotation(position, rotation);
            owner.Body.position = position;
            owner.Body.rotation = rotation;
            owner.Body.linearVelocity = Vector3.zero;
            owner.Body.angularVelocity = Vector3.zero;
            CutPhysicsStep.NotePlacementInputChanged(fragment);   // placed outside a physics step
            owner.Body.WakeUp();
        }

        private static IEnumerator Settle(IReadOnlyList<PhysicsFragmentOwner> owners)
        {
            Physics.SyncTransforms();
            double until = CutPhysicsStep.Clock.PhysicsSeconds + TrimEvalSettleSeconds;
            double least = CutPhysicsStep.Clock.PhysicsSeconds + 0.5;
            float deadline = Time.realtimeSinceStartup + 120f;
            while (Time.realtimeSinceStartup < deadline && CutPhysicsStep.Clock.PhysicsSeconds < until)
            {
                yield return null;
                if (CutPhysicsStep.Clock.PhysicsSeconds >= least && owners.All(o => o.Body.IsSleeping())) break;
            }
        }

        private IEnumerator RunTrimEvaluation(bool trimOn)
        {
            string folder = Environment.GetEnvironmentVariable(TrimEvalVariable);
            if (string.IsNullOrEmpty(folder)) Assert.Ignore("an evaluation: run with " + TrimEvalVariable + " = an absolute output folder");
            if (!File.Exists(CompoundTreeInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + CompoundTreeInputPath);
            Directory.CreateDirectory(folder);
            TrimEvalFile off = null;
            if (trimOn && File.Exists(TrimEvalPath(folder, false))) off = JsonUtility.FromJson<TrimEvalFile>(File.ReadAllText(TrimEvalPath(folder, false)));
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CompoundTreeInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            CutSpatialSplit split = SplitOpen(root);   // the additional split as the product has it (on), its budget open
            CutConvexTrim trim = root.ConvexTrim;
            if (trimOn) trim = TrimOn(root);
            string overhang = Environment.GetEnvironmentVariable(TrimEvalOverhangVariable);
            if (!string.IsNullOrEmpty(overhang))
            {
                CutConvexTrimSettings given = trim.Settings;
                given.minOverhangMeters = float.Parse(overhang, CultureInfo.InvariantCulture);
                trim.Settings = given;
            }

            var file = new TrimEvalFile { trimOn = trimOn, input = CompoundTreeInputPath, minSize = trim.Settings.minSizeMeters, minOverhang = trim.Settings.minOverhangMeters };

            // A floor wide enough that a piece that rolls stays on it, its top where the world's own floor has it; and the
            // lanes pieces are laid in: a grid from the middle outwards, clear of the trees' row.
            GameObject wide = Track(new GameObject("Wide floor"));
            var wideBox = wide.AddComponent<BoxCollider>();
            wideBox.center = new Vector3(0f, TrimFloorTop - 0.5f, 0f);
            wideBox.size = new Vector3(600f, 1f, 600f);
            var lanes = new List<Vector3>();
            for (int z = 0; z < 7; z++)
            {
                for (int x = 0; x < 8; x++) lanes.Add(new Vector3(12f * (((x + 1) / 2) * ((x & 1) == 0 ? 1 : -1)), 0f, 10f + (12f * z)));
            }

            for (int i = 0; i < k_trimEvalCuts.Length; i++)
            {
                var result = new TrimEvalCase { name = k_trimEvalCuts[i].name, cuts = k_trimEvalCuts[i].planes.Length };
                file.cases.Add(result);
                GameObject instance = TrackActor(new GameObject("tree_012 instance " + i));
                instance.transform.position = new Vector3(-48f + (12f * i), TrimFloorTop, -34f);
                PlacedCuttableRegistration made = PlacedCuttableRegistration.Register(root, data, instance.transform, new Renderer[0], new Collider[0], 50f, false);
                _actors.Add(made.Actor);
                int recordsBefore = trim.Records.Count, publishedBefore = split.Published, frame = Time.frameCount;
                bool quietAll = true;
                for (int cut = 0; cut < k_trimEvalCuts[i].planes.Length; cut++)
                {
                    float4 plane = k_trimEvalCuts[i].planes[cut];
                    if (cut == 0)
                    {
                        var sides = new LogicalFragmentId[2];
                        yield return CutInTwo(root, made.Fragment, plane, sides);
                        result.cutsAsked++;
                    }
                    else
                    {
                        // every free piece the plane crosses by 5 cm or more on both sides
                        foreach (LogicalFragmentId f in TrimEvalPiecesOf(root, made.Fragment))
                        {
                            if (!root.Owners.TryGet(f, out PhysicsFragmentOwner o) || o.FixedByAnchors || o.GeometryLocalToOwner == null || !root.Geometry.TryGetGeometry(f, out _)) continue;
                            float least = float.MaxValue, most = float.MinValue;
                            foreach (float3 p in MeshInOwner(root, f, o))
                            {
                                float d = math.dot(plane.xyz, p) + plane.w;
                                least = math.min(least, d);
                                most = math.max(most, d);
                            }

                            if (least > -0.05f || most < 0.05f) continue;
                            ProvisionalCutAsk ask = Ask(f, plane);
                            if (root.TryAsk(in ask)) result.cutsAsked++;
                        }

                        yield return null;
                        yield return null;
                    }

                    // Until nothing more is to come of this cut: its record over, the split with nothing to do, the trim idle
                    // -- for a stretch of frames. The free pieces are held where they are meanwhile.
                    int quietFrames = 0;
                    float deadline = Time.realtimeSinceStartup + 180f;
                    while (quietFrames < 30 && Time.realtimeSinceStartup < deadline)
                    {
                        foreach (LogicalFragmentId f in TrimEvalPiecesOf(root, made.Fragment))
                        {
                            if (root.Owners.TryGet(f, out PhysicsFragmentOwner o) && !o.FixedByAnchors) Hold(o);
                        }

                        bool quiet = root.Driver.Transactions.Count == 0 && !split.HasAttempt && split.FoundWaiting == 0 && split.DeferredNow == 0
                            && (split.LastNoIssue == SpatialSplitNoIssue.Idle || split.LastNoIssue == SpatialSplitNoIssue.NoCandidate || split.LastNoIssue == SpatialSplitNoIssue.TooLargeAlone)
                            && (!trimOn || trim.IsIdle)
                            && TrimEvalPiecesOf(root, made.Fragment).All(f => root.Geometry.TryGetGeometry(f, out _) || root.Geometry.HasNoGeometry(f));
                        quietFrames = quiet ? quietFrames + 1 : 0;
                        yield return null;
                    }

                    quietAll &= quietFrames >= 30;
                }

                result.quiet = quietAll;
                result.framesToQuiet = Time.frameCount - frame;
                result.splitPublished = split.Published - publishedBefore;

                // the pieces as they are now
                var free = new List<(LogicalFragmentId fragment, PhysicsFragmentOwner owner, TrimEvalPiece piece, List<float3> mesh)>();
                foreach (LogicalFragmentId f in TrimEvalPiecesOf(root, made.Fragment))
                {
                    root.Owners.TryGet(f, out PhysicsFragmentOwner o);
                    if (!root.Geometry.TryGetGeometry(f, out _) || o.GeometryLocalToOwner == null) continue;
                    List<float3> mesh = MeshInOwner(root, f, o);
                    o.Shape.TryLocalBounds(out float3 lo, out float3 hi);
                    var piece = new TrimEvalPiece
                    {
                        key = PieceKey(mesh), fragment = f.value, fixedByAnchors = o.FixedByAnchors, convexes = o.Shape.ConvexCount, meshPoints = mesh.Count,
                        convexVertices = Enumerable.Range(0, o.Shape.ConvexCount).Select(c => o.Shape.Convex(c).vertexCount).ToArray(),
                        extents = (Vector3)(hi - lo), reach26 = float.NegativeInfinity,
                    };
                    piece.vertices = piece.convexVertices.Sum();
                    for (int k = 0; k < ConvexTrimSearch.Directions; k++) piece.reach26 = Mathf.Max(piece.reach26, ReachBeyondMesh(root, f, o, ConvexTrimSearch.Direction(k)));
                    result.pieces.Add(piece);
                    if (!o.FixedByAnchors) free.Add((f, o, piece, mesh));
                }

                result.freePieces = free.Count;
                if (free.Count > lanes.Count)
                {
                    result.piecesNotLaid = free.Count - lanes.Count;
                    free.RemoveRange(lanes.Count, free.Count - lanes.Count);
                }

                TrimEvalCase offCase = off?.cases.FirstOrDefault(c => c.name == result.name);
                foreach ((string poseName, Quaternion turn) in TrimEvalPoses())
                {
                    for (int p = 0; p < free.Count; p++)
                    {
                        // what is drawn starts 0.6 m above the floor, over the lane's centre, whatever the convexes are
                        float lowest = float.MaxValue;
                        Vector3 centre = Vector3.zero;
                        foreach (float3 m in free[p].mesh)
                        {
                            Vector3 turned = turn * (Vector3)m;
                            lowest = Mathf.Min(lowest, turned.y);
                            centre += turned;
                        }

                        centre /= free[p].mesh.Count;
                        LetGoAt(free[p].fragment, free[p].owner, new Vector3(lanes[p].x - centre.x, TrimFloorTop + TrimEvalStartHeight - lowest, lanes[p].z - centre.z), turn);
                    }

                    yield return Settle(free.Select(f => f.owner).ToList());
                    var poses = new TrimEvalPose[free.Count];
                    for (int p = 0; p < free.Count; p++)
                    {
                        PhysicsFragmentOwner o = free[p].owner;
                        var pose = new TrimEvalPose
                        {
                            name = poseName, start = turn, slept = o.Body.IsSleeping(), restPosition = o.Root.transform.position, restRotation = o.Root.transform.rotation,
                            gap = LowestMeshY(root, free[p].fragment, o) - TrimFloorTop,
                        };
                        pose.reachDownAtRest = ReachBeyondMesh(root, free[p].fragment, o, Quaternion.Inverse(pose.restRotation) * Vector3.down);
                        poses[p] = pose;
                        free[p].piece.poses.Add(pose);
                        free[p].piece.laid = true;
                        Hold(o);
                    }

                    if (offCase == null) continue;

                    // The saved pose: where the same piece rested with the trim off. How far the trimmed convexes reach below
                    // what is drawn there; then the piece put there and let go.
                    var dropped = new List<int>();
                    for (int p = 0; p < free.Count; p++)
                    {
                        TrimEvalPose saved = offCase.pieces.FirstOrDefault(q => q.key == free[p].piece.key && !q.fixedByAnchors)?.poses.FirstOrDefault(q => q.name == poseName);
                        if (saved == null) continue;
                        TrimEvalPose pose = poses[p];
                        pose.hasOff = true;
                        pose.offReachDown = saved.reachDownAtRest;
                        pose.offGap = saved.gap;
                        pose.reachDownAtOffRest = ReachBeyondMesh(root, free[p].fragment, free[p].owner, Quaternion.Inverse(saved.restRotation) * Vector3.down);
                        // the lane may differ from the one the piece had with the trim off: the saved height and turn, this run's lane
                        Vector3 over = saved.restPosition;
                        LetGoAt(free[p].fragment, free[p].owner, new Vector3(pose.restPosition.x, over.y, pose.restPosition.z), saved.restRotation);
                        dropped.Add(p);
                    }

                    if (dropped.Count == 0) continue;
                    yield return Settle(dropped.Select(p => free[p].owner).ToList());
                    foreach (int p in dropped)
                    {
                        PhysicsFragmentOwner o = free[p].owner;
                        poses[p].dropSlept = o.Body.IsSleeping();
                        poses[p].dropGap = LowestMeshY(root, free[p].fragment, o) - TrimFloorTop;
                        poses[p].dropFell = poses[p].offGap - poses[p].dropGap;
                        Hold(o);
                    }
                }

                // parked well above everything, held
                for (int p = 0; p < free.Count; p++)
                {
                    Hold(free[p].owner);
                    free[p].owner.Root.transform.position = new Vector3(lanes[p].x, 200f + (15f * i), lanes[p].z);
                    CutPhysicsStep.NotePlacementInputChanged(free[p].fragment);
                }

                Physics.SyncTransforms();
                for (int r = recordsBefore; r < trim.Records.Count; r++)
                {
                    ConvexTrimRecord record = trim.Records[r];
                    result.records.Add(new TrimEvalRecord
                    {
                        fragment = record.fragment.value, end = record.end.ToString(), note = record.note, convexes = record.convexes,
                        verticesBefore = record.verticesBeforeInAll, verticesAfter = record.verticesAfterInAll, candidatesBefore = record.candidatesBefore,
                        clipsTried = record.clipsTried, trims = record.trimsInAll, trimmedConvexes = record.trimmedConvexes,
                        convexBefore = record.verticesBefore, convexAfter = record.verticesAfter, convexTrims = record.trims, left = record.left,
                        reachBefore = record.overhangBefore, reachAfter = record.overhangAfter, extents = (Vector3)record.extents,
                        searchMs = record.workerSearchSeconds * 1000.0, clipMs = record.workerClipSeconds * 1000.0, bakeMs = record.workerBakeSeconds * 1000.0,
                        issueMs = record.issueSeconds * 1000.0, collectMs = record.collectSeconds * 1000.0, meshMs = record.meshSeconds * 1000.0,
                        hullMs = record.hullCheckSeconds * 1000.0, publishMs = record.publishSeconds * 1000.0, frames = record.endedFrame - record.issuedFrame,
                    });
                }

                TestContext.Out.WriteLine((trimOn ? "trim on, " : "trim off, ") + result.name + ": pieces " + result.pieces.Count + " (free " + result.freePieces + "), split publications "
                    + result.splitPublished + ", quiet " + result.quiet + " after " + result.framesToQuiet + " frames, trim records " + result.records.Count);
            }

            for (int k = 0; k < (int)ConvexTrimCostKind.Count; k++)
            {
                ConvexTrimCost cost = trim.Cost((ConvexTrimCostKind)k);
                file.costs.Add(new TrimEvalCost { name = ((ConvexTrimCostKind)k).ToString(), count = cost.Count, medianMs = cost.MedianSeconds * 1000.0, maxMs = cost.MaxSeconds * 1000.0, totalMs = cost.TotalSeconds * 1000.0 });
            }

            file.trim = trim.Describe();
            file.split = split.Describe(false);
            file.splitSeveral = split.DescribeSeveral();
            file.splitTimeline = split.DescribeTimeline();
            File.WriteAllText(TrimEvalPath(folder, trimOn), JsonUtility.ToJson(file, true));
            TestContext.Out.WriteLine("written: " + TrimEvalPath(folder, trimOn));
            TestContext.Out.WriteLine(file.trim);
            Assert.That(root.GeometryFaults, Is.Zero);
            yield return EndWorld(root);
        }
    }
}
