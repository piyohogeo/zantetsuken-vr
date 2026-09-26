using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// **What the physics scene answers right after the switch, when a Provisional collider is refitted** (DESIGN 7.2):
    /// the old shape must not answer anywhere the final one does not reach, the final shape must answer where the
    /// products put it, and the refitted mesh must not answer from the old child's placement -- all in the same
    /// frame, with <c>Physics.autoSyncTransforms</c> off and nothing between the switch and the queries: no yield, no
    /// simulation step, and no <c>SyncTransforms</c> of the test's own.
    /// <para>
    /// The actor is live and kinematic, with the Provisional collider on a dedicated child at a pose that is neither
    /// the identity position nor rotation, as a character's bone frames are. The part is one the cut produced, so it
    /// has no frame of its own: the refit puts the child back to the identity pose. The final mesh is a box away from
    /// its own origin, so that the old shape, the final shape and "the final mesh at the old child's pose" are three
    /// places apart and each can be asked about. The settings: the shape frame kept, and moved as a switch does when
    /// nothing is kept; a collider that already carries the part's very mesh, which is not taken over (assigning the
    /// mesh a collider already has rebuilds nothing, so a new pose would not reach the scene); and a collider on the
    /// shape frame itself, as an authored body's is.
    /// </para>
    /// <para>
    /// **A control first.** A transform write alone is not seen by the queries until the transforms are synced here:
    /// a probe collider moved without a sync still answers where it was. That is what makes a pass below mean the
    /// switch itself brought the physics scene up to date, and not that the test could not tell.
    /// </para>
    /// </summary>
    public class FinalColliderRefitQueryPlayModeTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private readonly List<PhysicsCutProducts> _products = new List<PhysicsCutProducts>();

        // Placed far from anything else a scene may hold.
        private static readonly Vector3 Origin = new Vector3(0f, 500f, 0f);

        [TearDown]
        public void Cleanup()
        {
            foreach (PhysicsCutProducts products in _products)
            {
                products.Dispose();
            }

            _products.Clear();
            foreach (GameObject go in _objects)
            {
                if (go != null)
                {
                    Object.Destroy(go);
                }
            }

            _objects.Clear();
            foreach (Mesh mesh in _meshes)
            {
                if (mesh != null)
                {
                    Object.Destroy(mesh);
                }
            }

            _meshes.Clear();
        }

        [UnityTest]
        public IEnumerator AMoveWithoutASync_IsNotSeenByTheQueries_TheControl()
        {
            Assert.That(Physics.autoSyncTransforms, Is.False, "the project's setting, which this whole case is about");
            var body = NewKinematicActor("Control");
            var child = new GameObject("Probe");
            _objects.Add(child);
            child.transform.SetParent(body.transform, false);
            MeshCollider probe = child.AddComponent<MeshCollider>();
            probe.cookingOptions = PhysicsCutCook.DefaultCooking;
            probe.convex = true;
            probe.sharedMesh = Box(Vector3.zero, 0.3f);
            Physics.SyncTransforms();
            Assert.That(Hits(probe, Origin), Is.True, "answers where it is");

            child.transform.localPosition = new Vector3(4f, 0f, 0f);
            Assert.That(Hits(probe, Origin), Is.True, "a transform write alone: the scene still has it where it was");
            Assert.That(
                Hits(probe, body.transform.TransformPoint(new Vector3(4f, 0f, 0f))), Is.False, "and not where it was moved to");
            yield return null;
        }

        /// <summary>What the Provisional side holds, and how the switch moves the frame.</summary>
        public enum Setting
        {
            /// <summary>The first collider on a framed child, another mesh; the shape frame stays.</summary>
            FrameKept,

            /// <summary>The same, with the shape frame moved by the switch.</summary>
            FrameMoved,

            /// <summary>
            /// The first collider on a framed child already carries the part's very mesh -- two convexes that share one.
            /// It is not taken over; the next one is, and the first goes.
            /// </summary>
            SameMesh,

            /// <summary>The first collider on the shape frame itself, as an authored body's is; the frame moved.</summary>
            OnTheFrame,
        }

        [UnityTest]
        public IEnumerator ARefittedCollider_AnswersOnlyWithItsFinalShape_AtOnce(
            [Values(Setting.FrameKept, Setting.FrameMoved, Setting.SameMesh, Setting.OnTheFrame)] Setting setting)
        {
            Assert.That(Physics.autoSyncTransforms, Is.False, "the project's setting, which this whole case is about");
            bool moveFrame = setting == Setting.FrameMoved || setting == Setting.OnTheFrame;
            Rigidbody body = NewKinematicActor("Refit " + setting);
            var shapeFrame = new GameObject("Shape Frame");
            _objects.Add(shapeFrame);
            shapeFrame.transform.SetParent(body.transform, false);
            var frameBefore = new Vector3(0.5f, 0.25f, -0.25f);
            Quaternion turnBefore = Quaternion.Euler(0f, 20f, 0f);
            shapeFrame.transform.SetLocalPositionAndRotation(frameBefore, turnBefore);

            // The product: a box around (0, 2, 0) of its own origin, with no frame -- it belongs at the identity pose.
            var finalCentre = new Vector3(0f, 2f, 0f);
            Mesh finalMesh = Box(finalCentre, 0.3f);
            PhysicsCutProducts products = Products(finalMesh);

            // The Provisional side: two colliders. The first sits on a framed child at a pose that is neither the
            // identity position nor rotation (or on the frame itself); the second on a framed child of its own.
            var firstAt = setting == Setting.OnTheFrame ? Vector3.zero : new Vector3(1.5f, -0.5f, 0.75f);
            Quaternion firstTurn = setting == Setting.OnTheFrame ? Quaternion.identity : Quaternion.Euler(10f, 35f, -15f);
            Vector3 firstCentre = setting == Setting.SameMesh ? finalCentre : Vector3.zero;
            Mesh firstMesh = setting == Setting.SameMesh ? finalMesh : Box(Vector3.zero, 0.3f);
            MeshCollider first = setting == Setting.OnTheFrame
                ? NewColliderOnFrame(shapeFrame, firstMesh)
                : NewFramedCollider(shapeFrame, firstAt, firstTurn, firstMesh);
            var secondAt = new Vector3(-1.5f, 0f, 0f);
            Quaternion secondTurn = Quaternion.Euler(0f, 0f, 25f);
            MeshCollider second = NewFramedCollider(shapeFrame, secondAt, secondTurn, Box(Vector3.zero, 0.3f));
            var side = new PhysicsOwnerSide(true, body.gameObject, shapeFrame, body);
            side.Add(first);
            side.Add(second);

            // Which one the part takes over, and which one goes.
            bool secondTaken = setting == Setting.SameMesh;
            MeshCollider taken = secondTaken ? second : first;
            MeshCollider freed = secondTaken ? first : second;
            Vector3 takenAt = secondTaken ? secondAt : firstAt;
            Quaternion takenTurn = secondTaken ? secondTurn : firstTurn;

            Vector3 frameAfter = moveFrame ? new Vector3(-0.5f, 0.75f, 0.5f) : frameBefore;
            Quaternion turnAfter = moveFrame ? Quaternion.Euler(0f, -40f, 10f) : turnBefore;

            // Where each thing is in the world: the two old shapes, the final one where it belongs, and the final
            // mesh where the taken collider's old pose would put it -- in the frame before the switch and after it,
            // and at the frame's old pose.
            Transform root = body.transform;
            Vector3 firstPlace = World(root, frameBefore, turnBefore, firstAt, firstTurn, firstCentre);
            Vector3 secondPlace = World(root, frameBefore, turnBefore, secondAt, secondTurn, Vector3.zero);
            Vector3 finalPlace = World(root, frameAfter, turnAfter, Vector3.zero, Quaternion.identity, finalCentre);
            Vector3 wrongOld = World(root, frameBefore, turnBefore, takenAt, takenTurn, finalCentre);
            Vector3 wrongNew = World(root, frameAfter, turnAfter, takenAt, takenTurn, finalCentre);
            Vector3 wrongFrame = World(root, frameBefore, turnBefore, Vector3.zero, Quaternion.identity, finalCentre);
            var places = new List<Vector3> { firstPlace, secondPlace, finalPlace };
            var wrong = new List<Vector3>();
            if (setting != Setting.OnTheFrame)
            {
                wrong.Add(wrongOld); // on the frame itself, the taken collider's old pose is the frame's old pose
            }

            if (moveFrame)
            {
                if (setting != Setting.OnTheFrame)
                {
                    wrong.Add(wrongNew); // on the frame itself, this is the final place
                }

                wrong.Add(wrongFrame);
            }

            places.AddRange(wrong);
            for (int a = 0; a < places.Count; a++)
            {
                for (int b = a + 1; b < places.Count; b++)
                {
                    Assert.That(Vector3.Distance(places[a], places[b]), Is.GreaterThan(0.9f), "the places are apart: " + a + ", " + b);
                }
            }

            // The old state, synced, as the scene has it when the handoff begins.
            Physics.SyncTransforms();
            Assert.That(Hits(first, firstPlace), Is.True, "the old shapes answer before the switch");
            Assert.That(Hits(second, secondPlace), Is.True);
            Assert.That(Hits(null, finalPlace), Is.False, "and nothing answers where the final one will be");

            PreparedSideColliders prepared = PhysicsOwnerBuilder.PrepareFinalColliders(
                products, new List<Mesh>(), side, null, turnAfter, frameAfter);
            Assert.That(prepared.RefittedCount, Is.EqualTo(1), "the part takes one of the side's colliders over");
            Assert.That(prepared.MadeCount, Is.Zero);
            Assert.That(prepared.RefitsSideCollider(secondTaken ? 1 : 0), Is.True, "the one it can take");
            Assert.That(prepared.RefitsSideCollider(secondTaken ? 0 : 1), Is.False);
            Assert.That(Hits(first, firstPlace) && Hits(second, secondPlace), Is.True, "a preparation changes nothing the scene answers");

            prepared.Adopt(turnAfter, frameAfter);

            // Nothing between the switch and these: no yield, no step, no sync of the test's own.
            Assert.That(side.Colliders.Count, Is.EqualTo(1));
            Assert.That(side.Colliders[0], Is.SameAs(taken), "the refitted collider is the side's");
            Assert.That(Hits(taken, finalPlace), Is.True, "the final shape answers where the products put it, at once");
            Assert.That(Hits(null, firstPlace), Is.False, "the old shapes answer nowhere the final one does not reach");
            Assert.That(Hits(null, secondPlace), Is.False);
            foreach (Vector3 place in wrong)
            {
                Assert.That(Hits(null, place), Is.False, "the final mesh does not answer from a wrong placement: " + place);
            }

            Assert.That(freed.enabled, Is.False, "the collider no part needed stopped answering at once");

            // The collider's own reading agrees with the scene's.
            Assert.That(taken.Raycast(RayAt(finalPlace), out RaycastHit _, 0.8f), Is.True);
            foreach (Vector3 place in wrong)
            {
                Assert.That(taken.Raycast(RayAt(place), out RaycastHit _, 0.8f), Is.False);
            }

            GameObject takenObject = taken.gameObject;
            GameObject freedObject = freed.gameObject;
            yield return null;

            // After the frame: the refitted collider stays where it is, the one no part needed is gone -- with its
            // child when it had one of its own, and without taking the shape frame when it sat on it.
            Assert.That(taken != null && taken.enabled, Is.True, "the refitted collider stays, answering");
            Assert.That(
                takenObject == shapeFrame || (takenObject != null && takenObject.transform.parent == shapeFrame.transform),
                Is.True, "where it was");
            Assert.That(freed == null, Is.True, "the unneeded collider went");
            if (freedObject != shapeFrame)
            {
                Assert.That(freedObject == null, Is.True, "with its own child");
            }

            Assert.That(shapeFrame != null, Is.True, "and the shape frame is the side's still");
            Assert.That(shapeFrame.transform.childCount, Is.EqualTo(takenObject == shapeFrame ? 0 : 1));
        }

        // ----- the scene and its queries ----------------------------------------------------------------------------

        private Rigidbody NewKinematicActor(string name)
        {
            var root = new GameObject(name);
            _objects.Add(root);
            root.transform.SetPositionAndRotation(Origin, Quaternion.Euler(0f, 15f, 0f));
            Rigidbody body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            return body;
        }

        private static MeshCollider NewColliderOnFrame(GameObject shapeFrame, Mesh mesh)
        {
            MeshCollider collider = shapeFrame.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = mesh;
            return collider;
        }

        private MeshCollider NewFramedCollider(GameObject shapeFrame, Vector3 at, Quaternion turn, Mesh mesh)
        {
            var child = new GameObject("Convex mesh frame");
            child.transform.SetParent(shapeFrame.transform, false);
            child.transform.SetLocalPositionAndRotation(at, turn);
            MeshCollider collider = child.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = mesh;
            return collider;
        }

        private PhysicsCutProducts Products(Mesh produced)
        {
            var capacity = new ConvexCutOwnerCapacity { vertices = 1, faceOffsets = 1, faceIndices = 1, edges = 1, scratchBytes = 1 };
            var result = new ConvexCutOwnerResult { status = ConvexCutOwnerStatus.Ok };
            var products = new PhysicsCutProducts(
                new PhysicsCutArena(in capacity, 1), new ConvexCutOutcome[1], in result, float4x4.identity,
                PhysicsCutCook.DefaultCooking, 1, 0);
            _products.Add(products);
            products.Add(true, new PhysicsCutPart(0, false, default, produced, default));
            return products;
        }

        private Mesh Box(Vector3 centre, float half)
        {
            var mesh = new Mesh { name = "Refit query box", hideFlags = HideFlags.HideAndDontSave };
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                v[i] = centre + new Vector3((i & 1) != 0 ? half : -half, (i & 2) != 0 ? half : -half, (i & 4) != 0 ? half : -half);
            }

            mesh.vertices = v;
            mesh.triangles = new[]
            {
                0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4,
                2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
            };
            Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
            _meshes.Add(mesh);
            return mesh;
        }

        private static Vector3 World(
            Transform root, Vector3 frameAt, Quaternion frameTurn, Vector3 childAt, Quaternion childTurn, Vector3 point)
        {
            Vector3 inFrame = childAt + childTurn * point;
            return root.TransformPoint(frameAt + frameTurn * inFrame);
        }

        // A short ray straight down into the place: 0.3 m half-size boxes, a ray from 0.5 m above along 0.8 m meets the
        // top of the one centred there and nothing a metre away.
        private static Ray RayAt(Vector3 place)
        {
            return new Ray(place + 0.5f * Vector3.up, Vector3.down);
        }

        /// <summary>
        /// Whether the physics scene answers at <paramref name="place"/>, asked twice -- a short ray into it and a
        /// small sphere at it -- and the two must agree: with <paramref name="collider"/>, whether that collider is
        /// among the answers; with null, whether anything is.
        /// </summary>
        private static bool Hits(Collider collider, Vector3 place)
        {
            bool ray = false;
            foreach (RaycastHit hit in Physics.RaycastAll(RayAt(place), 0.8f, ~0, QueryTriggerInteraction.Ignore))
            {
                ray |= collider == null || hit.collider == collider;
            }

            bool overlap = false;
            foreach (Collider found in Physics.OverlapSphere(place, 0.05f, ~0, QueryTriggerInteraction.Ignore))
            {
                overlap |= collider == null || found == collider;
            }

            Assert.That(overlap, Is.EqualTo(ray), "the ray and the sphere agree at " + place);
            return ray;
        }
    }
}
