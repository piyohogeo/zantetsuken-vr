using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The ledger's replacement of one live fragment by two without a cut (DESIGN 7.9.2, 7.9.4:
    /// <see cref="LogicalCutLedger.ReplaceBySplit"/>), as pure data: what the two fragments are in the lineage, what
    /// is not made for them, what a reader of the replaced fragment finds, and what is refused.
    /// </summary>
    public class LogicalCutLedgerSplitTests
    {
        private static readonly float4 k_plane = new float4(0f, 1f, 0f, -0.25f);
        private static readonly float4 k_otherPlane = new float4(1f, 0f, 0f, 0.5f);
        private const float k_epsilon = 0.01f;

        private static readonly float3 k_high = new float3(0f, 4f, 0f);
        private static readonly float3 k_low = new float3(0f, -4f, 0f);
        private static readonly float3 k_lowFar = new float3(9f, -4f, 0f);

        private static LogicalCutLedger NewLedger()
        {
            return new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
        }

        private static CutOperationId Cut(
            LogicalCutLedger ledger, LogicalFragmentId source, float4 plane, out LogicalFragmentId positive, out LogicalFragmentId negative)
        {
            Assert.That(ledger.Admit(source, plane, true, out CutOperationId operation), Is.EqualTo(LogicalCutAdmission.Admitted));
            ledger.PrepareAnchorDistribution(operation, k_epsilon, out _);
            Assert.That(ledger.Publish(operation, out positive, out negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            return operation;
        }

        private static LogicalFragmentState StateOf(LogicalCutLedger ledger, LogicalFragmentId id)
        {
            Assert.That(ledger.TryGetFragmentState(id, out LogicalFragmentState state), Is.True, "fragment " + id + " exists");
            return state;
        }

        private static List<float3> AnchorsOf(LogicalCutLedger ledger, LogicalFragmentId id)
        {
            var into = new List<float3>();
            Assert.That(ledger.TryGetAnchors(id, into), Is.True);
            return into;
        }

        /// <summary>
        /// **Two ordinary fragments in the source's own place.** The lower child of a cut is replaced by two: both
        /// live, both cut fragments of the same family, both with the origin the source had; the source is replaced
        /// with no operation; nothing is admitted, nothing is charged to the budget, and the cut that made the
        /// source still names the source.
        /// </summary>
        [Test]
        public void ReplaceBySplit_PutsTwoLiveFragmentsInTheSourcesPlace_WithoutAnOperation()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new[] { k_high, k_low, k_lowFar });
            CutOperationId cut = Cut(ledger, root, k_plane, out LogicalFragmentId upper, out LogicalFragmentId lower);
            Assert.That(AnchorsOf(ledger, lower), Is.EquivalentTo(new[] { k_low, k_lowFar }), "the layout: the lower child holds the two low anchors");
            Assert.That(ledger.MayReplaceBySplit(lower), Is.True);

            int operations = ledger.OperationCount;
            int incomplete = ledger.Budget.IncompleteCutOperationCount;
            int fragments = ledger.FragmentCount;
            int liveCut = ledger.LiveCutFragmentCount;
            long revision = ledger.Revision;
            long familyRevision = ledger.FamilyRevisionOf(root);
            Assert.That(ledger.TryGetOrigin(lower, out CutOperationId origin, out float originSide), Is.True);

            Assert.That(
                ledger.ReplaceBySplit(lower, new[] { k_low }, new[] { k_lowFar }, out LogicalFragmentId near, out LogicalFragmentId far),
                Is.True);

            // The two: new ids, live, targets, with the anchors handed in.
            Assert.That(near.IsSet && far.IsSet && near != far && near != lower && far != lower, Is.True);
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragments + 2), "two ids were issued");
            Assert.That(StateOf(ledger, near), Is.EqualTo(LogicalFragmentState.Live));
            Assert.That(StateOf(ledger, far), Is.EqualTo(LogicalFragmentState.Live));
            Assert.That(ledger.IsCurrentTarget(near) && ledger.IsCurrentTarget(far), Is.True);
            Assert.That(AnchorsOf(ledger, near), Is.EquivalentTo(new[] { k_low }));
            Assert.That(AnchorsOf(ledger, far), Is.EquivalentTo(new[] { k_lowFar }));

            // In the source's own place in the lineage: the same origin, the same family, counted as cut fragments.
            foreach (LogicalFragmentId made in new[] { near, far })
            {
                Assert.That(ledger.TryGetOrigin(made, out CutOperationId madeOrigin, out float madeSide), Is.True);
                Assert.That(madeOrigin, Is.EqualTo(origin), "the origin of the source");
                Assert.That(madeSide, Is.EqualTo(originSide), "and its side");
                Assert.That(ledger.TryGetRoot(made, out LogicalFragmentId madeRoot) && madeRoot == root, Is.True, "the source's family");
                Assert.That(ledger.TryGetSplitSource(made, out LogicalFragmentId source) && source == lower, Is.True, "it names what it replaced");
                Assert.That(ledger.TryGetParent(made, out LogicalFragmentId parent) && parent == lower, Is.True,
                    "as an object it came from the fragment it replaced, though its origin is the cut above that one");
                Assert.That(ledger.TryGetActiveOperation(made, out _), Is.False);
            }

            Assert.That(ledger.LiveCutFragmentCount, Is.EqualTo(liveCut + 1), "one live cut fragment became two");
            var live = new List<LogicalFragmentId>();
            ledger.CopyLiveCutFragmentsOf(root, live);
            Assert.That(live, Is.EquivalentTo(new[] { upper, near, far }), "the family's live cut fragments");

            // The source: replaced, no target, its anchors let go, and found through the one reader for both kinds.
            Assert.That(StateOf(ledger, lower), Is.EqualTo(LogicalFragmentState.Replaced));
            Assert.That(ledger.IsCurrentTarget(lower), Is.False);
            Assert.That(AnchorsOf(ledger, lower), Is.Empty);
            Assert.That(ledger.TryGetSplitChildren(lower, out LogicalFragmentId first, out LogicalFragmentId second) && first == near && second == far, Is.True);
            Assert.That(ledger.TryGetReplacement(lower, out first, out second) && first == near && second == far, Is.True);
            Assert.That(ledger.MayReplaceBySplit(lower), Is.False, "not a second time");

            // Nothing of a cut was made, and the cut that made the source is as it was.
            Assert.That(ledger.OperationCount, Is.EqualTo(operations), "no operation");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(incomplete), "the budget of incomplete cuts is untouched");
            Assert.That(ledger.SplitReplacements, Is.EqualTo(1));
            Assert.That(ledger.TryGetOperation(cut, out LogicalCutOperation record), Is.True);
            Assert.That(record.positive == upper && record.negative == lower && record.source == root, Is.True, "the cut still names the source as its child");
            Assert.That(ledger.TryGetSplitSource(upper, out _), Is.False, "a child of a cut is not a split's");
            Assert.That(ledger.TryGetParent(upper, out LogicalFragmentId upperParent) && upperParent == root, Is.True, "a cut's child came from the cut's source");
            Assert.That(ledger.TryGetParent(lower, out LogicalFragmentId lowerParent) && lowerParent == root, Is.True, "and so did the fragment the split replaced");
            Assert.That(ledger.TryGetParent(root, out _), Is.False, "a registered fragment came from nothing");
            Assert.That(ledger.TryGetParent(default, out _) || ledger.TryGetParent(new LogicalFragmentId(fragments + 9), out _), Is.False, "an id that names nothing");
            Assert.That(ledger.TryGetSplitChildren(root, out _, out _), Is.False, "a fragment replaced by a cut has no split children");

            // Readers that keep what they read are told.
            Assert.That(ledger.Revision, Is.GreaterThan(revision));
            Assert.That(ledger.FamilyRevisionOf(root), Is.GreaterThan(familyRevision));
        }

        /// <summary>
        /// **Both fragments are cut again the ordinary way, and are replaced by a split again.** One of the two is
        /// admitted, published and replaced by its cut's children; the other is split once more. The lineage reads
        /// through all of it.
        /// </summary>
        [Test]
        public void TheTwoFragments_AreCutAndSplitAgain_AsAnyFragment()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment(null);
            Cut(ledger, root, k_plane, out LogicalFragmentId upper, out LogicalFragmentId lower);
            Assert.That(ledger.ReplaceBySplit(lower, null, null, out LogicalFragmentId near, out LogicalFragmentId far), Is.True);

            CutOperationId recut = Cut(ledger, near, k_otherPlane, out LogicalFragmentId nearPositive, out LogicalFragmentId nearNegative);
            Assert.That(StateOf(ledger, near), Is.EqualTo(LogicalFragmentState.Replaced));
            Assert.That(ledger.TryGetReplacement(near, out LogicalFragmentId a, out LogicalFragmentId b) && a == nearPositive && b == nearNegative, Is.True,
                "replaced by its cut: the cut's children");
            Assert.That(ledger.TryGetOrigin(nearPositive, out CutOperationId origin, out _) && origin == recut, Is.True);
            Assert.That(ledger.TryGetOperation(recut, out LogicalCutOperation record) && record.source == near, Is.True);

            Assert.That(ledger.ReplaceBySplit(far, null, null, out LogicalFragmentId farA, out LogicalFragmentId farB), Is.True, "a split's fragment is split again");
            Assert.That(ledger.TryGetSplitSource(farA, out LogicalFragmentId source) && source == far, Is.True);

            // The object's ancestry, step by step: through each split's source and each cut's source, down to the root.
            var up = new List<LogicalFragmentId>();
            for (LogicalFragmentId at = farB; ledger.TryGetParent(at, out LogicalFragmentId parent); at = parent)
            {
                up.Add(parent);
            }

            Assert.That(up, Is.EqualTo(new[] { far, lower, root }), "the second split's source, the first's, the cut's");
            up.Clear();
            for (LogicalFragmentId at = nearNegative; ledger.TryGetParent(at, out LogicalFragmentId parent); at = parent)
            {
                up.Add(parent);
            }

            Assert.That(up, Is.EqualTo(new[] { near, lower, root }), "a cut's source, the split's source it came from, the cut's above");

            var live = new List<LogicalFragmentId>();
            ledger.CopyLiveCutFragmentsOf(root, live);
            Assert.That(live, Is.EquivalentTo(new[] { upper, nearPositive, nearNegative, farA, farB }));
            Assert.That(ledger.LiveCutFragmentCount, Is.EqualTo(5));
            Assert.That(ledger.SplitReplacements, Is.EqualTo(2));
        }

        /// <summary>
        /// **Refused, with nothing changed**: for a registered fragment no cut made, for one whose cut is active, for
        /// one replaced or retired, and for an id that names nothing.
        /// </summary>
        [Test]
        public void ReplaceBySplit_IsRefused_ForARoot_AFragmentBeingCut_AReplacedOrRetiredOne()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new[] { k_high });
            LogicalFragmentId uncut = ledger.AddFragment(null);
            Cut(ledger, root, k_plane, out LogicalFragmentId upper, out LogicalFragmentId lower);
            Assert.That(ledger.Admit(upper, k_otherPlane, true, out CutOperationId active), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.Retire(lower), Is.True);

            int fragments = ledger.FragmentCount;
            long revision = ledger.Revision;
            foreach ((LogicalFragmentId fragment, string what) in new[]
                     {
                         (uncut, "a registered fragment no cut made"), (upper, "one whose cut is active"),
                         (root, "one replaced by its cut"), (lower, "a retired one"), (default(LogicalFragmentId), "the unset id"),
                         (new LogicalFragmentId(fragments + 5), "an id never issued"),
                     })
            {
                Assert.That(ledger.MayReplaceBySplit(fragment), Is.False, what);
                Assert.That(ledger.ReplaceBySplit(fragment, null, null, out LogicalFragmentId p, out LogicalFragmentId n), Is.False, what);
                Assert.That(p.IsSet || n.IsSet, Is.False, what);
            }

            Assert.That(ledger.FragmentCount, Is.EqualTo(fragments), "no id was issued");
            Assert.That(ledger.Revision, Is.EqualTo(revision), "and nothing changed");
            Assert.That(ledger.SplitReplacements, Is.Zero);
            Assert.That(ledger.TryGetActiveOperation(upper, out CutOperationId still) && still == active, Is.True, "the active cut goes on");
            Assert.That(ledger.TryGetReplacement(upper, out _, out _), Is.False, "a live fragment has no replacement");
            Assert.That(ledger.TryGetReplacement(lower, out _, out _), Is.False, "nor a retired one");
            Assert.That(ledger.TryGetReplacement(root, out LogicalFragmentId first, out LogicalFragmentId second) && first == upper && second == lower, Is.True,
                "a fragment replaced by a cut: the cut's children");
        }
    }
}
