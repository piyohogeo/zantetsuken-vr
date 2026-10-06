using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Object = UnityEngine.Object;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// Held placements in a cut world (2026-10-07; DESIGN 5.6, D-205): the world's physics step counts are what the
    /// display's held placements go by, and the cameras named to the world's camera drawing are their reference points.
    /// The display's own cases (EditMode) say what is held and who is asked; this says that a world is wired so.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static IEnumerator UntilHeld(System.Func<bool> done, float seconds, string what)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!done() && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(done(), Is.True, what + ": within " + seconds + " s");
        }

        private static VpHeldPlacementTotals HeldSince(CutWorldRoot root, in VpHeldPlacementTotals before)
        {
            VpHeldPlacementTotals now = root.Display.HeldPlacementTotals;
            now.Subtract(before);
            return now;
        }

        [UnityTest]
        public IEnumerator HeldPlacements_TheWorldsCamerasAreTheReferencePoints_AFarStandingBodyIsNotAsked_UntilACameraComesNear_OrAChangeIsTold()
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, SmallDisplayRoom(1024));
            root.Driver.RemainingMainSeconds = () => 1.0;
            ShadowStage stage = NewShadowStage();
            stage.camera.enabled = true;
            stage.camera.transform.position = new Vector3(0f, 3f, -8f);   // its proximity box: z from -28 to 12
            Assert.That(root.Display.PlacementProximity, Is.Null, "no camera named yet: every placement is asked");
            var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
            SetPrivate(drawing, "world", root);
            SetPrivate(drawing, "cameras", new[] { stage.camera });

            // Standing bodies (cubes of 2 m) in a row: three near the camera, two far.
            float[] rowZ = { 0f, 6f, 12f, 60f, 90f };
            var owners = new List<PhysicsFragmentOwner>();
            foreach (float z in rowZ)
            {
                LogicalFragmentId body = AddBody(root, new Vector3(0f, 1.5f, z));
                Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner owner), Is.True);
                owner.Body.isKinematic = true;
                owners.Add(owner);
            }

            // They are held once two different step results agree.
            yield return UntilHeld(() => root.Display.HeldPlacements == rowZ.Length, 30f, "every standing body held");
            Assert.That(root.Display.PlacementProximity, Is.Not.Null, "the world's camera drawing named its cameras to the display");
            Assert.That(root.Display.HeldPlacementTotals.pointsAtEnd, Is.EqualTo(1), "one camera, one reference point");

            // Over a few more steps: each pass asks the three near ones and leaves the two far ones.
            VpHeldPlacementTotals t = root.Display.HeldPlacementTotals;
            long step = CutPhysicsStep.Clock.StepId;
            long reuses = root.Display.PlacementReuses;
            yield return UntilHeld(() => CutPhysicsStep.Clock.StepId >= step + 4, 30f, "four more steps");
            yield return null;
            VpHeldPlacementTotals d = HeldSince(root, t);
            TestContext.Out.WriteLine("at rest: selective passes " + d.passesSelective + ", asked near " + d.queriedNear + ", ordinary " + d.queriedOrdinary + ", not asked " + d.omitted
                                      + "; collections that let the adopted placements stand " + (root.Display.PlacementReuses - reuses) + "; search ms " + (d.searchSeconds * 1000).ToString("F4"));
            Assert.That(d.passesSelective, Is.GreaterThanOrEqualTo(3), "a pass a step");
            Assert.That(new[] { d.queriedNear, d.omitted, d.queriedOrdinary, d.passesStructure, d.passesUnvouched }, Is.EqualTo(new[] { 3 * d.passesSelective, 2 * d.passesSelective, 0L, 0L, 0L }),
                "three asked, two not, in every pass");

            // The farthest is moved with nothing said. It is held and far: not asked, drawn where it is held.
            Bounds before = root.Display.BodyBatchForTest.WorldBounds;
            long written = root.Display.InstanceRecordsWritten;
            owners[4].Root.transform.position += new Vector3(0f, 2f, 0f);
            step = CutPhysicsStep.Clock.StepId;
            yield return UntilHeld(() => CutPhysicsStep.Clock.StepId >= step + 3, 30f, "three steps with the far body moved");
            yield return null;
            Assert.That(root.Display.InstanceRecordsWritten, Is.EqualTo(written), "nothing written: the far held body was not asked");
            Assert.That(root.Display.BodyBatchForTest.WorldBounds.max.y, Is.EqualTo(before.max.y), "drawn where it is held");

            // The camera comes near it: asked, found moved, drawn where it is.
            stage.camera.transform.position = new Vector3(0f, 3f, 85f);   // z from 65 to 105: the body at 90, not the one at 60
            t = root.Display.HeldPlacementTotals;
            yield return UntilHeld(() => HeldSince(root, t).demoted >= 1, 30f, "the moved body asked once the camera is near");
            yield return null;
            Assert.That(root.Display.BodyBatchForTest.WorldBounds.max.y, Is.EqualTo(before.max.y + 2f).Within(1e-3f), "drawn where it is");
            yield return UntilHeld(() => root.Display.HeldPlacements == rowZ.Length, 30f, "held again where it stands");

            // The body at 60 -- held, far from the camera now -- is put elsewhere and that is said (as whoever moves an
            // owner outside a step says it): every held one is asked, and it is drawn where it was put.
            owners[3].Root.transform.position += new Vector3(0f, 4f, 0f);
            CutPhysicsStep.NotePlacementInputChanged();
            t = root.Display.HeldPlacementTotals;
            yield return UntilHeld(() => HeldSince(root, t).invalidations >= 1, 30f, "the change told is taken up");
            yield return null;
            Assert.That(root.Display.BodyBatchForTest.WorldBounds.max.y, Is.EqualTo(before.max.y + 4f).Within(1e-3f), "the body put elsewhere is drawn there");

            // No camera named any more: nothing is held, every placement asked.
            stage.camera.enabled = false;
            yield return null;
            Object.Destroy(drawing);
            yield return null;
            yield return null;
            Assert.That(root.Display.PlacementProximity, Is.Null, "the camera drawing gone: no reference points");
            Assert.That(root.Display.HeldPlacements, Is.Zero, "and nothing held");
            yield return EndWorld(root);
        }

        /// <summary>
        /// The product's own way of a cut (2026-10-07, D-207): asked of the world, published and committed by its driver,
        /// its owners made, handed over and retired through the owner registry, which tells every change with the
        /// fragment it concerns. No change is told with no target, so never is every held placement asked: the bodies
        /// of other families far from the camera are left as they are held through the whole of it.
        /// </summary>
        [UnityTest]
        public IEnumerator HeldPlacements_ACutThroughTheOwnerRegistry_ItsCommitAndARetirement_AskOnlyItsOwnFamily_AndChangesToldOfTwoBodiesAtOnceAreBothSeen()
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, SmallDisplayRoom(1024));
            root.Driver.RemainingMainSeconds = () => 1.0;
            ShadowStage stage = NewShadowStage();
            stage.camera.enabled = true;
            stage.camera.transform.position = new Vector3(0f, 3f, -8f);   // its proximity box: z from -28 to 12
            var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
            SetPrivate(drawing, "world", root);
            SetPrivate(drawing, "cameras", new[] { stage.camera });

            // Standing bodies: two near the camera, two far.
            float[] rowZ = { 0f, 6f, 60f, 90f };
            var bodies = new List<LogicalFragmentId>();
            var owners = new List<PhysicsFragmentOwner>();
            foreach (float z in rowZ)
            {
                LogicalFragmentId body = AddBody(root, new Vector3(0f, 1.5f, z));
                Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner owner), Is.True);
                owner.Body.isKinematic = true;
                bodies.Add(body);
                owners.Add(owner);
            }

            root.Lifetime.MarkLineage(bodies[0]);
            yield return UntilHeld(() => root.Display.HeldPlacements == rowZ.Length, 30f, "every standing body held");
            Assert.That(root.Display.PlacementChanges, Is.Not.Null, "the world gives the display what the changes outside a step concern");
            VpHeldPlacementTotals t = root.Display.HeldPlacementTotals;

            // The first body is cut: a provisional pair is published, the cut's geometry commits, the owners are handed
            // over. Its two sides are held where this case leaves them.
            Assert.That(root.TryAsk(Ask(bodies[0], new float4(0f, 1f, 0f, 0f))), Is.True, "the cut asked");
            CutOperationId operation = default;
            yield return UntilHeld(
                () =>
                {
                    foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions) operation = candidate.Operation;
                    return operation.IsSet && root.Ledger.TryGetOperation(operation, out LogicalCutOperation now) && now.state == LogicalCutOperationState.Completed;
                },
                60f, "the cut published and committed");
            Assert.That(root.Ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
            foreach (LogicalFragmentId side in new[] { record.positive, record.negative })
            {
                Assert.That(root.Owners.TryGet(side, out PhysicsFragmentOwner sideOwner), Is.True, "a side's owner");
                sideOwner.Body.isKinematic = true;
            }

            yield return UntilHeld(() => root.Display.HeldPlacements == rowZ.Length + 1, 30f, "the two sides held beside the three other bodies");

            // One side is retired.
            Assert.That(root.Lifetime.TryRetire(record.positive, out string refusal), Is.True, refusal);
            yield return UntilHeld(() => root.Display.HeldPlacements == rowZ.Length, 30f, "the retired side gone, the others held");
            long step = CutPhysicsStep.Clock.StepId;
            yield return UntilHeld(() => CutPhysicsStep.Clock.StepId >= step + 3, 30f, "three more steps");
            yield return null;

            VpHeldPlacementTotals d = HeldSince(root, t);
            long passes = d.passesSelective + d.passesStructure;
            TestContext.Out.WriteLine("the cut, its commit and the retirement: passes " + passes + " (through the structure " + d.passesStructure + "), mappings " + d.remaps + ", carried " + d.carried + ", fresh " + d.fresh
                                      + ", held ones gone " + d.droppedHeld + "; families told " + d.toldFamilies + ", asked for a telling " + d.queriedNotified + ", passes that asked every held one " + d.invalidations
                                      + "; asked near " + d.queriedNear + ", ordinary " + d.queriedOrdinary + ", clipped " + d.queriedSelected + ", not asked " + d.omitted + "; became held " + d.promoted
                                      + ", made ordinary " + d.demoted + "; forgotten whole " + d.structureResets);
            Assert.That(d.toldFamilies, Is.GreaterThanOrEqualTo(1), "the owner registry told of the cut body's family");
            Assert.That(d.passesStructure, Is.GreaterThanOrEqualTo(2), "the structure changed: the publication, the commit, the retirement");
            Assert.That(new[] { d.invalidations, d.structureResets, d.passesUnvouched }, Is.EqualTo(new long[] { 0, 0, 0 }), "no change told with no target: never was every held one asked, nothing forgotten whole");
            Assert.That(d.omitted, Is.GreaterThanOrEqualTo(2 * passes), "the two far bodies were asked in no pass of all this: held as they were, their boxes untouched");
            Assert.That(d.droppedHeld, Is.LessThanOrEqualTo(3), "taken out of the tree: the cut body and at most its own sides");

            // Two far bodies moved with no step, each told of: both are asked and drawn where they are; nobody else's
            // held placement is asked for it.
            Bounds before = root.Display.BodyBatchForTest.WorldBounds;
            t = root.Display.HeldPlacementTotals;
            owners[2].Root.transform.position += new Vector3(0f, 2f, 0f);
            owners[3].Root.transform.position += new Vector3(5f, 0f, 0f);
            CutPhysicsStep.NotePlacementInputChanged(bodies[2]);
            CutPhysicsStep.NotePlacementInputChanged(bodies[3]);
            yield return UntilHeld(() => HeldSince(root, t).demoted >= 2, 30f, "both told of are asked and found moved");
            yield return null;
            d = HeldSince(root, t);
            Bounds after = root.Display.BodyBatchForTest.WorldBounds;
            Assert.That(after.max.y, Is.EqualTo(before.max.y + 2f).Within(1e-3f), "the one moved up is drawn where it is");
            Assert.That(after.max.x, Is.EqualTo(before.max.x + 5f).Within(1e-3f), "and the one moved aside");
            Assert.That(new[] { d.toldFamilies, d.queriedNotified, d.invalidations }, Is.EqualTo(new long[] { 2, 2, 0 }), "two families told, their two held bodies asked; not every held one");

            stage.camera.enabled = false;
            yield return null;
            Object.Destroy(drawing);
            yield return null;
            yield return EndWorld(root);
        }
    }
}
