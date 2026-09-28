using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    public sealed partial class CutWorldRoot
    {
        /// <summary>
        /// Main-thread cold preparation only. Authoring resolves and validates the bone-local bank/ranges and
        /// convex-to-bone mapping before calling. The handle copies them and owns D1/D2/D3; no caller-owned mutable
        /// D2 input is accepted. Does not register a body, append storage, reserve admission or publish physics.
        /// The caller owns the returned handle and must Dispose it, including after world shutdown.
        /// </summary>
        public bool TryPrepareCharacterCut(SkinnedMeshRenderer renderer, int[] topology, int topologyCount,
            ConvexBrepBank boneLocalBank, IReadOnlyList<ConvexBrepRange> convexes,
            IReadOnlyList<Transform> convexBones, VpPhysicsColdPreparation sharedCold,
            out VpPreparedCharacterCut prepared)
            => VpPreparedCharacterCut.TryCreate(this, renderer, topology, topologyCount, boneLocalBank,
                convexes, convexBones, sharedCold, null, out prepared);

        /// <summary>
        /// The same, borrowing a direct skin input its caller owns for the character's life (a crowd's prepared slot)
        /// instead of making one: the handle takes the loan, forgets any pose taken before, and gives it back -- never
        /// disposes it -- when it ends. False, lending nothing, when the input is disposed, lent already, or not made from
        /// this renderer and topology count.
        /// </summary>
        public bool TryPrepareCharacterCut(SkinnedMeshRenderer renderer, int[] topology, int topologyCount,
            ConvexBrepBank boneLocalBank, IReadOnlyList<ConvexBrepRange> convexes,
            IReadOnlyList<Transform> convexBones, VpPhysicsColdPreparation sharedCold, VpDirectSkinInput lentDirect,
            out VpPreparedCharacterCut prepared)
            => VpPreparedCharacterCut.TryCreate(this, renderer, topology, topologyCount, boneLocalBank,
                convexes, convexBones, sharedCold, lentDirect, out prepared);
    }

    /// <summary>
    /// Cold-owned resources for the limited first-cut path (unit-scale renderer, four weights, one submesh).
    /// Bind the dedicated source during cold preparation to enable the synchronous first-cut adapter.
    /// No raw shape, classification, prepared display slot or rearm capability escapes this handle.
    /// The rig, mesh, transforms, world and shared cold preparer are borrowed, never disposed here.
    /// Recreate after changing mesh contents, bone bindings or authoring data; no hot rescan is introduced.
    /// </summary>
    public sealed partial class VpPreparedCharacterCut : IDisposable
    {
        readonly CutWorldRoot world;
        readonly SkinnedMeshRenderer renderer;
        readonly VpPhysicsColdPreparation sharedCold;
        readonly Transform[] convexBones;
        readonly float4x4[] boneToOwner;
        VpDirectSkinInput direct;
        // False when the direct skin input is lent by the character's owner: then it goes back, not away, at the end.
        bool ownsDirect = true;
        VpPreparedPhysicsInput physics;
        VpLogicalCutDisplay.PreparedRoot slot;
        VpCharacterHitShape hitShape;
        bool disposed;

        VpPreparedCharacterCut(CutWorldRoot world, SkinnedMeshRenderer renderer,
            VpPhysicsColdPreparation sharedCold, Transform[] bones)
        {
            this.world = world; this.renderer = renderer; this.sharedCold = sharedCold;
            convexBones = bones; boneToOwner = new float4x4[bones.Length];
        }

        // The cold preparation's main-thread stages, for measurement.
        static readonly Unity.Profiling.ProfilerMarker s_prepareDirect = new Unity.Profiling.ProfilerMarker("Zantetsu.CharacterCut.Prepare.DirectSkin");
        static readonly Unity.Profiling.ProfilerMarker s_prepareSlot = new Unity.Profiling.ProfilerMarker("Zantetsu.CharacterCut.Prepare.DisplaySlot");
        static readonly Unity.Profiling.ProfilerMarker s_preparePhysics = new Unity.Profiling.ProfilerMarker("Zantetsu.CharacterCut.Prepare.Physics");
        static readonly Unity.Profiling.ProfilerMarker s_prepareHitShape = new Unity.Profiling.ProfilerMarker("Zantetsu.CharacterCut.Prepare.HitShape");
        static readonly Unity.Profiling.ProfilerMarker s_prepareCold = new Unity.Profiling.ProfilerMarker("Zantetsu.CharacterCut.Prepare.Cold");

        static bool Usable(CutWorldRoot world) => world != null && world.IsReady
            && !world.IsEnding && !world.IsReleased && !world.TerminationRequested;

        /// <summary>Cold lifetime readiness only, not admission or storage capacity. Does not advance preparation.</summary>
        public bool IsReady => !disposed && !busy && !terminal && !held && !disposeRequested && Usable(world) && sharedCold.IsPrepared
            && slot != null && !slot.IsDisposed && !slot.IsConsumed;
        public bool IsDisposed => disposed;

        /// <summary>
        /// Loading frames only: confirms deferred native destruction without waiting. Never call from a hit to
        /// make it ready. A disposed/closed handle does not finish the borrowed preparer; bootstrap still owns it.
        /// </summary>
        public bool TryFinishPreparation()
        {
            if (disposed || busy || terminal || disposeRequested || !Usable(world)) return false;
            sharedCold.TryFinish();
            return IsReady;
        }

        internal static bool TryCreate(CutWorldRoot world, SkinnedMeshRenderer renderer, int[] topology,
            int topologyCount, ConvexBrepBank bank, IReadOnlyList<ConvexBrepRange> convexes,
            IReadOnlyList<Transform> bones, VpPhysicsColdPreparation sharedCold, VpDirectSkinInput lentDirect,
            out VpPreparedCharacterCut prepared)
        {
            prepared = null;
            if (!Usable(world) || sharedCold == null || convexes == null || convexes.Count == 0
                || bones == null || bones.Count != convexes.Count) return false;
            var mapping = new Transform[bones.Count];
            for (int i = 0; i < mapping.Length; i++)
            {
                if (bones[i] == null) return false;
                mapping[i] = bones[i];
            }
            var made = new VpPreparedCharacterCut(world, renderer, sharedCold, mapping);
            bool complete = false;
            try
            {
                bool direct;
                if (lentDirect != null)
                {
                    // The owner's input, for this handle's life only.
                    if (lentDirect.IsDisposed || !ReferenceEquals(lentDirect.SourceRenderer, renderer) || lentDirect.SourceMesh != renderer.sharedMesh
                        || lentDirect.TopologyCount != topologyCount || !lentDirect.TryLend(made)) return false;
                    made.direct = lentDirect;
                    made.ownsDirect = false;
                    direct = true;
                }
                else
                {
                    using (s_prepareDirect.Auto()) direct = VpDirectSkinInput.TryCreate(renderer, topology, topologyCount, world.CutInputConnectivity, out made.direct);
                }

                if (!direct) return false;
                // Reserve the cold display allocations before creating/cooking per-instance physics meshes.
                bool slot;
                using (s_prepareSlot.Auto()) slot = world.Display.TryPrepareRoot(made.direct, out made.slot);
                if (!slot) return false;
                using (s_preparePhysics.Auto()) made.physics = new VpPreparedPhysicsInput(bank, convexes);
                // The bone-local copy a hit reads (DESIGN 19.1.7): made here, once, from the same authored bank.
                using (s_prepareHitShape.Auto()) made.hitShape = new VpCharacterHitShape(bank, convexes);
                using (s_prepareCold.Auto()) sharedCold.Prepare(made.physics);
                prepared = made; complete = true;
                return true; // PlayMode may still be pending; IsReady stays false until bootstrap finishes D5.
            }
            finally
            {
                // If D5 throws while native objects await Destroy, its independent Mesh hold outlives this input.
                // Never dispose the shared preparer here or claim its pending cleanup has completed.
                if (!complete) made.Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            if (busy) { disposeRequested = true; return; }
            disposed = true;
            // A request held before acceptance ends with its handle, and the driver keeps nothing of it.
            if (held) { held = false; world?.Driver?.ForgetHeldCharacterCut(this); }
            // A character identified by a hit but never cut leaves with this handle: its fragment ends here. Once its
            // cut registered an owner, the fragment is the owner's and the ledger's, not this handle's.
            if (Source.IsSet && !actorTransferred && Usable(world) && world.Ledger.IsCurrentTarget(Source))
                world.Ledger.Retire(Source);
            try { hitShape?.Dispose(); }
            finally { hitShape = null; }
            try { slot?.Dispose(); }
            finally
            {
                slot = null;
                try { physics?.Dispose(); }
                finally
                {
                    physics = null;
                    try
                    {
                        // A lent input goes back to its owner, its pose forgotten; an input of this handle's own goes away.
                        if (ownsDirect) direct?.Dispose();
                        else direct?.Return(this);
                    }
                    finally
                    {
                        direct = null;
                        if (!actorTransferred) PhysicsOwnerBuilder.DestroyObject(actor);
                        actor = null; actorBody = null;
                    }
                }
            }
        }
    }
}
