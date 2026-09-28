using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A direct skin input owned for a character's life (a crowd's prepared slot) and lent to each prepared cut made for
    /// it in turn: made once, never freed by a handle's end, never lent twice at once or freed while lent, and never
    /// carrying a pose from one individual to the next -- each cut skins the pose its own hit met.
    /// </summary>
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        // The boned character of PrepareBonedCharacter, without a handle: the slot's objects, kept across individuals.
        private SkinnedMeshRenderer LentBonedRig(out Transform bone, out GameObject root, out Rigidbody motion)
        {
            root = ColdTrack(new GameObject("Lent character root"));
            var rendererObject = new GameObject("Lent character renderer");
            rendererObject.transform.SetParent(root.transform, false);
            bone = new GameObject("Lent character bone").transform;
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
            motion = root.AddComponent<Rigidbody>();
            motion.isKinematic = true;
            motion.useGravity = false;
            motion.mass = 12;
            motion.automaticCenterOfMass = motion.automaticInertiaTensor = false;
            motion.inertiaTensor = Vector3.one * 4;
            return r;
        }

        // One prepared cut of that character, borrowing the lent input; null when the world refuses it.
        private VpPreparedCharacterCut PrepareLent(SkinnedMeshRenderer r, Transform bone, GameObject root, Rigidbody motion, VpDirectSkinInput lent)
        {
            if (_vertices.IsCreated) _disposables.Insert(0, new EarlierBank { arrays = new IDisposable[] { _vertices, _faceOffsets, _faceIndices, _faceEdges, _edges } });
            var source = NewAuthoredShape(1);
            CompleteRequestBoxEdges(source);
            if (!coldWorld.TryPrepareCharacterCut(r, new[] { 0, 1, 2, 3 }, 4, source.BankOf(0), new[] { source.Convex(0) },
                    new[] { bone }, coldWarm, lent, root, motion, out var handle)) return null;
            coldHandles.Add(handle);
            return handle;
        }

        private float LowestSkinnedZ(VpPreparedCharacterCut handle)
        {
            Assert.That(coldWorld.Geometry.TryGetGeometry(handle.Source, out VpStoredGeometry input), Is.True);
            Assert.That(coldWorld.Storage.TryGetCommittedVertices(input.vertexStart, input.vertexCount, out NativeArray<VpRenderVertex> skinned), Is.True);
            float lowest = float.PositiveInfinity;
            for (int v = 0; v < skinned.Length; v++) lowest = Mathf.Min(lowest, skinned[v].position.z);
            return lowest;
        }

        /// <summary>
        /// **One input for the slot, a new pose for each individual.** The input is made once and lent to one prepared
        /// cut, which cannot be lent again while it is out; the cut takes the pose its hit met (bone at z 5). When that
        /// handle ends the input comes back alive, its pose forgotten. The same objects then carry a new individual: a
        /// new prepared cut borrows the same input, and its cut skins the pose its own hit met (z 7), not the earlier one.
        /// </summary>
        [UnityTest]
        public IEnumerator ALentInput_IsMadeOnce_AndEachPreparedCutSkinsThePoseItsOwnHitMet()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            SkinnedMeshRenderer r = LentBonedRig(out Transform bone, out GameObject root, out Rigidbody motion);
            int made = VpDirectSkinInput.CreatedCount;
            Assert.That(VpDirectSkinInput.TryCreate(r, new[] { 0, 1, 2, 3 }, 4, coldWorld.CutInputConnectivity, out VpDirectSkinInput lent), Is.True);
            try
            {
                VpPreparedCharacterCut first = PrepareLent(r, bone, root, motion, lent);
                Assert.That(first, Is.Not.Null);
                Assert.That(lent.Borrower, Is.SameAs(first), "lent to the first prepared cut");
                Assert.That(PrepareLent(r, bone, root, motion, lent), Is.Null, "not lent to another while it is out");
                yield return null;
                Assert.That(first.TryFinishPreparation(), Is.True);
                var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
                detector.AddCharacter(first);

                bone.localPosition = new Vector3(0, 0, 5);
                var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
                Assert.That(first.IsDisposed, Is.True, "the handle ended with its cut");
                Assert.That(lent.IsDisposed, Is.False, "and gave the input back, alive");
                Assert.That(lent.Borrower, Is.Null);
                Assert.That(lent.HasCapturedPose, Is.False, "its pose forgotten");
                Assert.That(LowestSkinnedZ(first), Is.GreaterThanOrEqualTo(4.99f), "the first cut skinned the pose its hit met");
                LogicalFragmentId firstSource = first.Source;
                yield return UntilCommitted(first.Operation);
                Assert.That(root.activeInHierarchy, Is.False, "the first individual left at its publication");

                // The same objects, a new individual: the root back, the bone elsewhere, a new prepared cut on the same input.
                detector.RemoveCharacter(first);
                root.SetActive(true);
                bone.localPosition = new Vector3(0, 0, 7);
                VpPreparedCharacterCut second = PrepareLent(r, bone, root, motion, lent);
                Assert.That(second, Is.Not.Null, "the input is lent again once it is back");
                Assert.That(lent.Borrower, Is.SameAs(second));
                Assert.That(lent.HasCapturedPose, Is.False);
                yield return null;
                Assert.That(second.TryFinishPreparation(), Is.True);
                detector.AddCharacter(second);
                // Past the first individual's pieces (z 5 to 6), so this Slash meets the second individual alone.
                hits = Evaluate(detector, CharacterLevel(2, 0.3f, 6.5f, 9f), 2);
                Assert.That(hits.Count, Is.EqualTo(1), "the second individual alone");
                Assert.That(hits[0].Fragment, Is.EqualTo(second.Source));
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
                Assert.That(second.Source, Is.Not.EqualTo(firstSource), "a new lineage, not the first individual's");
                Assert.That(LowestSkinnedZ(second), Is.GreaterThanOrEqualTo(6.99f), "the second cut skinned its own hit's pose (z 7), not the first one's (z 5)");
                Assert.That(VpDirectSkinInput.CreatedCount - made, Is.EqualTo(1), "one input made for both individuals");
                yield return UntilCommitted(second.Operation);
                Assert.That(lent.Borrower, Is.Null);
                Assert.That(lent.IsDisposed, Is.False, "neither handle's end freed it");
            }
            finally
            {
                lent.Dispose();
            }

            Assert.That(lent.IsDisposed, Is.True, "freed once, by its owner");
            lent.Dispose();
        }

        /// <summary>
        /// **A held request keeps the input it borrowed.** While the hit waits for room, the handle is not ended, the
        /// input stays lent to it with the pose the hit met, it is lent to no one else and cannot be freed under it.
        /// When the handle ends the hold, the input comes back with that pose forgotten, and is lent again.
        /// </summary>
        [UnityTest]
        public IEnumerator ALentInput_StaysWithAHeldRequest_AndComesBackOnlyWhenTheHoldEnds()
        {
            yield return ReadyCharacter(SmallRoom);
            SkinnedMeshRenderer r = LentBonedRig(out Transform bone, out GameObject root, out Rigidbody motion);
            Assert.That(VpDirectSkinInput.TryCreate(r, new[] { 0, 1, 2, 3 }, 4, coldWorld.CutInputConnectivity, out VpDirectSkinInput lent), Is.True);
            try
            {
                VpPreparedCharacterCut first = PrepareLent(r, bone, root, motion, lent);
                yield return null;
                Assert.That(first.TryFinishPreparation(), Is.True);
                coldWorld.Driver.RemainingMainSeconds = () => 1;
                var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
                detector.AddCharacter(first);
                VpCutOutputReservation held = HoldAllButThreeVertices();
                bone.localPosition = new Vector3(0, 0, 5);
                var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Held));
                Assert.That(first.IsHeld, Is.True);
                Assert.That(first.IsDisposed, Is.False, "a held request's handle is not ended");
                Assert.That(lent.Borrower, Is.SameAs(first), "the input stays with the held request");
                Assert.That(lent.HasCapturedPose, Is.True, "with the pose the hit met");
                Assert.That(PrepareLent(r, bone, root, motion, lent), Is.Null, "not lent to another while held");
                Assert.Throws<InvalidOperationException>(() => lent.Dispose(), "not freed under the held request");
                Assert.That(lent.IsDisposed, Is.False);

                first.Dispose();
                Assert.That(first.IsHeld, Is.False, "the hold ended with its handle");
                Assert.That(coldWorld.Driver.HeldCharacterCutCount, Is.Zero);
                Assert.That(lent.Borrower, Is.Null, "the input came back");
                Assert.That(lent.HasCapturedPose, Is.False, "its pose forgotten");
                Assert.That(coldWorld.Storage.TryCancelCutOutput(held), Is.True);
                VpPreparedCharacterCut second = PrepareLent(r, bone, root, motion, lent);
                Assert.That(second, Is.Not.Null, "lent again once back");
                second.Dispose();
                Assert.That(lent.Borrower, Is.Null);
            }
            finally
            {
                lent.Dispose();
            }

            Assert.That(lent.IsDisposed, Is.True);
        }

        /// <summary>
        /// **An owner that ends before its input comes back leaves it to be freed on return, once.** The owner ends while
        /// a ready prepared cut borrows the input: nothing is freed then, the input stays lent and usable; when the handle
        /// ends, the input comes back and is freed there, once; a later free by anyone does nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator AnOwnerEndingBeforeTheReturn_FreesTheInputOnTheReturn_Once()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            SkinnedMeshRenderer r = LentBonedRig(out Transform bone, out GameObject root, out Rigidbody motion);
            Assert.That(VpDirectSkinInput.TryCreate(r, new[] { 0, 1, 2, 3 }, 4, coldWorld.CutInputConnectivity, out VpDirectSkinInput lent), Is.True);
            VpPreparedCharacterCut handle = PrepareLent(r, bone, root, motion, lent);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);

            Assert.That(lent.DisposeWhenReturned(), Is.False, "lent: not freed at the owner's end");
            Assert.That(lent.IsDisposed, Is.False);
            Assert.That(lent.IsDisposeRequested, Is.True);
            Assert.That(lent.Borrower, Is.SameAs(handle));
            Assert.That(handle.IsReady, Is.True, "the borrower goes on with it");
            handle.Dispose();
            Assert.That(lent.IsDisposed, Is.True, "freed on its return");
            Assert.That(lent.IsDisposeRequested, Is.False);
            Assert.DoesNotThrow(() => lent.Dispose(), "a second free does nothing");
            Assert.That(lent.DisposeWhenReturned(), Is.True);
        }

        /// <summary>
        /// **An owner that ends inside the cut, while its borrower's end is put off, frees nothing early.** In the
        /// character's withdrawal -- inside the cut's own call, the handle busy -- the handle is asked to end (put off)
        /// and the owner ends. The input is not freed there; the cut goes on with it and publishes; when the call ends
        /// the handle ends, gives the input back, and it is freed then, once.
        /// </summary>
        [UnityTest]
        public IEnumerator AnOwnerEndingInsideTheCut_WhileTheHandlesEndIsPutOff_FreesTheInputOnlyAtTheReturn()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            SkinnedMeshRenderer r = LentBonedRig(out Transform bone, out GameObject root, out Rigidbody motion);
            var probe = root.AddComponent<D6TDisableProbe>();
            Assert.That(VpDirectSkinInput.TryCreate(r, new[] { 0, 1, 2, 3 }, 4, coldWorld.CutInputConnectivity, out VpDirectSkinInput lent), Is.True);
            VpPreparedCharacterCut handle = PrepareLent(r, bone, root, motion, lent);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);

            int callbacks = 0;
            bool freedInside = true, lentInside = false;
            probe.Disabled = () =>
            {
                callbacks++;
                handle.Dispose();
                Assert.That(handle.IsDisposed, Is.False, "the handle's end is put off while its cut runs");
                lent.DisposeWhenReturned();
                freedInside = lent.IsDisposed;
                lentInside = ReferenceEquals(lent.Borrower, handle);
            };

            bone.localPosition = new Vector3(0, 0, 5);
            var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
            probe.Disabled = null;
            Assert.That(callbacks, Is.EqualTo(1), "the owner ended inside the cut, at the withdrawal");
            Assert.That(freedInside, Is.False, "not freed while the busy handle still borrowed it");
            Assert.That(lentInside, Is.True);
            Assert.That(hits.Count, Is.EqualTo(1));
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published), "the cut went on with the input and published");
            Assert.That(handle.IsDisposed, Is.True, "the put-off end came at the end of the call");
            Assert.That(lent.Borrower, Is.Null);
            Assert.That(lent.IsDisposed, Is.True, "and the input was freed on its return, once");
            Assert.That(LowestSkinnedZ(handle), Is.GreaterThanOrEqualTo(4.99f), "the published geometry holds its own copy of the pose");
            Assert.DoesNotThrow(() => lent.Dispose());
            yield return UntilCommitted(handle.Operation);
            Assert.That(coldWorld.TerminationRequested, Is.False);
        }

        /// <summary>
        /// **The world's end frees nothing of the owner's, and nothing twice.** A prepared cut borrowing the input is
        /// ready when the world ends: the input is still alive and still lent after the ending; the handle's end gives it
        /// back; the owner frees it once; a second free does nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator TheWorldsEnd_WithALentInput_FreesNothingEarly_AndNothingTwice()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            SkinnedMeshRenderer r = LentBonedRig(out Transform bone, out GameObject root, out Rigidbody motion);
            Assert.That(VpDirectSkinInput.TryCreate(r, new[] { 0, 1, 2, 3 }, 4, coldWorld.CutInputConnectivity, out VpDirectSkinInput lent), Is.True);
            VpPreparedCharacterCut handle = PrepareLent(r, bone, root, motion, lent);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True, "the world ended");
            Assert.That(lent.IsDisposed, Is.False, "the world's end did not free the owner's input");
            Assert.That(lent.Borrower, Is.SameAs(handle), "still lent to its handle");
            handle.Dispose();
            Assert.That(lent.Borrower, Is.Null, "the handle's end gave it back");
            Assert.That(lent.IsDisposed, Is.False);
            lent.Dispose();
            Assert.That(lent.IsDisposed, Is.True, "freed once, by its owner");
            Assert.DoesNotThrow(() => lent.Dispose(), "a second free does nothing");
            handle.Dispose();
            Assert.That(lent.IsDisposed, Is.True);
        }
    }
}
