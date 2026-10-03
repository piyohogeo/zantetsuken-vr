using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The city walk's remaining placed-cuttable cases (TL, 2026-10-03):
    /// <list type="bullet">
    /// <item>a prop of several overlapping convexes (tree_012: six, the trunk and the crown) registered as one authored
    /// compound -- a collider a convex, fixed by its anchor -- cut by a Slash through the overlap: each side holds the
    /// convexes the plane leaves it, the side with the anchor stays fixed and the other is free, and the world's ending
    /// collects everything;</item>
    /// <item>a building the hull trial refuses after its display took the geometry: nothing of it stays with the
    /// registrar, the display lets the geometry go at its collection and the world's vertex reclamation gives its room
    /// back, on ordinary frames, and the ending after it is clean;</item>
    /// <item>a geometry the registrar gave back itself (a display refusal) is not given back a second time by the world.</item>
    /// </list>
    /// Ignored where the licensed inputs are absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const string CompoundTreeInputPath = "Assets/Licensed/WalkCity/Inputs/tree_012.json";

        [UnityTest]
        public IEnumerator PlacedCompound_SixOverlappingConvexes_CutThroughTheOverlap_AnchoredSideStays_AndTheEndingCollects()
        {
            if (!File.Exists(CompoundTreeInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + CompoundTreeInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CompoundTreeInputPath));
            Assert.That(data.hulls.Length, Is.EqualTo(6), "tree_012's six convexes");
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            GameObject instance = TrackActor(new GameObject("tree_012 instance"));
            instance.transform.position = new Vector3(0f, -1f, 0f);
            PlacedCuttableRegistration made = PlacedCuttableRegistration.Register(root, data, instance.transform, new Renderer[0], new Collider[0], 50f, false);
            _actors.Add(made.Actor);
            TestContext.Out.WriteLine("registered: " + made.Description);
            Assert.That(root.Owners.TryGet(made.Fragment, out PhysicsFragmentOwner owner), Is.True);
            Assert.That(owner.Shape.ConvexCount, Is.EqualTo(6), "one owner of six convexes");
            Assert.That(made.Actor.GetComponents<MeshCollider>().Length, Is.EqualTo(6), "a collider a convex");
            Assert.That(owner.FixedByAnchors && owner.Body.isKinematic, Is.True, "fixed by its anchor");

            // A level cut through the crown, where convexes 2, 3 and 5 overlap; 0, 1 and 4 lie wholly below it.
            const float cutLocal = 6.0f;
            float y = instance.transform.position.y + cutLocal;
            int below = data.hulls.Count(h => h.vertices.Min(v => v.y) < cutLocal), above = data.hulls.Count(h => h.vertices.Max(v => v.y) > cutLocal);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            Evaluate(detector, Level(1, y, -6f, 6f, -6f, 6f), 1);
            List<SlashHitConfirmed> hits = Hits(detector);
            Assert.That(hits.Count, Is.EqualTo(1), "one hit on the compound");
            Assert.That(hits[0].Acceptance == ProvisionalCutAcceptance.Published || hits[0].Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "accepted: " + hits[0].Acceptance);
            CutOperationId operation = hits[0].Operation;
            yield return Until(() => root.Geometry.StageOf(operation) == CutGeometryStage.Committed, "the cut committed");
            LogicalCutOperation cut = OperationOf(root, operation);
            yield return Until(() => root.Owners.TryGet(cut.positive, out PhysicsFragmentOwner p) && p.Body != null && root.Owners.TryGet(cut.negative, out PhysicsFragmentOwner n) && n.Body != null,
                "both sides have their own bodies");
            root.Owners.TryGet(cut.positive, out PhysicsFragmentOwner upper);
            root.Owners.TryGet(cut.negative, out PhysicsFragmentOwner lower);
            if (upper.Body.worldCenterOfMass.y < lower.Body.worldCenterOfMass.y) (upper, lower) = (lower, upper);
            TestContext.Out.WriteLine("upper: convexes " + upper.Shape.ConvexCount + " fixed " + upper.FixedByAnchors + " kinematic " + upper.Body.isKinematic
                + "; lower: convexes " + lower.Shape.ConvexCount + " fixed " + lower.FixedByAnchors + " kinematic " + lower.Body.isKinematic + "; expected above " + above + ", below " + below);
            Assert.That(upper.Shape.ConvexCount, Is.EqualTo(above), "the upper side holds the parts of the convexes above the plane");
            Assert.That(lower.Shape.ConvexCount, Is.EqualTo(below), "the lower side holds the convexes below it, whole or cut");
            Assert.That(lower.FixedByAnchors && lower.Body.isKinematic, Is.True, "the side with the anchor stays fixed");
            Assert.That(upper.FixedByAnchors || upper.Body.isKinematic, Is.False, "the other side is free");
            Assert.That(root.GeometryFaults, Is.Zero);
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator PlacedHullRegistration_RefusedAfterTheDisplayTookIt_TheWorldCollectsItsGeometry_AndEndsCleanly()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            // A vertex limit below the building's hull: the hull trial refuses it after its display has shown it.
            CutWorldRoot root = NewHullWorld(0.9f, profile => SetPrivate(profile, "vertexLimit", 8));
            yield return null;
            GameObject instance = RefusalInstance("college_001 past the vertex limit", new Vector3(0f, -1f, 0f), out Renderer[] renderers, out Collider[] colliders);
            string before = States(renderers, colliders);
            int bodies = Bodies(), groups = root.Storage.VertexGroupCount, freeVertices = root.Storage.FreeVertexRoom, released = root.Geometry.VertexGroupsReleased;
            LogAssert.Expect(LogType.Error, new Regex("the hull trial refused the building"));
            var e = Assert.Throws<InvalidOperationException>(() =>
                PlacedCuttableRegistration.RegisterHull(root, data, instance.transform, renderers, colliders, 10000f, SideMaterial));
            TestContext.Out.WriteLine("refusal: " + e.Message);
            Assert.That(e.Message, Does.Contain("refused by the hull trial").And.Contain("its stored geometry left to the world"));
            Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups + 1), "the geometry is still the display's for now");
            // The display lets it go at a collection, and the world's reclamation gives its room back -- on ordinary frames.
            yield return Until(() => root.Storage.VertexGroupCount == groups, "the world gave the refused geometry's vertex group back");
            Assert.That(root.Storage.FreeVertexRoom, Is.EqualTo(freeVertices), "its vertex room back");
            Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "given back once, by the world's reclamation");
            Assert.That(Bodies(), Is.EqualTo(bodies), "no body of it stays");
            Assert.That(States(renderers, colliders), Is.EqualTo(before), "the instance as it was");
            for (int f = 0; f < 10; f++) yield return null;
            Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "and not again");
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator PlacedRegistration_GivenBackByTheRegistrar_IsNotGivenBackAgainByTheWorld_AndTheEndingIsClean()
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            CutWorldRoot root = NewHullWorld(0.9f);   // the input's source material 0 is not bound: the display refuses
            yield return null;
            GameObject instance = RefusalInstance("bench_001 refused twice over", new Vector3(2f, -1f, -2f), out Renderer[] renderers, out Collider[] colliders);
            int groups = root.Storage.VertexGroupCount, released = root.Geometry.VertexGroupsReleased, waiting = root.Geometry.VertexGroupsWaiting;
            LogAssert.Expect(LogType.Error, new Regex("the display refused to show this body"));
            Assert.Throws<InvalidOperationException>(() => PlacedCuttableRegistration.Register(root, data, instance.transform, renderers, colliders, 50f, false));
            Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups), "the registrar gave it back at once");
            for (int f = 0; f < 10; f++) yield return null;
            Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(groups));
            Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released), "the world's reclamation did not release it again");
            Assert.That(root.Geometry.VertexGroupsWaiting, Is.EqualTo(waiting), "nor is it left waiting there");
            yield return EndWorld(root);
        }
    }
}
