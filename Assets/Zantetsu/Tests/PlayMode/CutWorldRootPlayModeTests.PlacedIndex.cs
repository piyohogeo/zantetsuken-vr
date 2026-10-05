using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The index of the placed targets whose placement is told (2026-10-05; DESIGN 19.1.7, D-192;
    /// <see cref="SlashHitDetector.AddPlacedStanding"/>): an update reads and lists only the standing targets whose world
    /// box the box of all its sweeps comes near, and everything a caller can see stays what it was when every placed
    /// target was read every update -- which targets are asked to cut, in what order, with what plane and anchor, each
    /// answer, and how many candidates remain. The cases: the shared script of sweeps and changes run both ways (every
    /// change told); far targets neither read nor listed (counted by the targets themselves, not by the detector); a
    /// large target, sweeps at the edges of a box and of the index's own widening, sweeps far apart in one update; the
    /// life of a target (told moves, turns and re-placements, switching off and on, a move not told, removal, a cut
    /// refused and a cut done, a destroyed instance, a box that cannot be made, changes during an update); the city's own
    /// registrar. The targets are the placed box cases' stand-ins, their
    /// convexes a licensed input's; ignored where that input is absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        // A stand-in that counts what is read of it: the evidence that a target was not read is the target's own.
        private sealed class CountedPlaced : ISlashPlacedTarget
        {
            public StandInPlaced inner;
            public int stateReads, poseReads, frameReads;

            public int Reads => stateReads + poseReads + frameReads;

            public bool IsHitTarget { get { stateReads++; return inner.IsHitTarget; } }

            public float4x4 FrameToWorld { get { frameReads++; return inner.FrameToWorld; } }

            public bool TryGetHitFrame(out float4x4 frameToWorld) { frameReads++; return inner.TryGetHitFrame(out frameToWorld); }

            public bool TryGetHitPose(out float3 position, out quaternion rotation) { poseReads++; return inner.TryGetHitPose(out position, out rotation); }

            public VpCharacterHitShape HitShape => inner.HitShape;

            public LogicalFragmentId Source => inner.Source;

            public bool TryIdentify(out LogicalFragmentId fragment) => inner.TryIdentify(out fragment);

            public SlashPlacedCutResult TryCut(in SlashPlacedHit hit) => inner.TryCut(in hit);
        }

        // What the box test kept in each update: targets listed times sweeps, less the tests passed over by the box. The
        // index may only leave out what that test would have passed over for every sweep, so this is the same either way.
        private sealed class PlacedAccount
        {
            public readonly List<long> kept = new List<long>(), listed = new List<long>();
            private long _listed, _passed;

            public void Begin(SlashHitDetector d)
            {
                _listed = d.PlacedListed;
                _passed = d.PlacedPassedOver;
            }

            public void After(SlashHitDetector d, int sweeps)
            {
                long l = d.PlacedListed - _listed, p = d.PlacedPassedOver - _passed;
                kept.Add(l * sweeps - p);
                listed.Add(l);
                Begin(d);
            }
        }

        private static SlashPlacedCutResult StandInRefuses(StandInPlaced t) =>
            new SlashPlacedCutResult(ProvisionalCutAcceptance.NotAccepted, LogicalCutAdmission.SourceNotLive, default, false);

        private static void HullBox(PlacedCuttableInput data, out Vector3 lo, out Vector3 hi)
        {
            Vector3[] vs = data.hulls[0].vertices;
            lo = new Vector3(vs.Min(v => v.x), vs.Min(v => v.y), vs.Min(v => v.z));
            hi = new Vector3(vs.Max(v => v.x), vs.Max(v => v.y), vs.Max(v => v.z));
        }

        // One update of a wave through the middle of a stand-in's first convex, as it stands at p turned by q (offset:
        // where in its frame that convex is), in the plane its frame's y gives, wider than the convex both ways.
        private static SlashSweep ThroughStandIn(long slash, PlacedCuttableInput data, Vector3 p, Quaternion q, Vector3 offset = default)
        {
            HullBox(data, out Vector3 lo, out Vector3 hi);
            return Quad(slash, p + q * (offset + 0.5f * (lo + hi)), q, hi.z - lo.z + 1f, hi.x - lo.x + 1f);
        }

        private static int OneUpdate(SlashHitDetector detector, params SlashSweep[] sweeps)
        {
            detector.Evaluate(sweeps, sweeps.Select(s => s.SlashId).Distinct().ToArray());
            return detector.HitCount;
        }

        private static VpCharacterHitShape ScaledShape(PlacedCuttableInput data, float scale)
        {
            var natives = new List<IDisposable>();
            try
            {
                PlacedCuttableInput.Hull h = data.hulls[0];
                var hull = new PlacedCuttableInput.Hull { vertices = h.vertices.Select(v => v * scale).ToArray(), faceOffsets = h.faceOffsets, faceIndices = h.faceIndices };
                ConvexBrepBank bank = PlacedCuttableRegistration.BuildBank(new[] { hull }, natives, out List<ConvexBrepRange> ranges);
                return new VpCharacterHitShape(bank, ranges);
            }
            finally
            {
                foreach (IDisposable n in natives) n.Dispose();
            }
        }

        private static void AssertSameLines(List<string> before, List<string> now, string name)
        {
            Assert.That(now.Count, Is.EqualTo(before.Count), name + ": the same number of cuts asked and updates");
            for (int i = 0; i < before.Count; i++)
            {
                if (before[i] != now[i]) Assert.Fail(name + ": line " + i + " differs:\n  before: " + before[i] + "\n  now   : " + now[i]);
            }
        }

        private static void AssertSameKept(PlacedAccount before, PlacedAccount now, string name)
        {
            Assert.That(now.kept.Count, Is.EqualTo(before.kept.Count), name + ": the same number of updates");
            for (int i = 0; i < before.kept.Count; i++)
            {
                if (before.kept[i] != now.kept[i])
                {
                    Assert.Fail(name + ": update " + i + ": the box test kept " + before.kept[i] + " tests with every target listed (" + before.listed[i] + " listed) and " + now.kept[i]
                                + " with the index (" + now.listed[i] + " listed): the index left out a target the box test would have kept, or listed one twice");
                }
            }
        }

        [UnityTest]
        public IEnumerator PlacedIndex_GivesTheSameCutsOrderAnswersAndCandidates_AsEveryTargetReadEveryUpdate()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(PlacedPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            StandInPlaced.heldObject = false;
            Func<StandInPlaced, bool> all = t => true, twoInThree = t => t.index % 3 != 1;
            // keptAs: the way whose box test this way's is compared with, update by update (the same way of reading the pose).
            (string name, bool box, bool index, bool merged, bool pose, Func<StandInPlaced, bool> standing, int keptAs)[] ways =
            {
                ("box off, every target read every update (as before the box)", false, false, false, false, null, -1),
                ("box on , index off, placement told for all", true, false, true, true, all, -1),
                ("box on , index on , placement told for all", true, true, true, true, all, 1),
                ("box on , index on , placement told for two in three, the others read every update", true, true, true, true, twoInThree, 1),
                ("box on , index on , placement told for none", true, true, true, true, null, 1),
                ("box on , index off, told for all, the frame read as a matrix", true, false, true, false, all, -1),
                ("box on , index on , told for all, the frame read as a matrix", true, true, true, false, all, 5),
                ("box on , index off, told for all, state and frame read apart", true, false, false, false, all, -1),
                ("box on , index on , told for all, state and frame read apart", true, true, false, false, all, 7),
            };
            var logs = new List<List<string>>();
            var accounts = new List<PlacedAccount>();
            var tallies = new List<(int hits, int boundary, long reads, long listed, long untold, int indexed, int polled, long builds, long searches, long visited, long tested, long found)>();
            foreach (var way in ways)
            {
                using (StandInSet set = NewStandInSet(root, data, 300, 20, true))
                {
                    var detector = new SlashHitDetector(root, in k_hitSettings) { placedBoxReject = way.box, placedIndex = way.index, placedMergedRead = way.merged, placedPoseRead = way.pose };
                    var account = new PlacedAccount();
                    account.Begin(detector);
                    int indexedAtFirst = -1, polledAtFirst = -1;
                    RunPlacedBoxScript(detector, set, data, out int hits, out int boundary, way.standing, n =>
                    {
                        if (indexedAtFirst < 0) { indexedAtFirst = detector.PlacedIndexedCount; polledAtFirst = detector.PlacedReadEveryUpdateCount; }
                        account.After(detector, n);
                    });
                    tallies.Add((hits, boundary, detector.PlacedStateReads, detector.PlacedListed, detector.PlacedMovedUntold, indexedAtFirst, polledAtFirst,
                        detector.PlacedIndexBuilds, detector.PlacedIndexSearches, detector.PlacedIndexNodesVisited, detector.PlacedIndexEntriesTested, detector.PlacedIndexEntriesFound));
                    logs.Add(new List<string>(set.log));
                    accounts.Add(account);
                }

                yield return null;
            }

            for (int w = 0; w < ways.Length; w++)
            {
                var t = tallies[w];
                TestContext.Out.WriteLine(ways[w].name + ": hits " + t.hits + " (boundary sweeps " + t.boundary + "), updates " + accounts[w].kept.Count + ", state reads " + t.reads + ", listed " + t.listed
                    + "; at the first update in the index " + t.indexed + ", read every update " + t.polled + "; index builds " + t.builds + ", searches " + t.searches + ", nodes visited " + t.visited
                    + ", entries tested " + t.tested + ", found " + t.found + "; found moved untold " + t.untold + "; log lines " + logs[w].Count);
            }

            Assert.That(tallies[0].hits, Is.GreaterThan(200), "the script meets targets");
            Assert.That(tallies[0].boundary, Is.GreaterThan(50), "the boundary sweeps meet targets");
            for (int w = 1; w < ways.Length; w++)
            {
                AssertSameLines(logs[0], logs[w], ways[w].name);
                if (ways[w].keptAs >= 0) AssertSameKept(accounts[ways[w].keptAs], accounts[w], ways[w].name);
            }

            Assert.That(tallies[2].indexed, Is.EqualTo(300), "told for all: every target in the index at the first update");
            Assert.That(tallies[2].polled, Is.EqualTo(0));
            Assert.That(tallies[3].indexed, Is.EqualTo(200), "told for two in three");
            Assert.That(tallies[3].polled, Is.EqualTo(100));
            Assert.That(tallies[4].indexed, Is.EqualTo(0), "told for none: nothing in the index");
            Assert.That(tallies[2].reads, Is.LessThan(tallies[1].reads / 2), "with the index most readings are not made");
            Assert.That(tallies[2].untold, Is.EqualTo(0), "the script tells every change: none found moved untold");
            Assert.That(tallies[3].untold, Is.EqualTo(0));
            Assert.That(tallies[2].searches, Is.GreaterThan(500), "the index was searched");
        }

        [UnityTest]
        public IEnumerator PlacedIndex_FarTargets_AreNeitherReadNorListed_CountedByTheTargetsThemselves()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(PlacedPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            StandInPlaced.heldObject = false;
            HullBox(data, out Vector3 hullLo, out Vector3 hullHi);
            TestContext.Out.WriteLine("the convex: " + hullLo.ToString("F3") + " .. " + hullHi.ToString("F3") + "; 885 targets 4 m apart, 30 a row");
            var logs = new List<List<string>>();
            foreach (bool index in new[] { false, true })
            {
                using (StandInSet set = NewStandInSet(root, data, 885, 30, false))
                {
                    foreach (StandInPlaced t in set.targets) t.answer = StandInRefuses;
                    CountedPlaced[] counted = set.targets.Select(t => new CountedPlaced { inner = t }).ToArray();
                    var detector = new SlashHitDetector(root, in k_hitSettings) { placedIndex = index };
                    foreach (CountedPlaced c in counted) Assert.That(detector.AddPlacedStanding(c), Is.True, "in the index");
                    foreach (CountedPlaced c in counted) c.stateReads = c.poseReads = c.frameReads = 0;   // what the adding read is not an update's
                    var seen = new List<string>();
                    long slash = 0;
                    void Update(string what, Vector3 near, float beyond, int expectHits, params SlashSweep[] sweeps)
                    {
                        int[] before = counted.Select(c => c.Reads).ToArray();
                        long reads0 = detector.PlacedStateReads, listed0 = detector.PlacedListed;
                        int hits = OneUpdate(detector, sweeps);
                        int read = 0, farRead = 0;
                        for (int i = 0; i < counted.Length; i++)
                        {
                            int r = counted[i].Reads - before[i];
                            if (r == 0) continue;
                            read++;
                            if ((set.targets[i].at.position - near).magnitude > beyond) farRead++;
                        }

                        seen.Add(what + ": hits " + hits);
                        TestContext.Out.WriteLine((index ? "index on : " : "index off: ") + what + ": hits " + hits + "; targets read " + read + " of " + counted.Length + " (farther than " + beyond.ToString("F0", CultureInfo.InvariantCulture)
                            + " m: " + farRead + "); the detector's own count: state reads " + (detector.PlacedStateReads - reads0) + ", listed " + (detector.PlacedListed - listed0));
                        Assert.That(hits, Is.EqualTo(expectHits), what);
                        Assert.That(detector.PlacedStateReads - reads0, Is.EqualTo(read), what + ": the detector's count of its readings is the targets' own");
                        if (index && !float.IsInfinity(beyond))
                        {
                            Assert.That(farRead, Is.EqualTo(0), what + ": no far target was read");
                            Assert.That(read, Is.LessThanOrEqualTo(9), what + ": only the target met and its neighbours");
                            Assert.That(detector.PlacedListed - listed0, Is.LessThanOrEqualTo(9), what + ": and only they were listed");
                        }
                        else if (!index)
                        {
                            Assert.That(read, Is.EqualTo(counted.Length), what + ": without the index every target is read");
                        }
                    }

                    Vector3 first = set.targets[0].at.position, last = set.targets[884].at.position, middle = set.targets[435].at.position;
                    Update("a sweep through the first target", first, 6f, 1, ThroughStandIn(++slash, data, first, Quaternion.identity));
                    Update("a sweep through the last target", last, 6f, 1, ThroughStandIn(++slash, data, last, Quaternion.identity));
                    Update("a sweep through a target in the middle", middle, 6f, 1, ThroughStandIn(++slash, data, middle, Quaternion.identity));
                    Update("a sweep 50 m over the field", middle, 0f, 0, Quad(++slash, middle + new Vector3(0f, 50f, 0f), Quaternion.identity, 3f, 0.4f));
                    Update("a sweep 300 m beside the field", first, 0f, 0, Quad(++slash, first + new Vector3(-300f, 0.5f, 0f), Quaternion.identity, 3f, 0.4f));
                    // Two Slashes in one update, at the two ends of the field: one box holds all the update's sweeps, so
                    // everything between them is read as well -- conservative, and no target is missed or asked twice.
                    slash += 2;
                    Update("two sweeps in one update, at the two ends of the field", first, float.PositiveInfinity, 2,
                        ThroughStandIn(slash - 1, data, last, Quaternion.identity), ThroughStandIn(slash, data, first, Quaternion.identity));
                    logs.Add(new List<string>(set.log.Concat(seen)));
                }

                yield return null;
            }

            AssertSameLines(logs[0], logs[1], "index on");
        }

        [UnityTest]
        public IEnumerator PlacedIndex_ALargeTarget_BoxBoundaries_AndSweepsFarApart_MissNothingAndAskNothingTwice()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(PlacedPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            StandInPlaced.heldObject = false;
            HullBox(data, out Vector3 hullLo, out Vector3 hullHi);
            const float bigScale = 30f;
            var bigAt = new Vector3(38f, 0f, 38f);
            var logs = new List<List<string>>();
            var accounts = new List<PlacedAccount>();
            var notes = new List<string>();
            VpCharacterHitShape big = ScaledShape(data, bigScale);
            try
            {
                foreach (bool index in new[] { false, true })
                {
                    using (StandInSet set = NewStandInSet(root, data, 400, 20, false))
                    {
                        var bigObject = new GameObject("stand-in large");
                        set.objects.Add(bigObject);
                        bigObject.transform.position = bigAt;
                        var large = new StandInPlaced { index = 1000, at = bigObject.transform, held = bigObject, shape = big, ledger = root.Ledger, log = set.log, answer = StandInRefuses };
                        var detector = new SlashHitDetector(root, in k_hitSettings) { placedIndex = index };
                        foreach (StandInPlaced t in set.targets) detector.AddPlacedStanding(t);
                        Assert.That(detector.AddPlacedStanding(large), Is.True, "the large target in the index");
                        var account = new PlacedAccount();
                        account.Begin(detector);
                        long slash = 0;
                        int Update(params SlashSweep[] sweeps)
                        {
                            int hits = OneUpdate(detector, sweeps);
                            var line = new System.Text.StringBuilder("update sweeps " + sweeps.Length + " hits " + hits + " placed " + detector.PlacedCount + ":");
                            for (int i = 0; i < hits; i++) line.Append(' ').Append(detector.HitAt(i).SlashId).Append('/').Append(detector.HitAt(i).Acceptance).Append('/').Append(detector.HitAt(i).Admission);
                            set.log.Add(line.ToString());
                            account.After(detector, sweeps.Length);
                            return hits;
                        }

                        int Cuts(int target) => set.log.Count(l => l.StartsWith("cut " + target + " slash ", StringComparison.Ordinal));

                        // 1. The large target: met far from where its Transform stands -- its box, not its centre, finds it.
                        Vector3 bigLo = bigScale * hullLo, bigHi = bigScale * hullHi, bigMid = 0.5f * (bigLo + bigHi);
                        int largeCuts = 0;
                        foreach (float along in new[] { -0.4f, 0.4f, -0.2f, 0.2f, 0f })
                        {
                            var through = bigAt + new Vector3(bigMid.x + along * (bigHi.x - bigLo.x), bigMid.y, bigMid.z);
                            int before = Cuts(1000);
                            Update(Quad(++slash, through, Quaternion.identity, bigHi.z - bigLo.z + 2f, 0.5f));
                            largeCuts += Cuts(1000) - before;
                            if (index) notes.Add("the large target (" + (bigHi - bigLo).ToString("F1") + " m), a sweep " + (through - bigAt).magnitude.ToString("F1", CultureInfo.InvariantCulture) + " m from its Transform: asked " + (Cuts(1000) - before));
                        }

                        Assert.That(largeCuts, Is.EqualTo(5), "the large target is met by every sweep through it, far from its Transform as well");
                        // ... and sweeps beside it, above it and below it, which meet whatever small target stands there
                        foreach (Vector3 by in new[] { new Vector3(bigHi.x + 0.01f, bigMid.y, bigMid.z), new Vector3(bigLo.x - 0.01f, bigMid.y, bigMid.z), new Vector3(bigMid.x, bigHi.y + 0.01f, bigMid.z), new Vector3(bigMid.x, bigLo.y - 0.01f, bigMid.z) })
                        {
                            Update(Quad(++slash, bigAt + by, Quaternion.identity, 6f, 2f));
                        }

                        // 2. The boundary: flat sweeps beside each face of a small target's box, outside it by nothing, by one
                        //    float, by the box test's margin, by the index's widening and its margin, and a little more or less
                        //    than each. Whatever the box test keeps the index must list; past its widening it lists nothing.
                        int boundaryUpdates = 0, boundaryKept = 0, boundaryNothingListed = 0;
                        foreach (int subject in new[] { 3, 19, 208, 214, 383, 399 })
                        {
                            StandInPlaced t = set.targets[subject];
                            Vector3 lo = t.at.position + hullLo, hi = t.at.position + hullHi, c = 0.5f * (lo + hi), h = 0.3f * (hi - lo);
                            float reach = Mathf.Max(Mathf.Max(Mathf.Abs(lo.x), Mathf.Abs(lo.y), Mathf.Abs(lo.z)), Mathf.Max(Mathf.Abs(hi.x), Mathf.Abs(hi.y), Mathf.Abs(hi.z)));
                            float margin = 1e-4f * reach + 1e-4f, widening = 1e-3f * reach + 1e-3f;
                            for (int axis = 0; axis < 3; axis++)
                            {
                                foreach (int side in new[] { -1, 1 })
                                {
                                    float face = side > 0 ? hi[axis] : lo[axis];
                                    var at = new List<float>();
                                    foreach (float d in new[] { 0f, margin, margin + widening, 2f * (margin + widening), -0.01f, 0.25f })
                                    {
                                        float x = face + side * d;
                                        at.Add(x);
                                        if (d == 0.25f || d == -0.01f) continue;
                                        foreach (int step in new[] { -8, -1, 1, 8 }) at.Add(NextFloat(x, step));
                                    }

                                    int u = (axis + 1) % 3, w = (axis + 2) % 3;
                                    Vector3 normal = Vector3.zero, span = Vector3.zero, travel = Vector3.zero;
                                    normal[axis] = 1f; span[u] = 1f; travel[w] = 1f;
                                    foreach (float x in at)
                                    {
                                        Vector3 Corner(int su, int sw)
                                        {
                                            Vector3 p = c;
                                            p[axis] = x;
                                            p[u] += su * h[u];
                                            p[w] += sw * h[w];
                                            return p;
                                        }

                                        Update(new SlashSweep(++slash, 0.0, false, new Plane(normal, Corner(0, 0)), travel, span, Corner(-1, -1), Corner(1, -1), Corner(-1, 1), Corner(1, 1)));
                                        boundaryUpdates++;
                                        if (account.kept[account.kept.Count - 1] > 0) boundaryKept++;
                                        if (account.listed[account.listed.Count - 1] == 0) boundaryNothingListed++;
                                    }
                                }
                            }
                        }

                        if (index)
                        {
                            notes.Add("boundary sweeps " + boundaryUpdates + ": the box test kept a target in " + boundaryKept + ", the index listed nothing in " + boundaryNothingListed);
                            Assert.That(boundaryKept, Is.GreaterThan(50), "sweeps at the box's edge are kept by the box test");
                            Assert.That(boundaryNothingListed, Is.GreaterThan(50), "sweeps past the index's widening list nothing");
                        }

                        // 3. Sweeps far apart in one update: two Slashes at the two ends, one Slash twice through one target,
                        //    two Slashes through one target, and three sweeps with the large target among them.
                        Vector3 p0 = set.targets[3].at.position, p1 = set.targets[399].at.position, p2 = set.targets[23].at.position, p3 = set.targets[44].at.position;
                        slash += 2;
                        int twoEnds = Update(ThroughStandIn(slash - 1, data, p1, Quaternion.identity), ThroughStandIn(slash, data, p0, Quaternion.identity));
                        slash++;
                        int before23 = Cuts(23);
                        Update(ThroughStandIn(slash, data, p2, Quaternion.identity), ThroughStandIn(slash, data, p2 + new Vector3(0f, 0.05f, 0f), Quaternion.identity));
                        int oneSlashTwice = Cuts(23) - before23;
                        slash += 2;
                        int before44 = Cuts(44);
                        Update(ThroughStandIn(slash - 1, data, p3, Quaternion.identity), ThroughStandIn(slash, data, p3 + new Vector3(0f, 0.05f, 0f), Quaternion.identity));
                        int twoSlashes = Cuts(44) - before44;
                        slash += 3;
                        int beforeLarge = Cuts(1000);
                        Update(ThroughStandIn(slash - 2, data, p0, Quaternion.identity), Quad(slash - 1, bigAt + bigMid, Quaternion.identity, 4f, 1f), ThroughStandIn(slash, data, p1, Quaternion.identity));
                        if (index)
                        {
                            notes.Add("two Slashes at the two ends in one update: hits " + twoEnds + "; one Slash twice through target 23: asked " + oneSlashTwice + "; two Slashes through target 44: asked " + twoSlashes
                                      + "; three sweeps with the large target among them: the large one asked " + (Cuts(1000) - beforeLarge));
                        }

                        Assert.That(twoEnds, Is.EqualTo(2), "both ends met in one update");
                        Assert.That(oneSlashTwice, Is.EqualTo(1), "one Slash meets a target once, though two of its sweeps cross it");
                        Assert.That(twoSlashes, Is.EqualTo(2), "two Slashes each meet it (index 44 answers 'refused, a candidate still')");
                        Assert.That(Cuts(1000) - beforeLarge, Is.EqualTo(1));
                        logs.Add(new List<string>(set.log));
                        accounts.Add(account);
                    }

                    yield return null;
                }
            }
            finally
            {
                big.Dispose();
            }

            foreach (string n in notes) TestContext.Out.WriteLine(n);
            TestContext.Out.WriteLine("updates " + accounts[0].kept.Count + "; listed in all: every target read every update " + accounts[0].listed.Sum() + ", with the index " + accounts[1].listed.Sum());
            AssertSameLines(logs[0], logs[1], "index on");
            AssertSameKept(accounts[0], accounts[1], "index on");
        }

        // 40 stand-ins 4 m apart, 8 a row, standing straight, each counted and answering "refused, a candidate still".
        private CountedPlaced[] NewCountedSet(CutWorldRoot root, PlacedCuttableInput data, out StandInSet set)
        {
            set = NewStandInSet(root, data, 40, 8, false);
            foreach (StandInPlaced t in set.targets) t.answer = StandInRefuses;
            return set.targets.Select(t => new CountedPlaced { inner = t }).ToArray();
        }

        [UnityTest]
        public IEnumerator PlacedIndex_APlacementTold_IsWhereTheNextUpdateFindsIt_AndStatesNeedNoTelling()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(PlacedPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            StandInPlaced.heldObject = false;
            CountedPlaced[] c = NewCountedSet(root, data, out StandInSet set);
            using (set)
            {
                var detector = new SlashHitDetector(root, in k_hitSettings);
                Quaternion straight = Quaternion.identity;
                var shift = new Vector3(3f, 0.4f, -1f);   // where the second convex of the two-convex shape is, in its frame
                c[11].inner.shape = set.two;
                c[11].inner.at.position = new Vector3(-60f, 0f, -60f);   // by itself: its second convex next to no other target
                for (int i = 0; i < c.Length; i++)
                {
                    if (i == 16) detector.AddPlaced(c[i]);   // one whose placement is not told: read every update
                    else Assert.That(detector.AddPlacedStanding(c[i]), Is.True, "target " + i + " in the index");
                }

                Assert.That(detector.PlacedIndexedCount, Is.EqualTo(39));
                Assert.That(detector.PlacedReadEveryUpdateCount, Is.EqualTo(1));
                long slash = 0;
                int Through(Vector3 p, Quaternion q, Vector3 offset = default) => OneUpdate(detector, ThroughStandIn(++slash, data, p, q, offset));
                Transform T(int i) => c[i].inner.at;

                // moved, and told
                Vector3 was = T(10).position;
                Assert.That(Through(was, straight), Is.EqualTo(1), "met where it stands");
                T(10).position = was + new Vector3(0f, 0f, 200f);
                detector.PlacedChanged(c[10]);
                int reads = c[10].Reads;
                Assert.That(Through(was, straight), Is.EqualTo(0), "moved and told: not met where it stood");
                Assert.That(c[10].Reads - reads, Is.EqualTo(1), "read once, when its box was made again at the start of that update -- and not for the sweep");
                Assert.That(Through(T(10).position, straight), Is.EqualTo(1), "met where it stands now");
                Assert.That(detector.IsPlacedIndexed(c[10]), Is.True);

                // told before the move as well as after it: the box is made at the next update, from where it stands then
                detector.PlacedChanged(c[9]);
                Vector3 was9 = T(9).position;
                T(9).position = was9 + new Vector3(0f, 0f, -200f);
                Assert.That(Through(was9, straight), Is.EqualTo(0), "told before it moved: not met where it stood");
                Assert.That(Through(T(9).position, straight), Is.EqualTo(1), "met where it stands now");

                // turned, and told: its second convex is elsewhere
                Vector3 p11 = T(11).position;
                Assert.That(Through(p11, straight, shift), Is.EqualTo(1), "the second convex met");
                Quaternion turned = Quaternion.Euler(0f, 180f, 0f);
                T(11).rotation = turned;
                detector.PlacedChanged(c[11]);
                Assert.That(Through(p11, straight, shift), Is.EqualTo(0), "turned and told: the second convex is not where it was");
                Assert.That(Through(p11, turned, shift), Is.EqualTo(1), "it is met where the turn carried it");

                // placed under another parent, and the parent moved: each told
                Transform parent = set.parents[0];
                parent.SetPositionAndRotation(new Vector3(0f, 0f, -300f), Quaternion.identity);
                parent.localScale = Vector3.one;
                Vector3 was12 = T(12).position;
                T(12).SetParent(parent, false);
                detector.PlacedChanged(c[12]);
                Assert.That(Through(was12, straight), Is.EqualTo(0), "placed under another parent and told: not where it stood");
                Assert.That(Through(T(12).position, straight), Is.EqualTo(1), "met under its parent");
                Vector3 under = T(12).position;
                parent.position += new Vector3(100f, 0f, 0f);
                detector.PlacedChanged(c[12]);
                Assert.That(Through(under, straight), Is.EqualTo(0), "its parent moved, told for it: not where it stood");
                Assert.That(Through(T(12).position, straight), Is.EqualTo(1), "met where its parent carried it");

                // switched off and on: nothing told
                Vector3 p13 = T(13).position;
                T(13).gameObject.SetActive(false);
                reads = c[13].Reads;
                Assert.That(Through(p13, straight), Is.EqualTo(0), "switched off: not met");
                Assert.That(c[13].Reads - reads, Is.EqualTo(1), "its state was read for the sweep that came near");
                Assert.That(detector.IsPlacedIndexed(c[13]), Is.True, "still found by its box");
                T(13).gameObject.SetActive(true);
                Assert.That(Through(p13, straight), Is.EqualTo(1), "switched on: met, with nothing told");

                // moved a little and not told: the sweep that still comes near its box finds it where it stands
                long untold = detector.PlacedMovedUntold;
                T(14).position += new Vector3(0.3f, 0f, 0f);
                Assert.That(Through(T(14).position, straight), Is.EqualTo(1), "moved 0.3 m untold: found by its old box, tested where it stands");
                Assert.That(detector.PlacedMovedUntold - untold, Is.EqualTo(1), "and noticed");
                Assert.That(Through(T(14).position, straight), Is.EqualTo(1));
                Assert.That(detector.PlacedMovedUntold - untold, Is.EqualTo(1), "its box was made again: not noticed a second time");

                // moved while switched off, told: switched on before the next update, or with an update in between
                Vector3 was8 = T(8).position;
                T(8).gameObject.SetActive(false);
                T(8).position = was8 + new Vector3(0f, 0f, -220f);
                detector.PlacedChanged(c[8]);
                T(8).gameObject.SetActive(true);
                reads = c[8].Reads;
                Assert.That(Through(was8, straight), Is.EqualTo(0), "moved while off, on again before the update: not where it stood");
                Assert.That(c[8].Reads - reads, Is.EqualTo(1), "read once, for its box made again before the search -- and not for the sweep");
                Assert.That(detector.IsPlacedIndexed(c[8]), Is.True);
                Assert.That(Through(T(8).position, straight), Is.EqualTo(1), "met where it stands");
                Vector3 was17 = T(17).position;
                T(17).gameObject.SetActive(false);
                T(17).position = was17 + new Vector3(0f, 0f, -220f);
                detector.PlacedChanged(c[17]);
                Assert.That(Through(was17, straight), Is.EqualTo(0), "off");
                Assert.That(detector.IsPlacedIndexed(c[17]), Is.False, "off when its box was to be made again: read every update until a box can be made");
                T(17).gameObject.SetActive(true);
                Assert.That(Through(was17, straight), Is.EqualTo(0), "on again: not where it stood");
                Assert.That(Through(T(17).position, straight), Is.EqualTo(1), "met where it stands, with nothing more told");
                OneUpdate(detector, Quad(++slash, new Vector3(0f, 90f, 0f), straight, 1f, 1f));
                Assert.That(detector.IsPlacedIndexed(c[17]), Is.True, "and in the index again");

                // The limit of the undertaking: moved far and not told, a told target is not found until it is told --
                // which is why a target that something moves without telling is added with AddPlaced.
                T(15).position += new Vector3(0f, 0f, -200f);
                Assert.That(Through(T(15).position, straight), Is.EqualTo(0), "moved far untold: its box is still where it stood");
                detector.PlacedChanged(c[15]);
                Assert.That(Through(T(15).position, straight), Is.EqualTo(1), "told: met");
                T(16).position += new Vector3(0f, 0f, -200f);
                Assert.That(Through(T(16).position, straight), Is.EqualTo(1), "the one read every update is met wherever it has gone, with nothing told");
                Assert.That(detector.IsPlacedIndexed(c[16]), Is.False);
                detector.PlacedChanged(c[16]);
                OneUpdate(detector, Quad(++slash, new Vector3(0f, 90f, 0f), straight, 1f, 1f));
                Assert.That(detector.IsPlacedIndexed(c[16]), Is.False, "telling does not make a target one whose placement is told");
            }
        }

        [UnityTest]
        public IEnumerator PlacedIndex_Removal_ACutRefusedOrDone_ADestroyedInstance_AndABoxThatCannotBeMade()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(PlacedPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            StandInPlaced.heldObject = false;
            CountedPlaced[] c = NewCountedSet(root, data, out StandInSet set);
            using (set)
            {
                var detector = new SlashHitDetector(root, in k_hitSettings);
                Quaternion straight = Quaternion.identity;
                c[19].inner.answer = t => { t.candidate = false; return new SlashPlacedCutResult(ProvisionalCutAcceptance.Published, LogicalCutAdmission.Admitted, default, true); };
                c[20].inner.answer = t => { t.candidate = false; return new SlashPlacedCutResult(ProvisionalCutAcceptance.InvalidRequest, LogicalCutAdmission.NoOp, default, true); };
                c[22].inner.frame = new float4x4(float.NaN);          // no box can be made of it
                c[23].inner.at.gameObject.SetActive(false);           // switched off when it is added
                for (int i = 0; i < c.Length; i++)
                {
                    bool indexed = detector.AddPlacedStanding(c[i]);
                    Assert.That(indexed, Is.EqualTo(i != 22 && i != 23), "target " + i);
                }

                Assert.That(detector.PlacedIndexedCount, Is.EqualTo(38));
                Assert.That(detector.PlacedReadEveryUpdateCount, Is.EqualTo(2));
                long slash = 0;
                int Through(int i) => OneUpdate(detector, ThroughStandIn(++slash, data, c[i].inner.at.position, straight));
                int Far() => OneUpdate(detector, Quad(++slash, new Vector3(0f, 90f, 0f), straight, 1f, 1f));

                // taken away, and added again
                Assert.That(Through(17), Is.EqualTo(1));
                detector.RemovePlaced(c[17]);
                Assert.That(detector.PlacedCount, Is.EqualTo(39));
                Assert.That(detector.PlacedIndexedCount, Is.EqualTo(37));
                int reads = c[17].Reads;
                Assert.That(Through(17), Is.EqualTo(0), "taken away: not met");
                Assert.That(c[17].Reads - reads, Is.EqualTo(0), "nor read");
                Assert.That(detector.AddPlacedStanding(c[17]), Is.True);
                Assert.That(Through(17), Is.EqualTo(1), "added again: met");

                // a cut refused leaves it a candidate found by its box; a cut done, or refused for good, takes it out
                Assert.That(Through(18), Is.EqualTo(1));
                Assert.That(detector.HitAt(0).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.NotAccepted));
                Assert.That(detector.HasPlaced(c[18]) && detector.IsPlacedIndexed(c[18]), Is.True, "refused, a candidate still: it stays, in the index");
                Assert.That(Through(18), Is.EqualTo(1), "met by another Slash");
                int indexedBefore = detector.PlacedIndexedCount;
                Assert.That(Through(19), Is.EqualTo(1));
                Assert.That(detector.HitAt(0).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
                Assert.That(detector.HasPlaced(c[19]), Is.False, "cut: no candidate any more");
                Assert.That(detector.PlacedIndexedCount, Is.EqualTo(indexedBefore - 1), "and out of the index");
                reads = c[19].Reads;
                Assert.That(Through(19), Is.EqualTo(0), "not met again");
                Assert.That(c[19].Reads - reads, Is.EqualTo(0), "nor read");
                Assert.That(Through(20), Is.EqualTo(1));
                Assert.That(detector.HasPlaced(c[20]), Is.False, "refused for good: taken out as well");
                Assert.That(detector.PlacedIndexedCount, Is.EqualTo(indexedBefore - 2));

                // an instance destroyed with nothing told: read when a sweep comes near, never met, there until taken away
                Vector3 p21 = c[21].inner.at.position;
                UnityEngine.Object.DestroyImmediate(c[21].inner.at.gameObject);
                Assert.That(OneUpdate(detector, ThroughStandIn(++slash, data, p21, straight)), Is.EqualTo(0), "destroyed: not met, nothing thrown");
                Assert.That(detector.HasPlaced(c[21]), Is.True, "there until its owner takes it away, as before");
                detector.RemovePlaced(c[21]);
                Assert.That(detector.HasPlaced(c[21]), Is.False);

                // no box can be made: read every update, tested in its frame as before; once a box can be made it is indexed
                reads = c[22].Reads;
                Assert.That(Far(), Is.EqualTo(0));
                Assert.That(Far(), Is.EqualTo(0));
                Assert.That(c[22].Reads - reads, Is.EqualTo(2), "read in each update, wherever the sweep is");
                Assert.That(detector.IsPlacedIndexed(c[22]), Is.False);
                c[22].inner.frame = null;
                Assert.That(Through(22), Is.EqualTo(1), "a frame again: met (read every update still)");
                Far();
                Assert.That(detector.IsPlacedIndexed(c[22]), Is.True, "and in the index from the update after, with nothing told");
                reads = c[22].Reads;
                Far();
                Assert.That(c[22].Reads - reads, Is.EqualTo(0), "no longer read for a far sweep");

                // switched off when it was added: read every update; switched on, it is met and then indexed, nothing told
                Assert.That(Through(23), Is.EqualTo(0), "switched off: not met");
                c[23].inner.at.gameObject.SetActive(true);
                Assert.That(Through(23), Is.EqualTo(1), "switched on: met");
                Far();
                Assert.That(detector.IsPlacedIndexed(c[23]), Is.True, "in the index from the update after");
                Assert.That(Through(23), Is.EqualTo(1));
                Assert.That(detector.PlacedReadEveryUpdateCount, Is.EqualTo(0), "nothing is read every update any more");
            }
        }

        [UnityTest]
        public IEnumerator PlacedIndex_TheOrderIsTheOrderOfAdding_AndChangesDuringAnUpdateAreTakenAfterIt()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(PlacedPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            StandInPlaced.heldObject = false;
            HullBox(data, out Vector3 hullLo, out Vector3 hullHi);
            Vector3 mid = 0.5f * (hullLo + hullHi);
            var orders = new List<string>();
            foreach (bool index in new[] { false, true })
            {
                CountedPlaced[] c = NewCountedSet(root, data, out StandInSet set);
                using (set)
                {
                    var detector = new SlashHitDetector(root, in k_hitSettings) { placedIndex = index };
                    // added in an order that is not the order they stand in, some told and some not
                    int[] adding = Enumerable.Range(0, c.Length).OrderBy(i => (i * 17) % c.Length).ToArray();
                    foreach (int i in adding)
                    {
                        if (i % 4 == 2) detector.AddPlaced(c[i]);
                        else detector.AddPlacedStanding(c[i]);
                    }

                    long slash = 0;
                    // one sweep along a whole row (8 targets, 28 m)
                    SlashSweep Row(int row) => Quad(++slash, new Vector3(14f + mid.x, mid.y, 4f * row + mid.z), Quaternion.identity, hullHi.z - hullLo.z + 1f, 28f + hullHi.x - hullLo.x + 1f);
                    string Asked(int from) => string.Join(" ", set.log.Skip(from).Where(l => l.StartsWith("cut ", StringComparison.Ordinal)).Select(l => l.Split(' ')[1]));

                    int at = set.log.Count;
                    Assert.That(OneUpdate(detector, Row(4)), Is.EqualTo(8), "a row met");
                    string first = Asked(at);
                    Assert.That(first, Is.EqualTo(string.Join(" ", adding.Where(i => i / 8 == 4))), "asked in the order they were added");

                    // one taken away and added again goes last
                    detector.RemovePlaced(c[33]);
                    detector.AddPlacedStanding(c[33]);
                    at = set.log.Count;
                    Assert.That(OneUpdate(detector, Row(4)), Is.EqualTo(8));
                    string second = Asked(at);
                    Assert.That(second, Is.EqualTo(string.Join(" ", adding.Where(i => i / 8 == 4 && i != 33).Concat(new[] { 33 }))), "the one added again is asked last");

                    // During an update's enumeration (a hit identifying a target): one taken away, one added, one moved and
                    // told. The update goes on with the candidates it began with; the changes are there for the next.
                    var extraObject = new GameObject("stand-in added during an update");
                    set.objects.Add(extraObject);
                    extraObject.transform.position = new Vector3(-100f, 0f, 0f);
                    var extra = new CountedPlaced { inner = new StandInPlaced { index = 500, at = extraObject.transform, held = extraObject, shape = set.one, ledger = root.Ledger, log = set.log, answer = StandInRefuses } };
                    int inside = -1, before = detector.PlacedCount, fired = 0;
                    int firstOfRow = adding.First(i => i / 8 == 3);   // the first of the row to be identified
                    int taken = adding.Last(i => i / 8 == 3), moved = adding.Where(i => i / 8 == 3).ElementAt(4);
                    Vector3 movedFrom = c[moved].inner.at.position;
                    c[firstOfRow].inner.onIdentify = _ =>
                    {
                        fired++;
                        detector.RemovePlaced(c[taken]);
                        bool now = detector.AddPlacedStanding(extra);
                        c[moved].inner.at.position = movedFrom + new Vector3(0f, 0f, 150f);
                        detector.PlacedChanged(c[moved]);
                        inside = detector.PlacedCount;
                        Assert.That(now, Is.False, "not in the index while the update enumerates");
                        Assert.That(detector.HasPlaced(c[taken]), Is.True, "still a candidate while the update enumerates");
                        Assert.That(detector.HasPlaced(extra), Is.False);
                    };
                    at = set.log.Count;
                    int during = OneUpdate(detector, Row(3));
                    string third = Asked(at);
                    Assert.That(fired, Is.EqualTo(1));
                    Assert.That(inside, Is.EqualTo(before), "the candidates stand while the update enumerates");
                    Assert.That(during, Is.EqualTo(8), "the update meets the row it began with: the one taken away and the one moved as well, where they were read");
                    Assert.That(third, Is.EqualTo(string.Join(" ", adding.Where(i => i / 8 == 3))));
                    Assert.That(detector.PlacedCount, Is.EqualTo(before), "after it: one taken away, one added");
                    Assert.That(detector.HasPlaced(c[taken]), Is.False);
                    Assert.That(detector.HasPlaced(extra), Is.True);
                    if (index) Assert.That(detector.IsPlacedIndexed(extra), Is.True, "the one added is in the index after the update");
                    at = set.log.Count;
                    Assert.That(OneUpdate(detector, Row(3)), Is.EqualTo(6), "the next update: without the one taken away and the one moved");
                    string fourth = Asked(at);
                    Assert.That(OneUpdate(detector, ThroughStandIn(++slash, data, c[moved].inner.at.position, Quaternion.identity)), Is.EqualTo(1), "the one moved is met where it stands");
                    Assert.That(OneUpdate(detector, ThroughStandIn(++slash, data, extraObject.transform.position, Quaternion.identity)), Is.EqualTo(1), "the one added is met");
                    orders.Add(first + " | " + second + " | " + third + " | " + fourth);
                    TestContext.Out.WriteLine((index ? "index on : " : "index off: ") + orders[orders.Count - 1]);
                }

                yield return null;
            }

            Assert.That(orders[1], Is.EqualTo(orders[0]), "the same targets asked in the same order, with the index and without");
        }

        [UnityTest]
        public IEnumerator PlacedIndex_TheCitysRegistrar_IndexesOnlyWhatIsDeclaredTold_ReadsTheOthersEveryUpdate_AndTellsAMove()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var input = new TextAsset(File.ReadAllText(PlacedPropInputPath)) { name = "bench_001" };
            PlacedCuttableInput data = PlayableCityCuttable.ParsedInput(input);
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                PlayableCityCuttable standing = PlaceDeferred(root, input, detector, "bench standing", at, true);
                PlayableCityCuttable spare = PlaceDeferred(root, input, detector, "bench spare", at + new Vector3(20f, 0f, 0f), true);
                PlayableCityCuttable bodied = PlaceDeferred(root, input, detector, "bench with a body", at + new Vector3(40f, 0f, 0f), true, o => o.AddComponent<Rigidbody>().isKinematic = true);
                PlayableCityCuttable under = PlaceDeferred(root, input, detector, "bench under a body", at + new Vector3(60f, 0f, 0f), true, o =>
                {
                    GameObject carrier = TrackActor(new GameObject("carrier with a body"));
                    carrier.AddComponent<Rigidbody>().isKinematic = true;
                    o.transform.SetParent(carrier.transform, true);
                });
                // Not declared: the default. Nothing could move it -- no body on it or above it -- and it is read every update all the same.
                PlayableCityCuttable marked = PlaceDeferred(root, input, detector, "bench not declared", at + new Vector3(80f, 0f, 0f), false);
                Assert.That(new GameObject("a registrar as made").AddComponent<PlayableCityCuttable>().placementChangesAreTold, Is.False, "the default is: not told");
                UnityEngine.Object.DestroyImmediate(GameObject.Find("a registrar as made"));
                PlayableCityCuttable[] all = { standing, spare, bodied, under, marked };
                yield return UntilWithin(() => all.All(c => c.IsCutTarget || c.Failure != null), 5f, "the cut targets made");
                foreach (PlayableCityCuttable c in all) Assert.That(c.Failure, Is.Null, c.name);
                foreach (PlayableCityCuttable c in all) Assert.That(detector.HasPlaced(c.Candidate), Is.True, c.name + ": a candidate of the detector");

                // Through the city's own registration: what no body can move is found by its box; the others are read every update.
                Assert.That(standing.PlacementTold && spare.PlacementTold, Is.True, "declared told, no body on it or above it: its placement is told");
                Assert.That(detector.IsPlacedIndexed(standing.Candidate) && detector.IsPlacedIndexed(spare.Candidate), Is.True, "and it is in the detector's index");
                Assert.That(bodied.PlacementTold, Is.False, "declared told, but a body on it");
                Assert.That(under.PlacementTold, Is.False, "declared told, but a body above it");
                Assert.That(marked.PlacementTold, Is.False, "not declared: read every update, though it has no body");
                Assert.That(detector.IsPlacedIndexed(bodied.Candidate) || detector.IsPlacedIndexed(under.Candidate) || detector.IsPlacedIndexed(marked.Candidate), Is.False, "those are read every update");
                Assert.That(detector.PlacedIndexedCount, Is.EqualTo(2));
                Assert.That(detector.PlacedReadEveryUpdateCount, Is.EqualTo(3));

                // A sweep far from everything: the three are read, the two in the index are not.
                long reads = detector.PlacedStateReads, listed = detector.PlacedListed;
                Evaluate(detector, Level(1, 80f, -1f, 1f), 1);
                Assert.That(detector.HitCount, Is.EqualTo(0));
                Assert.That(detector.PlacedStateReads - reads, Is.EqualTo(3), "only the ones read every update were read");
                Assert.That(detector.PlacedListed - listed, Is.EqualTo(3));

                // The one with a body, moved without a word, is met where it stands.
                bodied.target.position += new Vector3(0f, 0f, -50f);
                Evaluate(detector, ThroughTheMiddle(2, data, bodied.target.position), 2);
                Assert.That(detector.HitCount, Is.EqualTo(1), "the bodied one is met where it has gone");
                Assert.That(Hits(detector)[0].Fragment, Is.EqualTo(bodied.Candidate.Source));

                // The standing one moved, the move told through its registrar: met where it stands, not where it stood.
                Vector3 moved = at + new Vector3(0f, 0f, 60f);
                standing.target.position = moved;
                standing.NotifyPlacementChanged();
                Evaluate(detector, ThroughTheMiddle(3, data, at), 3);
                Assert.That(detector.HitCount, Is.EqualTo(0), "not where it stood");
                Assert.That(detector.PlacedMovedUntold, Is.EqualTo(0));
                Evaluate(detector, ThroughTheMiddle(4, data, moved), 4);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1), "met where it stands");
                Assert.That(hits[0].Fragment, Is.EqualTo(standing.Candidate.Source));
                Assert.That(hits[0].Acceptance == ProvisionalCutAcceptance.Published || hits[0].Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "cut: " + hits[0].Acceptance);

                // Cut: it leaves the candidates and the index; its pieces are the world's, met by the ordinary search.
                Assert.That(standing.Candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Cut));
                Assert.That(detector.HasPlaced(standing.Candidate), Is.False, "no longer a candidate");
                Assert.That(detector.PlacedIndexedCount, Is.EqualTo(1), "out of the index");
                yield return Until(() => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed, "the cut committed");
                yield return null;
                Evaluate(detector, ThroughTheMiddle(5, data, moved + new Vector3(0f, 0.1f, 0f)), 5);
                List<SlashHitConfirmed> again = Hits(detector);
                TestContext.Out.WriteLine("its pieces, by another Slash: " + string.Join("; ", again.Select(h => h.Fragment + " " + h.Acceptance)));
                Assert.That(again.Any(h => h.Acceptance == ProvisionalCutAcceptance.Published || h.Acceptance == ProvisionalCutAcceptance.Pending), Is.True, "a piece cut again, by the search of the world's own fragments");

                // Its registrar destroyed: the spare leaves the detector and the index.
                PlacedCuttableCandidate spareCandidate = spare.Candidate;
                UnityEngine.Object.Destroy(spare.gameObject);
                yield return null;
                Assert.That(detector.HasPlaced(spareCandidate), Is.False, "taken away with its registrar");
                Assert.That(detector.PlacedIndexedCount, Is.EqualTo(0));
                Assert.That(detector.PlacedReadEveryUpdateCount, Is.EqualTo(2), "the bodied one was cut or stays; the others stay read every update");
                yield return EndWorld(root);
            }
            finally
            {
                TrackPlacedRegistrars();
            }
        }

        // An instance with its registrar, deferred until its cut and added to this detector; told: whether whoever placed it
        // undertakes that its placement changes are told (the registrar's default is that nobody does).
        private PlayableCityCuttable PlaceDeferred(CutWorldRoot root, TextAsset input, SlashHitDetector detector, string name, Vector3 at, bool told, Action<GameObject> prepare = null)
        {
            GameObject instance = RefusalInstance(name, at, out Renderer[] renderers, out Collider[] colliders);
            prepare?.Invoke(instance);
            PlayableCityCuttable c = PlaceRegistrar(root, input, name + " (registrar)", Vector3.zero, 0f, Vector3.one);
            c.target = instance.transform;
            c.instanceRenderers = renderers;
            c.instanceColliders = colliders;
            c.deferUntilCut = true;
            c.placementChangesAreTold = told;
            c.DetectorForTest = detector;
            return c;
        }

        // The city's own path for a placement that changes before the cut: the mover tells through the registrar, and the
        // next evaluation finds the instance where it stands -- its box made again before the index is searched, whether
        // the instance was switched on or off when it moved, and whether it moved itself or a parent carried it.
        [UnityTest]
        public IEnumerator PlacedIndex_TheCitysRegistrar_MovedWhileOff_CarriedByAParent_PutUnderAnother_IsFoundWhereItStands()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var input = new TextAsset(File.ReadAllText(PlacedPropInputPath)) { name = "bench_001" };
            PlacedCuttableInput data = PlayableCityCuttable.ParsedInput(input);
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                GameObject district = TrackActor(new GameObject("district")), closed = TrackActor(new GameObject("district switched off")), elsewhere = TrackActor(new GameObject("another parent"));
                elsewhere.transform.position = new Vector3(0f, 0f, 300f);
                var at = new Vector3(0f, -1f, 0f);
                PlayableCityCuttable offThenOn = PlaceDeferred(root, input, detector, "moved while off, on before any evaluation", at, true);
                PlayableCityCuttable offEvaluated = PlaceDeferred(root, input, detector, "moved while off, an evaluation before it is on", at + new Vector3(30f, 0f, 0f), true);
                PlayableCityCuttable carried = PlaceDeferred(root, input, detector, "under a parent that moves", at + new Vector3(60f, 0f, 0f), true, o => o.transform.SetParent(district.transform, true));
                PlayableCityCuttable carriedToo = PlaceDeferred(root, input, detector, "under the same parent, not declared", at + new Vector3(90f, 0f, 0f), false, o => o.transform.SetParent(district.transform, true));
                PlayableCityCuttable carriedOff = PlaceDeferred(root, input, detector, "under a parent moved while off", at + new Vector3(120f, 0f, 0f), true, o => o.transform.SetParent(closed.transform, true));
                PlayableCityCuttable reparented = PlaceDeferred(root, input, detector, "put under another parent", at + new Vector3(150f, 0f, 0f), true);
                PlayableCityCuttable still = PlaceDeferred(root, input, detector, "standing by", at + new Vector3(180f, 0f, 0f), true);
                PlayableCityCuttable[] all = { offThenOn, offEvaluated, carried, carriedToo, carriedOff, reparented, still };
                yield return UntilWithin(() => all.All(c => c.IsCutTarget || c.Failure != null), 5f, "the cut targets made");
                foreach (PlayableCityCuttable c in all) Assert.That(c.Failure, Is.Null, c.name);
                Assert.That(detector.PlacedIndexedCount, Is.EqualTo(6), "the six declared told are in the index");
                Assert.That(detector.PlacedReadEveryUpdateCount, Is.EqualTo(1), "the one not declared is read every update");
                long slash = 0;
                int At(Vector3 where)
                {
                    slash++;
                    Evaluate(detector, ThroughTheMiddle(slash, data, where), slash);
                    return detector.HitCount;
                }

                void Far()
                {
                    slash++;
                    Evaluate(detector, Level(slash, 80f, -1f, 1f), slash);
                    Assert.That(detector.HitCount, Is.EqualTo(0));
                }

                // 1. Switched off, moved, told, switched on again -- all before any evaluation.
                Vector3 was = offThenOn.target.position, now = was + new Vector3(0f, 0f, 70f);
                offThenOn.target.gameObject.SetActive(false);
                offThenOn.target.position = now;
                offThenOn.NotifyPlacementChanged();
                offThenOn.target.gameObject.SetActive(true);
                Assert.That(At(was), Is.EqualTo(0), "not where it stood");
                Assert.That(detector.IsPlacedIndexed(offThenOn.Candidate), Is.True, "in the index, at its box made again at the start of that evaluation");
                Assert.That(At(now), Is.EqualTo(1), "found where it stands");
                Assert.That(Hits(detector)[0].Fragment, Is.EqualTo(offThenOn.Candidate.Source));

                // 2. The same with an evaluation while it is off: no box can be made of it then, so it is read every update
                //    -- and is found where it stands as soon as it is switched on, with nothing more told.
                was = offEvaluated.target.position; now = was + new Vector3(0f, 0f, 70f);
                offEvaluated.target.gameObject.SetActive(false);
                offEvaluated.target.position = now;
                offEvaluated.NotifyPlacementChanged();
                Far();
                Assert.That(detector.IsPlacedIndexed(offEvaluated.Candidate), Is.False, "off when its box was to be made again: out of the index");
                Assert.That(detector.PlacedReadEveryUpdateCount, Is.EqualTo(2), "read every update meanwhile");
                Assert.That(At(now), Is.EqualTo(0), "off: not met");
                offEvaluated.target.gameObject.SetActive(true);
                Assert.That(At(was), Is.EqualTo(0), "on again: not where it stood");
                Assert.That(At(now), Is.EqualTo(1), "found where it stands, nothing more told");
                Assert.That(Hits(detector)[0].Fragment, Is.EqualTo(offEvaluated.Candidate.Source));

                // 3. A parent moved and turned: told once for the parent, for every declared instance under it.
                Vector3 carriedWas = carried.target.position, alsoWas = carriedToo.target.position;
                district.transform.SetPositionAndRotation(new Vector3(-40f, 0f, 120f), Quaternion.Euler(0f, 90f, 0f));
                Assert.That(PlayableCityCuttable.NotifyPlacementChangedUnder(district.transform), Is.EqualTo(1), "one declared instance under the parent (the other is read every update)");
                Assert.That((carried.target.position - carriedWas).magnitude, Is.GreaterThan(50f), "the parent carried it away");
                Assert.That(At(carriedWas), Is.EqualTo(0), "not where it stood");
                Assert.That(detector.IsPlacedIndexed(carried.Candidate), Is.True);
                Assert.That(At(carried.target.position), Is.EqualTo(1), "found where the parent carried it");
                Assert.That(Hits(detector)[0].Fragment, Is.EqualTo(carried.Candidate.Source));
                Assert.That(At(alsoWas), Is.EqualTo(0));
                Assert.That(At(carriedToo.target.position), Is.EqualTo(1), "the one read every update follows its parent with nothing told");

                // 4. A parent switched off, moved, told, switched on.
                was = carriedOff.target.position;
                closed.SetActive(false);
                closed.transform.position += new Vector3(0f, 0f, -90f);
                Assert.That(PlayableCityCuttable.NotifyPlacementChangedUnder(closed.transform), Is.EqualTo(1));
                Far();
                Assert.That(detector.IsPlacedIndexed(carriedOff.Candidate), Is.False, "its parent off when its box was to be made again: read every update meanwhile");
                closed.SetActive(true);
                Assert.That(At(was), Is.EqualTo(0), "not where it stood");
                Assert.That(At(carriedOff.target.position), Is.EqualTo(1), "found where its parent carried it");
                Assert.That(Hits(detector)[0].Fragment, Is.EqualTo(carriedOff.Candidate.Source));

                // 5. Put under another parent (which stands elsewhere), told by the instance's own registrar.
                was = reparented.target.position;
                reparented.target.SetParent(elsewhere.transform, false);
                reparented.NotifyPlacementChanged();
                Assert.That((reparented.target.position - was).magnitude, Is.GreaterThan(100f), "under the other parent it stands elsewhere");
                Assert.That(At(was), Is.EqualTo(0), "not where it stood");
                Assert.That(At(reparented.target.position), Is.EqualTo(1), "found under its new parent");
                Assert.That(Hits(detector)[0].Fragment, Is.EqualTo(reparented.Candidate.Source));
                // ... and its new parent moved, told for the parent
                Assert.That(PlayableCityCuttable.NotifyPlacementChangedUnder(elsewhere.transform), Is.EqualTo(1), "a cut instance's registrar is still asked (the detector has nothing of it any more)");

                // Nothing above was found by a box left where the instance had stood: the detector never met an indexed
                // target standing elsewhere than its box said.
                Assert.That(detector.PlacedMovedUntold, Is.EqualTo(0), "no move went untold");
                Assert.That(detector.IsPlacedIndexed(still.Candidate), Is.True, "the one that never moved is still found by its box");
                Assert.That(At(still.target.position), Is.EqualTo(1));
                yield return EndWorld(root);
            }
            finally
            {
                TrackPlacedRegistrars();
            }
        }

    }
}
