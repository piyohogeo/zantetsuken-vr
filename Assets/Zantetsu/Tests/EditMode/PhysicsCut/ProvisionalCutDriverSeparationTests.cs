using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The sibling D6 of DESIGN 7.1.1 **in a stepped scene** (T-091): a published pair whose final cut is held back, so
    /// that it stays Provisional while the simulation is stepped by script. Along the plane the two slide and about the
    /// normal they turn; along the normal they stay between the inward boundary and the outward one; about the tangents
    /// they turn together. A pair turned about the normal is then handed off as it stands.
    /// <para>
    /// **What is watched is the movement, against the movement the same push would make without the constraint**: each
    /// push is chosen so that a locked motion would leave the relation near where it started and a free one would carry
    /// it far past the bound the assertion uses. The bounds are loose on purpose. DESIGN 7.1.1 asks for no guarantee
    /// about any step, and none of these numbers is one.
    /// </para>
    /// <para>
    /// The relation is read from the two actors: the positive side's origin against the negative side's, along the
    /// adopted normal (the source's up, carried by the negative side) and across it; and the positive side's rotation
    /// relative to the negative side's, split into the turn about the normal and the tilt away from it. Both sides are
    /// free (no anchor), gravity is off and nothing else pushes them: the siblings do not collide with each other.
    /// </para>
    /// </summary>
    public unsafe partial class ProvisionalCutDriverTests
    {
        private const float k_step = 0.02f;

        /// <summary>Along the plane the two slide apart, and about the normal one turns against the other.</summary>
        [Test]
        public void TheSiblingsSlideAlongThePlaneAndTurnAboutItsNormal()
        {
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                ProvisionalCutTransaction transaction = Publish(w);
                ProvisionalOwnerPair pair = transaction.Pair;
                Still(pair);

                // Unconstrained, 0.5 s of this carries the positive side 0.56 m along the plane and turns it 1 rad.
                pair.Positive.Body.linearVelocity = new Vector3(1f, 0f, 0.5f);
                pair.Positive.Body.angularVelocity = new Vector3(0f, 2f, 0f);
                StepScene(25);

                Relation r = Relate(pair);
                Assert.That(r.across, Is.GreaterThan(0.25f), "the two slid apart along the plane: " + r);
                Assert.That(r.twist, Is.GreaterThan(0.3f), "and one turned against the other about the normal: " + r);
                Assert.That(r.along, Is.InRange(-0.1f, 2.1f), "while along the normal they stayed in the interval: " + r);
                Assert.That(r.tilt, Is.LessThan(0.1f), "and neither tilted against the other: " + r);
                Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Published), "all of it while Provisional");
                Assert.That(pair.Separation != null, Is.True, "with the constraint in place");
            }
        }

        /// <summary>
        /// Along the normal: a push inward is stopped at the relation the pair was built with, and a push outward runs
        /// up to two metres apart and no further. About the tangents: a turn of one side takes the other with it.
        /// </summary>
        [Test]
        public void AlongTheNormalThePairStaysBetweenItsBoundaries_AndAboutTheTangentsItTurnsAsOne()
        {
            // Inward: unconstrained, the positive side would be 1 m into the negative one.
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                ProvisionalOwnerPair pair = Publish(w).Pair;
                Still(pair);
                pair.Positive.Body.linearVelocity = new Vector3(0f, -2f, 0f);
                StepScene(25);
                Relation r = Relate(pair);
                Assert.That(r.along, Is.GreaterThan(-0.1f), "a push inward stopped at the inward boundary: " + r);
            }

            // Outward: unconstrained, the positive side would be 5 m away.
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                ProvisionalOwnerPair pair = Publish(w).Pair;
                Still(pair);
                pair.Positive.Body.linearVelocity = new Vector3(0f, 10f, 0f);
                StepScene(25);
                Relation r = Relate(pair);
                Assert.That(r.along, Is.LessThan(2.1f), "a push outward stopped at two metres apart: " + r);
                Assert.That(r.along, Is.GreaterThan(1.5f), "after running out to it: " + r);
            }

            // About the tangents: unconstrained, the positive side would tilt 2.1 rad against the other.
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                ProvisionalOwnerPair pair = Publish(w).Pair;
                Still(pair);
                pair.Positive.Body.angularVelocity = new Vector3(3f, 0f, 3f);
                StepScene(25);
                Relation r = Relate(pair);
                Assert.That(r.tilt, Is.LessThan(0.1f), "a turn about the tangents took the other side with it: " + r);
            }
        }

        /// <summary>
        /// A lopsided piece turned about the normal is handed off as it stands: the handoff keeps its actor where it is
        /// and turned as it is, and takes the sibling constraint away.
        /// </summary>
        [Test]
        public void ALopsidedPieceTurnedAboutTheNormal_IsHandedOffAsItStands_AndTheConstraintGoes()
        {
            // The second box lies wholly above the plane, off to one side: the positive piece is lopsided about the normal.
            using (World w = NewWorld(convexOffsets: new[] { new double3(0.0, 0.0, 0.0), new double3(1.5, 1.0, 0.0) }))
            {
                w.job.HoldEverything = true;
                ProvisionalCutTransaction transaction = Publish(w);
                ProvisionalOwnerPair pair = transaction.Pair;
                ConfigurableJoint joint = pair.Separation;
                GameObject positiveRoot = pair.Positive.Root;
                GameObject negativeRoot = pair.Negative.Root;
                Still(pair);

                pair.Positive.Body.angularVelocity = new Vector3(0f, 3f, 0f);
                StepScene(20);
                Relation turned = Relate(pair);
                Assert.That(turned.twist, Is.GreaterThan(0.3f), "the piece turned about the normal against its sibling: " + turned);
                Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Published), "while Provisional");

                Vector3 positiveAt = positiveRoot.transform.position;
                Quaternion positiveTurn = positiveRoot.transform.rotation;
                Vector3 negativeAt = negativeRoot.transform.position;
                Quaternion negativeTurn = negativeRoot.transform.rotation;
                Assert.That(Quaternion.Angle(Quaternion.identity, positiveTurn), Is.GreaterThan(10f), "away from the pose it was built in");

                // The scene is not stepped from here: whatever moves the actors now is the handoff.
                RunUntil(w, 400, () => transaction.Phase == ProvisionalCutPhase.HandedOff, "the handoff happens");

                Assert.That(joint == null, Is.True, "the sibling constraint went with the handoff");
                Assert.That(
                    positiveRoot.GetComponents<Joint>().Length + negativeRoot.GetComponents<Joint>().Length, Is.Zero,
                    "and no joint is left on either child");

                Assert.That(w.ledger.TryGetOperation(transaction.Operation, out LogicalCutOperation published), Is.True);
                Assert.That(w.registry.TryGet(published.positive, out PhysicsFragmentOwner positiveChild), Is.True);
                Assert.That(w.registry.TryGet(published.negative, out PhysicsFragmentOwner negativeChild), Is.True);
                Assert.That(positiveChild.Root, Is.SameAs(positiveRoot), "the positive child is the actor that turned");
                Assert.That(negativeChild.Root, Is.SameAs(negativeRoot));

                Assert.That(
                    Vector3.Distance(positiveRoot.transform.position, positiveAt), Is.LessThan(1e-4f),
                    "it stands where it stood");
                Assert.That(
                    Quaternion.Angle(positiveRoot.transform.rotation, positiveTurn), Is.LessThan(0.01f),
                    "turned as it was, not back to the pose it was built in");
                Assert.That(Vector3.Distance(negativeRoot.transform.position, negativeAt), Is.LessThan(1e-4f));
                Assert.That(Quaternion.Angle(negativeRoot.transform.rotation, negativeTurn), Is.LessThan(0.01f));
            }
        }

        private struct Relation
        {
            internal float along;
            internal float across;
            internal float twist;
            internal float tilt;

            public override string ToString()
            {
                return "along " + along.ToString("F3") + " m, across " + across.ToString("F3") + " m, twist "
                    + twist.ToString("F3") + " rad, tilt " + tilt.ToString("F3") + " rad";
            }
        }

        private static Relation Relate(ProvisionalOwnerPair pair)
        {
            Transform positive = pair.Positive.Root.transform;
            Transform negative = pair.Negative.Root.transform;
            Vector3 normal = negative.rotation * Vector3.up;
            Vector3 offset = positive.position - negative.position;
            float along = Vector3.Dot(offset, normal);

            // The positive side's rotation in the negative side's frame, split into a turn about the normal and a tilt.
            Quaternion relative = Quaternion.Inverse(negative.rotation) * positive.rotation;
            var twist = new Quaternion(0f, relative.y, 0f, relative.w);
            float length = Mathf.Sqrt((twist.y * twist.y) + (twist.w * twist.w));
            float twistAngle = length > 0f ? 2f * Mathf.Acos(Mathf.Clamp01(Mathf.Abs(twist.w) / length)) : 0f;
            var relation = new Relation
            {
                along = along,
                across = (offset - (along * normal)).magnitude,
                twist = twistAngle,
                tilt = Vector3.Angle(Vector3.up, relative * Vector3.up) * Mathf.Deg2Rad,
            };
            TestContext.Out.WriteLine("relation: " + relation);
            return relation;
        }

        /// <summary>No gravity and no motion: what moves the pair from here is the push the test gives it.</summary>
        private static void Still(ProvisionalOwnerPair pair)
        {
            foreach (PhysicsOwnerSide side in new[] { pair.Positive, pair.Negative })
            {
                side.Body.useGravity = false;
                side.Body.linearVelocity = Vector3.zero;
                side.Body.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>The scene stepped by script, the mode put back afterwards.</summary>
        private static void StepScene(int steps)
        {
            SimulationMode mode = UnityEngine.Physics.simulationMode;
            try
            {
                UnityEngine.Physics.simulationMode = SimulationMode.Script;
                UnityEngine.Physics.SyncTransforms();
                for (int i = 0; i < steps; i++)
                {
                    UnityEngine.Physics.Simulate(k_step);
                }
            }
            finally
            {
                UnityEngine.Physics.simulationMode = mode;
            }
        }
    }
}
