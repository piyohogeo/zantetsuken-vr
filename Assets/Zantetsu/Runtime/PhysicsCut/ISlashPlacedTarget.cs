using Unity.Mathematics;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// A placed object before its first cut (DESIGN 4.5.1, 4.5.2): drawn by its own renderers and colliding with its own
    /// colliders as the scene placed it, with no display geometry, owner or body of the cut world yet -- a candidate of the
    /// <see cref="SlashHitDetector"/> through its convexes in its own frame (<see cref="FrameToWorld"/>), as a prepared character
    /// is through its bones. A hit identifies it as its fragment (issued then, the first time), consumes that fragment like
    /// any other, and is passed once to its own acceptance (<see cref="TryCut"/>), which prepares its cut input only then;
    /// what it prepared stands only if the cut goes on, and until the cut's display takes over its own drawing stays.
    /// </summary>
    public interface ISlashPlacedTarget
    {
        /// <summary>Whether a hit may test it now: in the scene, not cut, not refused for good.</summary>
        bool IsHitTarget { get; }

        /// <summary>
        /// The frame its convexes and a cut's plane are in, to the world: the placed instance's position and rotation,
        /// unscaled (a scale is in its convexes), read as the instance stands now.
        /// </summary>
        float4x4 FrameToWorld { get; }

        /// <summary>Its convexes in its frame (<see cref="FrameToWorld"/>), with their boxes.</summary>
        VpCharacterHitShape HitShape { get; }

        /// <summary>The fragment it is, once a hit has identified it; unset before.</summary>
        LogicalFragmentId Source { get; }

        /// <summary>The fragment it is, issued the first time a hit identifies it; false while it is not a hit target.</summary>
        bool TryIdentify(out LogicalFragmentId fragment);

        /// <summary>Its own acceptance of one hit (Main, outside simulation, after the update's enumeration).</summary>
        SlashPlacedCutResult TryCut(in SlashPlacedHit hit);
    }

    /// <summary>One hit on a placed object, as the detector found it.</summary>
    public readonly struct SlashPlacedHit
    {
        public readonly float4 plane;        // in the target's frame (FrameToWorld)
        public readonly float4 planeWorld;
        public readonly float3 renderAnchor; // world
        public readonly long slashId, planeId;
        public readonly double at;
        public readonly float3 travelWorld;
        public readonly float positiveSeparationImpulse, negativeSeparationImpulse;

        public SlashPlacedHit(float4 plane, float4 planeWorld, float3 renderAnchor, long slashId, long planeId, double at, float3 travelWorld,
            float positiveSeparationImpulse, float negativeSeparationImpulse)
        {
            this.plane = plane; this.planeWorld = planeWorld; this.renderAnchor = renderAnchor; this.slashId = slashId; this.planeId = planeId;
            this.at = at; this.travelWorld = travelWorld;
            this.positiveSeparationImpulse = positiveSeparationImpulse; this.negativeSeparationImpulse = negativeSeparationImpulse;
        }
    }

    /// <summary>
    /// What a placed object's acceptance said: as the ordinary acceptance says it, with the operation if one was admitted,
    /// and whether it is done as a candidate -- its cut went on (it is its fragment's owner from here, or the hull trial's
    /// group), or it was refused for good; a refusal that may pass later (an empty side, a full budget, room to come back)
    /// leaves it a candidate, its own drawing and colliders untouched.
    /// </summary>
    public readonly struct SlashPlacedCutResult
    {
        public readonly ProvisionalCutAcceptance Acceptance;
        public readonly LogicalCutAdmission Admission;
        public readonly CutOperationId Operation;
        public readonly bool Done;

        public SlashPlacedCutResult(ProvisionalCutAcceptance acceptance, LogicalCutAdmission admission, CutOperationId operation, bool done)
        {
            Acceptance = acceptance; Admission = admission; Operation = operation; Done = done;
        }
    }
}
