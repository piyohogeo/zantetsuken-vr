using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Input;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The Slash hit detector in a running cut world (DESIGN 19.1.7, 19.1.9, 4.2; T-036, T-040, T-007): what a sweep
    /// hits is decided by the convexes the fragments are made of now, each hit is passed once to the ordinary acceptance,
    /// and whatever that acceptance says -- published, a no-op, an active source passed over, a full budget -- the same
    /// Slash does not try that lineage again while another Slash can.
    /// <para>
    /// The sweeps are given to the detector directly here: what is under test is what a sweep hits and what becomes of
    /// the hit, not the gesture. Nothing asks the driver for a cut or publishes anything; the update loop carries every
    /// accepted cut, as it does in the scene.
    /// </para>
    /// </summary>
    public partial class CutWorldRootPlayModeTests
    {
        private static readonly SlashHitSettings k_hitSettings = new SlashHitSettings
        {
            positiveSeparationImpulse = 0f,
            negativeSeparationImpulse = 0f,
        };

        // One update of a wave in the plane y = height, its span along z over [zFrom, zTo], travelling along x from
        // xFrom to xTo in that update.
        private static SlashSweep Level(long slash, float height, float xFrom, float xTo, float zFrom = -3f, float zTo = 3f)
        {
            return new SlashSweep(
                slash, 0.0, false, new Plane(Vector3.up, -height), Vector3.right, Vector3.forward,
                new Vector3(xFrom, height, zFrom), new Vector3(xFrom, height, zTo),
                new Vector3(xTo, height, zFrom), new Vector3(xTo, height, zTo));
        }

        // One update of a wave in the plane x = at, its span along y over [-3, 3] about the centre, travelling along z
        // over [-3, 3].
        private static SlashSweep Upright(long slash, float at, float centreY = 0f)
        {
            return new SlashSweep(
                slash, 0.0, false, new Plane(Vector3.right, -at), Vector3.forward, Vector3.up,
                new Vector3(at, centreY - 3f, -3f), new Vector3(at, centreY + 3f, -3f),
                new Vector3(at, centreY - 3f, 3f), new Vector3(at, centreY + 3f, 3f));
        }

        private static void Evaluate(SlashHitDetector detector, SlashSweep sweep, params long[] live)
        {
            detector.Evaluate(new[] { sweep }, live);
        }

        private static List<SlashHitConfirmed> Hits(SlashHitDetector detector)
        {
            var hits = new List<SlashHitConfirmed>();
            for (int i = 0; i < detector.HitCount; i++) hits.Add(detector.HitAt(i));
            return hits;
        }

        /// <summary>
        /// A real hit is passed on once; the same Slash sweeping on over its own Provisional pair and then over the two
        /// children its cut published hits nothing more; and another Slash hits each child on its own -- two unrelated
        /// fragments of the same object.
        /// </summary>
        [UnityTest]
        public IEnumerator ARealHit_IsPassedOnOnce_TheSameSlashLeavesItsOwnPieces_AndAnotherSlashHitsEachChild()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);

            Evaluate(detector, Level(1, 0.3f, -3f, 3f), 1);
            List<SlashHitConfirmed> first = Hits(detector);
            Assert.That(first.Count, Is.EqualTo(1), "one real hit");
            Assert.That(first[0].Fragment, Is.EqualTo(body));
            Assert.That(first[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            CutOperationId operation = first[0].Operation;
            yield return null;

            Evaluate(detector, Level(1, 0.3f, -3f, 3f), 1);
            Assert.That(detector.HitCount, Is.Zero, "the Provisional sides are the source it consumed");

            yield return Until(() => root.Geometry.StageOf(operation) == CutGeometryStage.Committed, "the first cut committed");
            LogicalCutOperation made = OperationOf(root, operation);
            Evaluate(detector, Level(1, 0.3f, -3f, 3f), 1);
            Assert.That(detector.HitCount, Is.Zero, "its children are descendants of what it consumed");

            Evaluate(detector, Upright(2, 0.2f), 1, 2);
            List<SlashHitConfirmed> second = Hits(detector);
            Assert.That(second.Count, Is.EqualTo(2), "another Slash hits both children");
            Assert.That(second.ConvertAll(h => h.Fragment), Is.EquivalentTo(new[] { made.positive, made.negative }));
            Assert.That(second.TrueForAll(h => h.Acceptance == ProvisionalCutAcceptance.Published), Is.True,
                "each accepted on its own");

            yield return Until(
                () => root.Geometry.StageOf(second[0].Operation) == CutGeometryStage.Committed
                      && root.Geometry.StageOf(second[1].Operation) == CutGeometryStage.Committed,
                "both child cuts committed");
            Assert.That(root.GeometryFaults, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// A hit the acceptance turns into a no-op -- the plane only touches the box -- consumes the box for that Slash
        /// all the same: the Slash does not try it again, and another Slash that really cuts it is accepted.
        /// </summary>
        [UnityTest]
        public IEnumerator ANoOpHit_IsNotTriedAgain_AndAnotherSlashIsAccepted()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);

            // The top face, exactly: a real hit of the closed convex, and one side with no support.
            Evaluate(detector, Level(1, 1f, -3f, 3f), 1);
            List<SlashHitConfirmed> touching = Hits(detector);
            Assert.That(touching.Count, Is.EqualTo(1), "touching the face is a hit");
            Assert.That(touching[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.EmptySide));
            Assert.That(root.Ledger.IsCurrentTarget(body), Is.True, "and nothing changed");
            yield return null;

            Evaluate(detector, Level(1, 1f, -3f, 3f), 1);
            Assert.That(detector.HitCount, Is.Zero, "the same Slash does not try it again");

            Evaluate(detector, Level(2, 0.3f, -3f, 3f), 1, 2);
            List<SlashHitConfirmed> cut = Hits(detector);
            Assert.That(cut.Count, Is.EqualTo(1));
            Assert.That(cut[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published), "another Slash cuts it");
            yield return Until(() => root.Geometry.StageOf(cut[0].Operation) == CutGeometryStage.Committed, "it committed");
            yield return EndWorld(root);
        }

        /// <summary>
        /// A hit on the Provisional pair of an active cut is observed and passed over by the acceptance -- the source
        /// is active -- and the Slash that made it does not try that source or its children again; a later Slash cuts
        /// the published children.
        /// </summary>
        [UnityTest]
        public IEnumerator AHitOnAnActiveSource_IsPassedOver_AndItsLineageIsNotTriedAgainBySameSlash()
        {
            HoldingExecutor unityJob = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.UnityJob
                    ? unityJob = Held(new HoldingExecutor(new UnityJobWorkExecutor(8)))
                    : null,
                null);
            unityJob.HoldEverything = true;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);

            Evaluate(detector, Level(1, 0.3f, -3f, 3f), 1);
            CutOperationId operation = Hits(detector)[0].Operation;
            yield return null;
            Assert.That(root.Owners.ProvisionalPairCount, Is.EqualTo(1), "the cut is active, its pair standing");

            Evaluate(detector, Upright(2, 0.2f), 1, 2);
            List<SlashHitConfirmed> passed = Hits(detector);
            Assert.That(passed.Count, Is.EqualTo(1), "the two Provisional sides are one source");
            Assert.That(passed[0].Fragment, Is.EqualTo(body));
            Assert.That(passed[0].Side, Is.Not.Zero, "hit on a Provisional side");
            Assert.That(passed[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.NotAccepted));
            Assert.That(passed[0].Admission, Is.EqualTo(LogicalCutAdmission.SourceActive), "passed over, active");
            yield return null;
            Evaluate(detector, Upright(2, 0.2f), 1, 2);
            Assert.That(detector.HitCount, Is.Zero, "and not tried again");

            unityJob.ReleaseEverything();
            yield return Until(() => root.Geometry.StageOf(operation) == CutGeometryStage.Committed, "the first cut committed");
            Evaluate(detector, Upright(2, 0.2f), 1, 2);
            Assert.That(detector.HitCount, Is.Zero, "the children are descendants of what Slash 2 consumed");

            Evaluate(detector, Upright(3, 0.2f), 3);
            List<SlashHitConfirmed> later = Hits(detector);
            Assert.That(later.Count, Is.EqualTo(2));
            Assert.That(later.TrueForAll(h => h.Acceptance == ProvisionalCutAcceptance.Published), Is.True,
                "a later Slash cuts both children");
            yield return Until(
                () => root.Geometry.StageOf(later[0].Operation) == CutGeometryStage.Committed
                      && root.Geometry.StageOf(later[1].Operation) == CutGeometryStage.Committed,
                "both committed");
            yield return EndWorld(root);
        }

        /// <summary>
        /// With room for one incomplete cut, one sweep over two boxes cuts one and is refused for the other; the same
        /// Slash does not come back for it, and a later Slash, after the first cut has committed, is accepted.
        /// </summary>
        [UnityTest]
        public IEnumerator AHitRefusedForAFullBudget_IsNotTriedAgain_AndALaterSlashIsAccepted()
        {
            CutWorldRoot root = NewWorld(
                out Shader _, null, null, profile => SetPrivate(profile, "maxIncompleteCuts", 1));
            LogicalFragmentId left = AddBody(root, new Vector3(-4f, 0f, 0f));
            LogicalFragmentId right = AddBody(root, new Vector3(4f, 0f, 0f));
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);

            Evaluate(detector, Level(1, 0.3f, -8f, 8f), 1);
            List<SlashHitConfirmed> both = Hits(detector);
            Assert.That(both.Count, Is.EqualTo(2), "both boxes are hit");
            SlashHitConfirmed cut = both.Find(h => h.Acceptance == ProvisionalCutAcceptance.Published);
            SlashHitConfirmed full = both.Find(h => h.Acceptance == ProvisionalCutAcceptance.NotAccepted);
            Assert.That(cut.Fragment.IsSet && full.Fragment.IsSet, Is.True, "one accepted, one refused");
            Assert.That(full.Admission, Is.EqualTo(LogicalCutAdmission.Full));
            yield return null;

            Evaluate(detector, Level(1, 0.3f, -8f, 8f), 1);
            Assert.That(detector.HitCount, Is.Zero, "the refused one is not tried again by the same Slash");

            yield return Until(() => root.Geometry.StageOf(cut.Operation) == CutGeometryStage.Committed, "the first cut committed");
            float x = full.Fragment == left ? -4f : 4f;
            Evaluate(detector, Level(2, 0.3f, x - 2f, x + 2f), 1, 2);
            List<SlashHitConfirmed> later = Hits(detector);
            Assert.That(later.Count, Is.EqualTo(1));
            Assert.That(later[0].Fragment, Is.EqualTo(full.Fragment));
            Assert.That(later[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published), "a later Slash is accepted");
            yield return Until(() => root.Geometry.StageOf(later[0].Operation) == CutGeometryStage.Committed, "committed");
            yield return EndWorld(root);
        }

        /// <summary>
        /// Once the first cut's Final physics and its two children are published, a new Slash cuts a child although the
        /// first cut's geometry has not been committed: acceptance does not wait for it, and only the child's geometry
        /// work waits for its ancestor's commit (T-007).
        /// </summary>
        [UnityTest]
        public IEnumerator AFinalChild_IsAcceptedByANewSlash_BeforeItsParentsGeometryIsCommitted()
        {
            HoldingExecutor geometry = null;
            WorkerPoolExecutor pool = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination =>
                {
                    if (destination != WorkDestination.GeometryPool) return null;
                    pool = WorkerPoolExecutor.GeometryPool(2);
                    return geometry = Held(new HoldingExecutor(pool));
                },
                null);
            geometry.HoldEverything = true;
            // This case is about the order of an ancestor's geometry commit and its children's acceptance and publication
            // (T-007), so the driver is given a sufficient Main remainder: the frame budget, the Pending carry-over and a
            // stage predicted over the whole budget are the subject of their own tests.
            root.Driver.RemainingMainSeconds = () => 1.0;
            AddBody(root, Vector3.zero);
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);

            Evaluate(detector, Level(1, 0.3f, -3f, 3f), 1);
            CutOperationId operation = Hits(detector)[0].Operation;
            yield return Until(
                () => root.Ledger.TryGetOperation(operation, out LogicalCutOperation op) && op.state == LogicalCutOperationState.Published,
                "the first cut's Final physics and children were published");
            Assert.That(root.Geometry.StageOf(operation), Is.Not.EqualTo(CutGeometryStage.Committed), "its geometry is held");

            Evaluate(detector, Upright(2, 0.2f), 1, 2);
            List<SlashHitConfirmed> children = Hits(detector);
            Assert.That(children.Count, Is.EqualTo(2));
            Assert.That(children.TrueForAll(h => h.Acceptance == ProvisionalCutAcceptance.Published), Is.True,
                "both children accepted while the parent's geometry is not committed");
            Assert.That(root.Geometry.StageOf(children[0].Operation), Is.Not.EqualTo(CutGeometryStage.Committed));

            geometry.ReleaseEverything();
            yield return Until(
                () => root.Geometry.StageOf(operation) == CutGeometryStage.Committed
                      && root.Geometry.StageOf(children[0].Operation) == CutGeometryStage.Committed
                      && root.Geometry.StageOf(children[1].Operation) == CutGeometryStage.Committed,
                "all three committed, the parent first");
            Assert.That(root.GeometryFaults, Is.Zero);
            yield return EndWorld(root);
            pool.Dispose();
        }

        /// <summary>
        /// T-040: once a wave is flying, it keeps sweeping and hitting while the blade cannot give a usable pose -- the
        /// tracking is lost -- and nothing of the blade itself is a hit.
        /// </summary>
        [UnityTest]
        public IEnumerator AFlyingWave_KeepsHitting_WhileTheBladesTrackingIsLost()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);

            // A forward sweep of the blade, as the core's own tests make one, until it latches.
            var core = new SlashWaveCore(new SlashBlade(new Pose(new Vector3(0f, 0f, 0.02f), Quaternion.Euler(-15f, 0f, 0f)), 0.9f));
            Quaternion upright = Quaternion.Inverse(core.Blade.GripToKatana.rotation);
            var position = new Vector3(0f, 1.4f, 0.3f);
            double time = 0.0;
            long frame = 0;
            const BladeTrackingState tracked = BladeTrackingState.Position | BladeTrackingState.Rotation;
            for (int i = 0; i < 14 && core.WaveCount == 0; i++)
            {
                position += new Vector3(0f, -0.12f, 0f);
                time += 0.011;
                core.Update(new BladePoseSample(++frame, time, position, upright, tracked), Vector3.zero, null, out _);
                detector.Evaluate(core);
                Assert.That(detector.HitCount, Is.Zero, "no box yet, and nothing of the blade is a hit");
            }

            Assert.That(core.WaveCount, Is.EqualTo(1), "the swing latched one wave");
            core.TryGetWave(0, out _, out _, out Vector3 origin, out Vector3 travel, out _, out _, out _, out _, out _, out _);

            // A box ahead on the wave's path, and from here on the tracking is lost.
            AddBody(root, origin + travel * 2.5f);
            yield return null;
            SlashHitConfirmed hit = default;
            for (int i = 0; i < 60 && !hit.Fragment.IsSet; i++)
            {
                time += 0.011;
                SlashInputOutcome outcome = core.Update(
                    new BladePoseSample(++frame, time, Vector3.zero, Quaternion.identity, BladeTrackingState.None),
                    Vector3.zero, null, out _);
                Assert.That(outcome, Is.EqualTo(SlashInputOutcome.PoseUnusable), "the blade gives no pose");
                detector.Evaluate(core);
                if (detector.HitCount > 0) hit = detector.HitAt(0);
            }

            Assert.That(hit.Fragment.IsSet, Is.True, "the flying wave hit the box with the tracking lost");
            Assert.That(hit.Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(core.WaveCount, Is.EqualTo(1), "and no new wave came of the lost blade");
            yield return Until(() => root.Geometry.StageOf(hit.Operation) == CutGeometryStage.Committed, "committed");
            yield return EndWorld(root);
        }

        // ----- the candidates and the exact test read the same present state --------------------------------------------

        /// <summary>
        /// A body moved by the physics is hit where it stands after the step, and not where it stood.
        /// </summary>
        [UnityTest]
        public IEnumerator ABodyMovedByThePhysics_IsHitWhereItStandsNow()
        {
            // Where this case runs: the scenes loaded when it starts, and what in them has a collider.
            UnityEngine.Debug.Log("SLASH HIT MOVED: scenes at start: " + DescribeLoadedScenes());
            CutWorldRoot root = NewWorld(out Shader _);
            // This tests current-shape hit coordinates, with enough budget for same-frame publication.
            root.Driver.RemainingMainSeconds = () => 1;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner owner), Is.True);
            ContactLog contacts = owner.Root.AddComponent<ContactLog>();
            yield return null;
            Vector3 before = owner.Root.transform.position;
            Assert.That(before, Is.EqualTo(Vector3.zero), "the body is where it was placed when it is set moving");
            owner.Body.linearVelocity = new Vector3(0f, 0f, 20f);
            yield return Until(() => owner.Root.transform.position.z > 4f, "the body moved more than the candidate margin");
            Vector3 velocity = owner.Body.linearVelocity;
            owner.Body.linearVelocity = Vector3.zero;
            float z = owner.Root.transform.position.z;
            UnityEngine.Debug.Log("SLASH HIT MOVED: before=" + before.ToString("F4") + " after=" + owner.Root.transform.position.ToString("F4")
                + " rotation=" + owner.Root.transform.rotation.eulerAngles.ToString("F2") + " velocity=" + velocity.ToString("F4")
                + " contacts=[" + string.Join(", ", contacts.Touched) + "]");
            Assert.That(contacts.Touched, Is.Empty, "the body touched nothing on its way");
            var detector = new SlashHitDetector(root, in k_hitSettings);

            Evaluate(detector, Level(1, 0.3f, -3f, 3f, -3f, 3f), 1);
            Assert.That(detector.HitCount, Is.Zero, "nothing where it stood");
            Evaluate(detector, Level(2, 0.3f, -3f, 3f, z - 3f, z + 3f), 2);
            Assert.That(detector.HitCount, Is.EqualTo(1), "hit where it stands");
            Assert.That(detector.HitAt(0).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            yield return Until(() => root.Geometry.StageOf(detector.HitAt(0).Operation) == CutGeometryStage.Committed, "committed");
            yield return EndWorld(root);
        }

        /// <summary>
        /// A body placed somewhere else and turned by its transform, with no step since, is hit where its transform says
        /// -- which is where the next step will have it, and where the cut reads it -- and not where it stood.
        /// </summary>
        [UnityTest]
        public IEnumerator ABodyMovedAndTurnedByItsTransform_IsHitWhereItStandsNow_BeforeAnyStep()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner owner), Is.True);
            var detector = new SlashHitDetector(root, in k_hitSettings);

            // 5 m on and a quarter turn, far past the candidate margin, and evaluated in this same update.
            owner.Root.transform.SetPositionAndRotation(new Vector3(0f, 0f, 5f), Quaternion.Euler(0f, 90f, 30f));
            Evaluate(detector, Level(1, 0.3f, -3f, 3f, -3f, 3f), 1);
            Assert.That(detector.HitCount, Is.Zero, "nothing where it stood");
            Evaluate(detector, Level(2, 0.3f, -3f, 3f, 2f, 8f), 2);
            Assert.That(detector.HitCount, Is.EqualTo(1), "hit where its transform has it");
            Assert.That(detector.HitAt(0).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            yield return Until(() => root.Geometry.StageOf(detector.HitAt(0).Operation) == CutGeometryStage.Committed, "committed");
            yield return EndWorld(root);
        }

        /// <summary>
        /// The Provisional pair a hit has just published is what the next sweep of that same update meets, with no step
        /// in between; and once the handoff has made the children, with the physics paused so that nothing has been
        /// stepped or synchronised since, they are hit where they stand -- one of them moved past the margin by its
        /// transform.
        /// </summary>
        [UnityTest]
        public IEnumerator APairJustPublished_AndChildrenJustHandedOver_AreHitWithNoStepInBetween()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);
            CutPhysicsStep.SetPaused(true);
            try
            {
                Evaluate(detector, Level(1, 0.3f, -3f, 3f), 1);
                CutOperationId operation = Hits(detector)[0].Operation;

                // The same update: the pair stands, nothing has stepped.
                Evaluate(detector, Upright(2, 0.2f), 1, 2);
                List<SlashHitConfirmed> onPair = Hits(detector);
                Assert.That(onPair.Count, Is.EqualTo(1), "the pair just published is met");
                Assert.That(onPair[0].Side, Is.Not.Zero);
                Assert.That(onPair[0].Admission, Is.EqualTo(LogicalCutAdmission.SourceActive));

                long stepBefore = CutPhysicsStep.Clock.StepId;
                yield return Until(
                    () => root.Ledger.TryGetOperation(operation, out LogicalCutOperation op) && op.state != LogicalCutOperationState.Admitted,
                    "the children were handed over");
                Assert.That(CutPhysicsStep.Clock.StepId, Is.EqualTo(stepBefore), "with no step since");
                LogicalCutOperation made = OperationOf(root, operation);
                Assert.That(root.Owners.TryGet(made.positive, out PhysicsFragmentOwner upper), Is.True);
                AssertBoundsHoldTheConvexes(upper.Shape);

                upper.Root.transform.SetPositionAndRotation(upper.Root.transform.position + new Vector3(0f, 0f, 5f), Quaternion.Euler(0f, 45f, 0f));
                Evaluate(detector, Level(3, 0.6f, -3f, 3f, 2f, 8f), 3);
                List<SlashHitConfirmed> moved = Hits(detector);
                Assert.That(moved.Count, Is.EqualTo(1), "the moved child is hit where its transform has it");
                Assert.That(moved[0].Fragment, Is.EqualTo(made.positive));
                Evaluate(detector, Level(4, -0.5f, -3f, 3f), 4);
                List<SlashHitConfirmed> lower = Hits(detector);
                Assert.That(lower.Count, Is.EqualTo(1), "the child that stayed is hit where it stayed");
                Assert.That(lower[0].Fragment, Is.EqualTo(made.negative));
            }
            finally
            {
                CutPhysicsStep.SetPaused(false);
            }

            yield return Until(() => root.Driver.Transactions.Count == 0 || root.Owners.ProvisionalPairCount == 0, "settled");
            yield return EndWorld(root);
        }

        // Every other collider the body touched while it moved, by name and scene.
        private sealed class ContactLog : MonoBehaviour
        {
            internal readonly List<string> Touched = new List<string>();

            private void OnCollisionEnter(Collision collision)
            {
                Note(collision);
            }

            private void OnCollisionStay(Collision collision)
            {
                Note(collision);
            }

            private void Note(Collision collision)
            {
                string name = collision.collider.name + " (" + collision.collider.gameObject.scene.name + ")";
                if (!Touched.Contains(name))
                {
                    Touched.Add(name);
                }
            }
        }

        private static string DescribeLoadedScenes()
        {
            var parts = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                var colliders = new List<string>();
                foreach (GameObject rootObject in scene.GetRootGameObjects())
                {
                    foreach (Collider collider in rootObject.GetComponentsInChildren<Collider>(true))
                    {
                        colliders.Add(collider.name + (collider.attachedRigidbody != null ? "+body" : ""));
                    }
                }

                parts.Add(scene.name + "(" + scene.path + ", loaded=" + scene.isLoaded + ", colliders: " + string.Join("/", colliders) + ")");
            }

            return string.Join("; ", parts);
        }

        // What the candidates are asked with is the shape's own recorded box: every vertex of every convex lies in it.
        private static unsafe void AssertBoundsHoldTheConvexes(PhysicsOwnerShape shape)
        {
            for (int c = 0; c < shape.ConvexCount; c++)
            {
                shape.ConvexBounds(c, out float3 lo, out float3 hi);
                Zantetsu.ConvexCut.ConvexBrepRange range = shape.Convex(c);
                Zantetsu.ConvexCut.ConvexBrepBank bank = shape.BankOf(c);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    float3 p = bank.vertices[range.vertexBase + v];
                    Assert.That(math.all(p >= lo) && math.all(p <= hi), Is.True, "convex " + c + " vertex " + v + " lies in its box");
                }
            }
        }
    }
}
