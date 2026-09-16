using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The distribution of an owner's fixed support anchors across a cut plane (DESIGN 7.1), on synthetic points only.
    /// Anchors here belong to an owner and to nothing else: no shape, convex or cell appears in this fixture, and
    /// points outside any plausible body are used on purpose to show that membership is never required. The last few
    /// tests hand the distribution to the existing <see cref="LogicalCutLedger"/> through a harness, to check where a
    /// prepared distribution may and may not be applied — that is a harness connection, not the product's own
    /// publication.
    /// </summary>
    public class FixedSupportAnchorsTests
    {
        // y = 0, positive side is +y.
        private static readonly float4 k_plane = new float4(0f, 1f, 0f, 0f);

        private static List<float3> Points(params float3[] points) => new List<float3>(points);

        private static AnchorDistributionResult DistributeOrFail(
            IReadOnlyList<float3> anchors, float4 plane, float epsilon, List<float3> positive, List<float3> negative)
        {
            Assert.That(
                FixedSupportAnchors.TryDistribute(anchors, plane, epsilon, positive, negative, out AnchorDistributionResult result),
                Is.True,
                "the distribution succeeds");
            Assert.That(result.status, Is.EqualTo(AnchorDistributionStatus.Ok));
            return result;
        }

        [Test]
        public void AnEmptySet_DistributesToNothing_AndBothChildrenAreDynamic()
        {
            var positive = new List<float3>();
            var negative = new List<float3>();

            AnchorDistributionResult result = DistributeOrFail(new List<float3>(), k_plane, 0.01f, positive, negative);

            Assert.That(result.positiveCount, Is.Zero);
            Assert.That(result.negativeCount, Is.Zero);
            Assert.That(positive, Is.Empty, "nothing on the positive side");
            Assert.That(negative, Is.Empty, "nor on the negative one");
            Assert.That(result.IsPositiveFixed, Is.False, "an owner with no anchor is dynamic");
            Assert.That(result.IsNegativeFixed, Is.False);

            // a null set is the same thing: an owner that sets no fixed support
            positive.Clear();
            negative.Clear();
            AnchorDistributionResult ofNull = DistributeOrFail(null, k_plane, 0.01f, positive, negative);
            Assert.That(ofNull.positiveCount, Is.Zero);
            Assert.That(ofNull.negativeCount, Is.Zero);
            Assert.That(FixedSupportAnchors.IsFixed(0), Is.False);
            Assert.That(FixedSupportAnchors.IsFixed(1), Is.True, "one anchor is enough to fix an owner");
        }

        [Test]
        public void EverySideOfThePlane_GoesWhereItsSignSays()
        {
            var anchors = Points(
                new float3(0f, 5f, 0f),
                new float3(3f, 0.5f, -2f),
                new float3(0f, -5f, 0f),
                new float3(-4f, -0.5f, 1f));
            var positive = new List<float3>();
            var negative = new List<float3>();

            AnchorDistributionResult result = DistributeOrFail(anchors, k_plane, 0.1f, positive, negative);

            Assert.That(result.positiveCount, Is.EqualTo(2));
            Assert.That(result.negativeCount, Is.EqualTo(2));
            Assert.That(positive, Is.EquivalentTo(new[] { anchors[0], anchors[1] }), "the two above the plane");
            Assert.That(negative, Is.EquivalentTo(new[] { anchors[2], anchors[3] }), "and the two below it");
            Assert.That(result.IsPositiveFixed && result.IsNegativeFixed, Is.True, "both children are fixed");
        }

        [Test]
        public void AllOnOneSide_LeavesTheOtherChildDynamic()
        {
            var above = Points(new float3(0f, 1f, 0f), new float3(2f, 7f, 3f));
            var positive = new List<float3>();
            var negative = new List<float3>();

            AnchorDistributionResult onlyPositive = DistributeOrFail(above, k_plane, 0.01f, positive, negative);
            Assert.That(onlyPositive.positiveCount, Is.EqualTo(2));
            Assert.That(onlyPositive.negativeCount, Is.Zero);
            Assert.That(onlyPositive.IsPositiveFixed, Is.True, "the positive child is fixed");
            Assert.That(onlyPositive.IsNegativeFixed, Is.False, "and the negative one is dynamic");

            var below = Points(new float3(0f, -1f, 0f), new float3(-2f, -7f, 3f));
            positive.Clear();
            negative.Clear();
            AnchorDistributionResult onlyNegative = DistributeOrFail(below, k_plane, 0.01f, positive, negative);
            Assert.That(onlyNegative.positiveCount, Is.Zero);
            Assert.That(onlyNegative.negativeCount, Is.EqualTo(2));
            Assert.That(onlyNegative.IsPositiveFixed, Is.False);
            Assert.That(onlyNegative.IsNegativeFixed, Is.True);
        }

        /// <summary>
        /// The epsilon band is closed on both ends: exactly ±e goes to both sides, and just outside it goes to one.
        /// Each side receives such a point once, never twice.
        /// </summary>
        [Test]
        public void TheEpsilonBand_IsInclusive_AndGivesEachSideThePointOnce()
        {
            const float e = 0.25f;
            var onPlane = new float3(1f, 0f, 1f);
            var atPlusE = new float3(2f, e, 0f);
            var atMinusE = new float3(3f, -e, 0f);
            var justAbove = new float3(4f, e * 1.5f, 0f);
            var justBelow = new float3(5f, -e * 1.5f, 0f);
            var anchors = Points(onPlane, atPlusE, atMinusE, justAbove, justBelow);
            var positive = new List<float3>();
            var negative = new List<float3>();

            AnchorDistributionResult result = DistributeOrFail(anchors, k_plane, e, positive, negative);

            Assert.That(result.positiveCount, Is.EqualTo(4), "three in the band plus the one above it");
            Assert.That(result.negativeCount, Is.EqualTo(4), "three in the band plus the one below it");
            foreach (float3 inBand in new[] { onPlane, atPlusE, atMinusE })
            {
                Assert.That(CountOf(positive, inBand), Is.EqualTo(1), inBand + " is on the positive side exactly once");
                Assert.That(CountOf(negative, inBand), Is.EqualTo(1), inBand + " is on the negative side exactly once");
            }

            Assert.That(CountOf(positive, justAbove), Is.EqualTo(1), "and the one above the band is only positive");
            Assert.That(CountOf(negative, justAbove), Is.Zero);
            Assert.That(CountOf(negative, justBelow), Is.EqualTo(1), "the one below it only negative");
            Assert.That(CountOf(positive, justBelow), Is.Zero);

            // a zero epsilon keeps the band, now only exactly on the plane
            positive.Clear();
            negative.Clear();
            AnchorDistributionResult exact = DistributeOrFail(Points(onPlane, atPlusE, atMinusE), k_plane, 0f, positive, negative);
            Assert.That(exact.positiveCount, Is.EqualTo(2), "the on-plane point and the one above");
            Assert.That(exact.negativeCount, Is.EqualTo(2), "the on-plane point and the one below");
            Assert.That(CountOf(positive, onPlane), Is.EqualTo(1));
            Assert.That(CountOf(negative, onPlane), Is.EqualTo(1));
        }

        /// <summary>
        /// Two anchors that happen to hold the same position are two anchors, and both are distributed. That is not the
        /// duplication the contract forbids — what is forbidden is one anchor reaching one side twice.
        /// </summary>
        [Test]
        public void TwoAnchorsAtTheSamePosition_AreBothDistributed()
        {
            var shared = new float3(0f, 2f, 0f);
            var positive = new List<float3>();
            var negative = new List<float3>();

            AnchorDistributionResult result = DistributeOrFail(Points(shared, shared), k_plane, 0.01f, positive, negative);

            Assert.That(result.positiveCount, Is.EqualTo(2), "two anchors, two entries");
            Assert.That(CountOf(positive, shared), Is.EqualTo(2));
            Assert.That(result.negativeCount, Is.Zero);
        }

        /// <summary>
        /// Anchors need no shape to belong to. These points are far outside anything the owner could plausibly occupy,
        /// and they are distributed by their side alone — no containment, proximity or projection is consulted.
        /// </summary>
        [Test]
        public void PointsFarOutsideAnyShape_AreDistributedByPositionAlone()
        {
            var farAbove = new float3(1000f, 900f, -1000f);
            var farBelow = new float3(-1000f, -900f, 1000f);
            var positive = new List<float3>();
            var negative = new List<float3>();

            AnchorDistributionResult result = DistributeOrFail(Points(farAbove, farBelow), k_plane, 0.01f, positive, negative);

            Assert.That(positive, Is.EqualTo(new List<float3> { farAbove }), "the distant point above is simply positive");
            Assert.That(negative, Is.EqualTo(new List<float3> { farBelow }), "and the distant one below is negative");
            Assert.That(result.IsPositiveFixed && result.IsNegativeFixed, Is.True, "each child is fixed by a point it does not contain");
        }

        /// <summary>
        /// A re-cut reads the child's current set and nothing else. The sibling is untouched, and a point that went to
        /// the sibling at the first cut cannot come back through the second.
        /// </summary>
        [Test]
        public void ARecut_UsesOnlyTheTargetsCurrentSet_AndRevivesNothing()
        {
            var high = new float3(0f, 4f, 0f);
            var low = new float3(0f, -4f, 0f);
            var source = Points(high, low);

            var upper = new List<float3>();
            var lower = new List<float3>();
            DistributeOrFail(source, k_plane, 0.01f, upper, lower);
            Assert.That(upper, Is.EqualTo(new List<float3> { high }));
            Assert.That(lower, Is.EqualTo(new List<float3> { low }));

            // cut the upper child again, by y = 2
            var secondPlane = new float4(0f, 1f, 0f, -2f);
            var upperTop = new List<float3>();
            var upperBottom = new List<float3>();
            AnchorDistributionResult recut = DistributeOrFail(upper, secondPlane, 0.01f, upperTop, upperBottom);

            Assert.That(recut.positiveCount, Is.EqualTo(1), "the child's own point is above the second plane");
            Assert.That(upperTop, Is.EqualTo(new List<float3> { high }));
            Assert.That(upperBottom, Is.Empty, "and nothing else appeared");
            Assert.That(recut.IsNegativeFixed, Is.False, "so that grandchild is dynamic");

            Assert.That(upper, Is.EqualTo(new List<float3> { high }), "the target's own set was not modified by the re-cut");
            Assert.That(lower, Is.EqualTo(new List<float3> { low }), "the sibling is unchanged");
            Assert.That(source, Is.EqualTo(Points(high, low)), "and the ancestor's set is unchanged and not consulted");
            Assert.That(upperTop.Contains(low) || upperBottom.Contains(low), Is.False, "the sibling's point never returns");
        }

        /// <summary>
        /// The frame is the caller's and the same one for the points and the plane. Taking a world configuration into a
        /// non-identity frame with the existing conversion gives the same distribution, side for side.
        /// </summary>
        [Test]
        public void ANonIdentityFrame_DistributesTheSameWay_WhenPointsAndPlaneShareIt()
        {
            Matrix4x4 localToWorld = Matrix4x4.TRS(
                new Vector3(12f, -3.5f, 7.25f),
                Quaternion.Euler(24f, -63f, 41f),
                Vector3.one);
            Matrix4x4 worldToLocal = localToWorld.inverse;

            var worldAnchors = Points(
                new float3(0f, 3f, 0f),
                new float3(1f, 0f, -1f),
                new float3(-2f, -3f, 4f));
            const float e = 0.05f;

            var worldPositive = new List<float3>();
            var worldNegative = new List<float3>();
            AnchorDistributionResult inWorld = DistributeOrFail(worldAnchors, k_plane, e, worldPositive, worldNegative);

            // the same plane and the same points, both taken into the frame
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(k_plane, localToWorld, out float4 localPlane), Is.True, "the plane converts");
            var localAnchors = new List<float3>();
            foreach (float3 world in worldAnchors)
            {
                Vector3 local = worldToLocal.MultiplyPoint3x4(new Vector3(world.x, world.y, world.z));
                localAnchors.Add(new float3(local.x, local.y, local.z));
            }

            var localPositive = new List<float3>();
            var localNegative = new List<float3>();
            AnchorDistributionResult inFrame = DistributeOrFail(localAnchors, localPlane, e, localPositive, localNegative);

            Assert.That(inFrame.positiveCount, Is.EqualTo(inWorld.positiveCount), "the same number go positive");
            Assert.That(inFrame.negativeCount, Is.EqualTo(inWorld.negativeCount), "and the same number negative");
            Assert.That(inFrame.IsPositiveFixed, Is.EqualTo(inWorld.IsPositiveFixed), "with the same fixity");
            Assert.That(inFrame.IsNegativeFixed, Is.EqualTo(inWorld.IsNegativeFixed));

            // and it is the same anchors, each one on the side it was on in world
            for (int i = 0; i < worldAnchors.Count; i++)
            {
                bool wasPositive = CountOf(worldPositive, worldAnchors[i]) > 0;
                bool isPositive = CountOf(localPositive, localAnchors[i]) > 0;
                Assert.That(isPositive, Is.EqualTo(wasPositive), "anchor " + i + " keeps its side");
            }
        }

        [Test]
        public void InvalidInput_IsRefused_WithBothOutputsUntouched()
        {
            var anchors = Points(new float3(0f, 1f, 0f), new float3(0f, -1f, 0f));
            var positive = Points(new float3(9f, 9f, 9f));
            var negative = Points(new float3(-9f, -9f, -9f));
            var positiveAtStart = new List<float3>(positive);
            var negativeAtStart = new List<float3>(negative);
            var anchorsAtStart = new List<float3>(anchors);

            void Refused(IReadOnlyList<float3> input, float4 plane, float epsilon, AnchorDistributionStatus expected)
            {
                Assert.That(
                    FixedSupportAnchors.TryDistribute(input, plane, epsilon, positive, negative, out AnchorDistributionResult result),
                    Is.False,
                    expected + " is refused");
                Assert.That(result.status, Is.EqualTo(expected));
                Assert.That(result.positiveCount, Is.Zero, "a refusal reports nothing distributed");
                Assert.That(result.negativeCount, Is.Zero);
                Assert.That(result.IsPositiveFixed || result.IsNegativeFixed, Is.False, "and derives no fixity");
                Assert.That(positive, Is.EqualTo(positiveAtStart), "the positive output is untouched");
                Assert.That(negative, Is.EqualTo(negativeAtStart), "and so is the negative one");
                Assert.That(anchors, Is.EqualTo(anchorsAtStart), "the source set is never modified");
            }

            Refused(anchors, k_plane, -0.001f, AnchorDistributionStatus.InvalidEpsilon);
            Refused(anchors, k_plane, float.NaN, AnchorDistributionStatus.InvalidEpsilon);
            Refused(anchors, k_plane, float.PositiveInfinity, AnchorDistributionStatus.InvalidEpsilon);
            Refused(anchors, new float4(0f, float.NaN, 0f, 0f), 0.01f, AnchorDistributionStatus.NonFinitePlane);
            Refused(anchors, new float4(0f, 1f, 0f, float.PositiveInfinity), 0.01f, AnchorDistributionStatus.NonFinitePlane);
            Refused(Points(new float3(0f, 1f, 0f), new float3(float.NaN, 0f, 0f)), k_plane, 0.01f, AnchorDistributionStatus.NonFiniteAnchor);
            Refused(Points(new float3(0f, float.NegativeInfinity, 0f)), k_plane, 0.01f, AnchorDistributionStatus.NonFiniteAnchor);

            // finite inputs whose signed distance overflows are refused too, not rounded onto the plane
            Refused(
                Points(new float3(float.MaxValue, float.MaxValue, float.MaxValue)),
                new float4(float.MaxValue, float.MaxValue, float.MaxValue, 0f),
                0.01f,
                AnchorDistributionStatus.NonFiniteClassification);

            // a plane whose normal is all zeros names no plane: neither an all-zero float4 nor (0,0,0,1) may pass as
            // one, and neither puts every anchor on the plane or on one side
            Refused(anchors, default, 0.01f, AnchorDistributionStatus.DegeneratePlane);
            Refused(anchors, new float4(0f, 0f, 0f, 1f), 0.01f, AnchorDistributionStatus.DegeneratePlane);
            Refused(anchors, new float4(0f, 0f, 0f, -3.5f), 0.01f, AnchorDistributionStatus.DegeneratePlane);

            Assert.That(
                FixedSupportAnchors.TryDistribute(anchors, k_plane, 0.01f, null, negative, out AnchorDistributionResult noOutput),
                Is.False,
                "a missing output set is refused");
            Assert.That(noOutput.status, Is.EqualTo(AnchorDistributionStatus.NoOutput));
            Assert.That(negative, Is.EqualTo(negativeAtStart));
        }

        /// <summary>
        /// The three sets must be three different lists. Sharing the input with an output would grow the set being
        /// read, and sharing the two outputs would put an on-plane anchor into the same set twice — so both are refused
        /// at the entrance, before anything is written.
        /// </summary>
        [Test]
        public void SharingAListBetweenTheSets_IsRefusedBeforeAnythingIsWritten()
        {
            var shared = Points(new float3(0f, 2f, 0f), new float3(0f, 0f, 0f), new float3(0f, -2f, 0f));
            var sharedAtStart = new List<float3>(shared);
            var other = new List<float3>();

            // the input used as the positive output, then as the negative one
            Assert.That(
                FixedSupportAnchors.TryDistribute(shared, k_plane, 0.01f, shared, other, out AnchorDistributionResult asPositive),
                Is.False,
                "the input set may not also be an output");
            Assert.That(asPositive.status, Is.EqualTo(AnchorDistributionStatus.AliasedSets));
            Assert.That(shared, Is.EqualTo(sharedAtStart), "the input set was not appended to");
            Assert.That(other, Is.Empty, "and the other output is untouched");

            Assert.That(
                FixedSupportAnchors.TryDistribute(shared, k_plane, 0.01f, other, shared, out AnchorDistributionResult asNegative),
                Is.False);
            Assert.That(asNegative.status, Is.EqualTo(AnchorDistributionStatus.AliasedSets));
            Assert.That(shared, Is.EqualTo(sharedAtStart));
            Assert.That(other, Is.Empty);

            // one list given as both outputs: the on-plane anchor would otherwise land in it twice
            var bothSides = new List<float3>();
            var anchors = Points(new float3(0f, 2f, 0f), new float3(1f, 0f, 1f));
            Assert.That(
                FixedSupportAnchors.TryDistribute(anchors, k_plane, 0.01f, bothSides, bothSides, out AnchorDistributionResult sameOutput),
                Is.False,
                "the two outputs may not be the same list");
            Assert.That(sameOutput.status, Is.EqualTo(AnchorDistributionStatus.AliasedSets));
            Assert.That(sameOutput.positiveCount, Is.Zero);
            Assert.That(sameOutput.negativeCount, Is.Zero);
            Assert.That(bothSides, Is.Empty, "nothing was written");
            Assert.That(anchors, Is.EqualTo(Points(new float3(0f, 2f, 0f), new float3(1f, 0f, 1f))), "and the input is unchanged");
        }

        /// <summary>
        /// The result's counts are what this call appended, not what the lists hold. A caller accumulating into lists
        /// that already carry something reads the total from the lists themselves.
        /// </summary>
        [Test]
        public void TheCounts_AreWhatThisCallAppended_NotTheListTotals()
        {
            var existingPositive = new float3(100f, 100f, 100f);
            var existingNegative = new float3(-100f, -100f, -100f);
            var positive = Points(existingPositive);
            var negative = Points(existingNegative);

            var high = new float3(0f, 3f, 0f);
            var onPlane = new float3(2f, 0f, 2f);
            AnchorDistributionResult result = DistributeOrFail(Points(high, onPlane), k_plane, 0.01f, positive, negative);

            Assert.That(result.positiveCount, Is.EqualTo(2), "this call put two on the positive side");
            Assert.That(result.negativeCount, Is.EqualTo(1), "and one on the negative side");
            Assert.That(positive.Count, Is.EqualTo(3), "while the list also still holds what it came with");
            Assert.That(negative.Count, Is.EqualTo(2));
            Assert.That(positive[0], Is.EqualTo(existingPositive), "the caller's own contents are kept, and first");
            Assert.That(negative[0], Is.EqualTo(existingNegative));
            Assert.That(positive.GetRange(1, 2), Is.EquivalentTo(new[] { high, onPlane }), "with this call's anchors appended");
            Assert.That(negative.GetRange(1, 1), Is.EqualTo(new List<float3> { onPlane }));

            // fixity from the result is about this distribution; fixity of the owner is about the list
            Assert.That(result.IsPositiveFixed, Is.True);
            Assert.That(FixedSupportAnchors.IsFixed(positive.Count), Is.True, "the owner is fixed by everything its set holds");
        }

        // ----- harness connection to the existing logical core -------------------------------------------------
        //
        // A tiny stand-in for what will later hold an owner's anchors. It is a test fixture, not a product boundary:
        // the ledger knows nothing about it, and nothing here is added to LogicalCutLedger.

        private sealed class AnchorHarness
        {
            private readonly Dictionary<int, List<float3>> _byOwner = new Dictionary<int, List<float3>>();

            public void Set(LogicalFragmentId owner, params float3[] anchors)
            {
                _byOwner[owner.value] = new List<float3>(anchors);
            }

            public List<float3> Of(LogicalFragmentId owner)
            {
                return _byOwner.TryGetValue(owner.value, out List<float3> anchors) ? anchors : new List<float3>();
            }

            public bool Has(LogicalFragmentId owner) => _byOwner.ContainsKey(owner.value);

            /// <summary>Prepares a distribution, unpublished: nobody is given these sets until a cut is published.</summary>
            public bool Prepare(LogicalFragmentId source, float4 plane, float epsilon, out List<float3> positive, out List<float3> negative)
            {
                positive = new List<float3>();
                negative = new List<float3>();
                return FixedSupportAnchors.TryDistribute(Of(source), plane, epsilon, positive, negative, out _);
            }

            /// <summary>Only a published cut's children take the prepared sets.</summary>
            public void Publish(LogicalFragmentId positiveChild, List<float3> positive, LogicalFragmentId negativeChild, List<float3> negative)
            {
                _byOwner[positiveChild.value] = positive;
                _byOwner[negativeChild.value] = negative;
            }
        }

        [Test]
        public void AHarness_AppliesADistributionOnlyWhenTheCutIsPublished()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
            var harness = new AnchorHarness();
            LogicalFragmentId source = ledger.AddFragment();
            var high = new float3(0f, 4f, 0f);
            var onPlane = new float3(1f, 0f, 1f);
            harness.Set(source, high, onPlane);

            // admission first; only then is a distribution prepared
            Assert.That(ledger.Admit(source, k_plane, true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(harness.Prepare(source, k_plane, 0.01f, out List<float3> positive, out List<float3> negative), Is.True);
            Assert.That(positive, Is.EquivalentTo(new[] { high, onPlane }), "the on-plane anchor goes to both sides");
            Assert.That(negative, Is.EquivalentTo(new[] { onPlane }));

            Assert.That(ledger.Publish(cut, out LogicalFragmentId positiveChild, out LogicalFragmentId negativeChild), Is.EqualTo(LogicalCutResultOutcome.Applied));
            harness.Publish(positiveChild, positive, negativeChild, negative);

            Assert.That(harness.Of(positiveChild), Is.EquivalentTo(new[] { high, onPlane }), "the positive child is fixed");
            Assert.That(harness.Of(negativeChild), Is.EquivalentTo(new[] { onPlane }), "and so is the negative one");
            Assert.That(FixedSupportAnchors.IsFixed(harness.Of(positiveChild).Count), Is.True);
            Assert.That(FixedSupportAnchors.IsFixed(harness.Of(negativeChild).Count), Is.True);
            Assert.That(harness.Of(source), Is.EquivalentTo(new[] { high, onPlane }), "the source's own set was never modified");
        }

        [Test]
        public void ANoOpOrASkippedRequest_LeavesTheAnchorsAsTheyWere()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(1));
            var harness = new AnchorHarness();
            LogicalFragmentId source = ledger.AddFragment();
            LogicalFragmentId other = ledger.AddFragment();
            var anchor = new float3(0f, 4f, 0f);
            harness.Set(source, anchor);
            harness.Set(other, new float3(0f, -4f, 0f));

            // a one-sided no-op never reaches the distribution
            Assert.That(ledger.Admit(source, k_plane, false, out _), Is.EqualTo(LogicalCutAdmission.NoOp));
            Assert.That(harness.Of(source), Is.EqualTo(new List<float3> { anchor }), "a no-op changes no anchor set");

            // nor does a request skipped because the source is already active, or because the budget is full
            Assert.That(ledger.Admit(source, k_plane, true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.Admit(source, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.SourceActive));
            Assert.That(ledger.Admit(other, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.Full));
            Assert.That(harness.Of(source), Is.EqualTo(new List<float3> { anchor }), "a skipped request changes nothing either");
            Assert.That(harness.Of(other), Is.EqualTo(new List<float3> { new float3(0f, -4f, 0f) }));

            // and the admitted one, once published, does change the children only
            harness.Prepare(source, k_plane, 0.01f, out List<float3> positive, out List<float3> negative);
            ledger.Publish(cut, out LogicalFragmentId positiveChild, out LogicalFragmentId negativeChild);
            harness.Publish(positiveChild, positive, negativeChild, negative);
            Assert.That(harness.Of(positiveChild), Is.EqualTo(new List<float3> { anchor }));
            Assert.That(harness.Of(negativeChild), Is.Empty, "the negative child inherited nothing and is dynamic");
            Assert.That(FixedSupportAnchors.IsFixed(harness.Of(negativeChild).Count), Is.False);
            Assert.That(harness.Of(other), Is.EqualTo(new List<float3> { new float3(0f, -4f, 0f) }), "the other owner is untouched throughout");
        }

        [Test]
        public void AnAbortOrAStaleResult_PublishesNoPreparedDistribution()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
            var harness = new AnchorHarness();
            LogicalFragmentId aborted = ledger.AddFragment();
            LogicalFragmentId stale = ledger.AddFragment();
            var anchor = new float3(0f, 4f, 0f);
            harness.Set(aborted, anchor);
            harness.Set(stale, anchor);

            // prepared, then the final fails: nothing is handed to any child
            Assert.That(ledger.Admit(aborted, k_plane, true, out CutOperationId abortedCut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(harness.Prepare(aborted, k_plane, 0.01f, out List<float3> abortedPositive, out _), Is.True);
            Assert.That(abortedPositive, Is.EqualTo(new List<float3> { anchor }), "a distribution was prepared");
            Assert.That(ledger.Abort(abortedCut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.TryGetOperation(abortedCut, out LogicalCutOperation abortedOperation), Is.True);
            Assert.That(abortedOperation.positive.IsSet || abortedOperation.negative.IsSet, Is.False, "there is no child to hand it to");
            Assert.That(harness.Of(aborted), Is.EqualTo(new List<float3> { anchor }), "and the retired source keeps its own set unchanged");

            // prepared, then the result turns out stale: likewise nothing is published
            Assert.That(ledger.Admit(stale, k_plane, true, out CutOperationId staleCut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(harness.Prepare(stale, k_plane, 0.01f, out List<float3> stalePositive, out _), Is.True);
            Assert.That(stalePositive, Is.EqualTo(new List<float3> { anchor }));
            ledger.NoteOwnershipChanged(stale);
            Assert.That(ledger.Publish(staleCut, out LogicalFragmentId positiveChild, out LogicalFragmentId negativeChild), Is.EqualTo(LogicalCutResultOutcome.Stale));
            Assert.That(positiveChild.IsSet || negativeChild.IsSet, Is.False, "no children were published");
            Assert.That(harness.Has(new LogicalFragmentId(ledger.FragmentCount + 1)), Is.False, "and no set was written for an unpublished id");
            Assert.That(harness.Of(stale), Is.EqualTo(new List<float3> { anchor }), "the source is still live with its set intact");
            Assert.That(ledger.IsCurrentTarget(stale), Is.True);
        }

        private static int CountOf(List<float3> points, float3 wanted)
        {
            int count = 0;
            foreach (float3 point in points)
            {
                if (point.Equals(wanted))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
