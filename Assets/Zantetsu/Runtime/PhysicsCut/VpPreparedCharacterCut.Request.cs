using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    public enum VpCharacterCutOutcome { Unavailable, EmptySide, Full, Failed, Requested }

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
        {
            prepared = null;
            if (renderer == null || characterRoot == null || motionBody == null
                || !renderer.transform.IsChildOf(characterRoot.transform)
                || !motionBody.transform.IsChildOf(characterRoot.transform)
                || transform.IsChildOf(characterRoot.transform)) return false;
            if (!TryPrepareCharacterCut(renderer,topology,topologyCount,bank,convexes,convexBones,sharedCold,out var made)) return false;
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

        internal void BindSource(GameObject root, Rigidbody motion)
        {
            characterRoot=root; motionBody=motion;
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
        public VpCharacterCutResult TryCut(float4 plane, float3 renderAnchor,
            float positiveSeparationImpulse=0, float negativeSeparationImpulse=0)
        {
            if (!IsReady || characterRoot==null || !characterRoot.activeInHierarchy || motionBody==null
                || actor==null || actorBody==null || renderer==null) return Result(VpCharacterCutOutcome.Unavailable);
            if (!math.all(math.isfinite(plane)) || math.lengthsq(plane.xyz)<=0 || !math.all(math.isfinite(renderAnchor))
                || !float.IsFinite(positiveSeparationImpulse) || positiveSeparationImpulse<0
                || !float.IsFinite(negativeSeparationImpulse) || negativeSeparationImpulse<0)
                return Result(VpCharacterCutOutcome.Failed);
            if (!world.BeginPreparedCharacterCall()) return Result(VpCharacterCutOutcome.Unavailable);
            busy=true; terminal=true;
            ProvisionalCutDriver.PreparedCutLease lease=null;
            PhysicsOwnerShape taken=null;
            bool registered=false;
            try
            {
                Matrix4x4 inverse=renderer.transform.worldToLocalMatrix;
                for(int i=0;i<convexBones.Length;i++)
                {
                    if(convexBones[i]==null)return Result(VpCharacterCutOutcome.Failed);
                    boneToOwner[i]=inverse*convexBones[i].localToWorldMatrix;
                }
                float mass=motionBody.mass;
                if(!physics.TryPose(boneToOwner,out _) || !world.Driver.TryPrepareFreshCut(physics,plane,mass,out lease))
                    return Result(VpCharacterCutOutcome.Failed);
                var eligible=world.Driver.AssessFreshCut(lease);
                if(eligible==ProvisionalCutDriver.FreshCutEligibility.EmptySide || eligible==ProvisionalCutDriver.FreshCutEligibility.Full)
                {
                    lease.Dispose();lease=null;
                    if(!physics.TryRearmAfterRefusal())return Result(VpCharacterCutOutcome.Failed);
                    terminal=false;
                    return Result(eligible==ProvisionalCutDriver.FreshCutEligibility.EmptySide?VpCharacterCutOutcome.EmptySide:VpCharacterCutOutcome.Full);
                }
                if(eligible!=ProvisionalCutDriver.FreshCutEligibility.Ready)return Result(VpCharacterCutOutcome.Failed);
                if(!direct.TryAppendForDisplay(world.Storage,out var output))return Result(VpCharacterCutOutcome.Failed);
                actor.transform.SetPositionAndRotation(renderer.transform.position,renderer.transform.rotation);
                actor.SetActive(true);
                actorBody.mass=mass;
                actorBody.centerOfMass=inverse.MultiplyPoint3x4(motionBody.worldCenterOfMass);
                actorBody.inertiaTensor=motionBody.inertiaTensor;
                actorBody.inertiaTensorRotation=Quaternion.Inverse(renderer.transform.rotation)*motionBody.rotation*motionBody.inertiaTensorRotation;
                actorBody.linearVelocity=motionBody.linearVelocity;actorBody.angularVelocity=motionBody.angularVelocity;
                Source=world.Ledger.AddFragment();
                if(!world.Display.TryShowPreparedRoot(slot,output,Source,renderer.transform.localToWorldMatrix,Matrix4x4.identity))
                    return Result(VpCharacterCutOutcome.Failed);
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
                owner.PreparedCharacterRoot=characterRoot;
                var ask=new ProvisionalCutAsk{source=Source,plane=plane,renderAnchor=renderAnchor,
                    positiveSeparationImpulse=positiveSeparationImpulse,negativeSeparationImpulse=negativeSeparationImpulse};
                ProvisionalCutTransaction transaction=null;
                try
                {
                    var accepted=world.Driver.RequestPreparedCut(ask,lease,out transaction,out _);
                    if(transaction!=null)Operation=transaction.Operation;
                    return Result(VpCharacterCutOutcome.Requested,accepted);
                }
                finally { if(transaction!=null)Operation=transaction.Operation; }
            }
            finally
            {
                try { lease?.Dispose(); }
                finally
                {
                    try
                    {
                        if(!registered)
                        {
                            taken?.Dispose();
                            if(Source.IsSet)world.Ledger.Retire(Source);
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
