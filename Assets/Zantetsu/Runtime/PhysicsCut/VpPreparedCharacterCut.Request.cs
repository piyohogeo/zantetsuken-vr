using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// Held: before acceptance, the display input could not be given room that will come back; the request is kept and
    /// the driver's update takes it up later (see <see cref="ProvisionalCutAcceptance.Held"/>).
    /// </summary>
    public enum VpCharacterCutOutcome { Unavailable, EmptySide, Full, Failed, Requested, Held }

    /// <summary>Requested means the ordinary driver was called; inspect Acceptance, which can include Abort.</summary>
    public readonly struct VpCharacterCutResult
    {
        public readonly VpCharacterCutOutcome Outcome;
        public readonly ProvisionalCutAcceptance Acceptance;
        public readonly LogicalFragmentId Source;
        public readonly CutOperationId Operation;
        internal VpCharacterCutResult(VpCharacterCutOutcome outcome, ProvisionalCutAcceptance acceptance,
            LogicalFragmentId source, CutOperationId operation)
        { Outcome=outcome; Acceptance=acceptance; Source=source; Operation=operation; }
    }

    public sealed partial class CutWorldRoot
    {
        bool _preparedCharacterCall, _preparedCharacterShutdown;
        internal bool BeginPreparedCharacterCall()
        {
            if (_preparedCharacterCall || !IsReady || _ending || TerminationRequested) return false;
            _preparedCharacterCall = true; return true;
        }
        internal void EndPreparedCharacterCall()
        {
            _preparedCharacterCall = false;
            if (_preparedCharacterShutdown) { _preparedCharacterShutdown = false; Shutdown(); }
        }

        /// <summary>
        /// Cold first-cut binding. characterRoot must contain the borrowed rig, old hit physics and motion body,
        /// but not this world. Withdrawal disables that entire hierarchy once; it is never destroyed or reactivated.
        /// One live handle per character is the caller's contract; recreate after hierarchy/binding changes.
        /// </summary>
        public bool TryPrepareCharacterCut(SkinnedMeshRenderer renderer, int[] topology, int topologyCount,
            ConvexBrepBank bank, IReadOnlyList<ConvexBrepRange> convexes, IReadOnlyList<Transform> convexBones,
            VpPhysicsColdPreparation sharedCold, GameObject characterRoot, Rigidbody motionBody,
            out VpPreparedCharacterCut prepared)
            => TryPrepareCharacterCut(renderer, topology, topologyCount, bank, convexes, convexBones, sharedCold, null,
                characterRoot, motionBody, out prepared);

        /// <summary>The same, borrowing the caller's direct skin input for this handle's life (see the overload above).</summary>
        public bool TryPrepareCharacterCut(SkinnedMeshRenderer renderer, int[] topology, int topologyCount,
            ConvexBrepBank bank, IReadOnlyList<ConvexBrepRange> convexes, IReadOnlyList<Transform> convexBones,
            VpPhysicsColdPreparation sharedCold, VpDirectSkinInput lentDirect, GameObject characterRoot, Rigidbody motionBody,
            out VpPreparedCharacterCut prepared)
            => TryPrepareCharacterCut(renderer, topology, topologyCount, bank, convexes, convexBones, sharedCold, lentDirect, null,
                characterRoot, motionBody, out prepared);

        /// <summary>
        /// The same, also taking the caller's baked collider meshes (<see cref="VpBakedConvexMeshes"/>, a crowd slot's
        /// kept ones) instead of baking them, when they were baked from this very bank and these convexes; baked afresh
        /// otherwise. The meshes stay the caller's object; this handle and what its cut publishes only hold them.
        /// </summary>
        public bool TryPrepareCharacterCut(SkinnedMeshRenderer renderer, int[] topology, int topologyCount,
            ConvexBrepBank bank, IReadOnlyList<ConvexBrepRange> convexes, IReadOnlyList<Transform> convexBones,
            VpPhysicsColdPreparation sharedCold, VpDirectSkinInput lentDirect, VpBakedConvexMeshes bakedMeshes,
            GameObject characterRoot, Rigidbody motionBody, out VpPreparedCharacterCut prepared)
        {
            prepared = null;
            if (renderer == null || characterRoot == null || motionBody == null
                || !renderer.transform.IsChildOf(characterRoot.transform)
                || !motionBody.transform.IsChildOf(characterRoot.transform)
                || transform.IsChildOf(characterRoot.transform)) return false;
            if (!VpPreparedCharacterCut.TryCreate(this,renderer,topology,topologyCount,bank,convexes,convexBones,sharedCold,lentDirect,bakedMeshes,out var made)) return false;
            try { made.BindSource(characterRoot,motionBody); prepared=made; return true; }
            catch { made.Dispose(); throw; }
        }
    }

    public sealed partial class VpPreparedCharacterCut
    {
        bool busy, terminal, disposeRequested, actorTransferred;
        GameObject characterRoot, actor;
        Rigidbody motionBody, actorBody;
        public LogicalFragmentId Source { get; private set; }
        public CutOperationId Operation { get; private set; }

        /// <summary>
        /// The live fragment this character is, issued the first time a hit identifies it (DESIGN 19.1.9): a hit's
        /// consumption and the cut that follows it name the same fragment, and the children of that cut descend from
        /// it. Nothing is issued before a hit, so cold preparation still publishes nothing. False while the handle is
        /// not a target -- not ready, busy, terminal, or its character withdrawn.
        /// </summary>
        internal bool TryIdentify(out LogicalFragmentId fragment)
        {
            fragment = Source;
            if (Source.IsSet) return true;
            if (!IsHitTarget) return false;
            Source = world.Ledger.AddFragment();
            fragment = Source;
            return true;
        }

        /// <summary>Whether a hit may test this character now: ready, and its character still in the scene.</summary>
        internal bool IsHitTarget => IsReady && characterRoot != null && characterRoot.activeInHierarchy && renderer != null
            && (withdrawal == null || !withdrawal.IsWithdrawn);

        // How the character's own hierarchy leaves at its first cut's publication (the whole root unless the parts are
        // confirmed, see PreparedCharacterWithdrawal).
        PreparedCharacterWithdrawal withdrawal;

        /// <summary>
        /// Asks for the character's parts, not its whole root, to be withdrawn at its first cut's publication: the renderer
        /// drawing it, <paramref name="updates"/> (the updates posing its bones) and its motion body. The hierarchy is
        /// confirmed to hold nothing else live, once, here; if it does, the whole root is kept and the reason is given.
        /// </summary>
        internal bool TryWithdrawParts(System.Collections.Generic.IReadOnlyList<Behaviour> updates, out string whyNot)
        {
            if (withdrawal == null) { whyNot = "not bound"; return false; }
            return withdrawal.TryUseParts(updates, out whyNot);
        }

        /// <summary>Whether the parts, not the whole root, will be withdrawn.</summary>
        internal bool WithdrawsParts => withdrawal != null && withdrawal.UsesParts;

        /// <summary>Whether the character has been withdrawn.</summary>
        public string LastFailure { get; private set; }
        private bool hasPlannedMotion;
        private Vector3 plannedVelocity, plannedAngularVelocity;
        public void SetPlannedMotion(Vector3 velocity, Vector3 angularVelocity)
        { hasPlannedMotion = true; plannedVelocity = velocity; plannedAngularVelocity = angularVelocity; }

        public bool IsWithdrawn => withdrawal != null && withdrawal.IsWithdrawn;

        /// <summary>The bone-local convexes a hit reads, and the bone that places each.</summary>
        internal VpCharacterHitShape HitShape => hitShape;

        internal Transform ConvexBone(int index) => convexBones[index];

        /// <summary>The frame a cut plane is given in (renderer-local, see <see cref="TryCut"/>).</summary>
        internal Transform RendererTransform => renderer != null ? renderer.transform : null;

        internal void BindSource(GameObject root, Rigidbody motion)
        {
            characterRoot=root; motionBody=motion;
            withdrawal=new PreparedCharacterWithdrawal(root,renderer,motion);
            actor=new GameObject("Prepared character cut source"); actor.SetActive(false);
            actorBody=actor.AddComponent<Rigidbody>(); actorBody.useGravity=false; actorBody.detectCollisions=false;
            actorBody.automaticCenterOfMass=actorBody.automaticInertiaTensor=false;
            // No intermediate Collider, Mesh or cook. This collider-free body is only a synchronous source of motion.
        }

        VpCharacterCutResult Result(VpCharacterCutOutcome outcome,
            ProvisionalCutAcceptance acceptance=ProvisionalCutAcceptance.InvalidRequest)
            =>new VpCharacterCutResult(outcome,acceptance,Source,Operation);

        /// <summary>
        /// Main only, outside simulation. Plane is renderer-local; renderAnchor is world-space; impulses are
        /// caller-supplied. No queue, waits, warm, fallback or second classification. Empty/Full may retry at a new
        /// pose; after append begins the handle is terminal. Exceptions propagate, with transferred ownership kept.
        /// A Failed result or exception after partial registration is NOT permission to continue/retry that handle.
        /// </summary>
        // The stages of a character's cut, apart from each other (all inside the hit's acceptance).
        static readonly Unity.Profiling.ProfilerMarker s_pose=new Unity.Profiling.ProfilerMarker("Zantetsu.CharacterCut.Pose");
        static readonly Unity.Profiling.ProfilerMarker s_prepare=new Unity.Profiling.ProfilerMarker("Zantetsu.CharacterCut.Prepare");
        static readonly Unity.Profiling.ProfilerMarker s_displayInput=new Unity.Profiling.ProfilerMarker("Zantetsu.CharacterCut.DisplayInput");
        static readonly Unity.Profiling.ProfilerMarker s_actor=new Unity.Profiling.ProfilerMarker("Zantetsu.CharacterCut.Actor");
        static readonly Unity.Profiling.ProfilerMarker s_request=new Unity.Profiling.ProfilerMarker("Zantetsu.CharacterCut.Request");
        static readonly Unity.Profiling.ProfilerMarker s_end=new Unity.Profiling.ProfilerMarker("Zantetsu.CharacterCut.End");

        public VpCharacterCutResult TryCut(float4 plane, float3 renderAnchor,
            float positiveSeparationImpulse=0, float negativeSeparationImpulse=0)
        {
            if (!IsReady || characterRoot==null || !characterRoot.activeInHierarchy || IsWithdrawn || motionBody==null
                || actor==null || actorBody==null || renderer==null) return Result(VpCharacterCutOutcome.Unavailable);
            if (!math.all(math.isfinite(plane)) || math.lengthsq(plane.xyz)<=0 || !math.all(math.isfinite(renderAnchor))
                || !float.IsFinite(positiveSeparationImpulse) || positiveSeparationImpulse<0
                || !float.IsFinite(negativeSeparationImpulse) || negativeSeparationImpulse<0)
                return Result(VpCharacterCutOutcome.Failed);
            if (!world.BeginPreparedCharacterCall()) return Result(VpCharacterCutOutcome.Unavailable);
            return Run(plane,renderAnchor,positiveSeparationImpulse,negativeSeparationImpulse,true);
        }

        // ----- a request held before acceptance (ProvisionalCutAcceptance.Held) -----------------------------------------
        // The boundary is the ledger's admission in RequestPreparedCut. Before it, a hit whose display input cannot be
        // given room that others hold and will return keeps: the character's fragment (issued by the hit), the pose the
        // hit met in the renderer's frame (boneToOwner for the physics, the display input's own matrices for the display),
        // the plane in that same frame, the anchor and the two impulses. It does not keep the fresh-cut lease, which is
        // one frame's capability: a resume poses the physics again from the kept pose and takes a new lease. Where the
        // character stands and how it moves are read at the resume, as a Pending publication reads them. While held the
        // character is no hit target, so no other hit can take it and nothing is accepted twice.
        bool held;
        float4 heldPlane;
        float3 heldAnchor;
        float heldPositive, heldNegative;

        /// <summary>Whether a request of this character is held before acceptance.</summary>
        public bool IsHeld => held;

        void Hold(float4 plane, float3 renderAnchor, float positiveSeparationImpulse, float negativeSeparationImpulse)
        {
            held=true; terminal=false;
            heldPlane=plane; heldAnchor=renderAnchor; heldPositive=positiveSeparationImpulse; heldNegative=negativeSeparationImpulse;
            world.Driver.HoldCharacterCut(this);
        }

        /// <summary>
        /// The driver's update takes the held request up once: the same request, from the pose the hit met, into the
        /// ordinary acceptance. Held again when the room is still to come back; ended with the handle when the
        /// character or the world is gone.
        /// </summary>
        internal VpCharacterCutResult ResumeHeld()
        {
            if (!held) return Result(VpCharacterCutOutcome.Unavailable);
            if (disposed || disposeRequested || !Usable(world) || !sharedCold.IsPrepared || slot==null || slot.IsDisposed || slot.IsConsumed
                || characterRoot==null || !characterRoot.activeInHierarchy || IsWithdrawn || motionBody==null
                || actor==null || actorBody==null || renderer==null)
            {
                EndHold();
                return Result(VpCharacterCutOutcome.Unavailable);
            }

            if (!world.BeginPreparedCharacterCall()) return Result(VpCharacterCutOutcome.Held);
            held=false;
            return Run(heldPlane,heldAnchor,heldPositive,heldNegative,false);
        }

        /// <summary>Ends a held request with its handle: its fragment goes as an identified character's does, and nothing is kept.</summary>
        internal void EndHold()
        {
            if (!held) return;
            held=false; terminal=true;
            Dispose();
        }

        VpCharacterCutResult Run(float4 plane, float3 renderAnchor, float positiveSeparationImpulse, float negativeSeparationImpulse,
            bool fromHit)
        {
            busy=true; terminal=true;
            ProvisionalCutDriver.PreparedCutLease lease=null;
            PhysicsOwnerShape taken=null;
            bool registered=false;
            try
            {
                Matrix4x4 inverse=renderer.transform.worldToLocalMatrix;
                float mass=motionBody.mass;
                var poseScope=s_pose.Auto();
                if(fromHit)
                {
                    // The pose the hit met, taken once: the physics' and the display's, both in the renderer's frame.
                    for(int i=0;i<convexBones.Length;i++)
                    {
                        if(convexBones[i]==null){poseScope.Dispose();return Result(VpCharacterCutOutcome.Failed);}
                        boneToOwner[i]=inverse*convexBones[i].localToWorldMatrix;
                    }
                    if(!direct.TryCapturePose()){LastFailure="capture pose";poseScope.Dispose();return Result(VpCharacterCutOutcome.Failed);}
                }
                bool posed=physics.TryPose(boneToOwner,out _);
                poseScope.Dispose();
                ProvisionalCutDriver.FreshCutEligibility eligible;
                using(s_prepare.Auto())
                {
                    if(!posed || !world.Driver.TryPrepareFreshCut(physics,plane,mass,out lease))
                    { LastFailure = !posed ? "pose physics" : "prepare fresh cut"; return Result(VpCharacterCutOutcome.Failed); }
                    eligible=world.Driver.AssessFreshCut(lease);
                }
                if(eligible==ProvisionalCutDriver.FreshCutEligibility.EmptySide || eligible==ProvisionalCutDriver.FreshCutEligibility.Full)
                {
                    lease.Dispose();lease=null;
                    if(!physics.TryRearmAfterRefusal())return Result(VpCharacterCutOutcome.Failed);
                    if(!fromHit && eligible==ProvisionalCutDriver.FreshCutEligibility.Full)
                    {
                        // A held request meeting a full budget stays held: the budget returns as cuts complete.
                        Hold(plane,renderAnchor,positiveSeparationImpulse,negativeSeparationImpulse);
                        return Result(VpCharacterCutOutcome.Held);
                    }
                    terminal=false;
                    return Result(eligible==ProvisionalCutDriver.FreshCutEligibility.EmptySide?VpCharacterCutOutcome.EmptySide:VpCharacterCutOutcome.Full);
                }
                if(eligible!=ProvisionalCutDriver.FreshCutEligibility.Ready){LastFailure="fresh cut eligibility: "+eligible;return Result(VpCharacterCutOutcome.Failed);}
                var displayScope=s_displayInput.Auto();
                int refusalsBefore=world.Storage.RoomRefusalCount;
                bool appended=direct.TryAppendCapturedForDisplay(world.Storage,out var output);
                displayScope.Dispose();
                if(!appended)
                {
                    if(world.Storage.RoomRefusalCount!=refusalsBefore)
                    {
                        // Room the storage refused for the display input. Room others hold and will give back is
                        // waited for: the request is held, before acceptance, and taken up again by a later update. Room
                        // that cannot come back -- past the reservation, a management area nobody returns -- and pages
                        // the backing would not commit are the common termination (DESIGN 4.5.4).
                        if(world.Storage.BackingFailure==null && world.Storage.LastRefusalIsTemporary)
                        {
                            lease.Dispose();lease=null;
                            if(!physics.TryRearmAfterRefusal())return Result(VpCharacterCutOutcome.Failed);
                            Hold(plane,renderAnchor,positiveSeparationImpulse,negativeSeparationImpulse);
                            return Result(VpCharacterCutOutcome.Held);
                        }
                        world.RequestTermination("the display input of a hit character could not be given room: "+world.Storage.DescribeRoom());
                    }
                    LastFailure="append display: "+world.Storage.DescribeRoom();
                    return Result(VpCharacterCutOutcome.Failed);
                }
                var actorScope=s_actor.Auto();
                try
                {
                actor.transform.SetPositionAndRotation(renderer.transform.position,renderer.transform.rotation);
                actor.SetActive(true);
                actorBody.mass=mass;
                actorBody.centerOfMass=inverse.MultiplyPoint3x4(motionBody.worldCenterOfMass);
                actorBody.inertiaTensor=motionBody.inertiaTensor;
                actorBody.inertiaTensorRotation=Quaternion.Inverse(renderer.transform.rotation)*motionBody.rotation*motionBody.inertiaTensorRotation;
                actorBody.linearVelocity=hasPlannedMotion ? plannedVelocity : motionBody.linearVelocity;actorBody.angularVelocity=hasPlannedMotion ? plannedAngularVelocity : motionBody.angularVelocity;
                // Issued here unless a hit already identified this character; either way the cut names that fragment.
                if(!Source.IsSet)Source=world.Ledger.AddFragment();
                if(!world.Display.TryShowPreparedRoot(slot,output,Source,renderer.transform.localToWorldMatrix,Matrix4x4.identity))
                {
                    // A refused show took nothing in: the display registers a geometry only as its last step, and every
                    // refusal comes before it. The appended root is still this request's, so its range is retired here,
                    // once, and the lineage's vertex room goes back through the ordinary reclamation (DESIGN 4.5.3).
                    world.Storage.TryRetireIndices(output.Geometry.indexRange);
                    LastFailure="show prepared root"; return Result(VpCharacterCutOutcome.Failed);
                }
                // The pieces this character's cuts leave are the lifetime's (DESIGN 7.10); the character itself never is.
                world.Lifetime?.MarkLineage(Source);
                taken=physics.TakeShape();
                PhysicsFragmentOwner owner;
                try { owner=world.Owners.RegisterAuthored(Source,actor,actorBody,taken,false,Matrix4x4.identity); }
                finally
                {
                    // RegisterAuthored can have inserted its owner before a later dictionary operation throws.
                    registered=world.Owners.TryGet(Source,out var found)&&ReferenceEquals(found.Shape,taken);
                    actorTransferred=registered;
                }
                world.Geometry.RegisterBaseGeometry(Source,output.Geometry,Matrix4x4.identity);
                owner.PreparedCharacterWithdrawal=withdrawal;
                }
                finally { actorScope.Dispose(); }
                var ask=new ProvisionalCutAsk{source=Source,plane=plane,renderAnchor=renderAnchor,
                    positiveSeparationImpulse=positiveSeparationImpulse,negativeSeparationImpulse=negativeSeparationImpulse};
                ProvisionalCutTransaction transaction=null;
                try
                {
                    ProvisionalCutAcceptance accepted;
                    using(s_request.Auto()) accepted=world.Driver.RequestPreparedCut(ask,lease,out transaction,out _);
                    if(transaction!=null)Operation=transaction.Operation;
                    if(accepted==ProvisionalCutAcceptance.Pending)withdrawal.BeginCutDisplay();
                    return Result(VpCharacterCutOutcome.Requested,accepted);
                }
                finally { if(transaction!=null)Operation=transaction.Operation; }
            }
            finally
            {
                using var endScope=s_end.Auto();
                try { lease?.Dispose(); }
                finally
                {
                    try
                    {
                        // A refusal that leaves the handle usable (EmptySide/Full) keeps the character and its fragment.
                        if(!registered)
                        {
                            taken?.Dispose();
                            if(terminal&&Source.IsSet&&world.Ledger.IsCurrentTarget(Source))world.Ledger.Retire(Source);
                        }
                    }
                    finally
                    {
                        busy=false;
                        try { if(terminal||disposeRequested)Dispose(); }
                        finally { world.EndPreparedCharacterCall(); }
                    }
                }
            }
        }
    }
}
