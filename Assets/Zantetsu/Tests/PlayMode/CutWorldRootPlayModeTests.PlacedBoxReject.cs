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
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The placed targets' world box (2026-10-04): a target passed over by its world box is one the test in its own frame
    /// would not have hit. The same script of sweeps and of changes to the targets is run twice on two equal sets of
    /// targets, with the box off (every target tested in its frame, as before) and on, and everything a caller can see is
    /// compared: which targets were asked to cut, in what order, with what plane and anchor, each answer's acceptance and
    /// admission, and how many candidates remain. The targets here are stand-ins that answer as they are told (accepted
    /// and done, refused and still a candidate, refused for good); their convexes are a licensed input's. A second case
    /// measures the cost of one update over 885 standing targets, both ways, in this Editor.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private sealed class StandInPlaced : ISlashPlacedTarget
        {
            public int index;
            public Transform at;
            public VpCharacterHitShape shape;
            public LogicalCutLedger ledger;
            public bool candidate = true, worldOpen = true;
            public int answers;
            public List<string> log;
            public Func<StandInPlaced, SlashPlacedCutResult> answer;
            public Action<StandInPlaced> onIdentify;   // given: something done when a hit identifies it, inside the update's enumeration

            public GameObject held;            // the Transform's GameObject, held as the real candidate holds it
            public static bool heldObject;     // the state through the held GameObject (two engine calls) or through the Transform (three)

            public bool IsHitTarget => candidate && worldOpen && (heldObject ? held != null && held.activeInHierarchy : at != null && at.gameObject.activeInHierarchy);

            public bool TryGetHitPose(out float3 position, out quaternion rotation)
            {
                position = default; rotation = default;
                if (!IsHitTarget) return false;
                if (frame.HasValue) { position = new float3(float.NaN); rotation = quaternion.identity; return true; }   // the broken frame, as a pose
                at.GetPositionAndRotation(out Vector3 p, out Quaternion r);
                position = p; rotation = r;
                return true;
            }
            public float4x4? frame;   // given: a frame that is not the transform's (a broken one, for the case without a box)
            public float4x4 FrameToWorld => frame ?? float4x4.TRS(at.position, at.rotation, new float3(1f));
            public VpCharacterHitShape HitShape => shape;

            public bool TryGetHitFrame(out float4x4 frameToWorld)
            {
                if (!IsHitTarget) { frameToWorld = default; return false; }
                if (frame.HasValue) { frameToWorld = frame.Value; return true; }
                at.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);   // one reading, as the real candidate does
                frameToWorld = float4x4.TRS(position, rotation, new float3(1f));
                return true;
            }
            public LogicalFragmentId Source { get; set; }

            public bool TryIdentify(out LogicalFragmentId fragment)
            {
                fragment = Source;
                if (Source.IsSet) return true;
                if (!IsHitTarget) return false;
                Source = ledger.AddFragment();
                fragment = Source;
                onIdentify?.Invoke(this);
                return true;
            }

            public SlashPlacedCutResult TryCut(in SlashPlacedHit hit)
            {
                answers++;
                SlashPlacedCutResult r = answer(this);
                log?.Add("cut " + index + " slash " + hit.slashId + " planeId " + hit.planeId + " plane " + Text(hit.plane) + " world " + Text(hit.planeWorld)
                         + " anchor " + Text(hit.renderAnchor) + " -> " + r.Acceptance + "/" + r.Admission + " done " + r.Done);
                return r;
            }

            private static string Text(float4 v) => v.x.ToString("R", CultureInfo.InvariantCulture) + "," + v.y.ToString("R", CultureInfo.InvariantCulture) + "," + v.z.ToString("R", CultureInfo.InvariantCulture) + "," + v.w.ToString("R", CultureInfo.InvariantCulture);
            private static string Text(float3 v) => v.x.ToString("R", CultureInfo.InvariantCulture) + "," + v.y.ToString("R", CultureInfo.InvariantCulture) + "," + v.z.ToString("R", CultureInfo.InvariantCulture);
        }

        // The answers: by the target's number, so both sets answer alike.
        private static SlashPlacedCutResult StandInAnswer(StandInPlaced t)
        {
            switch (t.index % 5)
            {
                case 0:   // accepted: its cut goes on, it is no candidate any more
                    t.candidate = false;
                    return new SlashPlacedCutResult(ProvisionalCutAcceptance.Published, LogicalCutAdmission.Admitted, default, true);
                case 1:   // refused for good
                    t.candidate = false;
                    return new SlashPlacedCutResult(ProvisionalCutAcceptance.InvalidRequest, LogicalCutAdmission.NoOp, default, true);
                case 2:   // refused the first two times and a candidate still, then accepted
                    if (t.answers <= 2) return new SlashPlacedCutResult(ProvisionalCutAcceptance.NotAccepted, LogicalCutAdmission.SourceNotLive, default, false);
                    t.candidate = false;
                    return new SlashPlacedCutResult(ProvisionalCutAcceptance.Published, LogicalCutAdmission.Admitted, default, true);
                default:  // refused, a candidate still (a later Slash meets it again)
                    return new SlashPlacedCutResult(ProvisionalCutAcceptance.NotAccepted, LogicalCutAdmission.SourceNotLive, default, false);
            }
        }

        private sealed class StandInSet : IDisposable
        {
            public readonly List<StandInPlaced> targets = new List<StandInPlaced>();
            public readonly List<Transform> parents = new List<Transform>();
            public readonly List<GameObject> objects = new List<GameObject>();
            public readonly List<string> log = new List<string>();
            public VpCharacterHitShape one, two;

            public void Dispose()
            {
                foreach (GameObject o in objects) if (o != null) UnityEngine.Object.DestroyImmediate(o);
                one?.Dispose();
                two?.Dispose();
            }
        }

        private static PlacedCuttableInput.Hull Shifted(PlacedCuttableInput.Hull h, Vector3 by) =>
            new PlacedCuttableInput.Hull { vertices = h.vertices.Select(v => v + by).ToArray(), faceOffsets = h.faceOffsets, faceIndices = h.faceIndices };

        // count targets on a grid, 4 m apart: every third with two convexes (the second 3 m away in its frame), every fifth
        // under a turned and scaled parent, every one turned its own way, some with a scale of their own.
        private StandInSet NewStandInSet(CutWorldRoot root, PlacedCuttableInput data, int count, int columns, bool posed)
        {
            var set = new StandInSet();
            var natives = new List<IDisposable>();
            try
            {
                ConvexBrepBank bank1 = PlacedCuttableRegistration.BuildBank(new[] { data.hulls[0] }, natives, out List<ConvexBrepRange> r1);
                set.one = new VpCharacterHitShape(bank1, r1);
                ConvexBrepBank bank2 = PlacedCuttableRegistration.BuildBank(new[] { data.hulls[0], Shifted(data.hulls[0], new Vector3(3f, 0.4f, -1f)) }, natives, out List<ConvexBrepRange> r2);
                set.two = new VpCharacterHitShape(bank2, r2);
            }
            finally
            {
                foreach (IDisposable n in natives) n.Dispose();
            }

            var random = new System.Random(20261004);
            for (int g = 0; g < 4; g++)
            {
                var parent = new GameObject("stand-in parent " + g);
                parent.transform.SetPositionAndRotation(new Vector3(3f * g, 0.5f * g, -2f * g), Quaternion.Euler(10f * g, 25f * g, 5f * g));
                parent.transform.localScale = new Vector3(1f + 0.3f * g, 1f, 1f + 0.2f * g);
                set.objects.Add(parent);
                set.parents.Add(parent.transform);
            }

            for (int i = 0; i < count; i++)
            {
                var o = new GameObject("stand-in " + i);
                set.objects.Add(o);
                var at = new Vector3(4f * (i % columns), 0f, 4f * (i / columns));
                Quaternion turn = Quaternion.identity;
                if (posed)
                {
                    if (i % 4 != 0) turn = Quaternion.Euler((float)(random.NextDouble() * 360.0), (float)(random.NextDouble() * 360.0), (float)(random.NextDouble() * 360.0));
                    if (i % 5 == 0) o.transform.SetParent(set.parents[(i / 5) % set.parents.Count], false);
                    if (i % 6 == 0) o.transform.localScale = new Vector3(2f, 0.5f, 3f);
                }

                o.transform.SetPositionAndRotation(at, turn);
                set.targets.Add(new StandInPlaced
                {
                    index = i, at = o.transform, held = o, shape = posed && i % 3 == 0 ? set.two : set.one, ledger = root.Ledger, log = set.log, answer = StandInAnswer,
                });
            }

            return set;
        }

        // The float next to x, up or down.
        private static float NextFloat(float x, int step)
        {
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(x), 0);
            bits += x >= 0f ? step : -step;
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        // One update of a wave: a quad in a plane through centre, span and travel of the given lengths.
        private static SlashSweep Quad(long slash, Vector3 centre, Quaternion turn, float span, float travel)
        {
            Vector3 n = turn * Vector3.up, s = turn * Vector3.forward, t = turn * Vector3.right;
            Vector3 a0 = centre - 0.5f * span * s - 0.5f * travel * t, b0 = centre + 0.5f * span * s - 0.5f * travel * t;
            Vector3 a1 = a0 + travel * t, b1 = b0 + travel * t;
            return new SlashSweep(slash, 0.0, false, new Plane(n, centre), t, s, a0, b0, a1, b1);
        }

        // The script: sweeps and changes, the same for both sets. Everything seen goes into the set's log.
        // With standing given (the index, 2026-10-05): the targets it says are added as ones whose placement is told, and
        // every change the script makes to where one of them stands is told to the detector; the others are added as before.
        private static void RunPlacedBoxScript(SlashHitDetector detector, StandInSet set, PlacedCuttableInput data, out int hits, out int boundaryHits,
            Func<StandInPlaced, bool> standing = null, Action<int> afterUpdate = null)
        {
            hits = 0; boundaryHits = 0;
            var random = new System.Random(977);
            var live = new List<long>();
            long slash = 0;
            void Add(StandInPlaced t)
            {
                if (standing != null && standing(t)) detector.AddPlacedStanding(t);
                else detector.AddPlaced(t);
            }

            void Tell(StandInPlaced t)
            {
                if (standing != null && standing(t)) detector.PlacedChanged(t);
            }

            foreach (StandInPlaced t in set.targets) Add(t);
            Quaternion RandomTurn() => Quaternion.Euler((float)(random.NextDouble() * 360.0), (float)(random.NextDouble() * 360.0), (float)(random.NextDouble() * 360.0));

            int Update(params SlashSweep[] sweeps)
            {
                live.Clear();   // the Slashes of this update are the ones still flying: every other has ended
                foreach (SlashSweep s in sweeps) if (!live.Contains(s.SlashId)) live.Add(s.SlashId);
                detector.Evaluate(sweeps, live.ToArray());
                var line = new System.Text.StringBuilder("update sweeps " + sweeps.Length + " hits " + detector.HitCount + " placed " + detector.PlacedCount + ":");
                for (int i = 0; i < detector.HitCount; i++) line.Append(' ').Append(detector.HitAt(i).SlashId).Append('/').Append(detector.HitAt(i).Acceptance).Append('/').Append(detector.HitAt(i).Admission);
                set.log.Add(line.ToString());
                afterUpdate?.Invoke(sweeps.Length);
                return detector.HitCount;
            }

            // 1. sweeps anywhere over the field, each Slash flying three updates (a target it met is not met again by it)
            for (int k = 0; k < 120; k++)
            {
                slash++;
                var centre = new Vector3((float)(random.NextDouble() * 80.0), (float)(random.NextDouble() * 2.0), (float)(random.NextDouble() * 60.0));
                Quaternion turn = RandomTurn();
                float span = 1f + (float)(random.NextDouble() * 12.0), travel = 0.2f + (float)(random.NextDouble() * 3.0);
                for (int step = 0; step < 3; step++) hits += Update(Quad(slash, centre + step * 0.5f * travel * (turn * Vector3.right), turn, span, travel));
            }

            // 2. two sweeps in one update, through targets where they stand
            for (int k = 0; k < 60; k++)
            {
                StandInPlaced a = set.targets[random.Next(set.targets.Count)], b = set.targets[random.Next(set.targets.Count)];
                if (a.at == null || b.at == null) continue;
                slash += 2;
                hits += Update(Quad(slash - 1, a.at.position + new Vector3(0f, 0.7f, 0f), Quaternion.identity, 6f, 2f), Quad(slash, b.at.position + new Vector3(0f, 0.6f, 0f), RandomTurn(), 5f, 1f));
            }

            // 3. the targets change: moved, turned, re-parented, their parents moved and scaled, scaled themselves, switched off,
            //    destroyed, taken away, added again (a cut one taken back as a candidate), and sweeps where they stood and stand
            for (int round = 0; round < 6; round++)
            {
                for (int k = 0; k < 25; k++)
                {
                    StandInPlaced t = set.targets[random.Next(set.targets.Count)];
                    if (t.at == null) continue;
                    Vector3 before = t.at.position;
                    switch (random.Next(9))
                    {
                        case 0: t.at.position += new Vector3((float)(random.NextDouble() * 6.0 - 3.0), (float)random.NextDouble(), (float)(random.NextDouble() * 6.0 - 3.0)); Tell(t); break;
                        case 1: t.at.rotation = RandomTurn(); Tell(t); break;
                        case 2: t.at.SetParent(set.parents[random.Next(set.parents.Count)], random.Next(2) == 0); Tell(t); break;
                        case 3: t.at.localScale = new Vector3(1f + (float)random.NextDouble() * 3f, 1f, 0.5f); Tell(t); break;
                        case 4: t.at.gameObject.SetActive(!t.at.gameObject.activeSelf); break;   // its state is read when a sweep comes near: nothing to tell
                        case 5: if (round == 4) UnityEngine.Object.DestroyImmediate(t.at.gameObject); break;
                        case 6: detector.RemovePlaced(t); break;
                        case 7: t.candidate = true; t.Source = default; t.answers = 0; Add(t); break;   // taken back, a candidate again
                        default:
                            Transform parent = set.parents[random.Next(set.parents.Count)];
                            parent.SetPositionAndRotation(parent.position + new Vector3(0.7f, 0.1f, -0.4f), parent.rotation * Quaternion.Euler(3f, 11f, 2f));
                            parent.localScale *= 1.1f;
                            foreach (StandInPlaced under in set.targets) if (under.at != null && under.at.IsChildOf(parent)) Tell(under);   // a parent moved: every one under it stands elsewhere
                            break;
                    }

                    slash++;
                    hits += Update(Quad(slash, before + new Vector3(0f, 0.6f, 0f), Quaternion.identity, 5f, 2f));
                    if (t.at != null)
                    {
                        slash++;
                        hits += Update(Quad(slash, t.at.position + t.at.rotation * new Vector3(0f, 0.6f, 0f), t.at.rotation, 5f, 2f));
                    }
                }
            }

            // 4. the boundary: sweeps that touch a target's convex -- at the top of its box exactly, one float above and below,
            //    and ending exactly at its lowest and highest x -- for targets standing straight and for turned ones (the
            //    sweep taken through a corner as the target's frame carries it)
            Vector3[] vs = data.hulls[0].vertices;
            float top = vs.Max(v => v.y), minX = vs.Min(v => v.x), maxX = vs.Max(v => v.x), minZ = vs.Min(v => v.z), maxZ = vs.Max(v => v.z);
            Vector3 topVertex = vs.First(v => v.y == top), minXVertex = vs.First(v => v.x == minX);
            foreach (StandInPlaced t in set.targets.Where(x => x.at != null && x.IsHitTarget).Take(120).ToArray())
            {
                Vector3 p = t.at.position;
                Quaternion q = t.at.rotation;
                foreach (float dy in new[] { 0f, NextFloat(top, 1) - top, NextFloat(top, -1) - top, 1e-5f, -1e-5f, 1e-3f })
                {
                    slash++;
                    Vector3 through = p + q * new Vector3(topVertex.x, top + dy, topVertex.z);
                    int n = Update(Quad(slash, through, q, 4f, 4f));
                    hits += n; boundaryHits += n;
                }

                foreach (float dx in new[] { 0f, 1e-6f, -1e-6f, 1e-4f })
                {
                    slash++;
                    // travelling along the frame's x up to the lowest x exactly: the quad ends where the convex begins
                    Vector3 end = p + q * new Vector3(minX + dx, minXVertex.y, minXVertex.z);
                    Vector3 s = q * Vector3.forward, tr = q * Vector3.right;
                    Vector3 a1 = end - 2f * s, b1 = end + 2f * s, a0 = a1 - 1.5f * tr, b0 = b1 - 1.5f * tr;
                    int n = Update(new SlashSweep(slash, 0.0, false, new Plane(q * Vector3.up, end), tr, s, a0, b0, a1, b1));
                    hits += n; boundaryHits += n;
                }
            }

            // 5. the world ends: nothing is a target any more
            foreach (StandInPlaced t in set.targets) t.worldOpen = false;
            slash++;
            hits += Update(Quad(slash, new Vector3(40f, 0.6f, 30f), Quaternion.identity, 200f, 200f));
        }

        [UnityTest]
        public IEnumerator PlacedBox_PassesOverOnlyWhatTheFrameTestWouldNotHit_SameCutsOrderAnswersAndCandidates()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(PlacedPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            var logs = new List<List<string>>();
            var counts = new List<(int hits, int boundary, long offered, long passed)>();
            string[] names =
            {
                "box off (as before the box)", "box on , state and frame read apart", "box on , one read giving the frame", "box on , one read giving position and rotation",
                "box on , position and rotation, the state through the held object", "box on , frame, the state through the held object", "box off, the state through the held object",
            };
            foreach ((bool box, bool reuse, bool pose, bool heldState) in new[] { (false, false, false, false), (true, false, false, false), (true, true, false, false), (true, true, true, false), (true, true, true, true), (true, true, false, true), (false, false, false, true) })
            {
                using (StandInSet set = NewStandInSet(root, data, 300, 20, true))
                {
                    StandInPlaced.heldObject = heldState;
                    var detector = new SlashHitDetector(root, in k_hitSettings) { placedBoxReject = box, placedMergedRead = reuse, placedPoseRead = pose };
                    long passed0 = detector.PlacedPassedOver;
                    RunPlacedBoxScript(detector, set, data, out int hits, out int boundary);
                    counts.Add((hits, boundary, 0L, detector.PlacedPassedOver - passed0));
                    logs.Add(new List<string>(set.log));
                }

                yield return null;
            }

            for (int c = 0; c < counts.Count; c++)
            {
                TestContext.Out.WriteLine(names[c] + ": hits " + counts[c].hits + " (boundary sweeps " + counts[c].boundary + "), tests offered " + counts[c].offered + ", passed over " + counts[c].passed + ", log lines " + logs[c].Count);
            }

            Assert.That(counts[0].hits, Is.GreaterThan(200), "the script meets targets");
            Assert.That(counts[0].boundary, Is.GreaterThan(50), "the boundary sweeps meet targets");
            Assert.That(counts[0].passed, Is.EqualTo(0), "off: nothing is passed over");
            Assert.That(counts[1].passed, Is.GreaterThan(counts[1].offered / 2), "on: most tests are passed over");
            for (int c = 1; c < logs.Count; c++)
            {
                Assert.That(logs[c].Count, Is.EqualTo(logs[0].Count), names[c] + ": the same number of cuts asked and updates");
                Assert.That(counts[c].offered, Is.EqualTo(counts[0].offered), names[c] + ": the same targets offered");
                for (int i = 0; i < logs[0].Count; i++)
                {
                    if (logs[0][i] != logs[c][i]) Assert.Fail(names[c] + ": line " + i + " differs:\n  before: " + logs[0][i] + "\n  now   : " + logs[c][i]);
                }
            }
        }

        [UnityTest]
        public IEnumerator PlacedBox_ATargetWhoseBoxCannotBeMade_IsTestedInItsFrameAsBefore()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(PlacedPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            using (StandInSet set = NewStandInSet(root, data, 2, 2, false))
            {
                // A target whose frame is not a finite matrix: no box can be made of it, so it is not passed over -- it goes
                // to the test in its frame as it did before (which finds nothing in such a frame, and throws nothing).
                var detector = new SlashHitDetector(root, in k_hitSettings);
                set.targets[0].frame = new float4x4(float.NaN);
                foreach (StandInPlaced t in set.targets) detector.AddPlaced(t);
                long passed0 = detector.PlacedPassedOver;
                detector.Evaluate(new[] { Quad(1, set.targets[1].at.position + new Vector3(0f, 0.6f, 0f), Quaternion.identity, 5f, 2f) }, new long[] { 1 });
                Assert.That(detector.HitCount, Is.EqualTo(1), "the standing target is hit");
                Assert.That(detector.PlacedPassedOver - passed0, Is.EqualTo(0), "the target without a box is not passed over");
            }
        }

        [UnityTest]
        public IEnumerator PlacedBox_Cost_OneUpdateOver885StandingTargets_BothWays()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(PlacedPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            using (StandInSet set = NewStandInSet(root, data, 885, 30, false))
            {
                foreach (StandInPlaced t in set.targets) { t.log = null; t.answer = x => new SlashPlacedCutResult(ProvisionalCutAcceptance.NotAccepted, LogicalCutAdmission.SourceNotLive, default, false); }
                var detector = new SlashHitDetector(root, in k_hitSettings);
                foreach (StandInPlaced t in set.targets) detector.AddPlaced(t);
                var random = new System.Random(5);
                const int updates = 1500;
                var sweeps = new SlashSweep[updates][];
                for (int u = 0; u < updates; u++)
                {
                    // a wave some 3 m wide moving 0.4 m an update, somewhere over the field; every third update carries two
                    var c = new Vector3((float)(random.NextDouble() * 116.0), 0.6f, (float)(random.NextDouble() * 116.0));
                    Quaternion turn = Quaternion.Euler(0f, (float)(random.NextDouble() * 360.0), (float)(random.NextDouble() * 30.0 - 15.0));
                    sweeps[u] = u % 3 == 0 ? new[] { Quad(0, c, turn, 3f, 0.4f), Quad(0, c + new Vector3(1f, 0f, 1f), turn, 3f, 0.4f) } : new[] { Quad(0, c, turn, 3f, 0.4f) };
                }

                // The readings themselves, one after the other over the 885 stand-ins (their members read the Transform as the
                // real candidate's do): the state, the frame, and both at once.
                {
                    const int loops = 300;
                    int targets = 0;
                    float sum = 0f;
                    var w1 = Stopwatch.StartNew();
                    for (int l = 0; l < loops; l++) foreach (StandInPlaced t in set.targets) if (t.IsHitTarget) targets++;
                    w1.Stop();
                    var w2 = Stopwatch.StartNew();
                    for (int l = 0; l < loops; l++) foreach (StandInPlaced t in set.targets) sum += t.FrameToWorld.c3.x;
                    w2.Stop();
                    var w3 = Stopwatch.StartNew();
                    for (int l = 0; l < loops; l++) foreach (StandInPlaced t in set.targets) if (t.TryGetHitFrame(out float4x4 f)) sum += f.c3.x;
                    w3.Stop();
                    double per = 1e6 / (loops * (double)set.targets.Count);
                    TestContext.Out.WriteLine("885 readings, ns each (this Editor, the stand-ins): IsHitTarget " + (w1.Elapsed.TotalMilliseconds * per).ToString("F0", CultureInfo.InvariantCulture)
                        + "; FrameToWorld " + (w2.Elapsed.TotalMilliseconds * per).ToString("F0", CultureInfo.InvariantCulture)
                        + "; both apart " + ((w1.Elapsed.TotalMilliseconds + w2.Elapsed.TotalMilliseconds) * per).ToString("F0", CultureInfo.InvariantCulture)
                        + "; TryGetHitFrame (both at once) " + (w3.Elapsed.TotalMilliseconds * per).ToString("F0", CultureInfo.InvariantCulture) + " (targets " + targets / loops + ", checksum " + sum.ToString("R", CultureInfo.InvariantCulture) + ")");
                }

                long slash = 1000;
                (bool box, bool reuse, bool pose, bool heldState, string name)[] ways =
                {
                    (false, false, false, false, "box off (as before the box)"), (true, false, false, false, "box on , state and frame read apart"),
                    (true, true, false, false, "box on , one read giving the frame"), (true, true, true, false, "box on , one read giving position and rotation"),
                    (true, true, false, true, "box on , frame, state through the held object"), (true, true, true, true, "box on , position and rotation, state through the held object"),
                };
                var ms = new Dictionary<int, List<double>>();
                var hitsBy = new Dictionary<int, List<int>>();
                var tallies = new Dictionary<int, (long offered, long passed)>();
                string[] markers = { "Zantetsu.SlashHit.Find", "Zantetsu.SlashHit.Find.Collect", "Zantetsu.SlashHit.Find.Lists", "Zantetsu.SlashHit.Find.Placed", "Zantetsu.SlashHit.Find.Fragments" };
                var recorders = markers.Select(m => Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Scripts, m, 4, Unity.Profiling.ProfilerRecorderOptions.SumAllSamplesInFrame | Unity.Profiling.ProfilerRecorderOptions.StartImmediately)).ToArray();
                var markerUs = new Dictionary<int, List<double[]>>();
                for (int w = 0; w < ways.Length; w++) { ms[w] = new List<double>(); hitsBy[w] = new List<int>(); markerUs[w] = new List<double[]>(); }
                for (int round = 0; round < 7; round++)
                {
                    for (int k0 = 0; k0 < ways.Length; k0++)
                    {
                        int box = (k0 + round) % ways.Length;   // the order turns from round to round
                        detector.placedBoxReject = ways[box].box;
                        detector.placedMergedRead = ways[box].reuse;
                        detector.placedPoseRead = ways[box].pose;
                        StandInPlaced.heldObject = ways[box].heldState;
                        yield return null;   // a frame of its own for this round, so the markers read are this round's only
                        long before = recorders[0].CurrentValue;
                        long passed0 = detector.PlacedPassedOver;
                        int hits = 0;
                        var live = new long[1];
                        var watch = Stopwatch.StartNew();
                        for (int u = 0; u < updates; u++)
                        {
                            slash++;
                            SlashSweep[] s = sweeps[u];
                            for (int k = 0; k < s.Length; k++) s[k] = new SlashSweep(slash, 0.0, false, s[k].SourceSlashPlane, s[k].TravelAxis, s[k].SpanAxis, s[k].PreviousA, s[k].PreviousB, s[k].CurrentA, s[k].CurrentB);
                            live[0] = slash;
                            detector.Evaluate(s, live);
                            hits += detector.HitCount;
                        }

                        watch.Stop();
                        // The markers of this frame so far: the round ran in this frame alone (it began after a yield).
                        double[] now = recorders.Select(r => r.CurrentValue / 1000.0 / updates).ToArray();
                        if (round > 0) { ms[box].Add(watch.Elapsed.TotalMilliseconds); hitsBy[box].Add(hits); markerUs[box].Add(now); }
                        tallies[box] = (0L, detector.PlacedPassedOver - passed0);
                    }
                }

                double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
                TestContext.Out.WriteLine("885 standing targets, " + updates + " updates (a third with two sweeps), 6 measured rounds each way, this Editor (Mono), whole Evaluate:");
                for (int w = 0; w < ways.Length; w++)
                {
                    TestContext.Out.WriteLine("  " + ways[w].name + ": median " + (1000.0 * Median(ms[w]) / updates).ToString("F1", CultureInfo.InvariantCulture) + " us an update (rounds ms " + string.Join(" ", ms[w].Select(x => x.ToString("F0", CultureInfo.InvariantCulture))) + "); hits a round " + hitsBy[w][0] + "; tests offered " + tallies[w].offered + ", passed over " + tallies[w].passed);
                }

                foreach (Unity.Profiling.ProfilerRecorder r in recorders) r.Dispose();
                for (int w = 0; w < ways.Length; w++)
                {
                    Assert.That(hitsBy[w].Distinct().Count(), Is.EqualTo(1), ways[w].name + ": the same hits every round");
                    Assert.That(hitsBy[w][0], Is.EqualTo(hitsBy[0][0]), ways[w].name + ": the same number of hits as before");
                }

                Assert.That(hitsBy[0][0], Is.GreaterThan(100), "the sweeps meet targets");
            }
        }

        // The readings of the real candidate (PlacedCuttableCandidate), 885 of them standing, under sweeps that pass
        // overhead and meet nothing: what an update costs for the state, the frame and the box alone, each way.
        [UnityTest]
        public IEnumerator PlacedBox_Cost_RealCandidates_885_SweepsThatMeetNothing()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(PlacedPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            var objects = new List<GameObject>();
            try
            {
                var detector = new SlashHitDetector(root, in k_hitSettings);
                for (int i = 0; i < 885; i++)
                {
                    var o = new GameObject("real candidate " + i);
                    objects.Add(o);
                    o.transform.SetPositionAndRotation(new Vector3(4f * (i % 30), 0f, 4f * (i / 30)), Quaternion.Euler(0f, 37f * i, 0f));
                    detector.AddPlaced(Candidate(root, data, o, new Renderer[0], new Collider[0]));
                }

                var random = new System.Random(11);
                const int updates = 1500;
                var sweeps = new SlashSweep[updates][];
                for (int u = 0; u < updates; u++)
                {
                    var c = new Vector3((float)(random.NextDouble() * 116.0), 50f, (float)(random.NextDouble() * 116.0));
                    Quaternion turn = Quaternion.Euler(0f, (float)(random.NextDouble() * 360.0), 0f);
                    sweeps[u] = u % 3 == 0 ? new[] { Quad(0, c, turn, 3f, 0.4f), Quad(0, c + new Vector3(1f, 0f, 1f), turn, 3f, 0.4f) } : new[] { Quad(0, c, turn, 3f, 0.4f) };
                }

                (bool box, bool merged, bool pose, bool heldState, string name)[] ways =
                {
                    (false, false, false, false, "box off (as before the box)"), (true, false, false, false, "box on , state and frame read apart"),
                    (true, false, false, true, "box on , read apart, state through the held object"),
                    (true, true, false, false, "box on , one read giving the frame"), (true, true, true, false, "box on , one read giving position and rotation"),
                    (true, true, false, true, "box on , frame, state through the held object"), (true, true, true, true, "box on , position and rotation, state through the held object"),
                };
                var ms = new List<double>[ways.Length];
                var hitsBy = new int[ways.Length];
                var passed = new long[ways.Length];
                for (int w = 0; w < ways.Length; w++) ms[w] = new List<double>();
                long slash = 5000;
                for (int round = 0; round < 7; round++)
                {
                    for (int k0 = 0; k0 < ways.Length; k0++)
                    {
                        int w = (k0 + round) % ways.Length;
                        detector.placedBoxReject = ways[w].box;
                        detector.placedMergedRead = ways[w].merged;
                        detector.placedPoseRead = ways[w].pose;
                        PlacedCuttableCandidate.stateFromHeldObject = ways[w].heldState;
                        long passed0 = detector.PlacedPassedOver;
                        int hits = 0;
                        var live = new long[1];
                        var watch = Stopwatch.StartNew();
                        for (int u = 0; u < updates; u++)
                        {
                            slash++;
                            SlashSweep[] s = sweeps[u];
                            for (int k = 0; k < s.Length; k++) s[k] = new SlashSweep(slash, 0.0, false, s[k].SourceSlashPlane, s[k].TravelAxis, s[k].SpanAxis, s[k].PreviousA, s[k].PreviousB, s[k].CurrentA, s[k].CurrentB);
                            live[0] = slash;
                            detector.Evaluate(s, live);
                            hits += detector.HitCount;
                        }

                        watch.Stop();
                        if (round > 0) ms[w].Add(watch.Elapsed.TotalMilliseconds);
                        hitsBy[w] += hits;
                        passed[w] = detector.PlacedPassedOver - passed0;
                    }

                    yield return null;
                }

                double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
                TestContext.Out.WriteLine("885 real candidates, " + updates + " updates of sweeps that meet nothing (a third with two sweeps), 6 measured rounds each way, this Editor (Mono), whole Evaluate:");
                for (int w = 0; w < ways.Length; w++)
                {
                    TestContext.Out.WriteLine("  real: " + ways[w].name + ": median " + (1000.0 * Median(ms[w]) / updates).ToString("F1", CultureInfo.InvariantCulture) + " us an update (rounds ms "
                        + string.Join(" ", ms[w].Select(x => x.ToString("F0", CultureInfo.InvariantCulture))) + "); hits " + hitsBy[w] + "; passed over by the box in a round " + passed[w]);
                    Assert.That(hitsBy[w], Is.EqualTo(0), ways[w].name + ": the sweeps meet nothing");
                }

                Assert.That(detector.PlacedCount, Is.EqualTo(885), "every candidate is still a candidate");
            }
            finally
            {
                PlacedCuttableCandidate.stateFromHeldObject = true;
                DisposeCandidates();
                foreach (GameObject o in objects) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            }
        }
    }
}
