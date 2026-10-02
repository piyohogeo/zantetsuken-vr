using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The kinematic mode of the building rest trial (<see cref="BuildingRestMode.Kinematic"/>, 2026-09-29, local release)
    /// on the product's cut path: a supported piece becomes temporarily kinematic after its own timeout, from the confirmed
    /// bottom up; a re-cut's physical publication releases only what stood on the cut piece (held with support still
    /// confirmed: dynamic and asleep once; held without: dynamic and woken; dynamic: untouched); the rest stay held.
    /// World D6 off unless a case says otherwise (the profile's trial switch; the product's value is on).
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private CutWorldRoot NewKinematicRestWorld(bool worldD6 = false, float floorTop = -1f)
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, profile =>
            {
                SetPrivate(profile, "buildingRestEnabled", true);
                SetPrivate(profile, "buildingRestMode", BuildingRestMode.Kinematic);
                SetPrivate(profile, "buildingWorldEnabled", worldD6);
            });
            Assert.That(root.Rest.Enabled && root.Rest.Settings.mode == BuildingRestMode.Kinematic, Is.True, "the kinematic mode is on");
            root.Driver.RemainingMainSeconds = () => 1.0;
            GameObject floor = Track(new GameObject("Floor"));
            var box = floor.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, floorTop - 0.5f, 0f);
            box.size = new Vector3(80f, 1f, 80f);
            return root;
        }

        /// <summary>A building box with one anchor at its bottom, so that the side keeping it is fixed by the ledger.</summary>
        private LogicalFragmentId AddAnchoredBuilding(CutWorldRoot root, Vector3 at)
        {
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxGeometry(root.Storage, default);
            var actor = TrackActor(new GameObject("Anchored Building"));
            actor.transform.position = at;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = true;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);
            var anchors = new[] { new float3(0f, -0.9f, 0f) };
            bool added = root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, anchors, true, out LogicalFragmentId fragment);
            if (added) _registered.Add(actor);
            Assert.That(added, Is.True, "the anchored building was taken into the world");
            return fragment;
        }

        private static IEnumerator UntilRested(CutWorldRoot root, int count, string what)
        {
            float deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            yield return Until(() => root.Rest.RestedNow >= count || Time.realtimeSinceStartup > deadline, what);
            Assert.That(root.Rest.RestedNow, Is.EqualTo(count), what + ": rested pieces");
            Assert.That(root.Rest.AsleepWithoutSupport, Is.Zero, what + ": none held or asleep without a chain to the ground");
        }

        private static void WriteKinematicRecord(CutWorldRoot root, string what)
        {
            BuildingRest rest = root.Rest;
            TestContext.Out.WriteLine(what + ": tracked " + rest.TrackedDynamic + "+" + rest.TrackedAnchored + ", kinematic rests " + rest.KinematicRests + ", held now " + rest.RestedNow + ", asleep by the rest " + rest.AsleepNow
                + ", re-cuts " + rest.ReCuts + " (candidates " + rest.ReCutCandidates + ", most " + rest.MaxReCutCandidates + "; held to dynamic " + rest.ReleasedPieces + ": asleep " + rest.SleptOnRelease + " woken " + rest.WokenOnRelease + "; max " + (rest.MaxReleaseSeconds * 1000).ToString("F3") + " ms for " + rest.MaxReleasePieces + ")"
                + ", woken by support loss " + rest.WokenBySupportLoss + ", grounds lost " + rest.LostGrounds + ", auto wakes " + rest.AutoWakes
                + ", unsupported past timeout " + rest.UnsupportedPastTimeout + ", held without support " + rest.AsleepWithoutSupport + ", moved until rest mean/max " + rest.MeanMovedUntilRest.ToString("F4") + "/" + rest.MaxMovedUntilRest.ToString("F4") + " m"
                + ", cost contacts/support/switch/rest ms " + (rest.ContactSeconds * 1000).ToString("F2") + "/" + (rest.SupportSeconds * 1000).ToString("F2") + "/" + (rest.SwitchSeconds * 1000).ToString("F3") + "/" + (rest.SleepSeconds * 1000).ToString("F2") + " over " + rest.Steps + " turns");
            foreach (string e in rest.Events)
            {
                TestContext.Out.WriteLine("  " + e);
            }
        }

        private static string MassOf(Rigidbody body) =>
            body.mass.ToString("F5") + "|" + body.centerOfMass.ToString("F5") + "|" + body.inertiaTensor.ToString("F5") + "|" + body.inertiaTensorRotation.ToString("F5");

        private static LogicalFragmentId Upper(CutWorldRoot root, LogicalFragmentId[] sides) =>
            BodyOf(root, sides[0]).worldCenterOfMass.y > BodyOf(root, sides[1]).worldCenterOfMass.y ? sides[0] : sides[1];

        private static LogicalFragmentId Other(LogicalFragmentId one, LogicalFragmentId[] sides) => one.Equals(sides[0]) ? sides[1] : sides[0];

        private static double ReleaseTime(CutWorldRoot root)
        {
            double at = -1.0;
            foreach (string e in root.Rest.Events) if (e.StartsWith("re-cut t ")) at = double.Parse(e.Substring(9, e.IndexOf(' ', 9) - 9), System.Globalization.CultureInfo.InvariantCulture);
            return at;
        }

        /// <summary>
        /// **Supported pieces become kinematic, and an outside collision does not release them.** The two halves on the
        /// floor are held kinematic after the timeout, with their velocities zero; a sphere dropped on them neither moves
        /// them nor releases anything. Mass properties read back unchanged across the switch.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_SupportedPiecesAreHeld_AndAnOutsideCollisionDoesNotRelease()
        {
            CutWorldRoot root = NewKinematicRestWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            string mass0 = MassOf(BodyOf(root, sides[0])), mass1 = MassOf(BodyOf(root, sides[1]));
            yield return UntilRested(root, 2, "the pieces' rest");
            WriteKinematicRecord(root, "after the rest");
            int reCutsBefore = root.Rest.ReCuts;   // the registered building's own first cut counts as one (its source was tracked)
            Rigidbody a = BodyOf(root, sides[0]), b = BodyOf(root, sides[1]);
            Assert.That(a.isKinematic && b.isKinematic, Is.True, "both held kinematic");
            Assert.That(a.linearVelocity.magnitude + b.linearVelocity.magnitude + a.angularVelocity.magnitude + b.angularVelocity.magnitude, Is.LessThan(1e-4f), "at rest, velocities zero");
            Assert.That(MassOf(a), Is.EqualTo(mass0), "mass, centre, inertia and axes read back unchanged");
            Assert.That(MassOf(b), Is.EqualTo(mass1));
            Assert.That(root.Owners.TryGet(sides[0], out PhysicsFragmentOwner owner) && !owner.FixedByAnchors, Is.True, "the ledger's fixity is untouched (not anchored)");
            Vector3 ca = a.worldCenterOfMass, cb = b.worldCenterOfMass;

            GameObject sphere = Track(new GameObject("Outside Sphere"));
            sphere.transform.position = new Vector3(0.3f, 5f, 0.2f);
            sphere.AddComponent<SphereCollider>().radius = 0.25f;
            var ball = sphere.AddComponent<Rigidbody>();
            ball.mass = 2f;
            ball.useGravity = true;
            yield return PhysicsSeconds(2.0);
            WriteKinematicRecord(root, "two seconds after the sphere");
            Assert.That(root.Rest.ReCuts - reCutsBefore + root.Rest.WokenBySupportLoss + root.Rest.LostGrounds, Is.Zero, "no release from an outside collision");
            Assert.That(a.isKinematic && b.isKinematic, Is.True, "still held");
            Assert.That((a.worldCenterOfMass - ca).magnitude + (b.worldCenterOfMass - cb).magnitude, Is.LessThan(1e-3f), "and they did not move");
            TestContext.Out.WriteLine("render fragments of the branch while held: " + DrawnFragmentsOf(root, building) + " (display collection in batch mode)");
            Assert.That(IsHitTarget(root, sides[0]) && IsHitTarget(root, sides[1]), Is.True, "still hit targets");
            yield return EndWorld(root);
            Assert.That(root.Rest, Is.Null, "the ending released the trial");
        }

        /// <summary>**Airborne pieces keep falling; pieces touching in the air are not held; once landed they are.**</summary>
        [UnityTest]
        public IEnumerator KinematicRest_AirbornePiecesKeepFalling_AndAreHeldOnceLanded()
        {
            CutWorldRoot root = NewKinematicRestWorld(false, -12f);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            double published = CutPhysicsStep.Clock.PhysicsSeconds;
            yield return Until(() => CutPhysicsStep.Clock.PhysicsSeconds >= published + 1.15, "past the timeout");
            float y0 = BodyOf(root, sides[0]).worldCenterOfMass.y, y1 = BodyOf(root, sides[1]).worldCenterOfMass.y;
            WriteKinematicRecord(root, "past the timeout in the air (centres y " + y0.ToString("F2") + ", " + y1.ToString("F2") + ")");
            Assert.That(Mathf.Min(y0, y1), Is.GreaterThan(-9f), "the layout: still in the air");
            Assert.That(root.Rest.KinematicRests, Is.Zero, "nothing held in the air");
            Assert.That(BodyOf(root, sides[0]).isKinematic || BodyOf(root, sides[1]).isKinematic, Is.False);
            Assert.That(root.Rest.UnsupportedPastTimeout, Is.EqualTo(2), "both past the timeout without support, and dynamic");
            yield return UntilRested(root, 2, "held once landed");
            WriteKinematicRecord(root, "after landing");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A re-cut releases only what stood on the cut piece; distant held pieces stay held.** The box is cut into a lower
        /// half and two upper quarters, all three held. Re-cutting one quarter (nothing stands on it) releases nothing: the
        /// lower half and the other quarter stay kinematic, the new children start from rest, and the operation is applied
        /// once (Provisional, not again at the Final). Once all are held again, re-cutting the lower half releases exactly
        /// the three pieces standing on it, woken (their only support was the cut piece); they are held again later.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_ReCutReleasesOnlyWhatStoodOnTheCutPiece_AndDistantHeldPiecesStayHeld()
        {
            CutWorldRoot root = NewKinematicRestWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = Upper(root, halves), lower = Other(upper, halves);
            var quarters = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(1f, 0f, 0f, 0f), quarters);
            yield return UntilRested(root, 3, "the three pieces' rest");
            WriteKinematicRecord(root, "three held");
            int reCutsBefore = root.Rest.ReCuts;

            var children = new LogicalFragmentId[2];
            yield return CutInTwo(root, quarters[0], new float4(0f, 0f, 1f, 0f), children);
            WriteKinematicRecord(root, "after the re-cut of a held quarter with nothing on it");
            Assert.That(root.Rest.ReCuts, Is.EqualTo(reCutsBefore + 1), "one re-cut for the cut, not one per notification");
            Assert.That(root.Rest.MaxReCutCandidates, Is.Zero, "nothing stood on the quarter: no candidate");
            Assert.That(root.Rest.ReleasedPieces, Is.Zero, "nothing returned to dynamic");
            Assert.That(BodyOf(root, lower).isKinematic && BodyOf(root, quarters[1]).isKinematic, Is.True, "the distant held pieces stay held");
            Assert.That(BodyOf(root, children[0]).linearVelocity.magnitude + BodyOf(root, children[1]).linearVelocity.magnitude, Is.LessThan(0.5f), "the children of a held piece start from rest");
            Assert.That(root.Rest.TrackedDynamic, Is.EqualTo(4));
            yield return UntilRested(root, 4, "the four pieces held");
            Assert.That(root.Rest.ReCuts, Is.EqualTo(reCutsBefore + 1), "and no other release");

            // The lower half supports the other quarter and the two children: cutting it releases exactly those, woken.
            Assert.That(root.Rest.SupportedCountOf(lower), Is.EqualTo(3), "the layout: three pieces stand on the lower half");
            var lowerChildren = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(1f, 0f, 0f, 0f), lowerChildren);
            WriteKinematicRecord(root, "after the re-cut of the lower half");
            Assert.That(root.Rest.ReCuts, Is.EqualTo(reCutsBefore + 2));
            Assert.That(root.Rest.MaxReCutCandidates, Is.EqualTo(3), "the three pieces standing on it are the candidates");
            Assert.That(root.Rest.ReleasedPieces, Is.EqualTo(3), "all three were held and return to dynamic");
            Assert.That(root.Rest.WokenOnRelease, Is.EqualTo(3), "their only support was the cut piece: woken, not put to sleep");
            foreach (LogicalFragmentId f in new[] { quarters[1], children[0], children[1] })
            {
                Rigidbody b = BodyOf(root, f);
                Assert.That(b.isKinematic, Is.False, "piece " + f.value + " dynamic");
                Assert.That(b.IsSleeping(), Is.False, "piece " + f.value + " awake");
            }

            yield return UntilRested(root, 5, "all five held again on the lower half's children");
            WriteKinematicRecord(root, "held again");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Cutting a supporter wakes the piece above, and it rests again by its own clock.** The upper half of a tower is
        /// held on the lower half; the lower half is re-cut; the upper half returns to dynamic, woken, its clock starting at
        /// the release; it settles on the children and is held again only a full timeout after the release. Its displacement
        /// is written out (the children remain where the lower half was, so it need not fall far).
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_CuttingTheSupporter_WakesThePieceAbove_AndItRestsAgainByItsOwnClock()
        {
            CutWorldRoot root = NewKinematicRestWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            LogicalFragmentId upper = Upper(root, sides), lower = Other(upper, sides);
            yield return UntilRested(root, 2, "the halves' rest");
            Rigidbody upperBody = BodyOf(root, upper);
            Vector3 before = upperBody.worldCenterOfMass;
            int reCutsBefore = root.Rest.ReCuts;

            var children = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(1f, 0f, 0f, 0f), children);
            double releaseAt = ReleaseTime(root);
            WriteKinematicRecord(root, "after the supporter's re-cut (release at t " + releaseAt.ToString("F3") + ")");
            Assert.That(root.Rest.ReCuts, Is.EqualTo(reCutsBefore + 1));
            Assert.That(root.Rest.MaxReCutCandidates, Is.EqualTo(1), "the upper half is the one candidate");
            Assert.That(root.Rest.WokenOnRelease, Is.EqualTo(1), "woken: its only support was the cut piece");
            Assert.That(upperBody.isKinematic, Is.False, "dynamic again");
            Assert.That(upperBody.IsSleeping(), Is.False, "and awake");
            Assert.That(root.Rest.TryGetClock(upper, out double clock) && clock >= releaseAt - 5e-4, Is.True, "its clock starts at the release (the release time is read back from the record, printed to 3 decimals)");
            Assert.That(BodyOf(root, children[0]).isKinematic || BodyOf(root, children[1]).isKinematic, Is.False, "the children are ordinary dynamic pieces");

            double restedAt = -1.0;
            float deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            while (restedAt < 0.0 && Time.realtimeSinceStartup < deadline)
            {
                if (root.Rest.IsRested(upper)) restedAt = CutPhysicsStep.Clock.PhysicsSeconds;
                yield return null;
            }

            WriteKinematicRecord(root, "the upper half held again at t " + restedAt.ToString("F3") + ", moved " + (upperBody.worldCenterOfMass - before).magnitude.ToString("F4") + " m since the release");
            Assert.That(restedAt, Is.GreaterThan(0.0), "held again in time");
            Assert.That(restedAt - releaseAt, Is.GreaterThanOrEqualTo(root.Rest.Settings.timeoutSeconds - 0.02), "only after its own full timeout from the release");
            yield return UntilRested(root, 3, "all three held");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A piece with an alternative support is put to sleep, not woken.** A slab stands on two held boxes; one box is
        /// re-cut; the slab, still grounded through the other box, returns to dynamic with zero velocity and one explicit
        /// sleep (no wake); the other box stays held.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_APieceWithAnAlternativeSupport_IsPutToSleepNotWoken()
        {
            CutWorldRoot root = NewKinematicRestWorld();
            LogicalFragmentId s1 = AddBuilding(root, new Vector3(-1.2f, 0f, 0f));
            LogicalFragmentId s2 = AddBuilding(root, new Vector3(1.2f, 0f, 0f));
            LogicalFragmentId slab = AddBuilding(root, new Vector3(0f, 2.05f, 0f));
            yield return null;
            yield return UntilRested(root, 3, "the two boxes and the slab held");
            Assert.That(root.Rest.SupportedCountOf(s1) == 1 && root.Rest.SupportedCountOf(s2) == 1, Is.True, "the layout: the slab stands on both boxes");
            Rigidbody slabBody = BodyOf(root, slab);
            int reCutsBefore = root.Rest.ReCuts;

            var children = new LogicalFragmentId[2];
            yield return CutInTwo(root, s1, new float4(0f, 0f, 1f, 0f), children);
            WriteKinematicRecord(root, "after the re-cut of one box under the slab");
            Assert.That(root.Rest.ReCuts, Is.EqualTo(reCutsBefore + 1));
            Assert.That(root.Rest.MaxReCutCandidates, Is.EqualTo(1), "the slab is the candidate");
            Assert.That(root.Rest.ReleasedPieces, Is.EqualTo(1), "it returns to dynamic");
            Assert.That(root.Rest.SleptOnRelease, Is.EqualTo(1), "put to sleep once: its support through the other box is still confirmed");
            Assert.That(root.Rest.WokenOnRelease, Is.Zero, "not woken");
            Assert.That(slabBody.isKinematic, Is.False, "dynamic");
            Assert.That(slabBody.linearVelocity.magnitude + slabBody.angularVelocity.magnitude, Is.LessThan(1e-4f), "velocity zero");
            Assert.That(BodyOf(root, s2).isKinematic, Is.True, "the other box stays held");
            yield return PhysicsSeconds(0.5);
            WriteKinematicRecord(root, "half a second later: slab asleep " + slabBody.IsSleeping() + ", |v| " + slabBody.linearVelocity.magnitude.ToString("F4"));
            Assert.That(slabBody.isKinematic, Is.False, "still dynamic before its own timeout");
            yield return UntilRested(root, 4, "the slab, the other box and the children held");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Another building is not released, a ledger-anchored piece stays fixed, and a refused cut releases nothing.**
        /// Building A and building B rest; re-cutting A's lower half releases A's upper half only. An anchored building's
        /// fixed side stays kinematic and FixedByAnchors through its free piece's re-cut. A cut whose plane misses is refused
        /// and releases nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_OtherBuildingsAndLedgerAnchorsAreNotReleased_NorByARefusedCut()
        {
            CutWorldRoot root = NewKinematicRestWorld();
            LogicalFragmentId a = AddBuilding(root, Vector3.zero);
            LogicalFragmentId b = AddBuilding(root, new Vector3(8f, 0f, 0f));
            LogicalFragmentId c = AddAnchoredBuilding(root, new Vector3(-8f, 0f, 0f));
            yield return null;
            var aSides = new LogicalFragmentId[2]; var bSides = new LogicalFragmentId[2]; var cSides = new LogicalFragmentId[2];
            yield return CutInTwo(root, a, new float4(0f, 1f, 0f, 0f), aSides);
            yield return CutInTwo(root, b, new float4(0f, 1f, 0f, 0f), bSides);
            yield return CutInTwo(root, c, new float4(0f, 1f, 0f, 0f), cSides);
            LogicalFragmentId cFixed = root.Owners.TryGet(cSides[0], out PhysicsFragmentOwner o0) && o0.FixedByAnchors ? cSides[0] : cSides[1];
            LogicalFragmentId cFree = Other(cFixed, cSides);
            Assert.That(root.Owners.TryGet(cFixed, out PhysicsFragmentOwner fixedOwner) && fixedOwner.FixedByAnchors && fixedOwner.Body.isKinematic, Is.True, "the layout: the anchored side is fixed by the ledger");
            yield return UntilRested(root, 5, "A's two, B's two and C's free piece held");
            WriteKinematicRecord(root, "five held");

            // A refused cut: a plane that misses A's upper piece.
            int reCutsBefore = root.Rest.ReCuts;
            ProvisionalCutAsk miss = Ask(aSides[0], new float4(0f, 1f, 0f, -100f));
            bool asked = root.TryAsk(in miss);
            yield return null;
            yield return null;
            TestContext.Out.WriteLine("a cut whose plane misses: asked " + asked + ", re-cuts " + root.Rest.ReCuts);
            Assert.That(root.Rest.ReCuts, Is.EqualTo(reCutsBefore), "a refused or missed cut releases nothing");
            Assert.That(root.Rest.RestedNow, Is.EqualTo(5));

            // A's lower half re-cut releases A's upper half only.
            LogicalFragmentId aUpper = Upper(root, aSides), aLower = Other(aUpper, aSides);
            var aChildren = new LogicalFragmentId[2];
            yield return CutInTwo(root, aLower, new float4(1f, 0f, 0f, 0f), aChildren);
            WriteKinematicRecord(root, "after A's lower half re-cut");
            Assert.That(root.Rest.ReCuts, Is.EqualTo(reCutsBefore + 1));
            Assert.That(root.Rest.MaxReCutCandidates, Is.EqualTo(1), "A's upper half is the one candidate");
            Assert.That(root.Rest.ReleasedPieces, Is.EqualTo(1));
            Assert.That(BodyOf(root, aUpper).isKinematic, Is.False, "A's upper half is dynamic");
            Assert.That(BodyOf(root, bSides[0]).isKinematic && BodyOf(root, bSides[1]).isKinematic, Is.True, "B stays held");
            Assert.That(BodyOf(root, cFree).isKinematic, Is.True, "C's free piece stays held");

            // C's free piece re-cut: nothing stands on it; the anchored side stays fixed.
            var cChildren = new LogicalFragmentId[2];
            yield return CutInTwo(root, cFree, new float4(1f, 0f, 0f, 0f), cChildren);   // planes are in the building's own frame
            WriteKinematicRecord(root, "after C's re-cut");
            Assert.That(root.Rest.ReCuts, Is.EqualTo(reCutsBefore + 2));
            Assert.That(root.Rest.ReleasedPieces, Is.EqualTo(1), "nothing more returned to dynamic");
            Assert.That(root.Owners.TryGet(cFixed, out fixedOwner) && fixedOwner.FixedByAnchors && fixedOwner.Body.isKinematic, Is.True, "the anchored side is still fixed by the ledger");
            Assert.That(root.Rest.IsRested(cFixed), Is.False, "and was never this trial's");
            Assert.That(BodyOf(root, bSides[0]).isKinematic, Is.True, "B still held");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Removing the support for real returns the piece above to dynamic, and it falls.** The lower half is retired
        /// (the lifetime's way: ledger, owners, geometry); the piece above loses its chain to the ground and is woken; it
        /// falls to the floor and is held there later. No re-cut is involved.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_RemovingTheSupporter_ReturnsThePieceAboveToDynamic_AndItFalls()
        {
            CutWorldRoot root = NewKinematicRestWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            yield return UntilRested(root, 2, "the pieces' rest");
            LogicalFragmentId upper = Upper(root, sides), lower = Other(upper, sides);
            Rigidbody upperBody = BodyOf(root, upper);
            float yBefore = upperBody.worldCenterOfMass.y;
            int reCutsBefore = root.Rest.ReCuts;

            Assert.That(root.Ledger.Retire(lower), Is.True, "the lower half retired in the ledger");
            Assert.That(root.Owners.Retire(lower), Is.True, "and its owner");
            root.Geometry?.Forget(lower);
            yield return null;
            WriteKinematicRecord(root, "after the supporter's retirement");
            Assert.That(root.Rest.WokenBySupportLoss, Is.EqualTo(1), "the piece above was woken as its supporter went");
            Assert.That(root.Rest.ReCuts, Is.EqualTo(reCutsBefore), "no re-cut");
            Assert.That(upperBody.isKinematic, Is.False, "the upper half is dynamic again");
            yield return PhysicsSeconds(1.0);
            float fallen = yBefore - upperBody.worldCenterOfMass.y;
            WriteKinematicRecord(root, "one second later: fallen " + fallen.ToString("F3") + " m");
            Assert.That(fallen, Is.GreaterThan(0.5f), "it fell");
            yield return UntilRested(root, 1, "held again on the floor");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Rest, re-cut, rest again, three times: the drawing, the hits, the mass properties and the ending hold.** Each
        /// round the held piece with the most pieces standing on it is re-cut (so that a release happens), every live piece
        /// is drawn and a hit target, its mass, centre, inertia and axes read back as published, and the world ends with
        /// everything given back.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_RepeatedRestAndReCut_KeepDrawingHitsMassAndTheEnding()
        {
            CutWorldRoot root = NewKinematicRestWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            var live = new List<LogicalFragmentId> { sides[0], sides[1] };
            var massAtPublication = new Dictionary<LogicalFragmentId, string> { [sides[0]] = MassOf(BodyOf(root, sides[0])), [sides[1]] = MassOf(BodyOf(root, sides[1])) };
            float4[] planes = { new float4(1f, 0f, 0f, 0f), new float4(0f, 0f, 1f, 0f), new float4(0f, 1f, 0f, -0.5f) };   // in the building's frame
            int reCutsBefore = root.Rest.ReCuts;
            for (int round = 0; round < 3; round++)
            {
                yield return UntilRested(root, live.Count, "round " + round + ": all held");
                foreach (LogicalFragmentId f in live)
                {
                    Assert.That(MassOf(BodyOf(root, f)), Is.EqualTo(massAtPublication[f]), "round " + round + ": piece " + f.value + " mass properties as published");
                    Assert.That(IsHitTarget(root, f), Is.True, "round " + round + ": piece " + f.value + " is a hit target while held");
                }

                TestContext.Out.WriteLine("round " + round + ": render fragments of the branch " + DrawnFragmentsOf(root, building) + " (display collection in batch mode)");
                // Re-cut the held piece with the most pieces standing on it (the lowest supporter).
                LogicalFragmentId target = live[0];
                foreach (LogicalFragmentId f in live) if (root.Rest.SupportedCountOf(f) > root.Rest.SupportedCountOf(target)) target = f;
                int releasedBefore = root.Rest.ReleasedPieces, expected = root.Rest.SupportedCountOf(target);
                var children = new LogicalFragmentId[2];
                float4 plane = round < 2 ? planes[round] : new float4(0f, 1f, 0f, -BodyOf(root, target).worldCenterOfMass.y);   // the last round: horizontal through the target (a lower piece lies below y 0.5)
                yield return CutInTwo(root, target, plane, children);
                live.Remove(target);
                live.AddRange(children);
                massAtPublication[children[0]] = MassOf(BodyOf(root, children[0]));
                massAtPublication[children[1]] = MassOf(BodyOf(root, children[1]));
                WriteKinematicRecord(root, "round " + round + ": after the re-cut of piece " + target.value + " (" + expected + " directly on it)");
                Assert.That(root.Rest.ReCuts, Is.EqualTo(reCutsBefore + round + 1), "one re-cut per round");
                Assert.That(root.Rest.ReleasedPieces - releasedBefore, Is.GreaterThanOrEqualTo(expected), "round " + round + ": at least the pieces directly on it returned to dynamic");
                foreach (LogicalFragmentId f in children) Assert.That(BodyOf(root, f).isKinematic, Is.False, "round " + round + ": child " + f.value + " dynamic");
            }

            yield return UntilRested(root, live.Count, "all held after the third round");
            WriteKinematicRecord(root, "after three rounds");
            foreach (LogicalFragmentId f in live) Assert.That(MassOf(BodyOf(root, f)), Is.EqualTo(massAtPublication[f]), "mass properties after three rounds");
            yield return EndWorld(root);
            Assert.That(root.Rest, Is.Null);
        }

        /// <summary>
        /// **With the World D6 present, a supporter's re-cut releases the piece above into the constraint's own motion.**
        /// The product's D6 on: the two halves are held, the lower one is re-cut, the upper one is woken; what moves in the
        /// next second is written out -- the constraint's reference was not moved by the rest.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_WithTheWorldD6_TheSupporterReCut_AndTheMotionIsWrittenOut()
        {
            CutWorldRoot root = NewKinematicRestWorld(true);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            LogicalFragmentId upper = Upper(root, sides), lower = Other(upper, sides);
            Assert.That(root.Owners.TryGet(upper, out PhysicsFragmentOwner o) && o.BuildingWorldConstraint != null, Is.True, "the layout: the World D6 is present");
            yield return UntilRested(root, 2, "held with the D6");
            Vector3 anchorBefore = o.BuildingWorldConstraint.connectedAnchor;
            int reCutsBefore = root.Rest.ReCuts;
            var children = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(1f, 0f, 0f, 0f), children);
            Assert.That(root.Rest.ReCuts, Is.EqualTo(reCutsBefore + 1));
            Assert.That(root.Rest.ReleasedPieces, Is.EqualTo(1), "the upper half returned to dynamic");
            float maxSpeed = 0f; Vector3 c0 = BodyOf(root, upper).worldCenterOfMass;
            double until = CutPhysicsStep.Clock.PhysicsSeconds + 1.0;
            while (CutPhysicsStep.Clock.PhysicsSeconds < until)
            {
                maxSpeed = Mathf.Max(maxSpeed, BodyOf(root, upper).linearVelocity.magnitude);
                yield return null;
            }

            WriteKinematicRecord(root, "one second after the supporter's re-cut: upper max |v| " + maxSpeed.ToString("F3") + " m/s, moved " + (BodyOf(root, upper).worldCenterOfMass - c0).magnitude.ToString("F4") + " m, D6 anchor unchanged " + (o.BuildingWorldConstraint.connectedAnchor == anchorBefore));
            Assert.That(o.BuildingWorldConstraint.connectedAnchor, Is.EqualTo(anchorBefore), "the constraint's reference was not moved");
            yield return EndWorld(root);
        }
    }
}
