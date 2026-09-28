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
    /// DESIGN 7.10's piece lifetime (<see cref="CutFragmentLifetime"/>) through the product's own world, on a few boxes
    /// whose lineage the case marks the way a character's is marked: what starts no retirement, what one retirement takes
    /// away and what it leaves, a piece retired before its geometry arrives, the recut of a piece left, and the choice of
    /// the far, unseen pieces only. The world is ended by every case, so what it holds is given back at its release.
    /// </summary>
    public partial class CutWorldRootPlayModeTests
    {
        private static CutFragmentLifetimeSettings Lifetime(bool enabled, int threshold, float distance, int retirePerFrame = 4)
        {
            return new CutFragmentLifetimeSettings(enabled, threshold, distance, 16, retirePerFrame, 0.004, 1.0);
        }

        /// <summary>A camera at <paramref name="at"/> looking along +z; its view is what the lifetime judges against.</summary>
        private Camera LifetimeCamera(Vector3 at)
        {
            Camera camera = Track(new GameObject("Lifetime View")).AddComponent<Camera>();
            camera.enabled = false;
            camera.transform.SetPositionAndRotation(at, Quaternion.identity);
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 500f;
            return camera;
        }

        /// <summary>Cuts <paramref name="source"/> in two and waits for its geometry commit; the two sides, in order.</summary>
        private static IEnumerator CutInTwo(CutWorldRoot root, LogicalFragmentId source, float4 plane, LogicalFragmentId[] sides)
        {
            ProvisionalCutAsk ask = Ask(source, plane);
            Assert.That(root.TryAsk(in ask), Is.True, "the cut was asked");
            yield return null;
            List<CutOperationId> admitted = AdmittedFor(root, new[] { source });
            Assert.That(admitted.Count, Is.EqualTo(1), "the cut was accepted");
            CutOperationId operation = admitted[0];
            yield return Until(() => root.Geometry.StageOf(operation) == CutGeometryStage.Committed, "the cut committed");
            LogicalCutOperation record = OperationOf(root, operation);
            yield return Until(
                () => root.Owners.TryGet(record.positive, out _) && root.Owners.TryGet(record.negative, out _),
                "both sides have their owners");
            sides[0] = record.positive;
            sides[1] = record.negative;
        }

        private static bool IsHitTarget(CutWorldRoot root, LogicalFragmentId fragment)
        {
            var shapes = new List<CurrentShape>();
            root.Owners.CollectCurrentShapes(shapes);
            return shapes.Exists(s => s.Fragment == fragment);
        }

        /// <summary>
        /// **Nothing new starts while it is off, while the frame's budget is short, or for a piece being cut.** Two pieces
        /// far behind the view qualify in every other way; nothing is retired in the first two, and the piece under a cut
        /// is refused while its sibling -- whose own state is all that is asked -- may go.
        /// </summary>
        [UnityTest]
        public IEnumerator PieceLifetime_StartsNothing_WhenOff_ShortOfBudget_OrForAPieceBeingCut()
        {
            HoldingExecutor unityJob = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.UnityJob
                    ? unityJob = Held(new HoldingExecutor(new UnityJobWorkExecutor(8)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentLifetime lifetime = root.Lifetime;
            lifetime.ViewCamera = LifetimeCamera(new Vector3(0f, 0f, 50f));
            lifetime.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            lifetime.MarkLineage(body);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), sides);
            Assert.That(lifetime.IsTarget(sides[0]) && lifetime.IsTarget(sides[1]), Is.True, "the layout: both pieces are targets");
            Assert.That(lifetime.IsTarget(body), Is.False, "the character itself never is");

            // Off.
            lifetime.Settings = Lifetime(false, 0, 1f);
            lifetime.Step();
            Assert.That(lifetime.Examined + lifetime.Retired, Is.Zero, "off: nothing looked at, nothing retired");

            // Short of budget.
            lifetime.Settings = Lifetime(true, 0, 1f);
            lifetime.RemainingMainSeconds = () => 0.0;
            lifetime.Step();
            Assert.That(lifetime.BudgetSkips, Is.GreaterThan(0));
            Assert.That(lifetime.Examined + lifetime.Retired, Is.Zero, "short of budget: nothing looked at, nothing retired");
            lifetime.Settings = Lifetime(false, 0, 1f);
            lifetime.RemainingMainSeconds = () => 1.0;

            // A piece being cut: its cut is held at its bake.
            unityJob.HoldEverything = true;
            ProvisionalCutAsk ask = Ask(sides[0], new float4(1f, 0f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            Assert.That(root.Ledger.TryGetActiveOperation(sides[0], out _), Is.True, "the layout: its cut is under way");
            Assert.That(lifetime.TryRetire(sides[0], out string refusal), Is.False);
            StringAssert.Contains("under way", refusal);
            Assert.That(root.Ledger.IsCurrentTarget(sides[0]), Is.True, "it stays live");
            Assert.That(lifetime.TryRetire(sides[1], out refusal), Is.True, "its sibling's own state is all that is asked: " + refusal);
            unityJob.HoldEverything = false;
            unityJob.ReleaseEverything();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **One retirement takes its piece out of the drawing, the physics and the hits, and leaves its sibling.** The
        /// retired piece's owner and actor go, its geometry reference and index range are given back at the display's
        /// collection, and the DAG forgets it; the sibling is drawn, owned, hit and cut from as before. The vertices both
        /// were written into stay where they are (DESIGN 4.5.3's vertex reuse is not part of this). Then the sibling is
        /// retired too, and the last of the pair's references goes with it.
        /// </summary>
        [UnityTest]
        public IEnumerator APieceRetired_LeavesTheDrawingPhysicsAndHits_AndItsSiblingStays()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentLifetime lifetime = root.Lifetime;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            lifetime.MarkLineage(body);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), sides);
            yield return null;
            LogicalFragmentId gone = sides[0];
            LogicalFragmentId kept = sides[1];
            Assert.That(root.Geometry.TryGetGeometry(gone, out VpStoredGeometry goneGeometry), Is.True, "the layout: its geometry");
            Assert.That(root.Geometry.TryGetGeometry(kept, out VpStoredGeometry keptGeometry), Is.True);
            Assert.That(IsDrawn(root, gone) && IsDrawn(root, kept), Is.True, "the layout: both drawn");
            root.Owners.TryGet(gone, out PhysicsFragmentOwner goneOwner);
            GameObject goneActor = goneOwner.Root;
            int references = root.References.LiveGeometryCount;
            int vertexCapacity = root.Storage.CommittedVertexCapacity;

            Assert.That(lifetime.TryRetire(gone, out string refusal), Is.True, refusal);
            Assert.That(root.Ledger.TryGetFragmentState(gone, out LogicalFragmentState state) && state == LogicalFragmentState.Retired, Is.True);
            Assert.That(root.Owners.TryGet(gone, out _), Is.False, "its owner is gone");
            Assert.That(IsHitTarget(root, gone), Is.False, "and so is its hit shape");
            Assert.That(root.Geometry.TryGetGeometry(gone, out _), Is.False, "the DAG forgot it");
            yield return null;
            yield return null;
            Assert.That(goneActor == null, Is.True, "its actor is destroyed");
            Assert.That(IsDrawn(root, gone), Is.False, "no longer drawn");
            Assert.That(root.References.LiveGeometryCount, Is.EqualTo(references - 1), "its geometry reference went back at the collection");
            Assert.That(
                root.Storage.TryGetIndexState(goneGeometry.indexRange, out VpIndexRangeState goneIndices, out _, out _)
                && goneIndices == VpIndexRangeState.Published, Is.False, "its index range is retired");

            Assert.That(root.Ledger.IsCurrentTarget(kept), Is.True, "the sibling stays");
            Assert.That(IsDrawn(root, kept), Is.True);
            Assert.That(root.Owners.TryGet(kept, out _) && IsHitTarget(root, kept), Is.True);
            Assert.That(root.Storage.TryGetIndexState(keptGeometry.indexRange, out VpIndexRangeState keptIndices, out _, out _)
                        && keptIndices == VpIndexRangeState.Published, Is.True, "the sibling's index range is kept");
            Assert.That(root.Storage.CommittedVertexCapacity, Is.EqualTo(vertexCapacity), "vertices are not given back (4.5.3 reuse is not here)");

            // The last of the pair.
            Assert.That(lifetime.TryRetire(kept, out refusal), Is.True, refusal);
            yield return null;
            yield return null;
            Assert.That(root.References.LiveGeometryCount, Is.EqualTo(references - 2), "and its reference with it");
            Assert.That(lifetime.Retired, Is.EqualTo(2));
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A piece retired before its geometry arrives is not brought back by it.** The geometry work is held after the
        /// cut's physics is published; one side is retired; the work is let go and commits for the side that is left.
        /// The retired side stays retired, ownerless and undrawn, and the DAG keeps nothing for it.
        /// </summary>
        [UnityTest]
        public IEnumerator APieceRetiredBeforeItsGeometry_IsNotBroughtBackByTheLateResult()
        {
            HoldingExecutor geometry = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.GeometryPool
                    ? geometry = Held(new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentLifetime lifetime = root.Lifetime;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            lifetime.MarkLineage(body);
            yield return null;

            geometry.HoldEverything = true;
            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            CutOperationId operation = AdmittedFor(root, new[] { body })[0];
            yield return Until(
                () => root.Ledger.TryGetOperation(operation, out LogicalCutOperation op) && op.state == LogicalCutOperationState.Published
                      && root.Owners.TryGet(op.positive, out _) && root.Owners.TryGet(op.negative, out _),
                "the cut's physics is published with both owners");
            Assert.That(root.Geometry.StageOf(operation), Is.Not.EqualTo(CutGeometryStage.Committed), "the layout: its geometry is held");
            LogicalCutOperation record = OperationOf(root, operation);

            Assert.That(lifetime.TryRetire(record.positive, out string refusal), Is.True, "an unfinished geometry is no reason to wait: " + refusal);
            geometry.HoldEverything = false;
            geometry.ReleaseEverything();
            yield return Until(() => root.Geometry.StageOf(operation) == CutGeometryStage.Committed, "committed for the side left");
            yield return null;
            yield return null;

            Assert.That(root.Ledger.TryGetFragmentState(record.positive, out LogicalFragmentState state) && state == LogicalFragmentState.Retired,
                Is.True, "still retired");
            Assert.That(root.Owners.TryGet(record.positive, out _), Is.False, "no owner came back");
            Assert.That(IsDrawn(root, record.positive), Is.False, "not drawn");
            Assert.That(root.Geometry.TryGetGeometry(record.positive, out _) || root.Geometry.TryGetGeometryFrame(record.positive, out _),
                Is.False, "the DAG keeps nothing for it");
            Assert.That(IsDrawn(root, record.negative) && root.Geometry.TryGetGeometry(record.negative, out _), Is.True, "the side left is");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The piece left is cut again as usual.** After its sibling is retired, an ordinary cut of it is accepted,
        /// published and committed, and both of its sides are drawn.
        /// </summary>
        [UnityTest]
        public IEnumerator APieceLeftAfterARetirement_IsCutAgainAsUsual()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            root.Lifetime.MarkLineage(body);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), sides);
            Assert.That(root.Lifetime.TryRetire(sides[0], out string refusal), Is.True, refusal);
            yield return null;

            var grandchildren = new LogicalFragmentId[2];
            yield return CutInTwo(root, sides[1], new float4(1f, 0f, 0f, 0f), grandchildren);
            yield return null;
            Assert.That(IsDrawn(root, grandchildren[0]) && IsDrawn(root, grandchildren[1]), Is.True, "both new sides drawn");
            Assert.That(root.Lifetime.IsTarget(grandchildren[0]), Is.True, "and they are of the same lineage");
            Assert.That(root.TerminationRequested, Is.False);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The count keeps moving after a round that counted too few.** The first rounds count two pieces against a
        /// threshold of two, so nothing is retired; a second marked box is cut far away, and a later round counts four
        /// and retires far pieces down to the threshold. (A round left unfinished because its count was low would have
        /// kept the first count for good.)
        /// </summary>
        [UnityTest]
        public IEnumerator TheCount_KeepsMoving_AfterARoundThatCountedTooFew()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentLifetime lifetime = root.Lifetime;
            lifetime.Settings = Lifetime(false, 2, 20f);
            lifetime.RemainingMainSeconds = () => 1.0;
            lifetime.ViewCamera = LifetimeCamera(new Vector3(0f, 0f, -8f));
            LogicalFragmentId first = AddBody(root, new Vector3(0f, 0f, -60f));
            LogicalFragmentId second = AddBody(root, new Vector3(4f, 0f, -60f));
            lifetime.MarkLineage(first);
            lifetime.MarkLineage(second);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, first, new float4(0f, 1f, 0f, 0f), sides);

            lifetime.Settings = Lifetime(true, 2, 20f);
            for (int i = 0; i < 4; i++)
            {
                lifetime.Step();
            }

            Assert.That(lifetime.TargetCount, Is.EqualTo(2), "the layout: two targets, at the threshold");
            Assert.That(lifetime.Retired, Is.Zero);
            int rounds = lifetime.Rounds;

            yield return CutInTwo(root, second, new float4(0f, 1f, 0f, 0f), sides);
            for (int i = 0; i < 6; i++)
            {
                lifetime.Step();
            }

            Assert.That(lifetime.Rounds, Is.GreaterThan(rounds), "new rounds were taken");
            Assert.That(lifetime.Retired, Is.EqualTo(2), "down to the threshold");
            Assert.That(lifetime.TargetCount, Is.EqualTo(2));
            lifetime.Settings = Lifetime(false, 2, 20f);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The policy retires only far pieces out of view, of a marked lineage, and only down to its threshold.** Near
        /// the view, one box's pieces are seen; far behind it, a marked box's and an unmarked box's pieces are not. With
        /// the threshold one below the target count, one far marked piece goes and nothing else does; with it at zero,
        /// the other far marked piece goes too, and the seen and the unmarked ones never do.
        /// </summary>
        [UnityTest]
        public IEnumerator ThePolicy_RetiresOnlyFarUnseenPiecesOfAMarkedLineage_DownToItsThreshold()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentLifetime lifetime = root.Lifetime;
            lifetime.Settings = Lifetime(false, 0, 20f);
            lifetime.RemainingMainSeconds = () => 1.0;
            lifetime.ViewCamera = LifetimeCamera(new Vector3(0f, 0f, -8f));
            LogicalFragmentId seen = AddBody(root, new Vector3(0f, 0f, 0f));
            LogicalFragmentId far = AddBody(root, new Vector3(0f, 0f, -60f));
            LogicalFragmentId unmarked = AddBody(root, new Vector3(4f, 0f, -60f));
            lifetime.MarkLineage(seen);
            lifetime.MarkLineage(far);
            yield return null;
            var seenSides = new LogicalFragmentId[2];
            var farSides = new LogicalFragmentId[2];
            var unmarkedSides = new LogicalFragmentId[2];
            yield return CutInTwo(root, seen, new float4(0f, 1f, 0f, 0f), seenSides);
            yield return CutInTwo(root, far, new float4(0f, 1f, 0f, 0f), farSides);
            yield return CutInTwo(root, unmarked, new float4(0f, 1f, 0f, 0f), unmarkedSides);

            // Four targets; down to three: one far marked piece.
            lifetime.Settings = Lifetime(true, 3, 20f, retirePerFrame: 4);
            for (int i = 0; i < 4; i++)
            {
                lifetime.Step();
            }

            Assert.That(lifetime.Retired, Is.EqualTo(1), "down to the threshold and no further");
            Assert.That(root.Ledger.IsCurrentTarget(farSides[0]) ^ root.Ledger.IsCurrentTarget(farSides[1]), Is.True, "one far marked piece");

            // Down to zero: the other far marked piece too, and never a seen or an unmarked one.
            lifetime.Settings = Lifetime(true, 0, 20f, retirePerFrame: 4);
            for (int i = 0; i < 8; i++)
            {
                lifetime.Step();
                yield return null;
            }

            Assert.That(lifetime.Retired, Is.EqualTo(2));
            Assert.That(root.Ledger.IsCurrentTarget(farSides[0]) || root.Ledger.IsCurrentTarget(farSides[1]), Is.False);
            Assert.That(root.Ledger.IsCurrentTarget(seenSides[0]) && root.Ledger.IsCurrentTarget(seenSides[1]), Is.True, "seen: kept");
            Assert.That(root.Ledger.IsCurrentTarget(unmarkedSides[0]) && root.Ledger.IsCurrentTarget(unmarkedSides[1]), Is.True, "unmarked: kept");
            Assert.That(lifetime.Rounds, Is.GreaterThan(0));
            lifetime.Settings = Lifetime(false, 0, 20f);
            yield return EndWorld(root);
        }
    }
}
