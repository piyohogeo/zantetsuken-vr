using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// Pose Table playback on bones (DESIGN 19.3, D-135): once per frame, the pose of the frame's resolved Source Time
    /// is on the bones, and a withdrawn character stops.
    /// </summary>
    public class PoseTablePlayerPlayModeTests
    {
        private readonly List<UnityEngine.Object> _made = new List<UnityEngine.Object>();

        // Bones at the given paths ("a" and "a/b" when none are given), three samples at 2 Hz over one looping second:
        // bone i at sample k is at (k, i, 0).
        internal static byte[] Table(params string[] paths)
        {
            if (paths.Length == 0) paths = new[] { "a", "a/b" };
            int n = paths.Length;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Encoding.ASCII.GetBytes("ZPTAB003"));
                foreach (string text in new[] { "c", "clip-id", "clip", "s", "h", "v", "k" }) writer.Write(text);
                writer.Write(1f); writer.Write(2f); writer.Write(3); writer.Write(false); writer.Write(true);
                writer.Write(1f); writer.Write(1f); writer.Write(1e-5f); writer.Write(0f);
                writer.Write(0); writer.Write(0); writer.Write(0);
                writer.Write(3); writer.Write(0f); writer.Write(0.5f); writer.Write(1f);
                writer.Write(n); foreach (string path in paths) writer.Write(path);
                writer.Write(3 * n); for (int k = 0; k < 3; k++) for (int i = 0; i < n; i++) { writer.Write((float)k); writer.Write((float)i); writer.Write(0f); }
                writer.Write(3 * n); for (int k = 0; k < 3 * n; k++) { writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f); }
                writer.Write(0); writer.Write(0); writer.Write(string.Empty); writer.Write(0); writer.Write(0);
                writer.Flush();
                return stream.ToArray();
            }
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            foreach (UnityEngine.Object made in _made) if (made != null) UnityEngine.Object.Destroy(made);
            _made.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator EachFrame_ThePoseOfItsSourceTimeIsOnTheBones_AndAWithdrawnCharacterStops()
        {
            var root = new GameObject("pose root");
            _made.Add(root);
            Transform a = new GameObject("a").transform;
            a.SetParent(root.transform, false);
            Transform b = new GameObject("b").transform;
            b.SetParent(a, false);
            var asset = new TextAsset(Table());
            _made.Add(asset);
            PoseTablePlayer player = root.AddComponent<PoseTablePlayer>();
            player.Configure(asset, root.transform, 0.0);
            Assert.That(player.Table, Is.Not.Null, player.BindError);
            Assert.That(player.ResolvedBoneCount, Is.EqualTo(2));

            for (int i = 0; i < 5; i++)
            {
                yield return null;
                Assert.That(player.AppliedFrame, Is.EqualTo(Time.frameCount), "applied in this frame's update");
                var positions = new Vector3[2];
                var rotations = new Quaternion[2];
                Assert.That(player.Table.TryEvaluate(player.AppliedSourceTime, positions, rotations), Is.True);
                Assert.That(a.localPosition, Is.EqualTo(positions[0]));
                Assert.That(b.localPosition, Is.EqualTo(positions[1]), "the pose of the Source Time it reports");
                Assert.That(player.AppliedSourceTime, Is.EqualTo(player.Table.ResolveSourceTime(player.AppliedTargetTime)));
            }

            root.SetActive(false);
            int last = player.AppliedFrame;
            Vector3 held = b.localPosition;
            yield return null;
            yield return null;
            Assert.That(player.AppliedFrame, Is.EqualTo(last), "a withdrawn character is not played any more");
            Assert.That(b.localPosition, Is.EqualTo(held), "its bones keep the last pose");
        }

        // root / a / b, with c beside a: the character's bones.
        private GameObject Rig(out Transform a, out Transform b, out Transform c)
        {
            var root = new GameObject("pose root");
            _made.Add(root);
            a = new GameObject("a").transform;
            a.SetParent(root.transform, false);
            b = new GameObject("b").transform;
            b.SetParent(a, false);
            c = new GameObject("c").transform;
            c.SetParent(root.transform, false);
            return root;
        }

        private PoseTablePlayer Player(GameObject root, bool requireBones, params string[] paths)
        {
            var asset = new TextAsset(Table(paths));
            _made.Add(asset);
            PoseTablePlayer player = root.AddComponent<PoseTablePlayer>();
            player.Configure(asset, root.transform, 0.0, requireBones);
            return player;
        }

        [UnityTest]
        public IEnumerator ARequiredBoneTheTableDoesNotDrive_IsRefused_AndNoPartialPoseIsApplied()
        {
            GameObject root = Rig(out Transform a, out Transform b, out Transform c);
            PoseTablePlayer player = Player(root, true, "a", "a/b");
            Assert.That(player.Table, Is.Not.Null, "the table itself binds");
            yield return null;
            Assert.That(player.AppliedFrame, Is.EqualTo(-1), "nothing is played before the bones are confirmed");

            Assert.That(player.TryRequireBones(new[] { a, b, c }, out string missing), Is.False);
            Assert.That(missing, Does.Contain("c"), "the bone the table does not drive is named");
            Assert.That(player.BonesConfirmed, Is.False);
            Assert.That(player.Table, Is.Null, "the table is let go");
            Assert.That(player.BindError, Does.Contain("c"));
            for (int i = 0; i < 3; i++) yield return null;
            Assert.That(player.AppliedFrame, Is.EqualTo(-1), "no partial pose, then or later");
            Assert.That(a.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(b.localPosition, Is.EqualTo(Vector3.zero));

            // A table that did not bind is refused the same way.
            GameObject other = Rig(out Transform a2, out Transform b2, out Transform _);
            var broken = new TextAsset(new byte[] { 1, 2, 3 });
            _made.Add(broken);
            PoseTablePlayer unread = other.AddComponent<PoseTablePlayer>();
            unread.Configure(broken, other.transform, 0.0, true);
            Assert.That(unread.TryRequireBones(new[] { a2, b2 }, out string why), Is.False);
            Assert.That(why, Does.Contain("not bound"));
            yield return null;
            Assert.That(unread.AppliedFrame, Is.EqualTo(-1));
        }

        [UnityTest]
        public IEnumerator AuxiliaryNodesNoOneNeeds_MayBeLeftOut_AndThePoseIsApplied()
        {
            // The table has an entry with no Transform ("a/widget"); the hierarchy has a node the table does not drive
            // (c). Neither is a bone the character needs.
            GameObject root = Rig(out Transform a, out Transform b, out Transform c);
            PoseTablePlayer player = Player(root, true, "a", "a/widget", "a/b");
            Assert.That(player.UnresolvedBoneCount, Is.EqualTo(1));
            Assert.That(player.TryRequireBones(new[] { a, b }, out string missing), Is.True, missing);
            Assert.That(player.BonesConfirmed, Is.True);
            yield return null;
            Assert.That(player.AppliedFrame, Is.EqualTo(Time.frameCount), "played once confirmed");
            var positions = new Vector3[3];
            var rotations = new Quaternion[3];
            Assert.That(player.Table.TryEvaluate(player.AppliedSourceTime, positions, rotations), Is.True);
            Assert.That(b.localPosition, Is.EqualTo(positions[2]));
            Assert.That(c.localPosition, Is.EqualTo(Vector3.zero), "the node no one needs is left as it is");
        }

        private const string IntakeJson =
            "{\"assets\":[{\"family\":\"character-casual\",\"objectName\":\"U8 bone check mesh\",\"topologyMap\":[0],\"topologyCount\":1}]}";

        private const string HullsJson =
            "{\"hulls\":[{\"boneName\":\"b\",\"rendererBindVertices\":[0,0,0],\"faceOffsets\":[0],\"faceIndices\":[]}]}";

        // A character on the rig, its renderer skinned to the given bones, prepared at its Start.
        private Zantetsu.Sandbox.SandboxNpcCharacter Character(GameObject root, Transform[] bones, int weightedCount = -1)
        {
            var rendererObject = new GameObject("renderer");
            rendererObject.transform.SetParent(root.transform, false);
            var skin = rendererObject.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh { name = "U8 bone check mesh" };
            _made.Add(mesh);
            int count = weightedCount < 0 ? bones.Length : weightedCount;
            mesh.vertices = new Vector3[count];
            var weights = new BoneWeight[count];
            var binds = new Matrix4x4[bones.Length];
            for (int i = 0; i < count; i++) weights[i] = new BoneWeight { boneIndex0 = i, weight0 = 1 };
            for (int i = 0; i < binds.Length; i++) binds[i] = Matrix4x4.identity;
            mesh.boneWeights = weights; mesh.bindposes = binds;
            skin.sharedMesh = mesh;
            skin.bones = bones;
            var intake = new TextAsset(IntakeJson);
            var hulls = new TextAsset(HullsJson);
            _made.Add(intake);
            _made.Add(hulls);
            var setup = new GameObject("character setup");
            _made.Add(setup);
            setup.SetActive(false);
            var character = setup.AddComponent<Zantetsu.Sandbox.SandboxNpcCharacter>();
            void Set(string field, object value) => typeof(Zantetsu.Sandbox.SandboxNpcCharacter)
                .GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(character, value);
            Set("characterRoot", root);
            Set("intake", intake);
            Set("hulls", hulls);
            setup.SetActive(true);
            return character;
        }

        [UnityTest]
        public IEnumerator ACharacterWhoseBonesTheTableDoesNotDrive_IsNotPrepared_AndNeverATarget()
        {
            GameObject root = Rig(out Transform a, out Transform b, out Transform c);
            PoseTablePlayer player = Player(root, true, "a", "a/b");
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("not prepared: the Pose Table does not drive the character's bones: .*c"));
            var character = Character(root, new[] { a, b, c });
            for (int i = 0; i < 3; i++) yield return null;
            Assert.That(character.Failure, Does.Contain("does not drive"));
            Assert.That(character.Handle, Is.Null, "nothing was prepared");
            Assert.That(character.IsTarget, Is.False, "never a hit target");
            Assert.That(player.AppliedFrame, Is.EqualTo(-1), "and no pose was applied");
        }

        [UnityTest]
        public IEnumerator MissingZeroWeightRendererBone_DoesNotRequireATableChannel()
        {
            GameObject root = Rig(out Transform a, out Transform b, out Transform c);
            PoseTablePlayer player = Player(root, true, "a", "a/b");
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("not prepared: needs a built world"));
            Character(root, new[] { a, b, c }, 2);
            yield return null;
            Assert.That(player.BonesConfirmed, Is.True);
        }

        [UnityTest]
        public IEnumerator ACharacterWhoseBonesTheTableDrives_PassesTheBoneCheck_WithAnAuxiliaryNodeLeftOut()
        {
            GameObject root = Rig(out Transform a, out Transform b, out Transform _);
            PoseTablePlayer player = Player(root, true, "a", "a/widget", "a/b");
            // No world is given here: past the bone check, the preparation stops at the world, which is what shows the
            // bones were accepted. (A real preparation and registration is the Player check's.)
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("not prepared: needs a built world"));
            var character = Character(root, new[] { a, b });
            yield return null;
            Assert.That(player.BonesConfirmed, Is.True, "the character's bones are all driven");
            Assert.That(character.Failure, Does.Contain("needs a built world"));
            Assert.That(character.IsTarget, Is.False);
        }
    }

    /// <summary>
    /// A character before its first cut, as the Slash hit detector meets it (DESIGN 19.1.7, 19.1.9, Phase 4.52): the
    /// hit and the cut read the bones as they stand when the wave arrives -- not as they stood at its latch -- the
    /// character is identified as its fragment at its first real hit, a No-op is not retried by that Slash, its cut
    /// consumes the whole lineage for that Slash, and another Slash cuts a child.
    /// </summary>
    // A stand-in for the update that poses a character's bones: it counts its own updates.
    public sealed class WithdrawalPoseStandIn : MonoBehaviour
    {
        public int Updates;

        private void Update() => Updates++;
    }

    // A component with an OnDisable of its own, which a withdrawal of parts would not run.
    public sealed class WithdrawalDisableListener : MonoBehaviour
    {
        public int Disabled;

        private void OnDisable() => Disabled++;
    }

    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        private static readonly SlashHitSettings k_characterHitSettings = default;

        private static SlashSweep CharacterLevel(long slash, float height, float zFrom, float zTo)
        {
            return new SlashSweep(
                slash, 0.0, false, new Plane(Vector3.up, -height), Vector3.right, Vector3.forward,
                new Vector3(-3f, height, zFrom), new Vector3(-3f, height, zTo),
                new Vector3(3f, height, zFrom), new Vector3(3f, height, zTo));
        }

        private static SlashSweep CharacterUpright(long slash, float x)
        {
            return new SlashSweep(
                slash, 0.0, false, new Plane(Vector3.right, -x), Vector3.forward, Vector3.up,
                new Vector3(x, -3f, -3f), new Vector3(x, 3f, -3f), new Vector3(x, -3f, 3f), new Vector3(x, 3f, 3f));
        }

        // A character whose bone is its own Transform under the root, apart from the renderer, so a pose moves the
        // convex without moving the renderer's frame.
        private VpPreparedCharacterCut PrepareBonedCharacter(out Transform bone, out GameObject root, bool motionOnItsOwn = false)
        {
            root = ColdTrack(new GameObject("U8 character root"));
            var rendererObject = new GameObject("U8 character renderer");
            rendererObject.transform.SetParent(root.transform, false);
            bone = new GameObject("U8 character bone").transform;
            bone.SetParent(root.transform, false);
            var r = rendererObject.AddComponent<SkinnedMeshRenderer>();
            r.quality = SkinQuality.Bone4;
            r.sharedMesh = ColdTrack(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                normals = Enumerable.Repeat(Vector3.up, 4).ToArray(),
                uv = Enumerable.Repeat(new Vector2(.5f, .5f), 4).ToArray(),
                triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 },
                bindposes = new[] { Matrix4x4.identity },
                boneWeights = Enumerable.Repeat(new BoneWeight { weight0 = 1 }, 4).ToArray(),
            });
            r.bones = new[] { bone };
            Rigidbody motion;
            if (motionOnItsOwn)
            {
                var motionObject = new GameObject("U8 character motion body");
                motionObject.transform.SetParent(root.transform, false);
                motion = motionObject.AddComponent<Rigidbody>();
            }
            else
            {
                motion = root.AddComponent<Rigidbody>();
            }

            motion.isKinematic = true;
            motion.useGravity = false;
            motion.mass = 12;
            motion.automaticCenterOfMass = motion.automaticInertiaTensor = false;
            motion.inertiaTensor = Vector3.one * 4;
            if (_vertices.IsCreated) _disposables.Insert(0, new EarlierBank { arrays = new IDisposable[] { _vertices, _faceOffsets, _faceIndices, _faceEdges, _edges } });
            var source = NewAuthoredShape(1);
            CompleteRequestBoxEdges(source);
            Assert.That(coldWorld.TryPrepareCharacterCut(r, new[] { 0, 1, 2, 3 }, 4, source.BankOf(0), new[] { source.Convex(0) },
                new[] { bone }, coldWarm, root, motion, out var handle), Is.True);
            coldHandles.Add(handle);
            return handle;
        }

        private static List<SlashHitConfirmed> Evaluate(SlashHitDetector detector, SlashSweep sweep, params long[] live)
        {
            detector.Evaluate(new[] { sweep }, live);
            var hits = new List<SlashHitConfirmed>();
            for (int i = 0; i < detector.HitCount; i++) hits.Add(detector.HitAt(i));
            return hits;
        }

        private IEnumerator UntilCommitted(CutOperationId operation)
        {
            for (int i = 0; i < 600 && coldWorld.Geometry.StageOf(operation) != CutGeometryStage.Committed; i++) yield return null;
            Assert.That(coldWorld.Geometry.StageOf(operation), Is.EqualTo(CutGeometryStage.Committed), "the cut committed");
        }

        [UnityTest]
        public IEnumerator BudgetPending_LiveMainClockDefersAndProductUpdatesResume()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            bone.localPosition = new Vector3(0, 0, 5);
            // Deliberately exhaust this real frame in the test, without a budget override or loop re-entry.
            System.Threading.Thread.Sleep((int)(CutPhysicsStep.MainBudgetSeconds * 1000) + 2);
            Assert.That(CutPhysicsStep.RemainingMainSeconds, Is.LessThanOrEqualTo(0));
            var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending));
            var pending = coldWorld.Driver.TransactionOf(hits[0].Operation);
            Assert.That(pending.Candidate, Is.Null);
            Assert.That(root.activeInHierarchy, Is.True);
            yield return UntilCommitted(hits[0].Operation);
            Assert.That(root.activeInHierarchy, Is.False);
            Assert.That(pending.HoldsInput, Is.False);
        }

        [UnityTest]
        public IEnumerator BudgetPending_CharacterHitResumesThroughDriverUpdates_AfterHandleDisposal()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            coldWorld.Driver.RemainingMainSeconds = () => 0;
            bone.localPosition = new Vector3(0, 0, 5);
            var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
            Assert.That(hits.Count, Is.EqualTo(1));
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending));
            var pending = coldWorld.Driver.TransactionOf(hits[0].Operation);
            Assert.That(handle.IsDisposed, Is.True, "the normal character request finishes its handle");
            Assert.That(pending.HoldsInput, Is.True, "the admitted transaction, not the handle, holds the input");
            Assert.That(root.activeInHierarchy, Is.True, "withdrawal waits for publication");
            yield return null;
            yield return null;
            Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Accepted));
            Assert.That(pending.InputShape.IsFreed, Is.False);
            Assert.That(coldWorld.Display.RenderFragmentCount, Is.GreaterThan(0), "the accepted cut input supplies the pending display");
            Assert.That(root.GetComponentInChildren<SkinnedMeshRenderer>().enabled, Is.False, "the original renderer must not duplicate the cut input");
            Assert.That(Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1), Is.Empty, "no second acceptance");
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            // No direct Advance or RequestCut here: the product Update/LateUpdate/after-rendering path resumes it.
            yield return UntilCommitted(hits[0].Operation);
            Assert.That(root.activeInHierarchy, Is.False);
            Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.HandedOff));
            Assert.That(pending.HoldsInput, Is.False);
            yield return null;
            Assert.That(coldWorld.Display.RenderFragmentCount, Is.GreaterThan(0), "published children replace the original renderer");
        }

        [UnityTest]
        public IEnumerator U8_ACharacterIsHitAndCutWhereItsBonesStandWhenTheWaveArrives()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            Assert.That(coldWorld.Ledger.FragmentCount, Is.Zero, "the prepared character is not yet a fragment");

            // The Slash latched while the bone stood at the root; by the time its wave arrives, the pose has moved it.
            bone.SetLocalPositionAndRotation(new Vector3(0f, 0f, 5f), Quaternion.Euler(0f, 0f, 30f));
            Assert.That(Evaluate(detector, CharacterLevel(1, 0.3f, -3f, 3f), 1), Is.Empty, "nothing where it stood at the latch");
            Assert.That(handle.Source.IsSet, Is.False, "and nothing identified");

            List<SlashHitConfirmed> hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
            Assert.That(hits.Count, Is.EqualTo(1), "hit where the bone stands now");
            Assert.That(hits[0].Fragment, Is.EqualTo(handle.Source));
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(root.activeInHierarchy, Is.False, "the character left in the frame its cut was published");

            // The cut posed the convex with the same bone: its two sides stand about z = 5 in the renderer's frame.
            Assert.That(coldWorld.Owners.TryGetProvisionalOf(handle.Source, out ProvisionalOwnerPair pair), Is.True);
            pair.PositiveShape.TryLocalBounds(out float3 plo, out float3 phi);
            pair.NegativeShape.TryLocalBounds(out float3 nlo, out float3 nhi);
            float centreZ = (math.min(plo.z, nlo.z) + math.max(phi.z, nhi.z)) * 0.5f;
            Assert.That(centreZ, Is.EqualTo(5f).Within(0.5f), "the pose at the hit, not at the latch");
            Assert.That(coldWorld.Ledger.TryGetOperation(hits[0].Operation, out LogicalCutOperation op), Is.True);
            Assert.That(math.abs(op.plane.y), Is.EqualTo(1f).Within(1e-5f), "the wave's plane in the renderer's frame");
            yield return UntilCommitted(hits[0].Operation);
        }

        [UnityTest]
        public IEnumerator U8_ANoOpIsNotRetried_TheCutConsumesTheLineage_AndAnotherSlashCutsAChild()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var handle = PrepareBonedCharacter(out Transform _, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);

            // Exactly on the top face: a real hit, and one side with no support.
            List<SlashHitConfirmed> touching = Evaluate(detector, CharacterLevel(1, 1f, -3f, 3f), 1);
            Assert.That(touching.Count, Is.EqualTo(1));
            Assert.That(touching[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.EmptySide));
            Assert.That(handle.IsDisposed, Is.False, "the No-op leaves the character to be cut later");
            Assert.That(root.activeInHierarchy, Is.True);
            yield return null;
            Assert.That(Evaluate(detector, CharacterLevel(1, 1f, -3f, 3f), 1), Is.Empty, "the same Slash does not try it again");

            List<SlashHitConfirmed> cut = Evaluate(detector, CharacterLevel(2, 0.3f, -3f, 3f), 1, 2);
            Assert.That(cut.Count, Is.EqualTo(1));
            Assert.That(cut[0].Fragment, Is.EqualTo(touching[0].Fragment), "the same character, the same fragment");
            Assert.That(cut[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(root.activeInHierarchy, Is.False);
            yield return null;
            Assert.That(Evaluate(detector, CharacterUpright(2, 0.2f), 2), Is.Empty, "its Provisional sides are what Slash 2 consumed");

            yield return UntilCommitted(cut[0].Operation);
            Assert.That(Evaluate(detector, CharacterUpright(2, 0.2f), 2), Is.Empty, "and so are its children");
            List<SlashHitConfirmed> children = Evaluate(detector, CharacterUpright(3, 0.2f), 2, 3);
            Assert.That(children.Count, Is.EqualTo(2), "another Slash hits each child");
            foreach (SlashHitConfirmed child in children)
            {
                Assert.That(child.Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
                Assert.That(coldWorld.Ledger.TryGetOrigin(child.Fragment, out CutOperationId origin, out _), Is.True);
                Assert.That(origin, Is.EqualTo(cut[0].Operation), "a child of the character's cut");
            }

            yield return UntilCommitted(children[0].Operation);
            yield return UntilCommitted(children[1].Operation);
        }

        [UnityTest]
        public IEnumerator Withdrawal_OfParts_StopsTheDrawingThePoseAndTheBody_AtThePublication_AndTheCutsGoOn()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var handle = PrepareBonedCharacter(out Transform _, out GameObject root, motionOnItsOwn: true);
            var pose = root.AddComponent<WithdrawalPoseStandIn>();
            SkinnedMeshRenderer renderer = root.GetComponentInChildren<SkinnedMeshRenderer>();
            Rigidbody motion = root.GetComponentInChildren<Rigidbody>();
            Assert.That(handle.TryWithdrawParts(new Behaviour[] { pose }, out string whyNot), Is.True, whyNot);
            Assert.That(handle.WithdrawsParts, Is.True);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);

            List<SlashHitConfirmed> cut = Evaluate(detector, CharacterLevel(1, 0.3f, -3f, 3f), 1);
            Assert.That(cut.Count, Is.EqualTo(1));
            Assert.That(cut[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            // In the frame of the publication: not drawn, not a hit target, not posed, its body out of the physics.
            Assert.That(renderer.enabled, Is.False, "not drawn");
            Assert.That(pose.enabled, Is.False, "not posed");
            Assert.That(motion.gameObject.activeInHierarchy, Is.False, "its motion body left the physics");
            Assert.That(handle.IsDisposed || !handle.IsHitTarget, Is.True, "not a hit target");
            Assert.That(root.activeInHierarchy, Is.True, "the hierarchy itself stays: only its parts left");
            Assert.That(renderer.sharedMesh, Is.Not.Null, "nothing it held was given back");
            int updates = pose.Updates;
            yield return null;
            yield return null;
            Assert.That(pose.Updates, Is.EqualTo(updates), "its pose is not updated again");

            // Provisional, Final and Geometry go on; another Slash cuts each child.
            yield return UntilCommitted(cut[0].Operation);
            List<SlashHitConfirmed> children = Evaluate(detector, CharacterUpright(3, 0.2f), 3);
            Assert.That(children.Count, Is.EqualTo(2), "another Slash hits each child");
            foreach (SlashHitConfirmed child in children)
            {
                Assert.That(child.Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            }

            yield return UntilCommitted(children[0].Operation);
            yield return UntilCommitted(children[1].Operation);
        }

        [UnityTest]
        public IEnumerator Withdrawal_KeepsTheWholeRoot_WhenTheCharacterHoldsAnythingElseLive()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var handle = PrepareBonedCharacter(out Transform _, out GameObject root, motionOnItsOwn: true);
            var pose = root.AddComponent<WithdrawalPoseStandIn>();
            var extra = new GameObject("another drawing");
            extra.transform.SetParent(root.transform, false);
            extra.AddComponent<MeshFilter>();
            extra.AddComponent<MeshRenderer>();
            Assert.That(handle.TryWithdrawParts(new Behaviour[] { pose }, out string whyNot), Is.False);
            Assert.That(whyNot, Does.Contain("renderer"));
            Assert.That(handle.WithdrawsParts, Is.False, "the whole root is kept");
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            // The acceptance follows the Main budget of this frame (the frame's own remainder against the stages' predictions):
            // Published at once, or Pending -- the root kept until the same operation is published (TL, 2026-10-01; md5-play
            // and wd1-play were Pending, published one frame later). Either way the whole root leaves at the publication, not
            // before it and not by parts.
            string Budget() => "frame " + Time.frameCount + ", Main remaining " + CutPhysicsStep.FrameRemainingMainSeconds.ToString("R") + " s, dispatch budget " + coldWorld.Dispatcher.RemainingBudget
                + ", build history " + coldWorld.Driver.BuildCosts.Count + " (predicted " + coldWorld.Driver.BuildCosts.ExpectedSeconds.ToString("R") + " s), publication history " + coldWorld.Driver.PublishCosts.Count
                + " (predicted " + coldWorld.Driver.PublishCosts.ExpectedSeconds.ToString("R") + " s)";
            string budget = Budget();
            List<SlashHitConfirmed> cut = Evaluate(detector, CharacterLevel(1, 0.3f, -3f, 3f), 1);
            Assert.That(cut.Count, Is.EqualTo(1));
            Assert.That(cut[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published).Or.EqualTo(ProvisionalCutAcceptance.Pending), "accepted");
            CutOperationId operation = cut[0].Operation;
            var record = new List<string> { "before the evaluation: " + budget };
            string State() => "frame " + Time.frameCount + ", acceptance " + cut[0].Acceptance + ", operation " + operation.value
                + ", ledger " + (coldWorld.Ledger.TryGetOperation(operation, out LogicalCutOperation op) ? op.state.ToString() : "none")
                + ", geometry " + coldWorld.Geometry.StageOf(operation) + ", transaction " + (coldWorld.Driver.TransactionOf(operation)?.Phase.ToString() ?? "none")
                + ", root active " + root.activeInHierarchy + ", withdrawn " + handle.IsWithdrawn + " (by parts " + handle.WithdrawsParts + "), handle held " + handle.IsHeld;
            // The operation's publication: its transaction published (or past it), or handed off and completed in the ledger.
            bool Published()
            {
                ProvisionalCutTransaction t = coldWorld.Driver.TransactionOf(operation);
                if (t != null) return t.Phase == ProvisionalCutPhase.Published || t.Phase == ProvisionalCutPhase.FinalHeld || t.Phase == ProvisionalCutPhase.HandedOff;
                return coldWorld.Ledger.TryGetOperation(operation, out LogicalCutOperation done) && done.state == LogicalCutOperationState.Completed;
            }

            record.Add("after the evaluation: " + State());
            record.Add("the budget after the evaluation: " + Budget());
            if (cut[0].Acceptance == ProvisionalCutAcceptance.Pending)
            {
                // Pending: the root kept, then the same operation followed to its publication.
                Assert.That(Published(), Is.False, "Pending: not published yet");
                Assert.That(root.activeInHierarchy && !handle.IsWithdrawn, Is.True, "Pending: the root kept until the publication");
                int frames = 0;
                while (!Published() && frames < 600)
                {
                    yield return null;
                    frames++;
                    if (!Published()) Assert.That(root.activeInHierarchy && !handle.IsWithdrawn, Is.True, "still Pending at frame " + Time.frameCount + ": the root kept");
                }

                record.Add("published: " + State() + " (" + frames + " frame(s) after the evaluation)");
                foreach (string line in record) TestContext.Out.WriteLine(line);
                Assert.That(Published(), Is.True, "the same operation was published");
            }
            else
            {
                foreach (string line in record) TestContext.Out.WriteLine(line);
            }

            Assert.That(root.activeInHierarchy, Is.False, "the whole root left, as before, at the publication");
            Assert.That(handle.IsWithdrawn && !handle.WithdrawsParts, Is.True, "withdrawn whole, not by parts");
            yield return UntilCommitted(operation);
        }

        [UnityTest]
        public IEnumerator Withdrawal_KeepsTheWholeRoot_WhenAComponentHasAnOnDisableOfItsOwn()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var handle = PrepareBonedCharacter(out Transform _, out GameObject root, motionOnItsOwn: true);
            var pose = root.AddComponent<WithdrawalPoseStandIn>();
            var listener = root.AddComponent<WithdrawalDisableListener>();
            listener.enabled = false;
            Assert.That(handle.TryWithdrawParts(new Behaviour[] { pose }, out string whyNot), Is.False);
            Assert.That(whyNot, Does.Contain("OnDisable"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator U8_ACharacterIdentifiedButNeverCut_LeavesWithItsHandle()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var handle = PrepareBonedCharacter(out Transform _, out GameObject _);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);

            Evaluate(detector, CharacterLevel(1, 1f, -3f, 3f), 1);
            LogicalFragmentId identified = handle.Source;
            Assert.That(coldWorld.Ledger.IsCurrentTarget(identified), Is.True, "identified by its first hit");

            handle.Dispose();
            Assert.That(coldWorld.Ledger.IsCurrentTarget(identified), Is.False, "and it leaves with the handle");
            Assert.That(handle.HitShape, Is.Null, "its hit copy is given back");
            Assert.That(Evaluate(detector, CharacterLevel(2, 0.3f, -3f, 3f), 1, 2), Is.Empty, "no longer a target");
        }
    }
}
