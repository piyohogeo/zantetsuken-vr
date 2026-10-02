using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The separation impulse's strength on the Provisional path (DESIGN 7.2), as the driver uses it: each free side
    /// is given what the strength returns for its own temporary mass, once, when the pair is published, along the
    /// adopted normal and outward; a side the anchors fix is given nothing; without a strength the caller's two values
    /// stand; and a strength that cannot be used ends the request with nothing published and nothing left behind.
    /// <para>
    /// The strength here is the provisional J = k × mass with this test's own k. The source is at rest, so a side's
    /// first velocity is its separation alone. The plane is the source's up, so the normal is world up.
    /// </para>
    /// </summary>
    public unsafe partial class ProvisionalCutDriverTests
    {
        private const float k_perKg = 1f;

        /// <summary>The masses the strength was asked about, in the order asked.</summary>
        private static SeparationImpulseStrength Recording(List<double> asked, float perKg = k_perKg)
        {
            return mass =>
            {
                asked.Add(mass);
                return (float)(perKg * mass);
            };
        }

        /// <summary>
        /// Two sides of different mass: each is given k × its own mass, so each moves off at k m/s, the positive side
        /// up and the negative side down.
        /// </summary>
        [Test]
        public void TheStrength_GivesEachSideKTimesItsOwnMass_OutwardAlongTheNormal()
        {
            // The second box lies wholly above the plane: the positive side is the heavier one.
            using (World w = NewWorld(convexOffsets: new[] { new double3(0.0, 0.0, 0.0), new double3(1.5, 1.0, 0.0) }))
            {
                w.job.HoldEverything = true;
                var asked = new List<double>();
                w.driver.SeparationStrength = Recording(asked);
                ProvisionalOwnerPair pair = Publish(w).Pair;

                double positiveMass = pair.Positive.Mass;
                double negativeMass = pair.Negative.Mass;
                Assert.That(positiveMass, Is.GreaterThan(negativeMass * 1.5), "the two sides are of different mass");
                Assert.That(asked, Is.EqualTo(new[] { positiveMass, negativeMass }), "asked once for each side's own mass");

                Assert.That(
                    (float3)pair.Positive.Body.linearVelocity,
                    Is.EqualTo(new float3(0f, k_perKg, 0f)).Using(Float3Within(1e-4f)),
                    "k × its mass moves the positive side off at k m/s, up the normal");
                Assert.That(
                    (float3)pair.Negative.Body.linearVelocity,
                    Is.EqualTo(new float3(0f, -k_perKg, 0f)).Using(Float3Within(1e-4f)),
                    "and the negative side at k m/s, down it");
            }
        }

        /// <summary>
        /// A side the anchors fix is given nothing and its strength is not asked; without a strength the caller's own
        /// two values are used, each over its side's own mass.
        /// </summary>
        [Test]
        public void AFixedSideIsGivenNothing_AndWithoutAStrengthTheCallersValuesStand()
        {
            using (World w = NewWorld(anchors: new[] { new float3(0f, 0.5f, 0f) }))
            {
                w.job.HoldEverything = true;
                var asked = new List<double>();
                w.driver.SeparationStrength = Recording(asked);
                ProvisionalOwnerPair pair = Publish(w).Pair;

                Assert.That(pair.Positive.FixedByAnchors, Is.True, "the anchor above the plane fixes the positive side");
                Assert.That(asked, Is.EqualTo(new[] { pair.Negative.Mass }), "only the free side's strength was asked");
                Assert.That(pair.Positive.Body.isKinematic, Is.True);
                Assert.That(
                    (float3)pair.Positive.Body.linearVelocity, Is.EqualTo(float3.zero).Using(Float3Within(1e-6f)),
                    "the fixed side does not move");
                Assert.That(
                    (float3)pair.Negative.Body.linearVelocity,
                    Is.EqualTo(new float3(0f, -k_perKg, 0f)).Using(Float3Within(1e-4f)),
                    "the free side does");
            }

            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                Assert.That(w.driver.SeparationStrength, Is.Null, "no strength");
                ProvisionalOwnerPair pair = Publish(w, 6f, 1.5f).Pair;
                Assert.That(
                    (float3)pair.Positive.Body.linearVelocity,
                    Is.EqualTo(new float3(0f, 6f / (float)pair.Positive.Mass, 0f)).Using(Float3Within(1e-4f)),
                    "the caller's positive value, over that side's mass");
                Assert.That(
                    (float3)pair.Negative.Body.linearVelocity,
                    Is.EqualTo(new float3(0f, -1.5f / (float)pair.Negative.Mass, 0f)).Using(Float3Within(1e-4f)),
                    "and the caller's negative value, over its own");
            }
        }

        /// <summary>
        /// k = 0 is a coefficient like any other: a profile that switches the mass strength on with k = 0 gives both
        /// free sides no separation impulse at all -- not the caller's own values, which only a profile with the switch
        /// off leaves in place.
        /// </summary>
        [Test]
        public void AProfileSwitchedOnWithKZero_GivesBothSidesNothing_NotTheCallersValues()
        {
            CutWorldProfile profile = ScriptableObject.CreateInstance<CutWorldProfile>();
            try
            {
                Assert.That(profile.SeparationStrength, Is.Null, "switched off by default: the callers' values are used");

                var serialized = new SerializedObject(profile);
                serialized.FindProperty("separationImpulseByMass").boolValue = true;
                serialized.FindProperty("separationImpulsePerKg").floatValue = 0f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(profile.IsUsable(out string reason), Is.True, "k = 0 is a usable value: " + reason);
                SeparationImpulseStrength strength = profile.SeparationStrength;
                Assert.That(strength, Is.Not.Null, "switched on with k = 0 is a strength, not the callers' values");
                Assert.That(strength(5.0), Is.EqualTo(0f), "which gives nothing for any mass");

                using (World w = NewWorld())
                {
                    w.job.HoldEverything = true;
                    w.driver.SeparationStrength = strength;
                    ProvisionalOwnerPair pair = Publish(w, 6f, 1.5f).Pair;
                    Assert.That(
                        (float3)pair.Positive.Body.linearVelocity, Is.EqualTo(float3.zero).Using(Float3Within(1e-6f)),
                        "the positive side is given nothing, not the caller's 6 N·s");
                    Assert.That(
                        (float3)pair.Negative.Body.linearVelocity, Is.EqualTo(float3.zero).Using(Float3Within(1e-6f)),
                        "nor the negative side the caller's 1.5 N·s");
                }

                serialized.FindProperty("separationImpulseByMass").boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(profile.SeparationStrength, Is.Null, "switched off again, the callers' values are used");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// The coexistence profile's k (TL, 2026-10-01: back to 1.0 N·s/kg, by mass): through a profile switched on with
        /// k = 1.0, two free sides of different mass are each given J = 1.0 × their own mass -- read as the impulse each was
        /// given (mass × its first velocity, before any step: no contact or gravity has acted yet) -- and a side the anchors
        /// fix is given nothing and not asked.
        /// </summary>
        [Test]
        public void AProfileByMassWithKOne_GivesEachFreeSideOneTimesItsMass_AndAFixedSideNothing()
        {
            CutWorldProfile profile = ScriptableObject.CreateInstance<CutWorldProfile>();
            try
            {
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("separationImpulseByMass").boolValue = true;
                serialized.FindProperty("separationImpulsePerKg").floatValue = 1.0f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(profile.IsUsable(out string reason), Is.True, reason);
                Assert.That(profile.SeparationImpulseByMass && profile.SeparationImpulsePerKg == 1.0f, Is.True, "read back: by mass, k 1.0");
                SeparationImpulseStrength fromProfile = profile.SeparationStrength;
                var asked = new List<double>();
                SeparationImpulseStrength strength = mass => { asked.Add(mass); return fromProfile(mass); };

                using (World w = NewWorld(convexOffsets: new[] { new double3(0.0, 0.0, 0.0), new double3(1.5, 1.0, 0.0) }))
                {
                    w.job.HoldEverything = true;
                    w.driver.SeparationStrength = strength;
                    ProvisionalOwnerPair pair = Publish(w).Pair;
                    double mp = pair.Positive.Mass, mn = pair.Negative.Mass;
                    Assert.That(mp, Is.GreaterThan(mn * 1.5), "two sides of different mass");
                    Assert.That(asked, Is.EqualTo(new[] { mp, mn }), "asked once for each free side's own mass");
                    double jp = mp * pair.Positive.Body.linearVelocity.magnitude, jn = mn * pair.Negative.Body.linearVelocity.magnitude;
                    TestContext.Out.WriteLine("masses " + mp.ToString("R") + " / " + mn.ToString("R") + " kg; impulses given " + jp.ToString("R") + " / " + jn.ToString("R") + " N·s");
                    Assert.That(jp, Is.EqualTo(1.0 * mp).Within(1e-3 * mp), "J = 1.0 × the positive side's mass");
                    Assert.That(jn, Is.EqualTo(1.0 * mn).Within(1e-3 * mn), "J = 1.0 × the negative side's mass");
                }

                asked.Clear();
                using (World w = NewWorld(anchors: new[] { new float3(0f, 0.5f, 0f) }))
                {
                    w.job.HoldEverything = true;
                    w.driver.SeparationStrength = strength;
                    ProvisionalOwnerPair pair = Publish(w).Pair;
                    Assert.That(pair.Positive.FixedByAnchors, Is.True, "the positive side fixed by its anchor");
                    Assert.That(asked, Is.EqualTo(new[] { pair.Negative.Mass }), "only the free side asked");
                    Assert.That((float3)pair.Positive.Body.linearVelocity, Is.EqualTo(float3.zero).Using(Float3Within(1e-6f)), "the fixed side is given nothing");
                    Assert.That(pair.Negative.Mass * pair.Negative.Body.linearVelocity.magnitude, Is.EqualTo(1.0 * pair.Negative.Mass).Within(1e-3 * pair.Negative.Mass), "the free side J = 1.0 × its mass");
                }
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// A cut kept Pending for its budget is given its strength at the publication that follows, once; the Final
        /// handoff after it adds nothing to either side's motion and asks nothing again.
        /// </summary>
        [Test]
        public void AfterPending_TheStrengthIsAppliedOnceAtThePublication_AndTheHandoffAddsNothing()
        {
            int frame = 40;
            using (World w = NewWorld(frameSource: () => frame))
            {
                w.job.HoldEverything = true;
                var asked = new List<double>();
                w.driver.SeparationStrength = Recording(asked);
                double remaining = 0;
                w.driver.RemainingMainSeconds = () => remaining;

                Assert.That(
                    w.driver.RequestCut(Ask(w), out ProvisionalCutTransaction pending, out LogicalCutAdmission _),
                    Is.EqualTo(ProvisionalCutAcceptance.Pending));
                Assert.That(asked, Is.Empty, "nothing is decided while the cut waits");
                w.driver.Advance(frame);
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Accepted), "still waiting");
                Assert.That(asked, Is.Empty);

                remaining = 1;
                w.driver.Advance(++frame);
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Published), "published at a later opportunity");
                Assert.That(asked.Count, Is.EqualTo(2), "each side's strength asked once, at that publication");

                ProvisionalOwnerPair pair = pending.Pair;
                Rigidbody positiveBody = pair.Positive.Body;
                Rigidbody negativeBody = pair.Negative.Body;
                Vector3 positiveMotion = positiveBody.linearVelocity;
                Vector3 negativeMotion = negativeBody.linearVelocity;
                Assert.That(
                    (float3)positiveMotion, Is.EqualTo(new float3(0f, k_perKg, 0f)).Using(Float3Within(1e-4f)),
                    "the positive side took it once");
                Assert.That(
                    (float3)negativeMotion, Is.EqualTo(new float3(0f, -k_perKg, 0f)).Using(Float3Within(1e-4f)),
                    "and the negative side once");

                // The scene is not stepped here: whatever changes the motion from now on is the handoff.
                RunUntil(w, frame + 1, () => pending.Phase == ProvisionalCutPhase.HandedOff, "the handoff happens");
                Assert.That(asked.Count, Is.EqualTo(2), "the handoff asks for no strength");
                Assert.That(
                    positiveBody.linearVelocity, Is.EqualTo(positiveMotion).Using(Vector3Within(1e-5f)),
                    "and adds nothing to the positive side's motion");
                Assert.That(
                    negativeBody.linearVelocity, Is.EqualTo(negativeMotion).Using(Vector3Within(1e-5f)),
                    "nor to the negative side's");
            }
        }

        /// <summary>
        /// A strength that cannot be used for one side is this publication's physics failure: neither side is
        /// published, the request is ended by the driver (Aborted), and nothing of it is left -- no pair, no record,
        /// no incomplete budget unit, no held input.
        /// </summary>
        [Test]
        public void AStrengthThatCannotBeUsed_EndsTheRequest_WithNothingPublishedOrLeftBehind()
        {
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                int asked = 0;
                w.driver.SeparationStrength = mass => ++asked == 2 ? float.NaN : (float)mass;
                int incompleteBefore = w.ledger.Budget.IncompleteCutOperationCount;

                Assert.That(
                    w.driver.RequestCut(Ask(w), out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission),
                    Is.EqualTo(ProvisionalCutAcceptance.Aborted),
                    "the driver ends the request as a physics failure");
                Assert.That(admission, Is.EqualTo(LogicalCutAdmission.Admitted), "it had been accepted");
                Assert.That(asked, Is.EqualTo(2), "one side's strength was usable, the other's was not");

                Assert.That(transaction.Pair, Is.Null, "no pair was published");
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "and none is in the correspondence");
                Assert.That(w.driver.Transactions, Is.Empty, "the driver keeps no record of it");
                Assert.That(transaction.Candidate, Is.Null, "the candidate went back");
                Assert.That(transaction.HoldsInput, Is.False, "and so did the input");

                Assert.That(w.ledger.TryGetOperation(transaction.Operation, out LogicalCutOperation record), Is.True);
                Assert.That(record.state, Is.EqualTo(LogicalCutOperationState.Aborted), "the operation ended");
                Assert.That(
                    w.ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(incompleteBefore),
                    "and its incomplete budget unit came back");
                Assert.That(
                    w.ledger.TryGetFragmentState(w.source, out LogicalFragmentState sourceState) ? sourceState : default,
                    Is.EqualTo(LogicalFragmentState.Retired),
                    "the source is retired by the ordinary physics-failure rule");
            }
        }
    }
}
