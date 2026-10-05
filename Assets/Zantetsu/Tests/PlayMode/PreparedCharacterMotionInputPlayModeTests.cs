using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A character not yet cut is followed by no body (DESIGN 9, D-197): what it weighs is kept as numbers in its root's
    /// frame, how it moves is given as numbers, and where it stands is read from its root when a cut is accepted. These
    /// cases go through the world's own entry and acceptance (<see cref="CutWorldRoot.TryPrepareCharacterCut"/>,
    /// <see cref="VpPreparedCharacterCut.TryCut"/>, the hit detector) and, for a crowd's character, through
    /// <see cref="SandboxNpcCharacter"/>'s preparation, activation and reuse.
    /// </summary>
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        // What the cut's source body was given, read where the registry takes it (inside the acceptance).
        private struct SourceValues
        {
            public PhysicsFragmentOwner owner;
            public Vector3 position, centreLocal, centreWorld, inertia, velocity, angular;
            public Quaternion rotation, axes;
            public float mass;
            public bool inScene;
        }

        private static SourceValues ReadSource(PhysicsFragmentOwner owner)
        {
            owner.Root.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Rigidbody body = owner.Body;
            return new SourceValues
            {
                owner = owner, position = position, rotation = rotation, mass = body.mass, centreLocal = body.centerOfMass,
                centreWorld = body.worldCenterOfMass, inertia = body.inertiaTensor, axes = body.inertiaTensorRotation,
                velocity = body.linearVelocity, angular = body.angularVelocity, inScene = owner.Root.activeInHierarchy,
            };
        }

        // One side of a Provisional pair as it was published, read inside the publication's notification.
        private struct SideValues
        {
            public bool positive;
            public double mass;
            public Vector3 centreWorld, velocity, angular;
        }

        private static SideValues[] ReadSides(ProvisionalOwnerPair pair)
        {
            var sides = new SideValues[2];
            PhysicsOwnerSide[] both = { pair.Positive, pair.Negative };
            for (int i = 0; i < 2; i++)
            {
                sides[i] = new SideValues
                {
                    positive = both[i].positive, mass = both[i].Mass, centreWorld = both[i].Root.transform.TransformPoint((Vector3)both[i].CenterOfMass),
                    velocity = (Vector3)both[i].LinearVelocity, angular = (Vector3)both[i].AngularVelocity,
                };
            }

            return sides;
        }

        private static GameObject SourceObjectOf(VpPreparedCharacterCut handle) =>
            (GameObject)typeof(VpPreparedCharacterCut).GetField("actor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handle);

        private static void Near(Vector3 actual, Vector3 expected, float within, string what) =>
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(within), what + ": " + actual.ToString("F5") + " against " + expected.ToString("F5"));

        private static void Near(Quaternion actual, Quaternion expected, string what) =>
            Assert.That(Quaternion.Angle(actual, expected), Is.LessThan(0.02f), what + ": " + actual.eulerAngles.ToString("F3") + " against " + expected.eulerAngles.ToString("F3"));

        // A character whose root, renderer, bone and the body its mass settings are authored on each have a frame of
        // their own: the origins and the axes all differ. The body stays under the root here, as it used to be kept --
        // following the root -- so that each case can read from it what the cut was given before.
        private VpPreparedCharacterCut MotionCharacter(Vector3 at, Quaternion facing, out GameObject root, out SkinnedMeshRenderer r, out Transform bone, out Rigidbody following)
        {
            root = ColdTrack(new GameObject("motion input root"));
            root.transform.SetPositionAndRotation(at, facing);
            var rendererObject = new GameObject("motion input renderer");
            rendererObject.transform.SetParent(root.transform, false);
            rendererObject.transform.localPosition = new Vector3(0.3f, -0.2f, 0.1f);
            rendererObject.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            bone = new GameObject("motion input bone").transform;
            bone.SetParent(root.transform, false);
            r = rendererObject.AddComponent<SkinnedMeshRenderer>();
            r.quality = SkinQuality.Bone4;
            r.sharedMesh = ColdTrack(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
                uv = new[] { new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(.5f, .5f) },
                triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 },
                bindposes = new[] { Matrix4x4.identity },
                boneWeights = new[] { new BoneWeight { weight0 = 1 }, new BoneWeight { weight0 = 1 }, new BoneWeight { weight0 = 1 }, new BoneWeight { weight0 = 1 } },
            });
            r.bones = new[] { bone };
            var bodyObject = new GameObject("motion input following body");
            bodyObject.transform.SetParent(root.transform, false);
            bodyObject.transform.localPosition = new Vector3(-0.2f, 0.4f, 0.3f);
            bodyObject.transform.localRotation = Quaternion.Euler(15f, -30f, 20f);
            following = bodyObject.AddComponent<Rigidbody>();
            following.isKinematic = true;
            following.useGravity = false;
            following.mass = 12f;
            following.automaticCenterOfMass = following.automaticInertiaTensor = false;
            following.centerOfMass = new Vector3(0.1f, 1f, -0.2f);
            following.inertiaTensor = new Vector3(4f, 3f, 2f);
            following.inertiaTensorRotation = Quaternion.Euler(10f, 20f, 30f);

            // The settings are read with the body in the scene. Out of it Unity gives no centre of mass, and the read
            // says so by giving numbers that cannot be used, rather than numbers with a centre of mass of zero.
            VpCharacterMassProperties mass = VpCharacterMassProperties.FromBody(following, root.transform);
            Assert.That(mass.IsUsable, Is.True);
            bodyObject.SetActive(false);
            TestContext.Out.WriteLine("a body out of the scene: Unity gives its centre of mass as " + following.centerOfMass.ToString("F3") + " (set (0.100, 1.000, -0.200)), mass "
                + following.mass + ", inertia " + following.inertiaTensor.ToString("F3"));
            Assert.That(VpCharacterMassProperties.FromBody(following, root.transform).IsUsable, Is.False, "a body out of the scene is not read");
            bodyObject.SetActive(true);
            VpCharacterMassProperties again = VpCharacterMassProperties.FromBody(following, root.transform);
            Assert.That(again.Mass == mass.Mass && again.CentreOfMass == mass.CentreOfMass && again.InertiaTensor == mass.InertiaTensor
                && again.InertiaTensorRotation == mass.InertiaTensorRotation, Is.True, "back in the scene, its settings are what they were");

            if (_vertices.IsCreated) _disposables.Insert(0, new EarlierBank { arrays = new System.IDisposable[] { _vertices, _faceOffsets, _faceIndices, _faceEdges, _edges } });
            var source = NewAuthoredShape(1);
            CompleteRequestBoxEdges(source);
            Assert.That(coldWorld.TryPrepareCharacterCut(r, new[] { 0, 1, 2, 3 }, 4, source.BankOf(0), new[] { source.Convex(0) },
                new[] { bone }, coldWarm, root, mass, out var handle), Is.True);
            coldHandles.Add(handle);
            return handle;
        }

        /// <summary>
        /// **The cut's source body is given what a following body gave it** (TL, 2026-10-05): at rest, moved, turned, and
        /// both with the character tilted -- with the character root's, the renderer's and the mass settings' origins and
        /// axes all apart. The character is moved after its preparation; a kinematic body left under the root, as the
        /// motion body used to be, is read once the physics has its pose, with the lines the cut and the crowd used to
        /// read it by. The source body's mass, centre of mass, inertia, principal axes, velocity and angular velocity
        /// are those, it stands where the renderer does, and it comes into the scene at the acceptance and not before.
        /// The two sides then start from that motion, each carried to its own centre, parted along the plane's normal.
        /// </summary>
        [UnityTest]
        public IEnumerator TheSourceBodyOfACharactersCut_IsGivenWhatAFollowingBodyGaveIt_AtRest_Moved_Turned_AndBoth()
        {
            ColdWorld();
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            coldWarm = new VpPhysicsColdPreparation();
            var cases = new (string name, Vector3 move, Quaternion turn, Vector3 velocity, Vector3 angular)[]
            {
                ("at rest", Vector3.zero, Quaternion.identity, Vector3.zero, Vector3.zero),
                ("moved", new Vector3(3f, 0.5f, -2f), Quaternion.identity, new Vector3(1.2f, 0f, -0.7f), Vector3.zero),
                ("turned", Vector3.zero, Quaternion.Euler(0f, 70f, 0f), Vector3.zero, new Vector3(0f, 1.5f, 0f)),
                ("moved and turned, tilted", new Vector3(-2f, 1f, 4f), Quaternion.Euler(20f, -110f, 35f), new Vector3(-0.8f, 0.3f, 1.1f), new Vector3(0.4f, -1.2f, 0.6f)),
            };
            for (int c = 0; c < cases.Length; c++)
            {
                var (name, move, turn, velocity, angular) = cases[c];
                var start = new Vector3(5f + 40f * c, 0.25f, -3f);
                Quaternion facing = Quaternion.Euler(0f, 30f, 0f);
                var handle = MotionCharacter(start, facing, out GameObject root, out SkinnedMeshRenderer r, out Transform bone, out Rigidbody following);
                GameObject source = SourceObjectOf(handle);
                Assert.That(source.activeSelf, Is.False, name + ": the source body is out of the scene until a cut is accepted");
                Assert.That(source.transform.parent, Is.Null, name + ": and under no character");
                source.transform.GetPositionAndRotation(out Vector3 sourceWas, out Quaternion sourceFacingWas);
                yield return null;
                Assert.That(handle.TryFinishPreparation(), Is.True);

                // The character moves after its preparation, and says how it moves.
                root.transform.SetPositionAndRotation(start + move, turn * facing);
                handle.SetRootMotion(velocity, angular);
                source.transform.GetPositionAndRotation(out Vector3 sourceNow, out Quaternion sourceFacingNow);
                Assert.That(sourceNow == sourceWas && sourceFacingNow == sourceFacingWas && !source.activeSelf, Is.True, name + ": moving the character moves no body of its cut");

                // What a following body gave: read as it was read, once the physics has the body where its Transform is.
                Vector3 beforeSync = following.worldCenterOfMass;
                Physics.SyncTransforms();
                Vector3 followingCentre = following.worldCenterOfMass;
                Matrix4x4 inverse = r.transform.worldToLocalMatrix;
                Vector3 centreWas = inverse.MultiplyPoint3x4(followingCentre);
                Quaternion axesWas = Quaternion.Inverse(r.transform.rotation) * following.rotation * following.inertiaTensorRotation;
                Vector3 velocityWas = velocity + Vector3.Cross(angular, followingCentre - root.transform.position);
                float massWas = following.mass;
                Vector3 inertiaWas = following.inertiaTensor;
                TestContext.Out.WriteLine(name + ": the following body's centre of mass read before the physics had its new pose stood "
                    + Vector3.Distance(beforeSync, followingCentre).ToString("F4") + " m from where it stands (the character moved "
                    + move.magnitude.ToString("F3") + " m and turned " + Quaternion.Angle(Quaternion.identity, turn).ToString("F1") + " degrees since)");

                // A plane through the box, a quarter of a metre off its centre, in the renderer's frame.
                Vector3 normal = r.transform.InverseTransformDirection(bone.up);
                Vector3 through = r.transform.InverseTransformPoint(bone.position + bone.up * 0.25f);
                var plane = new float4(normal.x, normal.y, normal.z, -Vector3.Dot(normal, through));
                Vector3 normalWorld = bone.up;

                SourceValues given = default;
                SideValues[] sides = null;
                void Added(LogicalFragmentId fragment, PhysicsFragmentOwner owner) { if (owner.Root == source) given = ReadSource(owner); }
                void Published(ProvisionalOwnerPair p) { sides = ReadSides(p); }
                coldWorld.Owners.OwnerAdded += Added;
                coldWorld.Owners.ProvisionalAdded += Published;
                VpCharacterCutResult result;
                try { result = handle.TryCut(plane, (float3)bone.position, 6f, 3f); }
                finally
                {
                    coldWorld.Owners.OwnerAdded -= Added;
                    coldWorld.Owners.ProvisionalAdded -= Published;
                }

                Assert.That(result.Outcome, Is.EqualTo(VpCharacterCutOutcome.Requested), name + ": " + handle.LastFailure);
                Assert.That(result.Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published), name);
                Assert.That(given.owner, Is.Not.Null, name + ": the source body was registered");
                Assert.That(given.inScene, Is.True, name + ": in the scene at the acceptance");
                Near(given.position, r.transform.position, 1e-4f, name + ": the source body stands where the renderer does");
                Near(given.rotation, r.transform.rotation, name + ": and as it is turned");
                Assert.That(given.mass, Is.EqualTo(massWas), name + ": mass");
                Near(given.centreLocal, centreWas, 2e-4f, name + ": the centre of mass, in the body's frame");
                Near(given.centreWorld, followingCentre, 2e-4f, name + ": the centre of mass, in the world");
                Near(given.inertia, inertiaWas, 1e-5f, name + ": the inertia");
                Near(given.axes, axesWas, name + ": the principal axes");
                Near(given.velocity, velocityWas, 2e-4f, name + ": the velocity (the centre of mass's)");
                Near(given.angular, angular, 1e-5f, name + ": the angular velocity");

                // The two sides start from that motion: each carried to its own centre, and parted along the normal.
                Assert.That(sides, Is.Not.Null, name + ": the Provisional pair was published");
                float parting = 0f;
                foreach (SideValues side in sides)
                {
                    Vector3 inherited = given.velocity + Vector3.Cross(given.angular, side.centreWorld - given.centreWorld);
                    Vector3 added = side.velocity - inherited;
                    float along = Vector3.Dot(added, normalWorld) * (side.positive ? 1f : -1f);
                    Assert.That((added - Vector3.Dot(added, normalWorld) * normalWorld).magnitude, Is.LessThan(2e-3f), name + ": a side's velocity is the inherited one but for the parting along the normal");
                    Assert.That(along, Is.GreaterThanOrEqualTo(-1e-4f), name + ": the parting takes a side away from the other");
                    Near(side.angular, angular, 1e-5f, name + ": a side's angular velocity");
                    parting += along;
                    TestContext.Out.WriteLine(name + ": " + (side.positive ? "positive" : "negative") + " side mass " + side.mass.ToString("F3") + " kg, inherited "
                        + inherited.ToString("F4") + ", parting along the normal " + along.ToString("F4") + " m/s");
                }

                Assert.That(parting, Is.GreaterThan(0f), name + ": the two sides are parted");
                yield return UntilCommitted(result.Operation);
            }

            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True);
        }

        // A crowd's slot as the scene builders make one: the motion body on an object of its own under the character
        // root (mass 12, centre of mass one metre up, inertia 4), the preparation on an object outside the root.
        private SandboxNpcCharacter MotionSlot(Vector3 at, Quaternion facing, out GameObject setup, out GameObject root, out Rigidbody authored)
        {
            if (_slotIntake == null) _slotIntake = ColdTrack(new TextAsset(SlotIntakeJson) { name = "slot intake" });
            if (_slotHulls == null) _slotHulls = ColdTrack(new TextAsset(SlotHullsJson()) { name = "slot hulls" });
            SkinnedMeshRenderer r = LentBonedRig(out Transform _, out root, out Rigidbody onTheRoot);
            Object.DestroyImmediate(onTheRoot);
            r.sharedMesh.name = "slot mesh a";
            root.transform.SetPositionAndRotation(at, facing);
            var motion = new GameObject("NPC motion body");
            motion.transform.SetParent(root.transform, false);
            authored = motion.AddComponent<Rigidbody>();
            authored.isKinematic = true;
            authored.useGravity = false;
            authored.automaticCenterOfMass = false;
            authored.automaticInertiaTensor = false;
            authored.mass = 12f;
            authored.centerOfMass = Vector3.up;
            authored.inertiaTensor = Vector3.one * 4f;
            var table = ColdTrack(new TextAsset(PoseTablePlayerPlayModeTests.Table("Lent character bone")));
            root.AddComponent<PoseTablePlayer>().Configure(table, root.transform, 0.0, true);
            setup = new GameObject("motion slot");
            setup.SetActive(false);
            var character = setup.AddComponent<SandboxNpcCharacter>();
            void Set(string field, object value) => typeof(SandboxNpcCharacter).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(character, value);
            Set("world", coldWorld);
            Set("characterRoot", root);
            Set("motionBody", authored);
            Set("intake", _slotIntake);
            Set("hulls", _slotHulls);
            Set("family", "slot-a");
            character.PrepareAsSlot(new SandboxNpcCharacter.SlotShare());
            setup.SetActive(true);
            return character;
        }

        private static IEnumerable<Rigidbody> BodiesUnder(GameObject root) => root.GetComponentsInChildren<Rigidbody>(true);

        /// <summary>
        /// **A crowd's character has no body following it, and its cut takes its numbers** (TL, 2026-10-05), through the
        /// product's own preparation, activation, hit, Pending acceptance, publication, return and reuse.
        /// <para>
        /// The preparation reads the authored body's settings once and sets the body aside: out of the character,
        /// deactivated, under the preparation's own object. The character then holds no body, its parts (not its whole
        /// root) are what its cut withdraws, and moving it moves neither that body nor the cut's source body, which
        /// stays out of the scene. A hit accepted as Pending gives the source body the character's numbers at the
        /// acceptance; what the character does and is told afterwards -- moved away, a new motion -- changes nothing of
        /// them, and the published sides carry the accepted motion. The slot comes back, and its next individual's
        /// prepared cut has the slot's numbers, a source body of its own, and no motion left from the one before.
        /// </para>
        /// </summary>
        [UnityTest]
        public IEnumerator ACrowdsCharacter_HasNoBodyFollowingIt_AndItsCutTakesItsNumbers_ThroughPendingAndReuse()
        {
            ColdWorld();
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            SandboxNpcCharacter slot = MotionSlot(new Vector3(2f, 0f, 1f), Quaternion.Euler(0f, 40f, 0f), out GameObject setup, out GameObject root, out Rigidbody authored);
            Assert.That(authored.transform.IsChildOf(root.transform), Is.True, "authored under the character root, as the scenes have it");
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);

            // 1. The numbers, read once; the authored body set aside; the character holding no body.
            VpCharacterMassProperties numbers = slot.MassProperties;
            Assert.That(numbers.Mass, Is.EqualTo(12f));
            Near(numbers.CentreOfMass, Vector3.up, 1e-5f, "the centre of mass, in the character root's frame");
            Near(numbers.InertiaTensor, Vector3.one * 4f, 1e-6f, "the inertia");
            Near(numbers.InertiaTensorRotation, Quaternion.identity, "the principal axes");
            Assert.That(slot.MotionBodyAside, Is.True, "the authored body was set aside");
            Assert.That(slot.MotionBody, Is.SameAs(authored));
            Assert.That(authored.gameObject.activeInHierarchy, Is.False, "out of the physics");
            Assert.That(authored.transform.parent, Is.SameAs(setup.transform), "held under the preparation's own object");
            Assert.That(authored.transform.IsChildOf(root.transform), Is.False, "not under the character");
            Assert.That(BodiesUnder(root), Is.Empty, "the character holds no body");
            Assert.That(slot.WithdrawsParts, Is.True, "its parts are what its cut withdraws: " + slot.WholeRootReason);
            VpPreparedCharacterCut first = slot.Handle;
            Assert.That(first.MassProperties.Mass == numbers.Mass && first.MassProperties.CentreOfMass == numbers.CentreOfMass
                && first.MassProperties.InertiaTensor == numbers.InertiaTensor && first.MassProperties.InertiaTensorRotation == numbers.InertiaTensorRotation, Is.True, "the prepared cut keeps the slot's numbers");
            GameObject source = SourceObjectOf(first);
            Assert.That(source.activeSelf, Is.False, "the cut's source body is out of the scene");
            Assert.That(source.transform.parent, Is.Null, "under no character");

            // 2. Activated and moved about: neither body follows, neither is brought into the scene.
            ActivateStill(slot);
            Assert.That(authored.gameObject.activeInHierarchy, Is.False, "an activation does not bring the authored body back");
            authored.transform.GetPositionAndRotation(out Vector3 authoredAt, out Quaternion authoredFacing);
            source.transform.GetPositionAndRotation(out Vector3 sourceAt, out Quaternion sourceFacing);
            for (int f = 0; f < 3; f++)
            {
                root.transform.SetPositionAndRotation(new Vector3(2f + 3f * (f + 1), 0f, 1f - 2f * (f + 1)), Quaternion.Euler(0f, 40f + 50f * (f + 1), 0f));
                yield return null;
                authored.transform.GetPositionAndRotation(out Vector3 a, out Quaternion aq);
                source.transform.GetPositionAndRotation(out Vector3 s, out Quaternion sq);
                Assert.That(a == authoredAt && aq == authoredFacing && !authored.gameObject.activeInHierarchy, Is.True, "the authored body does not follow the character (frame " + f + ")");
                Assert.That(s == sourceAt && sq == sourceFacing && !source.activeSelf, Is.True, "the cut's source body does not follow the character (frame " + f + ")");
                Assert.That(BodiesUnder(root), Is.Empty);
            }

            // 3. A hit accepted as Pending: the source body is given the numbers of the acceptance.
            var placed = new Vector3(20f, 0f, -6f);
            Quaternion placedFacing = Quaternion.Euler(0f, 215f, 0f);
            var velocity = new Vector3(0.9f, 0f, -1.3f);
            var angular = new Vector3(0f, 1.1f, 0f);
            root.transform.SetPositionAndRotation(placed, placedFacing);
            first.SetRootMotion(velocity, angular);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(first);
            yield return null;
            Assert.That(MobPlanReuseProbe.TryAim(first, null, Vector3.forward, 1, 0.0, 0.25f, out SlashSweep sweep, out string aimed), Is.True, aimed);
            SourceValues given = default;
            SideValues[] sides = null;
            void Added(LogicalFragmentId fragment, PhysicsFragmentOwner owner) { if (owner.Root == source) given = ReadSource(owner); }
            void Published(ProvisionalOwnerPair p) { sides = ReadSides(p); }
            coldWorld.Owners.OwnerAdded += Added;
            coldWorld.Owners.ProvisionalAdded += Published;
            coldWorld.Driver.RemainingMainSeconds = () => 0;
            List<SlashHitConfirmed> hits = Evaluate(detector, sweep, 1);
            Assert.That(hits.Count, Is.EqualTo(1));
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending));
            Assert.That(given.owner, Is.Not.Null, "the source body was registered at the acceptance");
            Assert.That(given.inScene, Is.True, "and brought into the scene there");
            Vector3 centre = placed + placedFacing * Vector3.up;
            Vector3 centreVelocity = velocity + Vector3.Cross(angular, centre - placed);
            Assert.That(given.mass, Is.EqualTo(12f));
            Near(given.position, slot.Renderer.transform.position, 1e-4f, "the source body stands where the renderer does");
            Near(given.centreWorld, centre, 2e-4f, "its centre of mass: the kept one, placed by the root as it stood at the acceptance");
            Near(given.inertia, Vector3.one * 4f, 1e-5f, "its inertia");
            Near(given.velocity, centreVelocity, 2e-4f, "its velocity: the root's, carried to the centre of mass");
            Near(given.angular, angular, 1e-5f, "its angular velocity");
            Assert.That(first.IsDisposed, Is.True, "the accepted request ended its handle");

            // While Pending the character is moved on and told another motion, as its crowd goes on doing: nothing of the
            // accepted input changes. The source body keeps its own motion and is not where the character went.
            // (The source body is simulated while Pending, as before, under Unity's default angular damping: its angular
            // velocity falls a little each step. That is read here, not expected to be none.)
            Vector3 centreLocal = given.centreLocal;
            Vector3 lastCentre = given.centreWorld;
            Vector3 lastAngular = given.angular;
            for (int f = 0; f < 3; f++)
            {
                root.transform.SetPositionAndRotation(placed + new Vector3(100f, 0f, 100f), Quaternion.Euler(0f, 10f, 0f));
                first.SetRootMotion(new Vector3(50f, 0f, 0f), new Vector3(0f, -9f, 0f));
                yield return null;
                Assert.That(sides, Is.Null, "still Pending");
                Rigidbody body = given.owner.Body;
                Assert.That(body.mass, Is.EqualTo(12f));
                Near(body.centerOfMass, centreLocal, 1e-6f, "Pending: the centre of mass kept");
                Near(body.inertiaTensor, Vector3.one * 4f, 1e-5f, "Pending: the inertia kept");
                Near(body.linearVelocity, centreVelocity, 1e-3f, "Pending: the accepted velocity kept");
                Near(body.angularVelocity, angular, 0.02f, "Pending: the accepted angular velocity kept (but for the body's own damping)");
                Assert.That(Vector3.Dot(body.angularVelocity, new Vector3(0f, -9f, 0f)), Is.LessThan(0f), "Pending: not the angular velocity told afterwards");
                lastAngular = body.angularVelocity;
                Assert.That(Vector3.Distance(body.worldCenterOfMass, centre), Is.LessThan(1f), "Pending: the source body is about where it was accepted, not where the character went");
                lastCentre = body.worldCenterOfMass;
                Assert.That(BodiesUnder(root), Is.Empty);
            }

            // 4. Published: the sides carry the accepted motion, not the later one.
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            for (int i = 0; i < 600 && sides == null; i++) yield return null;
            coldWorld.Owners.OwnerAdded -= Added;
            coldWorld.Owners.ProvisionalAdded -= Published;
            Assert.That(sides, Is.Not.Null, "the Pending cut was published");
            TestContext.Out.WriteLine("Pending: the source body's angular velocity went from " + angular.ToString("F5") + " to " + lastAngular.ToString("F5") + " before the publication");
            foreach (SideValues side in sides)
            {
                Near(side.angular, lastAngular, 0.01f, "a side's angular velocity is the source body's, from the accepted one");
                Vector3 inherited = centreVelocity + Vector3.Cross(lastAngular, side.centreWorld - lastCentre);
                Assert.That(Vector3.Distance(side.velocity, inherited), Is.LessThan(0.15f),
                    "a side's velocity is the accepted one carried to its centre: " + side.velocity.ToString("F3") + " against " + inherited.ToString("F3"));
            }

            // 5. The character left by its parts; the slot comes back and carries a new individual.
            yield return UntilCommitted(hits[0].Operation);
            Assert.That(first.IsWithdrawn && first.WithdrawsParts, Is.True, "withdrawn by its parts");
            Assert.That(root.activeInHierarchy, Is.True, "the hierarchy stays");
            Assert.That(slot.Renderer.enabled, Is.False, "not drawn");
            Assert.That(BodiesUnder(root), Is.Empty);
            Assert.That(authored != null && !authored.gameObject.activeInHierarchy && authored.transform.parent == setup.transform, Is.True, "the authored body is where it was set aside");
            for (int i = 0; i < 120 && !slot.IsReturnReady; i++) yield return null;
            Assert.That(slot.IsReturnReady, Is.True);
            Assert.That(slot.TryReprepare(), Is.True, slot.Failure);
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            VpPreparedCharacterCut second = slot.Handle;
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.MassProperties.Mass == numbers.Mass && second.MassProperties.CentreOfMass == numbers.CentreOfMass
                && second.MassProperties.InertiaTensor == numbers.InertiaTensor && second.MassProperties.InertiaTensorRotation == numbers.InertiaTensorRotation, Is.True, "the next prepared cut has the slot's numbers");
            Assert.That(slot.WithdrawsParts, Is.True, slot.WholeRootReason);
            GameObject secondSource = SourceObjectOf(second);
            Assert.That(secondSource, Is.Not.SameAs(source), "a source body of its own");
            Assert.That(secondSource.activeSelf, Is.False);
            Assert.That(secondSource.transform.parent, Is.Null);
            Assert.That(authored.gameObject.activeInHierarchy, Is.False, "the authored body stays aside through the reuse");
            ActivateStill(slot);
            Assert.That(slot.Activations, Is.EqualTo(2));
            Assert.That(BodiesUnder(root), Is.Empty);

            // The new individual, placed and not told any motion: at rest, whatever the one before was told.
            var again = new Vector3(-8f, 0f, 12f);
            Quaternion againFacing = Quaternion.Euler(0f, -75f, 0f);
            root.transform.SetPositionAndRotation(again, againFacing);
            detector.AddCharacter(second);
            yield return null;
            Assert.That(MobPlanReuseProbe.TryAim(second, null, Vector3.forward, 2, 0.0, 0.25f, out SlashSweep next, out aimed), Is.True, aimed);
            SourceValues givenAgain = default;
            void AddedAgain(LogicalFragmentId fragment, PhysicsFragmentOwner owner) { if (owner.Root == secondSource) givenAgain = ReadSource(owner); }
            coldWorld.Owners.OwnerAdded += AddedAgain;
            List<SlashHitConfirmed> more = Evaluate(detector, next, 2).FindAll(h => h.Fragment == second.Source);
            coldWorld.Owners.OwnerAdded -= AddedAgain;
            Assert.That(more.Count, Is.EqualTo(1), "the new individual is hit");
            Assert.That(more[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(givenAgain.owner, Is.Not.Null);
            Near(givenAgain.centreWorld, again + againFacing * Vector3.up, 2e-4f, "the new individual's centre of mass, where it stands");
            Near(givenAgain.velocity, Vector3.zero, 1e-6f, "no motion left from the individual before");
            Near(givenAgain.angular, Vector3.zero, 1e-6f, "nor an angular velocity");
            Assert.That(givenAgain.mass, Is.EqualTo(12f));
            yield return UntilCommitted(more[0].Operation);

            // 6. The authored body goes with the preparation's object.
            Object.Destroy(setup);
            yield return null;
            Assert.That(authored == null, Is.True, "the body set aside went with the slot");
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True);
        }

        /// <summary>
        /// **A body that shares its object stays, and the whole root leaves.** The settings are read into numbers all the
        /// same; a body on the character root itself cannot be taken out alone, so it stays where it is, and the
        /// character's withdrawal takes its whole root -- as for any hierarchy that holds a body.
        /// </summary>
        [UnityTest]
        public IEnumerator ABodyThatSharesItsObject_Stays_ItsSettingsAreReadAllTheSame_AndTheWholeRootLeaves()
        {
            ColdWorld();
            SandboxNpcCharacter slot = Slot("slot-a", "slot mesh a", new SandboxNpcCharacter.SlotShare(), new Vector3(0f, 0f, 0f), out GameObject setup);
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            Assert.That(slot.MassProperties.IsUsable, Is.True);
            Assert.That(slot.MassProperties.Mass, Is.EqualTo(12f));
            Near(slot.MassProperties.InertiaTensor, Vector3.one * 4f, 1e-6f, "the inertia");
            Assert.That(slot.MotionBodyAside, Is.False, "a body on the root itself is not taken out");
            Assert.That(slot.MotionBody.gameObject, Is.SameAs(slot.CharacterRoot));
            Assert.That(slot.WithdrawsParts, Is.False, "the whole root is what leaves");
            Assert.That(slot.WholeRootReason, Does.Contain("Rigidbody"));
            Assert.That(slot.CharacterRoot.activeSelf, Is.True, "a dormant slot's root is not deactivated for its body");
            yield return Destroy(setup);
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True);
        }
    }
}
