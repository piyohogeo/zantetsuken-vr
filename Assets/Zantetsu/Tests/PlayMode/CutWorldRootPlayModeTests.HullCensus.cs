using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The city check's count of the hull trial's buildings (<see cref="HullBuildingCensus"/>) beside the group
    /// collection (DESIGN 7.10): a building ended whole by a collection of its own group is no missing building, and
    /// what it left and how its original came back are read; a building gone any other way, a standing one short of
    /// its collider, and an original back otherwise than it stood are faults still. Through the product's own world,
    /// a placed building cut by a hit. Ignored where the licensed input is absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private sealed class CensusCase
        {
            public CutWorldRoot root;
            public CutFragmentGroupCollection groups;
            public SlashHitDetector detector;
            public PlacedCuttableInput college;
            public Vector3 at;
            public GameObject instance;
            public Renderer[] renderers;
            public Collider[] colliders;
            public string asPlaced;
            public PlacedCuttableCandidate candidate;
            public HullBuildingCensus census;
            public HullGroup group;
            public LogicalFragmentId first;
            public List<LogicalFragmentId> pieces;
        }

        /// <summary>The census reads this frame (once a frame), then the condition is asked; a frame at a time until it holds.</summary>
        private static IEnumerator ObservingUntil(HullBuildingCensus census, Func<bool> condition, string what)
        {
            float by = Time.realtimeSinceStartup + 20f;
            while (true)
            {
                census.Observe(Time.frameCount);
                if (condition())
                {
                    yield break;
                }

                Assert.That(Time.realtimeSinceStartup, Is.LessThan(by), "timed out, the census reading every frame: " + what + "; " + census.Describe());
                yield return null;
            }
        }

        private static IEnumerator ObservingFrames(HullBuildingCensus census, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                yield return null;
                census.Observe(Time.frameCount);
            }
        }

        /// <summary>
        /// A placed building given to the census as the scene placed it, cut by a hit into its group, its first cut
        /// published and nothing of it under way, the view 40 m beyond its hull looking away: far and unseen.
        /// </summary>
        private IEnumerator CutABuildingFarAway(CensusCase k, string name)
        {
            k.college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            k.root = NewKinematicWorld(BindSourceZero);
            k.root.Driver.RemainingMainSeconds = () => 1.0;
            k.groups = GroupsOf(k.root, new Vector3(0f, 0f, 400f));
            yield return null;
            k.detector = new SlashHitDetector(k.root, in k_hitSettings);
            k.at = new Vector3(0f, -1f, 0f);
            k.instance = RefusalInstance(name, k.at, out k.renderers, out k.colliders);
            k.asPlaced = States(k.renderers, k.colliders);
            k.candidate = new PlacedCuttableCandidate(k.root, k.college, k.instance.transform, k.renderers, k.colliders, 10000f, SideMaterial);
            _candidates.Add(k.candidate);
            SlashHitDetector detector = k.detector;
            k.candidate.Rearmed = again => detector.AddPlaced(again);
            k.detector.AddPlaced(k.candidate);
            k.census = new HullBuildingCensus(k.root);
            k.census.Add(k.candidate);
            k.census.Observe(Time.frameCount);
            Assert.That(k.census.BuildingsSeen, Is.EqualTo(k.root.Hulls.GroupCount), "the layout: nothing but what the world began with stands");

            yield return null;
            Evaluate(k.detector, ThroughTheMiddle(1, k.college, k.at), 1);
            List<SlashHitConfirmed> hits = Hits(k.detector);
            Assert.That(hits.Count, Is.EqualTo(1));
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending), "the group took the hit");
            k.first = k.candidate.Source;
            k.group = k.candidate.Registration.Group;
            PlacedCuttableCandidate candidate = k.candidate;
            HullGroup group = k.group;
            CutWorldRoot root = k.root;
            yield return ObservingUntil(k.census, () => candidate.Registration != null && !candidate.Registration.Group.AwaitsFirstCut, "the first cut published");
            yield return ObservingUntil(k.census, () => root.Hulls.IsBuildingQuiet(group, out _), "nothing of the building is under way");
            k.pieces = LivePiecesOf(k.root, k.first);
            Assert.That(k.pieces.Count, Is.GreaterThanOrEqualTo(2), "the layout: its display fragments");
            Assert.That(States(k.renderers, k.colliders), Is.EqualTo("off,off|off,off"), "the layout: its own drawing and colliders left at the hand-over");
            Bounds hull = k.group.Collider.bounds;
            k.groups.ViewCamera.transform.position = new Vector3(hull.center.x, hull.center.y, hull.max.z + 40f);
        }

        /// <summary>
        /// **A building ended by a collection of its own group is not a missing building.** Cut, standing: one group,
        /// body, collider and hull at every frame. Collected: from the first frame it is missing it is counted as
        /// ended by a collection -- the count still makes every group the trial made -- and nothing its pieces shared
        /// is found left. Its original is read back only after that, as the census read it before the cut, and the
        /// building cut again is a second building standing. No frame is a fault, and no fault is listed.
        /// </summary>
        [UnityTest]
        public IEnumerator HullCensus_ABuildingEndedByACollectionOfItsGroup_IsNoFault_AndItsReturnIsReadAsItStood()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var k = new CensusCase();
            try
            {
                yield return CutABuildingFarAway(k, "college_001 counted");
                HullBuildingCensus census = k.census;
                CutFragmentGroupCollection groups = k.groups;
                int made = k.root.Hulls.GroupsMade;
                Assert.That(census.FramesNotOneEach, Is.Zero, "standing, cut: " + census.Describe());
                Assert.That(census.EndedByCollection + census.Lost, Is.Zero);

                groups.Settings = GroupsOn(0);
                yield return ObservingUntil(census, () => groups.GroupsRetired == 1, "the building's group was collected");
                Assert.That(groups.BuildingsRetired, Is.EqualTo(1));
                Assert.That(k.root.Hulls.GroupsMade, Is.EqualTo(made), "the layout: a collection makes no group");
                yield return ObservingUntil(census, () => census.EndedByCollection == 1, "read as ended by a collection at the first frame it is missing");
                Assert.That(census.Lost, Is.Zero, census.Describe());
                Assert.That(census.FramesNotOneEach, Is.Zero, "its absence is no fault: " + census.Describe());
                Assert.That(census.Faults, Is.Empty, "nothing its pieces shared is left");

                // (Whether its original is back already at that reading is not this case's: a test's turn does not
                // come between every two frames of the world.)
                yield return ObservingUntil(census, () => census.OriginalsBack == 1, "its original was read back");
                Assert.That(groups.Returned, Is.EqualTo(1));
                Assert.That(States(k.renderers, k.colliders), Is.EqualTo(k.asPlaced));
                Assert.That(census.Faults, Is.Empty, "back as it stood, the display holding nothing of its pieces: " + census.Describe());
                yield return ObservingUntil(census, () => groups.Rearmed == 1, "a cut target again");
                groups.Settings = GroupsOff();

                // Cut again: a second building of the trial stands, the first stays ended.
                yield return null;
                census.Observe(Time.frameCount);
                Evaluate(k.detector, ThroughTheMiddle(2, k.college, k.at), 2);
                Assert.That(Hits(k.detector).Count, Is.EqualTo(1));
                PlacedCuttableCandidate candidate = k.candidate;
                yield return ObservingUntil(census, () => candidate.Registration != null && !candidate.Registration.Group.AwaitsFirstCut, "the second first cut published");
                yield return ObservingFrames(census, 6);
                Assert.That(k.root.Hulls.GroupsMade, Is.EqualTo(made + 1));
                census.AtEnd(Time.frameCount);
                TestContext.Out.WriteLine(census.Describe());
                Assert.That(census.FramesNotOneEach, Is.Zero, census.Describe());
                Assert.That(census.Faults, Is.Empty, census.Describe());
                Assert.That(census.EndedByCollection, Is.EqualTo(1));
                Assert.That(census.Lost, Is.Zero);
                Assert.That(k.root.Display.IsHalted, Is.False);
                Assert.That(k.root.TerminationRequested, Is.False);
                yield return EndWorld(k.root);
            }
            finally
            {
                k.census?.Dispose();
                DisposeCandidates();
            }
        }

        /// <summary>
        /// **What was a fault is one still.** A standing building whose hull collider is off is not one of each at
        /// that frame. And a building that is gone without a group collection of its own group -- its pieces retired
        /// and the building ended by the same calls a collection makes, but by somebody else -- is lost: a fault at
        /// the first frame it is missing and at every frame after, never counted as ended by a collection, and the
        /// trial's count of ended buildings is not the collection's.
        /// </summary>
        [UnityTest]
        public IEnumerator HullCensus_ABuildingShortOfItsCollider_OrGoneWithoutACollection_IsAFaultStill()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var k = new CensusCase();
            HullBuildingCensus second = null;
            try
            {
                yield return CutABuildingFarAway(k, "college_001 lost");
                Assert.That(k.census.FramesNotOneEach, Is.Zero, "the layout: no fault so far: " + k.census.Describe());

                // Its hull collider off for two frames.
                k.group.Collider.enabled = false;
                yield return ObservingFrames(k.census, 2);
                k.group.Collider.enabled = true;
                Assert.That(k.census.FramesNotOneEach, Is.EqualTo(2), k.census.Describe());
                Assert.That(k.census.NotOneEachWhy, Does.Contain("enabled colliders 0"));
                yield return ObservingFrames(k.census, 2);
                Assert.That(k.census.FramesNotOneEach, Is.EqualTo(2), "one of each again");
                k.census.Dispose();

                // Gone outside a collection: the same retirements and the same end, with no collection retiring its group.
                second = new HullBuildingCensus(k.root);
                second.Add(k.candidate);
                second.Observe(Time.frameCount);
                Assert.That(second.FramesNotOneEach, Is.Zero, "the layout: it stands, one of each: " + second.Describe());
                Assert.That(k.groups.RetiringRoot.IsSet, Is.False, "the layout: no collection is retiring anything");
                foreach (LogicalFragmentId piece in k.pieces)
                {
                    Assert.That(k.root.Ledger.Retire(piece), Is.True, "retired by the test: " + piece);
                    k.root.Owners.Retire(piece);
                    k.root.Geometry.Forget(piece);
                }

                k.root.Hulls.EndBuilding(k.group, "ended by the test, outside a collection");
                Assert.That(k.root.Hulls.BuildingsEnded, Is.EqualTo(1));
                Assert.That(k.groups.BuildingsRetired, Is.Zero, "the layout: the group collection retired nothing");
                yield return ObservingFrames(second, 4);
                TestContext.Out.WriteLine(second.Describe());
                Assert.That(second.Lost, Is.EqualTo(1), second.Describe());
                Assert.That(second.EndedByCollection, Is.Zero, "not counted as ended by a collection");
                Assert.That(second.FramesNotOneEach, Is.EqualTo(4), "a fault at every frame it is missing");
                Assert.That(second.NotOneEachWhy, Does.Contain("while no group collection was retiring its own group"));
                second.AtEnd(Time.frameCount);
                Assert.That(second.Faults.Any(f => f.Contains("the counts of ended buildings differ")), Is.False, "the trial's one end is the one the census heard outside a collection");
                Assert.That(k.root.Display.IsHalted, Is.False);
                yield return EndWorld(k.root);
            }
            finally
            {
                k.census?.Dispose();
                second?.Dispose();
                DisposeCandidates();
            }
        }

        /// <summary>
        /// **An original back otherwise than it stood is a fault.** The building is collected as it should be and its
        /// original stands again; before the census reads that frame, the collider of it that was on is off. The
        /// census reads the instance's own components against what it read before the cut, and lists the difference
        /// -- once -- while the building's end itself stays a formal one.
        /// </summary>
        [UnityTest]
        public IEnumerator HullCensus_AnOriginalBackOtherwiseThanItStood_IsAFault()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var k = new CensusCase();
            try
            {
                yield return CutABuildingFarAway(k, "college_001 back otherwise");
                HullBuildingCensus census = k.census;
                CutFragmentGroupCollection groups = k.groups;
                Assert.That(k.asPlaced, Is.EqualTo("on,off|on,off"), "the layout: one renderer and one collider on, one of each off");
                Assert.That(census.FramesNotOneEach, Is.Zero, "the layout: no fault so far: " + census.Describe());

                // From here no frame is read until the building is collected, its original back and changed: the next
                // reading is the first that finds the building missing, and the one that reads its original.
                groups.Settings = GroupsOn(0);
                yield return Until(() => groups.Returned == 1, "the building was collected and its original stands again");
                Assert.That(groups.BuildingsRetired, Is.EqualTo(1));
                Assert.That(States(k.renderers, k.colliders), Is.EqualTo(k.asPlaced), "the layout: back as it stood");
                k.colliders[0].enabled = false;
                yield return null;
                census.Observe(Time.frameCount);
                TestContext.Out.WriteLine(census.Describe());
                Assert.That(census.EndedByCollection, Is.EqualTo(1), "its end is a formal one: " + census.Describe());
                Assert.That(census.OriginalsBack, Is.EqualTo(1));
                Assert.That(census.Faults.Count, Is.EqualTo(1), census.Describe());
                Assert.That(census.Faults[0], Does.Contain("otherwise than it stood").And.Contain("collider 0 is off, was on"));
                yield return ObservingFrames(census, 3);
                Assert.That(census.Faults.Count, Is.EqualTo(1), "listed once");
                Assert.That(census.Lost, Is.Zero, "its end stays a formal one");
                Assert.That(census.FramesNotOneEach, Is.Zero, census.Describe());
                k.colliders[0].enabled = true;
                groups.Settings = GroupsOff();
                yield return Until(() => groups.Rearmed == 1, "a cut target again");
                Assert.That(k.root.Display.IsHalted, Is.False);
                yield return EndWorld(k.root);
            }
            finally
            {
                k.census?.Dispose();
                DisposeCandidates();
            }
        }
    }
}
