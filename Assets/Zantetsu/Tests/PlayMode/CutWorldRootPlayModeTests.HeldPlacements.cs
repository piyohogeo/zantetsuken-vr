using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
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
    }
}
