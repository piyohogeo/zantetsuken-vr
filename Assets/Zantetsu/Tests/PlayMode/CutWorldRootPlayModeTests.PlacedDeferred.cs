using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// A placed cuttable deferred until its first cut (TL, 2026-10-03; DESIGN 4.5.1): before a hit it is a cut target
    /// only -- nothing of it in the world's storage, display or physics, its renderers and colliders as placed -- and the
    /// first cut prepares its input for it alone and takes the drawing over without a frame drawn twice or not at all;
    /// a preparation that is refused leaves the instance as it was (for good, or a candidate still); two Slashes meeting
    /// it in one update make one cut; its pieces are cut again the ordinary way; a building goes into the hull trial at
    /// its first hit unshown, its own drawing kept while Pending and handed over at the first publication (never both,
    /// never neither, frame by frame), and is taken back out -- the instance as it was, everything given back once -- when
    /// that cut is refused at once, at the display registration, or by a throw there; and the world's ending collects
    /// what was cut while the candidates give back what they hold. Ignored where the licensed inputs are absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private readonly List<PlacedCuttableCandidate> _candidates = new List<PlacedCuttableCandidate>();

        private void DisposeCandidates()
        {
            foreach (PlacedCuttableCandidate c in _candidates) c.Dispose();
            _candidates.Clear();
        }

        private PlacedCuttableCandidate Candidate(CutWorldRoot root, PlacedCuttableInput data, GameObject instance, Renderer[] renderers, Collider[] colliders)
        {
            var c = new PlacedCuttableCandidate(root, data, instance.transform, renderers, colliders, 50f);
            _candidates.Add(c);
            return c;
        }

        // A level sweep through the middle of the input's convexes, as placed at the instance.
        private static SlashSweep ThroughTheMiddle(long slash, PlacedCuttableInput data, Vector3 at)
        {
            Vector3[] all = data.hulls.SelectMany(h => h.vertices).ToArray();
            float lo = all.Min(v => v.y), hi = all.Max(v => v.y);
            float y = at.y + 0.5f * (lo + hi);
            return Level(slash, y, at.x + all.Min(v => v.x) - 1f, at.x + all.Max(v => v.x) + 1f, at.z + all.Min(v => v.z) - 1f, at.z + all.Max(v => v.z) + 1f);
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_UncutIsTheScenesOwn_ThenTheFirstCutTakesTheDrawingOver_NeverTwiceNorNone()
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("bench_001 deferred", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                int groups = root.Storage.VertexGroupCount, shown = root.Display.ShownCount, bodies = Bodies();
                var detector = new SlashHitDetector(root, in k_hitSettings);
                PlacedCuttableCandidate candidate = Candidate(root, data, instance, renderers, colliders);
                detector.AddPlaced(candidate);
                for (int f = 0; f < 3; f++) yield return null;
                Assert.That(candidate.IsHitTarget, Is.True, "a cut target");
                Assert.That(candidate.Source.IsSet, Is.False, "no fragment before a hit");
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "nothing of it in the storage");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "nothing of it in the display");
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body of it");
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "drawn and colliding as placed");

                Evaluate(detector, ThroughTheMiddle(1, data, at), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1), "one hit");
                ProvisionalCutAcceptance accepted = hits[0].Acceptance;
                TestContext.Out.WriteLine("first cut: " + accepted + " " + hits[0].Admission + " operation " + hits[0].Operation + "; " + candidate.LastRefusal);
                Assert.That(accepted == ProvisionalCutAcceptance.Published || accepted == ProvisionalCutAcceptance.Pending, Is.True, "accepted: " + accepted);
                Assert.That(hits[0].Fragment, Is.EqualTo(candidate.Source), "the hit names the fragment it was identified as");
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Cut));
                Assert.That(detector.HasPlaced(candidate), Is.False, "no longer a candidate: its pieces are the ordinary ones");
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups + 1), "its display input, prepared by the hit, once");
                // In the hit's own call: the cut world draws it, and the instance's drawing is off -- neither none nor twice.
                Assert.That(root.Display.ShownCount, Is.GreaterThan(shown), "the cut world draws it from the hit on");
                Assert.That(renderers.All(r => !r.enabled), Is.True, "the instance's drawing is off as the cut world's starts");
                if (accepted == ProvisionalCutAcceptance.Pending)
                {
                    Assert.That(States(renderers, colliders), Is.EqualTo("off,off|on,off"), "Pending: its colliders stand until the publication");
                }

                yield return Until(() => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed, "the cut committed");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "the instance has left at the publication");
                Assert.That(candidate.IsWithdrawn, Is.True);
                Assert.That(root.GeometryFaults, Is.Zero);

                // Its pieces are cut again the ordinary way, by another Slash.
                yield return null;
                Evaluate(detector, ThroughTheMiddle(2, data, at + new Vector3(0f, 0.1f, 0f)), 2);
                List<SlashHitConfirmed> again = Hits(detector);
                TestContext.Out.WriteLine("again: " + string.Join("; ", again.Select(h => h.Fragment + " " + h.Acceptance)));
                Assert.That(again.Count, Is.GreaterThanOrEqualTo(1), "a piece met again");
                Assert.That(again.Any(h => h.Acceptance == ProvisionalCutAcceptance.Published || h.Acceptance == ProvisionalCutAcceptance.Pending), Is.True, "a piece cut again");
                yield return EndWorld(root);
            }
            finally
            {
                DisposeCandidates();
            }
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_ThroughItsRegistrar_ACutTargetOnly_CutByAHit_AndGivenBackWhenDestroyed()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var input = new TextAsset(File.ReadAllText(PlacedPropInputPath)) { name = "bench_001" };
            PlacedCuttableInput data = PlayableCityCuttable.ParsedInput(input);
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                const float scale = 1.25f;
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("bench_001 registrar deferred", at, out Renderer[] renderers, out Collider[] colliders);
                instance.transform.localScale = Vector3.one * scale;
                string asPlaced = States(renderers, colliders);
                int groups = root.Storage.VertexGroupCount, shown = root.Display.ShownCount, bodies = Bodies();
                PlayableCityCuttable c = PlaceRegistrar(root, input, "bench_001 registrar deferred (registrar)", Vector3.zero, 0f, Vector3.one);
                c.target = instance.transform;
                c.instanceRenderers = renderers;
                c.instanceColliders = colliders;
                c.deferUntilCut = true;
                c.DetectorForTest = detector;
                yield return UntilWithin(() => c.IsCutTarget || c.Failure != null, 5f, "the cut target made");
                Assert.That(c.Failure, Is.Null);
                Assert.That(c.Candidate, Is.Not.Null);
                Assert.That(c.IsRegistered, Is.False, "nothing registered into the world");
                Assert.That(c.RegisteredScale, Is.EqualTo(scale).Within(1e-5f), "at the instance's scale");
                Assert.That(detector.HasPlaced(c.Candidate), Is.True, "a candidate of the detector");
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "nothing stored");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "nothing shown");
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body");
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "as placed");
                Assert.That(c.enabled, Is.False, "one turn, then done");

                // Its convexes at the placed size: a sweep through the scaled bench's middle meets it.
                Vector3[] all = data.hulls.SelectMany(h => h.vertices).ToArray();
                float y = at.y + scale * 0.5f * (all.Min(v => v.y) + all.Max(v => v.y));
                Evaluate(detector, Level(1, y, at.x + scale * all.Min(v => v.x) - 1f, at.x + scale * all.Max(v => v.x) + 1f, at.z + scale * all.Min(v => v.z) - 1f, at.z + scale * all.Max(v => v.z) + 1f), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Acceptance == ProvisionalCutAcceptance.Published || hits[0].Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "cut: " + hits[0].Acceptance);
                Assert.That(c.Candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Cut));
                Assert.That(renderers.All(r => !r.enabled), Is.True, "drawn by the cut world from its cut");
                yield return Until(() => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed, "the cut committed");
                PlacedCuttableCandidate candidate = c.Candidate;
                UnityEngine.Object.Destroy(c.gameObject);
                yield return null;
                Assert.That(candidate.HitShape, Is.Null, "the registrar gave its hit shape back when destroyed");
                yield return EndWorld(root);
            }
            finally
            {
                TrackPlacedRegistrars();
            }
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_APendingFirstCut_ItsDisplayDrawsAtOnce_TheInstanceCollidesUntilThePublication()
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("bench_001 pending", at, out Renderer[] renderers, out Collider[] colliders);
                int shown = root.Display.ShownCount;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                PlacedCuttableCandidate candidate = Candidate(root, data, instance, renderers, colliders);
                detector.AddPlaced(candidate);
                root.Driver.RemainingMainSeconds = () => 0;   // the Main budget spent: the publication waits
                Evaluate(detector, ThroughTheMiddle(1, data, at), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending));
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown + 1), "taken by the display in the hit's own call, for this frame's drawing");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|on,off"), "its drawing off at once, its colliders standing");
                Assert.That(candidate.IsWithdrawn, Is.False, "not withdrawn before the publication");
                yield return null;
                Assert.That(root.Display.RenderFragmentCount, Is.GreaterThan(0), "the admitted cut's display draws it");
                for (int f = 0; f < 2; f++) yield return null;
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|on,off"), "while Pending");
                Assert.That(Evaluate2(detector, ThroughTheMiddle(2, data, at)), Is.Empty.Or.All.Matches<SlashHitConfirmed>(h => h.Acceptance != ProvisionalCutAcceptance.Published && h.Acceptance != ProvisionalCutAcceptance.Pending),
                    "no second cut of it while Pending");
                root.Driver.RemainingMainSeconds = null;
                yield return Until(() => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed, "the cut committed");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "the instance has left at the publication");
                Assert.That(candidate.IsWithdrawn, Is.True);
                yield return EndWorld(root);
            }
            finally
            {
                if (root != null && root.Driver != null) root.Driver.RemainingMainSeconds = null;
                DisposeCandidates();
            }
        }

        private static List<SlashHitConfirmed> Evaluate2(SlashHitDetector detector, SlashSweep sweep)
        {
            detector.Evaluate(new[] { sweep }, new long[] { sweep.SlashId });
            return Hits(detector);
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_ARefusedPreparation_LeavesTheInstanceAsItWas()
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            var broken = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            broken.render[broken.indices[0]].normal = Vector3.zero;   // the cut input gate refuses a zero normal
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                int groups = root.Storage.VertexGroupCount, shown = root.Display.ShownCount, bodies = Bodies(), free = root.Storage.FreeVertexRoom;

                // An empty side: answered before anything is made; a candidate still, as it was.
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("bench_001 empty side", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                PlacedCuttableCandidate candidate = Candidate(root, data, instance, renderers, colliders);
                Assert.That(candidate.TryIdentify(out LogicalFragmentId fragment), Is.True);
                SlashPlacedCutResult empty = candidate.TryCut(new SlashPlacedHit(new float4(0f, 1f, 0f, -50f), new float4(0f, 1f, 0f, -49f), (float3)at, 7, 1, 0.0, new float3(1f, 0f, 0f), 0f, 0f));
                TestContext.Out.WriteLine("empty side: " + empty.Acceptance + " " + empty.Admission + " done " + empty.Done + "; " + candidate.LastRefusal);
                Assert.That(empty.Acceptance, Is.EqualTo(ProvisionalCutAcceptance.EmptySide));
                Assert.That(empty.Done, Is.False, "a candidate still");
                Assert.That(candidate.IsHitTarget, Is.True);
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "as placed");
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "nothing stored");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "nothing shown");

                // The gate refuses its geometry: refused for good, the instance as it was, nothing kept.
                GameObject brokenInstance = RefusalInstance("bench_001 refused by the gate", at + new Vector3(6f, 0f, 0f), out Renderer[] brokenRenderers, out Collider[] brokenColliders);
                PlacedCuttableCandidate refused = Candidate(root, broken, brokenInstance, brokenRenderers, brokenColliders);
                detector.AddPlaced(refused);
                Evaluate(detector, ThroughTheMiddle(1, broken, at + new Vector3(6f, 0f, 0f)), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1));
                TestContext.Out.WriteLine("gate: " + hits[0].Acceptance + "; " + refused.LastRefusal);
                Assert.That(hits[0].Acceptance, Is.Not.EqualTo(ProvisionalCutAcceptance.Published).And.Not.EqualTo(ProvisionalCutAcceptance.Pending));
                Assert.That(refused.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Refused));
                Assert.That(refused.LastRefusal, Does.Contain("refused"));
                Assert.That(detector.HasPlaced(refused), Is.False, "no longer a candidate");
                Assert.That(States(brokenRenderers, brokenColliders), Is.EqualTo(asPlaced), "drawn and colliding as placed");
                Assert.That(root.Ledger.IsCurrentTarget(refused.Source), Is.False, "its fragment retired");
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "nothing kept in the storage");
                Assert.That(root.Storage.FreeVertexRoom, Is.EqualTo(free), "its room untouched");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown));
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body made");
                yield return EndWorld(root);
            }
            finally
            {
                DisposeCandidates();
            }
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_TheDisplayRefusing_LeavesTheInstanceAsItWas_AndGivesItsRoomBack()
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            CutWorldRoot root = NewHullWorld(0.9f);   // binds the source materials 7 and 2 only: the input's 0 is refused at the display
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("bench_001 display refused", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                int groups = root.Storage.VertexGroupCount, shown = root.Display.ShownCount, bodies = Bodies(), free = root.Storage.FreeVertexRoom;
                PlacedCuttableCandidate candidate = Candidate(root, data, instance, renderers, colliders);
                detector.AddPlaced(candidate);
                Evaluate(detector, ThroughTheMiddle(1, data, at), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1));
                TestContext.Out.WriteLine("display: " + hits[0].Acceptance + "; " + candidate.LastRefusal);
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Refused));
                Assert.That(candidate.LastRefusal, Does.Contain("display"));
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "drawn and colliding as placed");
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "its vertex group given back");
                Assert.That(root.Storage.FreeVertexRoom, Is.EqualTo(free), "its room given back");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown));
                Assert.That(Bodies(), Is.EqualTo(bodies));
                Assert.That(root.Ledger.IsCurrentTarget(candidate.Source), Is.False, "its fragment retired");
                for (int f = 0; f < 5; f++) yield return null;
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "and not taken again");
                yield return EndWorld(root);
            }
            finally
            {
                DisposeCandidates();
            }
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_TwoSlashesInOneUpdate_MakeOneCut_TheOtherFindsItTaken()
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("bench_001 two slashes", at, out Renderer[] renderers, out Collider[] colliders);
                int groups = root.Storage.VertexGroupCount;
                PlacedCuttableCandidate candidate = Candidate(root, data, instance, renderers, colliders);
                detector.AddPlaced(candidate);
                SlashSweep a = ThroughTheMiddle(1, data, at), b = ThroughTheMiddle(2, data, at + new Vector3(0f, 0.05f, 0f));
                detector.Evaluate(new[] { a, b }, new long[] { 1, 2 });
                List<SlashHitConfirmed> hits = Hits(detector);
                TestContext.Out.WriteLine("hits: " + string.Join("; ", hits.Select(h => h.SlashId + " " + h.Fragment + " " + h.Acceptance + " " + h.Admission)));
                Assert.That(hits.Count, Is.EqualTo(2), "each Slash met it");
                Assert.That(hits.Count(h => h.Acceptance == ProvisionalCutAcceptance.Published || h.Acceptance == ProvisionalCutAcceptance.Pending), Is.EqualTo(1), "one cut");
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups + 1), "prepared once");
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Cut));
                Assert.That(renderers.All(r => !r.enabled), Is.True, "drawn by the cut world, once");
                yield return EndWorld(root);
            }
            finally
            {
                DisposeCandidates();
            }
        }

        // The source material 0 bound as well, as the walk city's world binds it (the hull trial's kinematic world binds 7 and 2).
        private void BindSourceZero(CutWorldRoot root)
        {
            var field = typeof(CutWorldRoot).GetField("materials", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var bound = (CutWorldRoot.MaterialBinding[])field.GetValue(root);
            var first = new CutWorldRoot.MaterialBinding { sourceIndex = 0, material = Track(new Material(Shader.Find("Zantetsu/VP Indexed Indirect Unlit")) { name = "first" }) };
            field.SetValue(root, bound.Concat(new[] { first }).ToArray());
        }

        // A deferred building's state each frame: its own drawing on and nothing of it shown, or its own drawing off and
        // the display holding it -- never both, never neither.
        private static string HandoverState(Renderer[] renderers, Collider[] colliders, CutWorldRoot root, int shownBefore)
        {
            bool own = renderers[0].enabled, shownNow = root.Display.ShownCount > shownBefore;
            return own == !shownNow ? (own ? "own" : "display") : (own ? "BOTH" : "NONE") + " (" + States(renderers, colliders) + ", shown " + root.Display.ShownCount + ")";
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_ABuilding_PendingKeepsItsOwnDrawing_TheFirstPublicationTakesItOver_ThenTheEndingCollects()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            var bench = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            CutWorldRoot root = NewKinematicWorld(BindSourceZero);
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("college_001 deferred", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                int groups = root.Storage.VertexGroupCount, hullGroups = root.Hulls.GroupCount, shown = root.Display.ShownCount, handedOver = root.Hulls.HandedOver;
                var candidate = new PlacedCuttableCandidate(root, college, instance.transform, renderers, colliders, 10000f, SideMaterial);
                _candidates.Add(candidate);
                detector.AddPlaced(candidate);
                // An uncut prop beside it, never hit: a candidate at the ending.
                GameObject benchInstance = RefusalInstance("bench_001 never hit", at + new Vector3(30f, 0f, 0f), out Renderer[] benchRenderers, out Collider[] benchColliders);
                PlacedCuttableCandidate uncut = Candidate(root, bench, benchInstance, benchRenderers, benchColliders);
                detector.AddPlaced(uncut);
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups), "no group before a hit");

                Evaluate(detector, ThroughTheMiddle(1, college, at), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                TestContext.Out.WriteLine("building: " + string.Join("; ", hits.Select(h => h.Fragment + " " + h.Acceptance)) + "; " + candidate.LastRefusal);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending), "the group took the hit");
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Cut));
                Assert.That(candidate.Registration.Fragment, Is.EqualTo(candidate.Source), "the trial took the fragment the hit identified");
                Assert.That(candidate.Registration.Group.AwaitsFirstCut, Is.True, "its first cut is to take the drawing over");
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups + 1), "its group made by the hit");
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups + 1), "its display input, once");
                // Pending: nothing of it shown, its own drawing and colliders as placed.
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "not shown while Pending");
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "its own drawing and colliders while Pending");

                // Frame by frame: its own drawing until the display has the cut, then the display's -- never both, never neither.
                var seen = new List<string>();
                int framesOwn = 0;
                for (int f = 0; f < 600 && candidate.Registration != null && candidate.Registration.Group.AwaitsFirstCut; f++)
                {
                    yield return null;
                    string state = HandoverState(renderers, colliders, root, shown);
                    if (seen.Count == 0 || seen[seen.Count - 1] != state) seen.Add(state);
                    if (state == "own") framesOwn++;
                }

                TestContext.Out.WriteLine("states: " + string.Join(" -> ", seen) + "; frames with its own drawing after the hit " + framesOwn);
                Assert.That(candidate.Registration.Group.AwaitsFirstCut, Is.False, "the first cut published");
                Assert.That(root.Hulls.HandedOver, Is.EqualTo(handedOver + 1));
                Assert.That(seen.All(s => s == "own" || s == "display"), Is.True, "never both nor neither: " + string.Join(" -> ", seen));
                Assert.That(seen.Last(), Is.EqualTo("display"));
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "its own drawing and colliders off from the hand-over");
                Assert.That(candidate.IsWithdrawn, Is.True);
                Assert.That(uncut.IsHitTarget, Is.True, "the other is still a candidate");
                for (int f = 0; f < 10; f++) yield return null;

                // The ending: the world collects the cut building; the candidates give back what they hold.
                yield return EndWorld(root);
                DisposeCandidates();
                Assert.That(uncut.HitShape, Is.Null, "its hit shape given back");
                Assert.That(States(benchRenderers, benchColliders), Is.EqualTo(asPlaced), "the never-hit one stays as placed");
            }
            finally
            {
                DisposeCandidates();
            }
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_ABuildingOutsideTheAlwaysKinematicMode_IsRefusedAtItsRegistration_TheInstanceAsItWas()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            CutWorldRoot root = NewPlacedHullWorld();   // the hull trial without its always-kinematic mode: no display-only first publication
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("college_001 not kinematic", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                int groups = root.Storage.VertexGroupCount, hullGroups = root.Hulls.GroupCount, shown = root.Display.ShownCount, bodies = Bodies(), free = root.Storage.FreeVertexRoom;
                var candidate = new PlacedCuttableCandidate(root, college, instance.transform, renderers, colliders, 10000f, SideMaterial);
                _candidates.Add(candidate);
                detector.AddPlaced(candidate);
                Evaluate(detector, ThroughTheMiddle(1, college, at), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                TestContext.Out.WriteLine("not kinematic: " + hits[0].Acceptance + "; " + candidate.LastRefusal);
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.InvalidRequest));
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Refused));
                Assert.That(candidate.LastRefusal, Does.Contain("always-kinematic"));
                Assert.That(root.Ledger.IsCurrentTarget(candidate.Source), Is.False, "its fragment retired");
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "its own drawing and colliders as placed");
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "its display input given back at once (never shown)");
                Assert.That(root.Storage.FreeVertexRoom, Is.EqualTo(free));
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups), "no group");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "never shown");
                yield return null;
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body of it");
                yield return EndWorld(root);
            }
            finally
            {
                DisposeCandidates();
            }
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_ABuildingRefusedAtOnce_IsTakenBackOut_ACandidateAgain_TheInstanceAsItWas()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            CutWorldRoot root = NewKinematicWorld(BindSourceZero);
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("college_001 refused at once", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                int groups = root.Storage.VertexGroupCount, hullGroups = root.Hulls.GroupCount, shown = root.Display.ShownCount, bodies = Bodies();
                int released = root.Geometry.VertexGroupsReleased, takenBack = root.Hulls.UncutTakenBack;
                int hullHits = root.Hulls.Hits.Count, hullRefused = root.Hulls.HitsRefused, hullPending = root.Hulls.HitsPending;
                var candidate = new PlacedCuttableCandidate(root, college, instance.transform, renderers, colliders, 10000f, SideMaterial);
                _candidates.Add(candidate);
                detector.AddPlaced(candidate);
                root.Hulls.refuseHitForTest = g => "refused for the test";
                Evaluate(detector, ThroughTheMiddle(1, college, at), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                LogicalFragmentId first = hits[0].Fragment;
                TestContext.Out.WriteLine("refused at once: " + hits[0].Acceptance + "; " + candidate.LastRefusal);
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.NotAccepted));
                Assert.That(root.Hulls.UncutTakenBack, Is.EqualTo(takenBack + 1), "taken back out");
                Assert.That(root.Hulls.Hits.Count, Is.EqualTo(hullHits + 1), "one hit recorded");
                Assert.That(root.Hulls.Hits[root.Hulls.Hits.Count - 1].outcome, Is.EqualTo("NotAccepted: refused for the test"), "its outcome kept: the take-back does not overwrite it");
                Assert.That(root.Hulls.HitsRefused, Is.EqualTo(hullRefused + 1), "one outcome, given once");
                Assert.That(root.Hulls.HitsPending, Is.EqualTo(hullPending), "none left without one");
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Candidate), "a candidate again");
                Assert.That(candidate.Source.IsSet, Is.False, "its fragment let go");
                Assert.That(root.Ledger.IsCurrentTarget(first), Is.False, "the fragment it was identified as retired");
                Assert.That(detector.HasPlaced(candidate), Is.True, "still a candidate of the detector");
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups), "its group gone");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "never shown");
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "its own drawing and colliders as placed");
                yield return Until(() => root.Storage.VertexGroupCount == groups, "its display input's vertex room given back by the world's reclamation");
                Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "once");
                yield return null;
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body of it");

                // Let the trial accept: the same candidate is cut by the next Slash.
                root.Hulls.refuseHitForTest = null;
                Evaluate(detector, ThroughTheMiddle(2, college, at), 2);
                List<SlashHitConfirmed> again = Hits(detector);
                Assert.That(again.Count, Is.EqualTo(1));
                Assert.That(again[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending), "its next hit taken");
                Assert.That(again[0].Fragment, Is.Not.EqualTo(first), "a fresh fragment");
                yield return Until(() => !candidate.Registration.Group.AwaitsFirstCut, "its first cut published");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"));
                yield return EndWorld(root);
            }
            finally
            {
                if (root != null && root.Hulls != null) root.Hulls.refuseHitForTest = null;
                DisposeCandidates();
            }
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_ABuildingWhoseDisplayRefusesAtThePublication_IsTakenBackOut_TheInstanceAsItWas()
        {
            yield return BuildingRefusedAtThePublication(false);
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_ABuildingWhoseDisplayRegistrationThrows_IsTakenBackOut_TheInstanceAsItWas()
        {
            yield return BuildingRefusedAtThePublication(true);
        }

        // Pending, then its first publication refused at the display registration (its source material not bound, or the
        // registration throwing): the cut ends without a publication and the trial takes the building back out at the
        // Step's end; the instance never left; everything prepared is given back once.
        private IEnumerator BuildingRefusedAtThePublication(bool throws)
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            CutWorldRoot root = throws ? NewKinematicWorld(BindSourceZero) : NewKinematicWorld();   // the latter binds the source materials 7 and 2 only
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("college_001 refused at the publication", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                int groups = root.Storage.VertexGroupCount, hullGroups = root.Hulls.GroupCount, shown = root.Display.ShownCount, bodies = Bodies();
                int released = root.Geometry.VertexGroupsReleased, takenBack = root.Hulls.UncutTakenBack, owners = root.Owners.Count;
                var candidate = new PlacedCuttableCandidate(root, college, instance.transform, renderers, colliders, 10000f, throws ? SideMaterial : 0);
                _candidates.Add(candidate);
                detector.AddPlaced(candidate);
                Evaluate(detector, ThroughTheMiddle(1, college, at), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending), "accepted, its publication to come");
                HullGroup group = candidate.Registration.Group;
                LogicalFragmentId fragment = candidate.Source;
                if (throws) group.Handover.show = () => throw new InvalidOperationException("the display registration threw for the test");
                yield return Until(() => group.State == HullGroupState.Gone, "the group taken back out");
                TestContext.Out.WriteLine("taken back: " + candidate.LastRefusal);
                Assert.That(root.Hulls.UncutTakenBack, Is.EqualTo(takenBack + 1), "once");
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Refused), "refused for good");
                Assert.That(candidate.LastRefusal, Does.Contain("taken back out").And.Contain("display"));
                Assert.That(candidate.Registration, Is.Null);
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "its own drawing and colliders never left");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "never shown");
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups), "its group gone");
                Assert.That(root.Ledger.IsCurrentTarget(fragment), Is.False, "its fragment retired");
                Assert.That(root.Geometry.TryGetGeometry(fragment, out _), Is.False, "forgotten by the geometry");
                Assert.That(root.Owners.Count, Is.EqualTo(owners), "its owner retired");
                yield return Until(() => root.Storage.VertexGroupCount == groups, "its display input's vertex room given back");
                Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "once");
                for (int f = 0; f < 5; f++) yield return null;
                Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "and not again");
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body of it");
                Assert.That(GameObject.Find("Building " + college.name), Is.Null, "no actor of it");
                yield return EndWorld(root);
            }
            finally
            {
                DisposeCandidates();
            }
        }

        // The group's own acceptance throwing at the building's first hit (after the hit is recorded and the group
        // reserved): the candidate takes it back out at once -- not through the Step's queue -- and passes the exception
        // on; the instance never left, the group, owner, fragment and geometry go once, the recorded hit gets its one
        // outcome, and nothing of it is published or given back again in the frames after, nor at the world's ending.
        [UnityTest]
        public IEnumerator PlacedDeferred_ABuildingWhoseFirstCutThrows_IsTakenBackOutOnce_TheInstanceAsItWas_NothingPublishedAfter()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            CutWorldRoot root = NewKinematicWorld(BindSourceZero);
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("college_001 first cut throws", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                int groups = root.Storage.VertexGroupCount, hullGroups = root.Hulls.GroupCount, shown = root.Display.ShownCount, bodies = Bodies();
                int released = root.Geometry.VertexGroupsReleased, takenBack = root.Hulls.UncutTakenBack, handedOver = root.Hulls.HandedOver, owners = root.Owners.Count;
                int hullHits = root.Hulls.Hits.Count, hullPending = root.Hulls.HitsPending, hullRefused = root.Hulls.HitsRefused;
                var candidate = new PlacedCuttableCandidate(root, college, instance.transform, renderers, colliders, 10000f, SideMaterial);
                _candidates.Add(candidate);
                detector.AddPlaced(candidate);
                root.Hulls.refuseHitForTest = g => throw new InvalidOperationException("the group's acceptance threw for the test");
                InvalidOperationException thrown;
                try
                {
                    thrown = Assert.Throws<InvalidOperationException>(() => Evaluate(detector, ThroughTheMiddle(1, college, at), 1));
                }
                finally
                {
                    root.Hulls.refuseHitForTest = null;
                }

                LogicalFragmentId fragment = candidate.Source;
                TestContext.Out.WriteLine("first cut threw: " + thrown.Message + "; " + candidate.LastRefusal);
                Assert.That(thrown.Message, Does.Contain("threw for the test"), "the exception passed on");
                Assert.That(fragment.IsSet, Is.True, "the hit identified a fragment");

                // At once, in the same call: taken back out, the instance as it was.
                Assert.That(root.Hulls.UncutTakenBack, Is.EqualTo(takenBack + 1), "taken back out");
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Refused), "refused for good");
                Assert.That(candidate.LastRefusal, Does.Contain("taken back out").And.Contain("threw"));
                Assert.That(candidate.Registration, Is.Null);
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "its own drawing and colliders never left");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "never shown");
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups), "its group gone");
                Assert.That(root.Ledger.IsCurrentTarget(fragment), Is.False, "its fragment retired");
                Assert.That(root.Owners.TryGet(fragment, out _), Is.False, "no owner of the fragment");
                Assert.That(root.Owners.Count, Is.EqualTo(owners), "its owner retired");
                Assert.That(root.Geometry.TryGetGeometry(fragment, out _), Is.False, "forgotten by the geometry");
                Assert.That(root.Hulls.Hits.Count, Is.EqualTo(hullHits + 1), "the hit was recorded");
                Assert.That(root.Hulls.HitsPending, Is.EqualTo(hullPending), "and has its one outcome");
                Assert.That(root.Hulls.Hits[root.Hulls.Hits.Count - 1].outcome, Does.StartWith("Abandoned"));
                Assert.That(root.Hulls.HitsRefused, Is.EqualTo(hullRefused + 1), "its one outcome, counted once");
                yield return Until(() => root.Storage.VertexGroupCount == groups, "its display input's vertex room given back by the world's reclamation");
                Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "once");

                // The frames after, and a next Slash: nothing taken back or given back again, nothing published or shown.
                for (int f = 0; f < 30; f++) yield return null;
                Evaluate(detector, ThroughTheMiddle(2, college, at), 2);
                List<SlashHitConfirmed> after = Hits(detector);
                TestContext.Out.WriteLine("next Slash: " + string.Join("; ", after.Select(h => h.Fragment + " " + h.Acceptance)));
                Assert.That(after.All(h => h.Acceptance == ProvisionalCutAcceptance.NotAccepted), Is.True, "no cut of it accepted again");
                for (int f = 0; f < 30; f++) yield return null;
                Assert.That(root.Hulls.UncutTakenBack, Is.EqualTo(takenBack + 1), "taken back once");
                Assert.That(root.Hulls.HandedOver, Is.EqualTo(handedOver), "never handed over");
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups), "no group again");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "never shown");
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "no display input again");
                Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "given back once");
                Assert.That(root.Hulls.HitsPending, Is.EqualTo(hullPending), "no hit left without its outcome");
                Assert.That(root.Hulls.HitsRefused, Is.EqualTo(hullRefused + 1), "no outcome given again");
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "its own drawing and colliders as placed");
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body of it");
                Assert.That(GameObject.Find("Building " + college.name), Is.Null, "no actor of it");

                // The world's ending: confirmed on the ordinary frames; the instance stays as placed.
                yield return EndWorld(root);
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "the instance as placed after the ending");
            }
            finally
            {
                if (root != null && root.Hulls != null) root.Hulls.refuseHitForTest = null;
                DisposeCandidates();
            }
        }
    }
}
