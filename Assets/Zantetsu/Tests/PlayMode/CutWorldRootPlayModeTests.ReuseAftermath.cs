using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The reused-slot check's wait for everything its one Slash set going (TL, 2026-10-01; MobPlanSlashAftermath, the Player
    /// check's own follower). In the always-kinematic hull world, one level sweep given once to the ordinary detector meets a
    /// prepared character and a building: the character's cut is published and committed first, while the building's hit is
    /// still being prepared, published, dropped and its hull updated -- the follower is not done until all of that has ended.
    /// With the hull's update refused (the old hull kept) it still ends. A wait shorter than the building's work times out and
    /// is not done.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        // A box character (the fixture's box as its one convex, a renderer skinned to one bone) prepared for its first cut.
        private IEnumerator PrepareBoxCharacter(CutWorldRoot root, Vector3 at, List<VpPreparedCharacterCut> into)
        {
            GameObject actor = Track(new GameObject("Aftermath character"));
            actor.transform.position = at;
            var rendererObject = new GameObject("Aftermath character renderer");
            rendererObject.transform.SetParent(actor.transform, false);
            Transform bone = new GameObject("Aftermath character bone").transform;
            bone.SetParent(actor.transform, false);
            var r = rendererObject.AddComponent<SkinnedMeshRenderer>();
            r.quality = SkinQuality.Bone4;
            // The display input of the character tests' own rig (a tetrahedron the input gate takes); the hit shape is the box.
            r.sharedMesh = Track(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
                uv = new[] { new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(.5f, .5f) },
                triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 },
                bindposes = new[] { Matrix4x4.identity },
                boneWeights = new[] { new BoneWeight { weight0 = 1 }, new BoneWeight { weight0 = 1 }, new BoneWeight { weight0 = 1 }, new BoneWeight { weight0 = 1 } },
            });
            r.bones = new[] { bone };
            var motion = actor.AddComponent<Rigidbody>();
            motion.isKinematic = true;
            motion.useGravity = false;
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            var warm = new VpPhysicsColdPreparation();
            _disposables.Add(warm);
            int[] topology = { 0, 1, 2, 3 };
            Assert.That(VpDirectSkinInput.TryCreate(r, topology, 4, root.CutInputConnectivity, out VpDirectSkinInput probe), Is.True, "the display input is taken");
            probe.Dispose();
            Assert.That(root.TryPrepareCharacterCut(r, topology, 4, shape.BankOf(0), new[] { shape.Convex(0) }, new[] { bone }, warm, actor, motion, out VpPreparedCharacterCut handle), Is.True, "the character prepared");
            for (int i = 0; i < 120 && !(handle.IsReady || handle.TryFinishPreparation()); i++) yield return null;
            Assert.That(handle.IsReady, Is.True, "and ready");
            into.Add(handle);
        }

        // The building beside the character, one sweep over both; the follower made as the check makes it.
        private IEnumerator OneSlashOverBoth(bool refuseHull, List<object> result)
        {
            // A character's display draws with the material of source index 0 (the box's are the side's and the end's).
            CutWorldRoot root = NewKinematicWorld(beforeAwake: r =>
            {
                var field = typeof(CutWorldRoot).GetField("materials", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var bindings = new List<CutWorldRoot.MaterialBinding>((CutWorldRoot.MaterialBinding[])field.GetValue(r));
                bindings.Add(new CutWorldRoot.MaterialBinding { sourceIndex = 0, material = Track(new Material(Shader.Find("Zantetsu/VP Indexed Indirect Unlit")) { name = "character" }) });
                field.SetValue(r, bindings.ToArray());
            });
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var characters = new List<VpPreparedCharacterCut>();
            yield return PrepareBoxCharacter(root, new Vector3(3.5f, 0f, 0f), characters);
            VpPreparedCharacterCut character = characters[0];
            var detector = new SlashHitDetector(root, in k_hitSettings);
            detector.AddCharacter(character);
            yield return null;
            Mesh hullBefore = building.Collider.sharedMesh;
            h.refuseHullForTest = refuseHull;
            int opsBefore = root.Ledger.OperationCount, hullHitsBefore = h.Hits.Count;
            Evaluate(detector, Level(7, 0.2f, -3f, 5.5f), 7);
            var hits = new List<SlashHitConfirmed>();
            for (int i = 0; i < detector.HitCount; i++) hits.Add(detector.HitAt(i));
            foreach (SlashHitConfirmed hit in hits) TestContext.Out.WriteLine("hit fragment " + hit.Fragment.value + " => " + hit.Acceptance + " / " + hit.Admission + " operation " + hit.Operation.value);
            Assert.That(hits.Count, Is.EqualTo(2), "one sweep met the character and the building");
            Assert.That(character.Source.IsSet && hits.Exists(x => x.Fragment == character.Source && x.Acceptance == ProvisionalCutAcceptance.Published), Is.True, "the character's cut published at once");
            Assert.That(hits.Exists(x => x.Fragment != character.Source && x.Acceptance == ProvisionalCutAcceptance.Pending), Is.True, "the building's hit pending");
            var aftermath = new MobPlanSlashAftermath(root, 7, hits, characters, opsBefore, hullHitsBefore, null);
            result.Add(root); result.Add(aftermath); result.Add(character); result.Add(hullBefore); result.Add(building);
        }

        /// <summary>
        /// **One Slash over a character and a building: the character's commit comes first, and the follower waits for the
        /// building's display commit, its drop and its hull update.**
        /// </summary>
        [UnityTest]
        public IEnumerator ReuseAftermath_CharacterCommitsFirst_TheWaitGoesOnToTheBuildingsEnd()
        {
            var made = new List<object>();
            yield return OneSlashOverBoth(false, made);
            var root = (CutWorldRoot)made[0];
            var aftermath = (MobPlanSlashAftermath)made[1];
            var character = (VpPreparedCharacterCut)made[2];
            BuildingHullFusion h = root.Hulls;
            int characterCommitted = -1, ended = -1;
            bool buildingWorkAtCharacterCommit = false;
            float until = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < until)
            {
                bool done = aftermath.Poll();
                if (characterCommitted < 0 && character.Operation.IsSet && root.Geometry.StageOf(character.Operation) == CutGeometryStage.Committed)
                {
                    characterCommitted = Time.frameCount;
                    buildingWorkAtCharacterCommit = !done;
                    TestContext.Out.WriteLine("the character committed at frame " + characterCommitted + ": " + aftermath.Describe());
                }

                if (done) { ended = Time.frameCount; break; }
                yield return null;
            }

            TestContext.Out.WriteLine("ended at frame " + ended + ": " + aftermath.Describe());
            Assert.That(characterCommitted, Is.GreaterThanOrEqualTo(0), "the character's cut committed");
            Assert.That(buildingWorkAtCharacterCommit, Is.True, "with the building's work still going then");
            Assert.That(ended, Is.GreaterThan(characterCommitted), "the follower ended later");
            Assert.That(aftermath.Passed || (aftermath.Finished && aftermath.Acceptable), Is.True, "acceptably");
            Assert.That(h.Hits[h.Hits.Count - 1].outcome, Is.EqualTo("Published"));
            foreach (CutOperationId op in h.Hits[h.Hits.Count - 1].displayOperations) Assert.That(root.Geometry.StageOf(op), Is.EqualTo(CutGeometryStage.Committed), "the building's display operation committed");
            Assert.That(h.AnimationsCompleted, Is.EqualTo(1), "its drop completed");
            Assert.That(h.HullUpdatesAdopted, Is.EqualTo(1), "its hull exchanged");
            Assert.That(h.AnimationsRunning + h.HullUpdatesInFlight + h.WaitingHullRequests + h.DisplayOperationsOpen + h.CutsInProgress, Is.Zero, "nothing left running or waiting");
            character.Dispose();
            yield return EndWorld(root);
        }

        /// <summary>**The hull's update refused: the old hull kept, and the follower still ends.**</summary>
        [UnityTest]
        public IEnumerator ReuseAftermath_HullUpdateRefused_StillEnds()
        {
            var made = new List<object>();
            yield return OneSlashOverBoth(true, made);
            var root = (CutWorldRoot)made[0];
            var aftermath = (MobPlanSlashAftermath)made[1];
            var hullBefore = (Mesh)made[3];
            var building = (HullGroup)made[4];
            yield return aftermath.Wait(30f);
            TestContext.Out.WriteLine("after " + aftermath.WaitedSeconds.ToString("F2") + " s: " + aftermath.Describe());
            Assert.That(aftermath.Passed, Is.True, "ended within the wait, acceptably");
            Assert.That(root.Hulls.HullUpdatesRefused, Is.GreaterThanOrEqualTo(1), "the hull's update refused");
            Assert.That(building.Collider.sharedMesh, Is.SameAs(hullBefore), "the old hull kept");
            ((VpPreparedCharacterCut)made[2]).Dispose();
            yield return EndWorld(root);
        }

        /// <summary>**A wait shorter than the building's work times out and is not done; what is left is named.**</summary>
        [UnityTest]
        public IEnumerator ReuseAftermath_AShortWait_TimesOut_NotDone()
        {
            var made = new List<object>();
            yield return OneSlashOverBoth(false, made);
            var root = (CutWorldRoot)made[0];
            var aftermath = (MobPlanSlashAftermath)made[1];
            yield return aftermath.Wait(0.01f);
            string left = aftermath.Describe();
            TestContext.Out.WriteLine("after " + aftermath.WaitedSeconds.ToString("F3") + " s: " + left);
            Assert.That(aftermath.TimedOut && !aftermath.Finished && !aftermath.Passed, Is.True, "timed out, not finished, not passed");
            Assert.That(left, Does.Contain("NOT finished"));
            // The world's own end after everything has ended (not part of the judgement).
            yield return UntilWithin(() => root.Hulls.IsSettled && root.Hulls.AnimationsRunning == 0 && root.Hulls.HullUpdatesInFlight == 0, 30f, "the building's work ended");
            ((VpPreparedCharacterCut)made[2]).Dispose();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The world ending while the building's hit is still being worked is no end:** the building's hit abandoned by the
        /// end, the world's end recorded, finished and not passed.
        /// </summary>
        [UnityTest]
        public IEnumerator ReuseAftermath_TheWorldEndingWithTheBuildingsWorkLeft_IsNoEnd()
        {
            var made = new List<object>();
            yield return OneSlashOverBoth(false, made);
            var root = (CutWorldRoot)made[0];
            var aftermath = (MobPlanSlashAftermath)made[1];
            Assert.That(aftermath.Poll(), Is.False, "the building's hit still being worked");
            BuildingHullFusion.HullHit record = root.Hulls.Hits[root.Hulls.Hits.Count - 1];
            root.Shutdown();
            Assert.That(aftermath.Poll(), Is.True, "finished: nothing more will come");
            TestContext.Out.WriteLine("at the end: " + aftermath.Describe());
            Assert.That(aftermath.WorldEnded && !aftermath.Passed && !aftermath.Acceptable, Is.True, "the world's end is no end");
            // The ending's own frames: the building's hit answered by the end, read while its record stands.
            for (int i = 0; i < 120 && record.IsPending && !root.IsReleased; i++)
            {
                if (root.Shutdown()) break;
                aftermath.Poll();
                yield return null;
            }

            aftermath.Poll();
            TestContext.Out.WriteLine("the building's hit at the end: " + (record.outcome ?? "PENDING") + "; " + aftermath.Describe());
            if (record.outcome != null && record.outcome.StartsWith("Abandoned")) Assert.That(string.Join("; ", aftermath.Anomalies), Does.Contain("Abandoned"), "the building's abandoned hit is an anomaly, not read past");
            ((VpPreparedCharacterCut)made[2]).Dispose();
            yield return EndWorld(root);
            aftermath.Poll();
            TestContext.Out.WriteLine("after the world's end: " + aftermath.Describe());
            Assert.That(aftermath.Passed, Is.False);
        }

        /// <summary>
        /// **An ordinary refusal that leaves an operation is followed, not ended at once.** A body whose anchor lies near
        /// the largest float and an ordinary plane through the box whose normal makes that anchor's distance overflow: the ledger refuses the distribution, the driver
        /// answers AnchorsRefused and leaves the cut its source's active operation, which only the ending closes (DESIGN
        /// 7.1.1; the driver's own contract). The follower, given that answer as the detector would give it (with the
        /// operation), is not finished and not passed while that operation is open; its wait runs out, the operation left is
        /// named, and the world's ordinary end then closes it. The refusal itself is no anomaly: a refusal with no operation
        /// left ends at once and passes.
        /// </summary>
        [UnityTest]
        public IEnumerator ReuseAftermath_AnchorsRefusedLeavingItsOperation_IsFollowed_NotPassedEarly()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxGeometry(root.Storage, default);
            var actor = TrackActor(new GameObject("Far-anchored body"));
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);
            Assert.That(root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, new[] { new float3(3e38f, 0f, 0f) }, false, out LogicalFragmentId fragment), Is.True);
            _registered.Add(actor);
            yield return null;

            int opsBefore = root.Ledger.OperationCount;
            var ask = new ProvisionalCutAsk { source = fragment, plane = new float4(1.5f, 1f, 0f, 0f), renderAnchor = float3.zero, slashId = 9 };
            ProvisionalCutAcceptance answer = root.Driver.RequestCut(in ask, out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission);
            Assert.That(answer, Is.EqualTo(ProvisionalCutAcceptance.AnchorsRefused));
            Assert.That(root.Ledger.TryGetActiveOperation(fragment, out CutOperationId left) && left.Equals(transaction.Operation), Is.True, "the cut left as the source's active operation");
            var hit = new SlashHitConfirmed(9, 0.0, false, fragment, 0f, answer, admission, transaction.Operation);
            var aftermath = new MobPlanSlashAftermath(root, 9, new[] { hit }, new VpPreparedCharacterCut[0], opsBefore, 0, null);
            Assert.That(aftermath.Poll(), Is.False, "not finished while the operation is open");
            Assert.That(aftermath.Passed, Is.False, "and not passed early");
            Assert.That(aftermath.Acceptable, Is.True, "the refusal itself is no anomaly");
            yield return aftermath.Wait(0.5f);
            string described = aftermath.Describe();
            TestContext.Out.WriteLine("after " + aftermath.WaitedSeconds.ToString("F2") + " s: " + described);
            Assert.That(aftermath.TimedOut && !aftermath.Finished && !aftermath.Passed && !aftermath.Acceptable, Is.True, "timed out with the operation open: not passed");
            Assert.That(described, Does.Contain("AnchorsRefused) that left operation " + transaction.Operation.value + " Admitted"), "the operation left is named");

            // A refusal that left nothing ends at once, acceptably.
            var refusal = new MobPlanSlashAftermath(root, 10, new[] { new SlashHitConfirmed(10, 0.0, false, fragment, 0f, ProvisionalCutAcceptance.EmptySide, LogicalCutAdmission.NoOp, default) }, new VpPreparedCharacterCut[0], root.Ledger.OperationCount, 0, null);
            Assert.That(refusal.Poll() && refusal.Passed, Is.True, refusal.Describe());

            // The ordinary end closes what the ledger still has (the driver's record is the ending's to close).
            yield return EndWorld(root);
            aftermath.Poll();
            TestContext.Out.WriteLine("after the world's end: " + aftermath.Describe());
            Assert.That(aftermath.Passed, Is.False);
        }
    }
}
