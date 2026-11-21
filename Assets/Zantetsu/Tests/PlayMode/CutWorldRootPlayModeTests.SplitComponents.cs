using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// SPLIT-CANDIDATES-3: a fragment whose geometry is known to be one topology connected component is passed over before an
    /// attempt of the additional split is issued for it -- through the real path: the bodies' geometries appended through the
    /// cut input gate, the waist cut on a worker with its caps and its new vertex block, the split's two shares counted on its
    /// worker. A piece in several components and a piece whose count is unknown go on as before; the piece passed over keeps its
    /// geometry, its owner, its convexes and its place in the display. Every expected count is taken apart, by a flood over the
    /// stored triangles.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        // The topology connected components of a fragment's geometry, apart from the code under test: a flood over the triangles
        // its own indices name, joined where they share a topology id of the storage's map.
        private static int ComponentsByFlood(CutWorldRoot root, LogicalFragmentId fragment, out VpStoredGeometry geometry)
        {
            Assert.That(root.Geometry.TryGetGeometry(fragment, out geometry), Is.True, "the fragment has a geometry");
            VpCpuGeometryStorage storage = root.Storage;
            Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly indices), Is.True);
            NativeArray<int>.ReadOnly topology = storage.TopologyOfVertex;
            int triangles = indices.Length / 3;
            var trianglesOf = new Dictionary<int, List<int>>();
            for (int t = 0; t < triangles; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int id = topology[(int)indices[(3 * t) + k]];
                    if (!trianglesOf.TryGetValue(id, out List<int> list)) trianglesOf[id] = list = new List<int>();
                    list.Add(t);
                }
            }

            var seen = new bool[triangles];
            var stack = new Stack<int>();
            int components = 0;
            for (int t = 0; t < triangles; t++)
            {
                if (seen[t]) continue;
                components++;
                seen[t] = true;
                stack.Push(t);
                while (stack.Count > 0)
                {
                    int at = stack.Pop();
                    for (int k = 0; k < 3; k++)
                    {
                        foreach (int other in trianglesOf[topology[(int)indices[(3 * at) + k]]])
                        {
                            if (seen[other]) continue;
                            seen[other] = true;
                            stack.Push(other);
                        }
                    }
                }
            }

            Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);
            return components;
        }

        private IEnumerator OnePieceSideIsPassedOver(bool countsOn, bool skipOn)
        {
            bool enabledBefore = VpTopologyComponents.Enabled, skipBefore = CutSpatialSplit.SkipOneComponent;
            VpTopologyComponents.Enabled = countsOn;
            CutSpatialSplit.SkipOneComponent = skipOn;
            try
            {
                CutWorldRoot root = NewWorld(out Shader _);
                // The split's own counts, with the shapes as the cut left them: the convex trim (on by default, D-265)
                // would replace a side's shape, and the split would try that side again, as it is meant to.
                TrimOff(root);
                root.Driver.RemainingMainSeconds = () => 1.0;
                var remaining = new double[1];
                CutSpatialSplit split = SplitCutClosed(root, remaining, CutSpatialSplitSettings.DefaultMinCutSideTriangles);
                LogicalFragmentId body = AddWrappedBody(root, new Vector3(0f, 0f, 0f));
                yield return null;
                int bodyFlood = ComponentsByFlood(root, body, out VpStoredGeometry bodyGeometry);
                Assert.That(bodyFlood, Is.EqualTo(2), "the layout: the trunk and the detached hand");
                Assert.That(root.Storage.TopologyComponentsOf(bodyGeometry), Is.EqualTo(countsOn ? 2 : VpTopologyComponents.Unknown), "the gate's count, kept with the body's geometry");
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides, 1);
                LogicalFragmentId upper = sides[0], lower = sides[1];

                // The waist cut ran on a worker: each side's count is its own, on its final indices (the cap among them), over the
                // body's vertex block and the block the cut appended.
                int upperFlood = ComponentsByFlood(root, upper, out VpStoredGeometry upperGeometry);
                int lowerFlood = ComponentsByFlood(root, lower, out VpStoredGeometry lowerGeometry);
                Assert.That(upperFlood, Is.EqualTo(1), "the layout: the side above is one piece (the trunk's upper part, capped)");
                Assert.That(lowerFlood, Is.EqualTo(2), "the layout: the side below is the rest of the trunk and the hand");
                Assert.That(upperGeometry.blockCount >= 2 && lowerGeometry.blockCount >= 2, Is.True, "the layout: the sides are over two vertex blocks");
                Assert.That(root.Storage.TopologyComponentsOf(upperGeometry), Is.EqualTo(countsOn ? upperFlood : VpTopologyComponents.Unknown));
                Assert.That(root.Storage.TopologyComponentsOf(lowerGeometry), Is.EqualTo(countsOn ? lowerFlood : VpTopologyComponents.Unknown));
                Assert.That(root.Owners.TryGet(upper, out PhysicsFragmentOwner upperOwner), Is.True);
                PhysicsOwnerShape upperShape = upperOwner.Shape;
                int owners = root.Owners.Count;

                bool passedOver = countsOn && skipOn;
                Assert.That(split.Examine(upper), Is.EqualTo(passedOver ? SpatialSplitPass.OneComponent : SpatialSplitPass.Candidate),
                    "one piece: passed over only where the count is known and the pass is on");
                Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate), "several pieces: looked at as before");

                // The budget open: the piece in several components is tried as before; the one-piece side is not issued.
                remaining[0] = 1.0;
                yield return Until(() => split.LastEndOfIs(lower, SpatialSplitEnd.NotFound) && !split.HasAttempt && (passedOver || split.LastEndOfIs(upper, SpatialSplitEnd.NotFound)),
                    "the pieces to be tried were tried");
                for (int k = 0; k < 6; k++) yield return null;
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.Describe());
                TestContext.Out.WriteLine(split.DescribeSeveral());
                if (passedOver)
                {
                    Assert.That(split.Issued, Is.EqualTo(1), "one attempt: the lower side's");
                    Assert.That(split.TryGetLastEndOf(upper, out _), Is.False, "no attempt of the one-piece side ever ended: none was issued");
                    Assert.That(split.OneComponentInputs, Is.EqualTo(1), "resolved without an attempt, once for its geometry");
                    Assert.That(split.EndedAs(SpatialSplitEnd.NotFound), Is.EqualTo(1), "and not among the attempts' ends");
                    Assert.That(split.IsMarked(upper), Is.False, "nor marked as tried");
                    Assert.That(split.IsIdle, Is.True, "nothing is left to try");
                }
                else
                {
                    Assert.That(split.Issued, Is.EqualTo(2), "both pieces were tried, as before");
                    Assert.That(split.OneComponentInputs, Is.Zero);
                    Assert.That(split.EndedAs(SpatialSplitEnd.NotFound), Is.EqualTo(2));
                }

                // The piece passed over stands as it was: its geometry, its owner and convexes, its place in the display.
                Assert.That(root.Ledger.IsCurrentTarget(upper), Is.True);
                Assert.That(root.Geometry.TryGetGeometry(upper, out VpStoredGeometry upperNow) && upperNow.indexRange.Equals(upperGeometry.indexRange), Is.True, "the same geometry");
                Assert.That(root.Owners.TryGet(upper, out PhysicsFragmentOwner upperOwnerNow) && ReferenceEquals(upperOwnerNow, upperOwner) && ReferenceEquals(upperOwnerNow.Shape, upperShape)
                    && !upperShape.IsFreed && upperShape.ConvexCount == 1, Is.True, "the same owner and convex");
                Assert.That(root.Display.HasOwnRegistration(upper), Is.True, "drawn from its own registration");
                Assert.That(root.Owners.Count, Is.EqualTo(owners));
                yield return null;
                Assert.That(IsDrawn(root, upper) && IsDrawn(root, lower), Is.True);
                Assert.That(root.GeometryFaults, Is.Zero);
                Assert.That(root.TerminationRequested, Is.False);

                // The one-piece side is cut again: the pieces are new geometries with counts of their own.
                var pieces = new LogicalFragmentId[2];
                yield return CutInTwo(root, upper, new float4(0f, 0f, 1f, 0f), pieces);
                yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
                yield return null;
                foreach (LogicalFragmentId piece in pieces)
                {
                    int flood = ComponentsByFlood(root, piece, out VpStoredGeometry pieceGeometry);
                    Assert.That(root.Storage.TopologyComponentsOf(pieceGeometry), Is.EqualTo(countsOn ? flood : VpTopologyComponents.Unknown), "a cut of a cut: the piece's own count");
                    Assert.That(pieceGeometry.blockCount, Is.GreaterThanOrEqualTo(3), "the layout: over three vertex blocks");
                    Assert.That(pieceGeometry.indexRange.Equals(upperGeometry.indexRange), Is.False, "another geometry");
                }

                split.Settings = default;
                yield return EndWorld(root);
            }
            finally
            {
                VpTopologyComponents.Enabled = enabledBefore;
                CutSpatialSplit.SkipOneComponent = skipBefore;
            }
        }

        [UnityTest]
        public IEnumerator SplitComponents_AOnePieceSide_IsPassedOverBeforeAnAttempt_AndStandsAsItWas()
        {
            yield return OnePieceSideIsPassedOver(countsOn: true, skipOn: true);
        }

        [UnityTest]
        public IEnumerator SplitComponents_AKnownCountWithThePassOff_IsTriedAsBefore()
        {
            yield return OnePieceSideIsPassedOver(countsOn: true, skipOn: false);
        }

        [UnityTest]
        public IEnumerator SplitComponents_AnUnknownCount_IsTriedAsBefore()
        {
            yield return OnePieceSideIsPassedOver(countsOn: false, skipOn: true);
        }

        [UnityTest]
        public IEnumerator SplitComponents_ASplitsChildren_HaveTheirOwnCounts_AndAOnePieceChildIsPassedOver()
        {
            bool enabledBefore = VpTopologyComponents.Enabled, skipBefore = CutSpatialSplit.SkipOneComponent;
            VpTopologyComponents.Enabled = true;
            CutSpatialSplit.SkipOneComponent = true;
            try
            {
                CutWorldRoot root = NewWorld(out Shader _);
                root.Driver.RemainingMainSeconds = () => 1.0;
                var remaining = new double[1];
                CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
                // (With the convex cut on, a piece of one convex is looked at too: what stops a one-piece child here is its count.)
                split.Settings = SplitOnWithBent(cutConvexes: true);
                LogicalFragmentId body = AddUWithAPartInside(root, new Vector3(0f, 0f, 0f));
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides, lowerConvexes: 5);
                LogicalFragmentId upper = sides[0], lower = sides[1];
                Assert.That(ComponentsByFlood(root, upper, out _), Is.EqualTo(1), "the layout: the side above is one piece");
                Assert.That(split.Examine(upper), Is.EqualTo(SpatialSplitPass.OneComponent));
                int lowerFlood = ComponentsByFlood(root, lower, out VpStoredGeometry lowerGeometry);
                Assert.That(lowerFlood, Is.GreaterThanOrEqualTo(2), "the layout: the U and the part inside it");
                Assert.That(root.Storage.TopologyComponentsOf(lowerGeometry), Is.EqualTo(lowerFlood));
                Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate));

                // The lower side is split by the bent partition: the two children's counts were taken on the split's worker, on
                // the shares it wrote.
                remaining[0] = 1.0;
                yield return Until(() => split.LastEndOfIs(lower, SpatialSplitEnd.Published), "the lower side was split");
                for (int k = 0; k < 8; k++) yield return null;
                remaining[0] = 0.0;
                LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
                Assert.That(split.LastSource, Is.EqualTo(lower));
                int sum = 0, onePiece = 0;
                foreach (LogicalFragmentId child in new[] { a, b })
                {
                    int flood = ComponentsByFlood(root, child, out VpStoredGeometry childGeometry);
                    Assert.That(root.Storage.TopologyComponentsOf(childGeometry), Is.EqualTo(flood), "the child's own count");
                    Assert.That(flood, Is.GreaterThanOrEqualTo(1));
                    sum += flood;
                    Assert.That(root.Owners.TryGet(child, out PhysicsFragmentOwner owner), Is.True);
                    TestContext.Out.WriteLine("child " + child.value + ": " + flood + " components, " + owner.Shape.ConvexCount + " convexes, " + split.Examine(child));
                    if (flood == 1)
                    {
                        // One piece: looked at for a split before -- now passed over, with no attempt.
                        onePiece++;
                        Assert.That(split.Examine(child), Is.EqualTo(SpatialSplitPass.OneComponent));
                        Assert.That(split.TryGetLastEndOf(child, out _), Is.False, "no attempt was issued for it");
                        Assert.That(split.IsMarked(child), Is.False);
                    }
                    else
                    {
                        Assert.That(split.Examine(child), Is.Not.EqualTo(SpatialSplitPass.OneComponent), "several pieces: not passed over");
                    }

                    Assert.That(IsDrawn(root, child), Is.True, "drawn");
                }

                Assert.That(sum, Is.EqualTo(lowerFlood), "no triangle was cut: the children's components are the parent's, shared out");
                Assert.That(onePiece, Is.GreaterThanOrEqualTo(1), "the layout: a child of one piece (the part)");
                Assert.That(split.OneComponentInputs, Is.GreaterThanOrEqualTo(2), "the side above and the one-piece child, each resolved without an attempt");
                Assert.That(split.TryGetLastEndOf(upper, out _), Is.False, "no attempt was issued for the side above");
                Assert.That(root.GeometryFaults, Is.Zero);
                Assert.That(root.TerminationRequested, Is.False);
                TestContext.Out.WriteLine(split.DescribeSeveral());
                split.Settings = default;
                yield return EndWorld(root);
            }
            finally
            {
                VpTopologyComponents.Enabled = enabledBefore;
                CutSpatialSplit.SkipOneComponent = skipBefore;
            }
        }
    }
}
