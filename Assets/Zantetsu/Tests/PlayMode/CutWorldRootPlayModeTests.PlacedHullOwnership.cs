using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// Who gives a building's stored geometry back when its hull registration fails (2026-10-03): the geometry the world's
    /// display never showed is the caller's and is given back by it, once -- a refusal before the display, or an exception
    /// before it; the geometry the display showed is the world's -- an exception after it (the trial's refusal after it is
    /// PlacedCompound's PlacedHullRegistration_RefusedAfterTheDisplayTookIt_...) -- and the caller leaves it, the world
    /// retiring the fragment and taking the geometry back, once. In every case no actor
    /// stays and the instance keeps its states. Ignored where the licensed input is absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private readonly struct StorageRoom
        {
            private readonly int _groups, _vertices, _blocks;

            internal StorageRoom(CutWorldRoot root)
            {
                _groups = root.Storage.VertexGroupCount;
                _vertices = root.Storage.FreeVertexRoom;
                _blocks = root.Storage.FreeVertexBlockRoom;
            }

            internal bool Same(CutWorldRoot root) =>
                root.Storage.VertexGroupCount == _groups && root.Storage.FreeVertexRoom == _vertices && root.Storage.FreeVertexBlockRoom == _blocks;

            internal void AssertSame(CutWorldRoot root, string what)
            {
                Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(_groups), what + ": vertex groups");
                Assert.That(root.Storage.FreeVertexRoom, Is.EqualTo(_vertices), what + ": vertex room");
                Assert.That(root.Storage.FreeVertexBlockRoom, Is.EqualTo(_blocks), what + ": vertex blocks");
            }

            internal int Groups => _groups;
        }

        private const string GivenBackOnce = "given back: the actor, the convex, the stored geometry (indices retired True, vertex room released True)";
        private const string LeftToTheWorld = "left to the world";

        private PlacedCuttableInput CollegeOrIgnore()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            return JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
        }

        private InvalidOperationException RefusedHull(CutWorldRoot root, PlacedCuttableInput data, GameObject instance, Renderer[] renderers, Collider[] colliders, float mass, int material = SideMaterial)
        {
            var e = Assert.Throws<InvalidOperationException>(() =>
                PlacedCuttableRegistration.RegisterHull(root, data, instance.transform, renderers, colliders, mass, material));
            TestContext.Out.WriteLine("refusal: " + e.Message);
            return e;
        }

        private void AssertNothingStays(string building, Renderer[] renderers, Collider[] colliders, string before, int bodies)
        {
            Assert.That(Bodies(), Is.EqualTo(bodies), "no body of the refused building stays");
            Assert.That(GameObject.Find("Building " + building), Is.Null, "no actor of it stays");
            Assert.That(States(renderers, colliders), Is.EqualTo(before), "the instance's renderers and colliders keep their states");
        }

        /// <summary>Refused before the display (a mass that is not positive): the caller gives the geometry back, once.</summary>
        [UnityTest]
        public IEnumerator PlacedHullRegistration_RefusedBeforeTheDisplay_GivesTheGeometryBackOnce()
        {
            PlacedCuttableInput data = CollegeOrIgnore();
            CutWorldRoot root = NewHullWorld(0.9f);
            yield return null;
            GameObject instance = RefusalInstance("college_001 refused before display", new Vector3(0f, -1f, 0f), out Renderer[] renderers, out Collider[] colliders);
            string before = States(renderers, colliders);
            int bodies = Bodies(), groupsMade = root.Hulls.GroupsMade;
            var room = new StorageRoom(root);
            int released = root.Geometry.VertexGroupsReleased;
            InvalidOperationException e = RefusedHull(root, data, instance, renderers, colliders, 0f);
            Assert.That(e.Message, Does.Contain("refused by the hull trial").And.Contain(GivenBackOnce).And.Not.Contain(LeftToTheWorld));
            room.AssertSame(root, "given back by the caller at once");
            yield return null;
            AssertNothingStays(data.name, renderers, colliders, before, bodies);
            Assert.That(root.Hulls.GroupsMade, Is.EqualTo(groupsMade), "no hull group made");
            for (int f = 0; f < 10; f++) yield return null;
            room.AssertSame(root, "and nothing more");
            Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released), "the world's reclamation did not release it again");
            yield return EndWorld(root);
        }

        /// <summary>An exception before the display: not shown, so given back by the caller, once; the error is passed on.</summary>
        [UnityTest]
        public IEnumerator PlacedHullRegistration_AnExceptionBeforeTheDisplay_GivesTheGeometryBackOnce()
        {
            PlacedCuttableInput data = CollegeOrIgnore();
            CutWorldRoot root = NewHullWorld(0.9f);
            yield return null;
            GameObject instance = RefusalInstance("college_001 exception before display", new Vector3(0f, -1f, 0f), out Renderer[] renderers, out Collider[] colliders);
            string before = States(renderers, colliders);
            int bodies = Bodies();
            var room = new StorageRoom(root);
            int released = root.Geometry.VertexGroupsReleased;
            CutWorldRoot.addBuildingHullHookForTest = stage => { if (stage == "before display") throw new InvalidOperationException("thrown before the display"); };
            InvalidOperationException e;
            try
            {
                e = RefusedHull(root, data, instance, renderers, colliders, 10000f);
            }
            finally
            {
                CutWorldRoot.addBuildingHullHookForTest = null;
            }

            Assert.That(e.Message, Does.Contain("thrown before the display").And.Contain(GivenBackOnce).And.Not.Contain(LeftToTheWorld));
            room.AssertSame(root, "given back by the caller at once");
            yield return null;
            AssertNothingStays(data.name, renderers, colliders, before, bodies);
            for (int f = 0; f < 10; f++) yield return null;
            room.AssertSame(root, "and nothing more");
            Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released), "the world's reclamation did not release it again");
            yield return EndWorld(root);
        }

        /// <summary>An exception after the display took the geometry: the world's, so the caller leaves it and the world takes it back.</summary>
        [UnityTest]
        public IEnumerator PlacedHullRegistration_AnExceptionAfterTheDisplay_LeavesTheGeometryToTheWorld()
        {
            PlacedCuttableInput data = CollegeOrIgnore();
            CutWorldRoot root = NewHullWorld(0.9f);
            yield return null;
            GameObject instance = RefusalInstance("college_001 exception after display", new Vector3(0f, -1f, 0f), out Renderer[] renderers, out Collider[] colliders);
            string before = States(renderers, colliders);
            int bodies = Bodies(), groupsMade = root.Hulls.GroupsMade;
            var room = new StorageRoom(root);
            int released = root.Geometry.VertexGroupsReleased;
            CutWorldRoot.addBuildingHullHookForTest = stage => { if (stage == "after display") throw new InvalidOperationException("thrown after the display"); };
            InvalidOperationException e;
            try
            {
                e = RefusedHull(root, data, instance, renderers, colliders, 10000f);
            }
            finally
            {
                CutWorldRoot.addBuildingHullHookForTest = null;
            }

            Assert.That(e.Message, Does.Contain("thrown after the display").And.Contain(LeftToTheWorld).And.Not.Contain("the stored geometry ("));
            Assert.That(root.Hulls.GroupsMade, Is.EqualTo(groupsMade), "no hull group made");
            Assert.That(root.Storage.VertexGroupCount, Is.EqualTo(room.Groups + 1), "the caller did not give the shown geometry back");
            yield return null;
            AssertNothingStays(data.name, renderers, colliders, before, bodies);
            yield return UntilWithin(() => room.Same(root), 5f, "the world took the shown geometry back");
            room.AssertSame(root, "taken back by the world");
            Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "given back once, by the world's reclamation");
            for (int f = 0; f < 10; f++) yield return null;
            Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "and not again");
            yield return EndWorld(root);
        }

        /// <summary>
        /// An exception after the trial registered the group (before its owner and base geometry): the world unregisters
        /// the group and retires the fragment; the geometry, shown, is the world's and taken back by it once; the caller
        /// leaves it.
        /// </summary>
        [UnityTest]
        public IEnumerator PlacedHullRegistration_AnExceptionAfterTheRegistration_UnregistersTheGroup_AndLeavesTheGeometryToTheWorld()
        {
            PlacedCuttableInput data = CollegeOrIgnore();
            CutWorldRoot root = NewHullWorld(0.9f);
            yield return null;
            GameObject instance = RefusalInstance("college_001 exception after register", new Vector3(0f, -1f, 0f), out Renderer[] renderers, out Collider[] colliders);
            string before = States(renderers, colliders);
            int bodies = Bodies(), groupCount = root.Hulls.GroupCount, unregistered = root.Hulls.GroupsUnregistered;
            var room = new StorageRoom(root);
            int released = root.Geometry.VertexGroupsReleased, tracked = root.Geometry.TrackedFragmentCount;
            CutWorldRoot.addBuildingHullHookForTest = stage => { if (stage == "after register") throw new InvalidOperationException("thrown after the registration"); };
            InvalidOperationException e;
            try
            {
                e = RefusedHull(root, data, instance, renderers, colliders, 10000f);
            }
            finally
            {
                CutWorldRoot.addBuildingHullHookForTest = null;
            }

            Assert.That(e.Message, Does.Contain("thrown after the registration").And.Contain(LeftToTheWorld).And.Not.Contain("the stored geometry ("));
            Assert.That(root.Hulls.GroupCount, Is.EqualTo(groupCount), "the group was unregistered");
            Assert.That(root.Hulls.GroupsUnregistered, Is.EqualTo(unregistered + 1), "once");
            Assert.That(root.Geometry.TrackedFragmentCount, Is.EqualTo(tracked), "its base geometry forgotten by the DAG");
            yield return null;
            AssertNothingStays(data.name, renderers, colliders, before, bodies);
            yield return UntilWithin(() => room.Same(root), 5f, "the world took the shown geometry back");
            room.AssertSame(root, "taken back by the world");
            Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "given back once, by the world's reclamation");
            for (int f = 0; f < 10; f++) yield return null;
            Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "and not again");
            yield return EndWorld(root);
        }

        /// <summary>
        /// An exception inside the trial's registration (its group made, not yet listed): the trial takes what it made back
        /// itself, the world retires the fragment, and the shown geometry is the world's -- taken back once; the caller
        /// leaves it.
        /// </summary>
        [UnityTest]
        public IEnumerator PlacedHullRegistration_AnExceptionInsideTheRegistration_TheTrialTakesItsPartBack_AndTheGeometryIsTheWorlds()
        {
            PlacedCuttableInput data = CollegeOrIgnore();
            CutWorldRoot root = NewHullWorld(0.9f);
            yield return null;
            GameObject instance = RefusalInstance("college_001 exception inside register", new Vector3(0f, -1f, 0f), out Renderer[] renderers, out Collider[] colliders);
            string before = States(renderers, colliders);
            int bodies = Bodies(), groupCount = root.Hulls.GroupCount, unregistered = root.Hulls.GroupsUnregistered;
            var room = new StorageRoom(root);
            int released = root.Geometry.VertexGroupsReleased;
            BuildingHullFusion.registerHookForTest = () => throw new InvalidOperationException("thrown inside the registration");
            InvalidOperationException e;
            try
            {
                e = RefusedHull(root, data, instance, renderers, colliders, 10000f);
            }
            finally
            {
                BuildingHullFusion.registerHookForTest = null;
            }

            Assert.That(e.Message, Does.Contain("thrown inside the registration").And.Contain(LeftToTheWorld));
            Assert.That(root.Hulls.GroupCount, Is.EqualTo(groupCount), "no group stays");
            Assert.That(root.Hulls.GroupsUnregistered, Is.EqualTo(unregistered + 1), "what it made was taken back once");
            yield return null;
            AssertNothingStays(data.name, renderers, colliders, before, bodies);
            yield return UntilWithin(() => room.Same(root), 5f, "the world took the shown geometry back");
            Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "given back once, by the world's reclamation");
            yield return EndWorld(root);
        }

        /// <summary>
        /// A fragment issued for the building that has an owner already is refused before the display: nothing of the
        /// world's changes, the geometry is given back by the caller, once, and the building the fragment belongs to stands.
        /// </summary>
        [UnityTest]
        public IEnumerator PlacedHullRegistration_AnIssuedFragmentWithAnOwner_IsRefusedBeforeTheDisplay()
        {
            PlacedCuttableInput data = CollegeOrIgnore();
            CutWorldRoot root = NewHullWorld(0.9f);
            yield return null;
            GameObject first = RefusalInstance("college_001 first", new Vector3(0f, -1f, 0f), out Renderer[] firstRenderers, out Collider[] firstColliders);
            PlacedCuttableRegistration made = PlacedCuttableRegistration.RegisterHull(root, data, first.transform, firstRenderers, firstColliders, 10000f, SideMaterial);
            TrackActor(made.Actor);
            yield return null;
            GameObject second = RefusalInstance("college_001 second", new Vector3(30f, -1f, 0f), out Renderer[] renderers, out Collider[] colliders);
            string before = States(renderers, colliders);
            int groupCount = root.Hulls.GroupCount;
            var room = new StorageRoom(root);
            LogAssert.Expect(LogType.Error, new Regex("the fragment issued for the building already has an owner"));
            var e = Assert.Throws<InvalidOperationException>(() =>
                PlacedCuttableRegistration.RegisterHull(root, data, second.transform, renderers, colliders, 10000f, SideMaterial, made.Fragment));
            TestContext.Out.WriteLine("refusal: " + e.Message);
            Assert.That(e.Message, Does.Contain("refused by the hull trial").And.Contain(GivenBackOnce).And.Not.Contain(LeftToTheWorld));
            room.AssertSame(root, "given back by the caller at once");
            Assert.That(States(renderers, colliders), Is.EqualTo(before), "the second instance keeps its states");
            Assert.That(root.Hulls.GroupCount, Is.EqualTo(groupCount), "the first building's group stands, no other made");
            Assert.That(root.Owners.TryGet(made.Fragment, out PhysicsFragmentOwner _), Is.True, "the fragment keeps its owner");
            yield return EndWorld(root);
        }
    }
}
