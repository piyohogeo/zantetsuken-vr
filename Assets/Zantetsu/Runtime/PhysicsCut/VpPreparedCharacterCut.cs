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
                convexes, convexBones, sharedCold, out prepared);
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
        VpPreparedPhysicsInput physics;
        VpLogicalCutDisplay.PreparedRoot slot;
        bool disposed;

        VpPreparedCharacterCut(CutWorldRoot world, SkinnedMeshRenderer renderer,
            VpPhysicsColdPreparation sharedCold, Transform[] bones)
        {
            this.world = world; this.renderer = renderer; this.sharedCold = sharedCold;
            convexBones = bones; boneToOwner = new float4x4[bones.Length];
        }

        static bool Usable(CutWorldRoot world) => world != null && world.IsReady
            && !world.IsEnding && !world.IsReleased && !world.TerminationRequested;

        /// <summary>Cold lifetime readiness only, not admission or storage capacity. Does not advance preparation.</summary>
        public bool IsReady => !disposed && !busy && !terminal && !disposeRequested && Usable(world) && sharedCold.IsPrepared
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
            IReadOnlyList<Transform> bones, VpPhysicsColdPreparation sharedCold, out VpPreparedCharacterCut prepared)
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
                if (!VpDirectSkinInput.TryCreate(renderer, topology, topologyCount, out made.direct)) return false;
                // Reserve the cold display allocations before creating/cooking per-instance physics meshes.
                if (!world.Display.TryPrepareRoot(made.direct, out made.slot)) return false;
                made.physics = new VpPreparedPhysicsInput(bank, convexes);
                sharedCold.Prepare(made.physics);
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
            try { slot?.Dispose(); }
            finally
            {
                slot = null;
                try { physics?.Dispose(); }
                finally
                {
                    physics = null;
                    try { direct?.Dispose(); }
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
