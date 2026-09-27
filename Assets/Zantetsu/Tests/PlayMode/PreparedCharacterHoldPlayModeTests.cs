using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A prepared character's hit whose display input cannot be given room (DESIGN 4.5.3, 4.5.4), through the product's
    /// own hit, acceptance and update: room that another work holds and will give back holds the request before
    /// acceptance, and the driver's update takes it up once, from the pose the hit met, when the room is back; room that
    /// cannot come back -- or pages the backing will not commit -- is the common termination, once; a request held when
    /// its handle or the world ends leaves nothing held. Small, deterministic room: a reservation of a few hundred
    /// vertices of which another work's open cut output reservation holds all but three.
    /// </summary>
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        // Set by a case that means the Player's termination: the teardown leaves the world as the termination left it.
        bool coldTerminationExpected;

        void ColdWorldWith(Action<CutWorldProfile> configure, Action terminatePlayer = null)
        {
            var profile = ColdTrack(ScriptableObject.CreateInstance<CutWorldProfile>());
            configure(profile);
            var mat = ColdTrack(new Material(Shader.Find("Zantetsu/VP Indexed Indirect Unlit")));
            var go = ColdTrack(new GameObject("Hold cold world")); go.SetActive(false); coldWorld = go.AddComponent<CutWorldRoot>();
            ColdSet(coldWorld, "profile", profile);
            ColdSet(coldWorld, "materials", new[] { new CutWorldRoot.MaterialBinding { sourceIndex = 0, material = mat } });
            if (terminatePlayer != null) ColdSet(coldWorld, "terminatePlayer", terminatePlayer);
            go.SetActive(true); Assert.That(coldWorld.IsReady, Is.True);
        }

        static void SmallRoom(CutWorldProfile p)
        {
            ColdSet(p, "vertexReserve", 512); ColdSet(p, "vertexInitialCommit", 512);
            ColdSet(p, "indexReserve", 4096); ColdSet(p, "indexInitialCommit", 4096);
            ColdSet(p, "gpuVertexInitialCapacity", 512); ColdSet(p, "gpuIndexInitialCapacity", 4096);
        }

        // Another work: a small geometry of its own and an open cut output reservation on it that leaves 3 vertices
        // free -- fewer than the character's display input (4) needs, and room that comes back when it is cancelled.
        VpCutOutputReservation HoldAllButThreeVertices()
        {
            VpCpuGeometryStorage storage = coldWorld.Storage;
            var up = new VpRenderVertex { position = Vector3.zero, normal = Vector3.up, uv0 = new Vector2(.5f, .5f) };
            var vertices = new[] { up, up, up };
            vertices[1].position = Vector3.right; vertices[2].position = Vector3.forward;
            Assert.That(storage.TryAppendPrepared(vertices, new uint[] { 0, 2, 1 }, new[] { 0, 1, 2 }, 3,
                new[] { new VpGeometrySubmesh(0, 3, 0) }, out VpStoredGeometry other), Is.True, "another work's geometry");
            Assert.That(storage.TryReserveCutOutput(other, storage.FreeVertexRoom - 3, 3, 2, 2, out VpCutOutputReservation held), Is.True,
                "another work's open reservation");
            Assert.That(storage.FreeVertexRoom, Is.EqualTo(3));
            return held;
        }

        IEnumerator ReadyCharacter(Action<CutWorldProfile> room, Action terminatePlayer = null)
        {
            ColdWorldWith(room, terminatePlayer);
            coldWarm = new VpPhysicsColdPreparation();
            yield return null;
        }

        /// <summary>
        /// **Room that will come back holds the request, and the same request goes on once when it is back, from the pose
        /// the hit met.** No termination, no Operation while held, the character no target for another Slash; after the
        /// other work's reservation is cancelled the next update takes it up: one Operation, committed, and its display
        /// input skinned at the bone where the hit met it, though the bone has moved since.
        /// </summary>
        [UnityTest]
        public IEnumerator RoomThatWillComeBack_HoldsTheHit_AndTheSameRequestGoesOnOnceFromTheHitsPose()
        {
            yield return ReadyCharacter(SmallRoom);
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            VpCutOutputReservation held = HoldAllButThreeVertices();

            bone.localPosition = new Vector3(0, 0, 5);
            var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
            Assert.That(hits.Count, Is.EqualTo(1));
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Held), "held, before acceptance");
            Assert.That(hits[0].Operation.IsSet, Is.False, "no Operation: the ledger was not asked");
            Assert.That(handle.IsHeld, Is.True);
            Assert.That(coldWorld.Driver.HeldCharacterCutCount, Is.EqualTo(1));
            Assert.That(coldWorld.TerminationRequested, Is.False, "not the termination");
            StringAssert.Contains("can clear", coldWorld.Storage.LastRefusal);
            Assert.That(Evaluate(detector, CharacterLevel(2, 0.3f, 2f, 8f), 1, 2), Is.Empty, "no second hit, by another Slash either");

            // The room is still held: the request stays held, frame after frame.
            for (int i = 0; i < 3; i++) yield return null;
            Assert.That(handle.IsHeld, Is.True);
            Assert.That(handle.Operation.IsSet, Is.False);
            Assert.That(coldWorld.TerminationRequested, Is.False);

            // The pose moves on; the request keeps the pose the hit met.
            bone.localPosition = new Vector3(0, 0, -5);
            Assert.That(coldWorld.Storage.TryCancelCutOutput(held), Is.True, "the other work gives its room back");
            yield return null;
            Assert.That(handle.IsHeld, Is.False, "taken up by the driver's update");
            Assert.That(handle.Operation.IsSet, Is.True, "accepted: one Operation");
            Assert.That(coldWorld.Driver.HeldCharacterCutCount, Is.Zero);
            Assert.That(coldWorld.Geometry.TryGetGeometry(handle.Source, out VpStoredGeometry input), Is.True);
            Assert.That(coldWorld.Storage.TryGetCommittedVertices(input.vertexStart, input.vertexCount, out NativeArray<VpRenderVertex> skinned), Is.True);
            for (int v = 0; v < skinned.Length; v++)
            {
                Assert.That(skinned[v].position.z, Is.GreaterThanOrEqualTo(4.99f), "skinned at the bone the hit met (z 5), not where it is now (z -5)");
            }

            CutOperationId operation = handle.Operation;
            yield return UntilCommitted(operation);
            Assert.That(coldWorld.Ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
            Assert.That(record.state, Is.EqualTo(LogicalCutOperationState.Completed));
            Assert.That(coldWorld.Ledger.OperationCount, Is.EqualTo(1), "one acceptance, not two");
            Assert.That(root.activeInHierarchy, Is.False, "the character left at its publication");
            Assert.That(coldWorld.TerminationRequested, Is.False);
        }

        /// <summary>
        /// **Pages the backing will not commit are not a wait, even with room held.** The character's display input needs
        /// pages past what the backing gives, while another work holds room: the common termination, once, and the hit
        /// is not held.
        /// </summary>
        [UnityTest]
        public IEnumerator PagesTheBackingWillNotCommit_AreTheTermination_EvenWithRoomHeld()
        {
            coldTerminationExpected = true;
            int terminations = 0;
            CutWorldRoot.nextWorldPageBacking = new TestPagedBacking { CommitLimitBytes = 4096 };
            yield return ReadyCharacter(p =>
            {
                ColdSet(p, "vertexReserve", 4096); ColdSet(p, "vertexInitialCommit", 64);
                ColdSet(p, "indexReserve", 4096); ColdSet(p, "indexInitialCommit", 64);
                ColdSet(p, "gpuVertexInitialCapacity", 64); ColdSet(p, "gpuIndexInitialCapacity", 64);
            }, () => terminations++);
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject _);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);

            // Another work holds all but 3 of the committed vertices (256 in the 4 KiB committed); the input needs 4,
            // which needs a page past the backing's limit.
            VpCpuGeometryStorage storage = coldWorld.Storage;
            var up = new VpRenderVertex { position = Vector3.zero, normal = Vector3.up, uv0 = new Vector2(.5f, .5f) };
            Assert.That(storage.TryAppendPrepared(new[] { up, up, up }, new uint[] { 0, 2, 1 }, new[] { 0, 1, 2 }, 3,
                new[] { new VpGeometrySubmesh(0, 3, 0) }, out VpStoredGeometry other), Is.True);
            Assert.That(storage.TryReserveCutOutput(other, storage.CommittedVertexCapacity - 6, 3, 2, 2, out VpCutOutputReservation _), Is.True);

            LogAssert.Expect(LogType.Error, new Regex("the Player is being ended"));
            bone.localPosition = new Vector3(0, 0, 5);
            var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
            Assert.That(hits.Count, Is.EqualTo(1));
            Assert.That(hits[0].Acceptance, Is.Not.EqualTo(ProvisionalCutAcceptance.Held), "not held");
            Assert.That(coldWorld.TerminationRequested, Is.True, "the common termination");
            StringAssert.Contains("backing failed", coldWorld.Storage.BackingFailure);
            yield return null;
            Assert.That(terminations, Is.EqualTo(1), "called once");
            Assert.That(coldWorld.Driver.HeldCharacterCutCount, Is.Zero);
        }

        /// <summary>
        /// **A held request ends with its handle, or with the world, and nothing of it stays.** Disposed while held: the
        /// driver holds nothing and the character's fragment is retired. The world ended while held: the ordinary ending
        /// ends the hold, and the world is released.
        /// </summary>
        [UnityTest]
        public IEnumerator AHeldRequest_EndedByItsHandleOrByTheWorld_LeavesNothingHeld()
        {
            yield return ReadyCharacter(SmallRoom);
            var first = PrepareBonedCharacter(out Transform firstBone, out GameObject _);
            var second = PrepareBonedCharacter(out Transform secondBone, out GameObject _);
            yield return null;
            Assert.That(first.TryFinishPreparation() && second.TryFinishPreparation(), Is.True);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(first);
            detector.AddCharacter(second);
            HoldAllButThreeVertices();

            firstBone.localPosition = new Vector3(0, 0, 5);
            secondBone.localPosition = new Vector3(0, 0, 5);
            var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
            Assert.That(hits.Count, Is.EqualTo(2));
            Assert.That(hits.TrueForAll(h => h.Acceptance == ProvisionalCutAcceptance.Held), Is.True, "both held");
            Assert.That(coldWorld.Driver.HeldCharacterCutCount, Is.EqualTo(2));

            LogicalFragmentId firstSource = first.Source;
            first.Dispose();
            Assert.That(coldWorld.Driver.HeldCharacterCutCount, Is.EqualTo(1), "the disposed one is no longer held");
            Assert.That(coldWorld.Ledger.TryGetFragmentState(firstSource, out LogicalFragmentState state), Is.True);
            Assert.That(state, Is.Not.EqualTo(LogicalFragmentState.Live), "its fragment ended with it");
            yield return null;

            // The world ends with the second still held: the ordinary ending takes the hold with it.
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True, "the world was released");
            Assert.That(second.IsHeld, Is.False, "the hold ended with the world");
            Assert.That(second.IsDisposed, Is.True);
            Assert.That(coldWorld.Driver.HeldCharacterCutCount, Is.Zero);
            Assert.That(second.Operation.IsSet, Is.False, "it was never accepted");
        }
    }
}
