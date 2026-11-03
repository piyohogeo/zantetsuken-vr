using System;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The GPU-first route of a prepared character's cut (DESIGN 4.5.15): inside the hit's own Run, between the fresh cut's
    /// assessment and the actor, the hit's fixed pose is shown by a compute bake in the hit's frame and the CPU geometry the
    /// cuts need is begun on a worker; the acceptance, the physics and the publication then run in this same call exactly
    /// as the CPU route's do. The deferred bake belongs to the world (<see cref="VpSkinBakePending"/>), not to this handle,
    /// which ends with its cut as any handle does; the base geometry is registered with the cut DAG as pending
    /// (<see cref="CutDag.RegisterPendingBaseGeometry"/>) and the cuts below it wait for it there.
    /// </summary>
    public sealed partial class VpPreparedCharacterCut
    {
        /// <summary>Why the last hit did not take the GPU-first route (the synchronous route ran instead); null when it did, or no hit yet.</summary>
        public string DeferredFallbackReason { get; private set; }

        /// <summary>The storage geometry the last GPU-first hit reserved (its vertices the GPU's until the CPU bake completes); default before any.</summary>
        public VpStoredGeometry GpuFirstGeometry { get; private set; }

        /// <summary>The snapshot request id of the last GPU-first hit (the key of its <see cref="VpSkinBakeStats"/> record); 0 before any.</summary>
        public long GpuFirstRequestId { get; private set; }

        enum GpuFirstBegin { NotTaken, Begun, Failed }

        /// <summary>
        /// Prepares the shared input of this character's model for the GPU-first route (cold: with the handle, never at a
        /// hit). False, with the reason, when the mesh is outside the accepted conditions; the hits then take the CPU route.
        /// </summary>
        public bool TryPrepareSharedSkinInput(out string refusal)
        {
            refusal = null;
            if (disposed || direct == null || direct.IsDisposed) { refusal = "no direct skin input"; return false; }
            return direct.Model.TryPrepareSharedInput(out refusal);
        }

        // Inside Run, after the fresh cut was assessed Ready and in place of the display input's append and show: begins the
        // GPU-first display where everything it needs is there -- the resources, the shared input, a snapshot, the storage's
        // room, a place in the dispatcher, the display's show and the first dispatch. Begun: the geometry is shown and its
        // CPU bake is the world's; Run goes on to the actor, the owner, the pending base geometry and the acceptance. NotTaken:
        // everything taken is given back and Run goes down the synchronous route. Failed: the display took the root in and
        // then refused the first dispatch; the handle is terminal, as a refused show makes it, and the body leaves with its fragment.
        /// <summary>The character's skin model, for the start-up warm of the GPU bake (<see cref="VpSkinBakeWarm"/>, SKIN-GPU-FIRST-STARTUP-1): read only, null before the preparation.</summary>
        public VpSkinModel SkinModelForWarm => direct != null ? direct.Model : null;
        internal VpSkinModel SkinModelForDiagnosis => SkinModelForWarm;

        GpuFirstBegin TryBeginGpuFirst(out VpStoredGeometry geometry, out VpSkinBakePending.Work work, out string reason)
        {
            geometry = default;
            work = null;
            reason = null;
            VpSkinBakeResources resources = world.SkinBake;
            VpSkinBakePending pending = world.SkinBakePending;
            if (resources == null || resources.Bake == null || pending == null) { reason = "no GPU bake resources"; return GpuFirstBegin.NotTaken; }
            if (world.Dispatcher == null) { reason = "no dispatcher"; return GpuFirstBegin.NotTaken; }
            VpSkinModel model = direct.Model;
            if (!model.TryPrepareSharedInput(out string refusal)) { reason = "shared input: " + refusal; return GpuFirstBegin.NotTaken; }
            if (!resources.TryRent(model, model.SharedInput, out VpSkinBakeSnapshot s, out refusal)) { reason = "snapshot: " + refusal; return GpuFirstBegin.NotTaken; }
            bool reserved = false, begun = false, shown = false, kept = false;
            try
            {
                if (!s.TryCaptureFrom(direct)) { reason = "no captured pose for the snapshot"; return GpuFirstBegin.NotTaken; }
                int refusalsBefore = world.Storage.RoomRefusalCount;
                if (!world.Storage.TryReserveDeferredSkin(model, s.Conservative, out geometry))
                {
                    reason = "storage: " + (world.Storage.RoomRefusalCount != refusalsBefore ? world.Storage.DescribeRoom() : "refused the reservation");
                    return GpuFirstBegin.NotTaken;
                }

                reserved = true;
                // Issued here unless a hit already identified this character; the cut names that fragment either way (Run
                // retires it with the handle where the hit fails before its registration).
                if (!Source.IsSet) Source = world.Ledger.AddFragment();
                if (!pending.TryBegin(Source, geometry, s, Time.frameCount, out work, out refusal)) { reason = refusal; return GpuFirstBegin.NotTaken; }
                begun = true;
                if (!world.Display.TryShowPreparedRootDeferred(slot, geometry, Source, renderer.transform.localToWorldMatrix, s, out shown))
                {
                    if (!shown) { reason = "display: the deferred show was refused"; return GpuFirstBegin.NotTaken; }
                    reason = "display: the first dispatch was refused";
                    return GpuFirstBegin.Failed;
                }

                kept = true;
                GpuFirstGeometry = geometry;
                GpuFirstRequestId = s.RequestId;
                DeferredFallbackReason = null;
                return GpuFirstBegin.Begun;
            }
            finally
            {
                if (!kept)
                {
                    // Not begun: everything taken for it goes back, and the synchronous route runs. A root the display took in
                    // (Failed) leaves with the handle: its fragment is retired by Run, and the display retires the geometry with it.
                    if (begun) pending.Withdraw(work); else resources.Return(s);
                    if (shown) world.Display.ForgetGpuRegion(geometry);
                    if (reserved) world.Storage.TryAbandonDeferredSkin(geometry, !shown);
                    if (shown) VpSkinBakeStats.NoteFailedAtBegin(reason);
                    geometry = default;
                    work = null;
                }
            }
        }

        /// <summary>
        /// The synchronous float route (<see cref="VpSkinBakeMode.Float"/>, the functional contrast): the same reservation
        /// and float kernel as the GPU-first route, run here on the main thread and completed at once, so that the storage
        /// holds the geometry as the CPU bake would have written it. False, with the reason and nothing changed but the
        /// storage's refusal count, where the inputs or the room are refused; the 24-byte route then runs.
        /// </summary>
        bool TryAppendFloatSync(out VpStoredGeometry geometry, out string reason)
        {
            geometry = default;
            reason = null;
            VpSkinBakeResources resources = world.SkinBake;
            if (resources == null) { reason = "no skin bake resources"; return false; }
            VpSkinModel model = direct.Model;
            if (!model.TryPrepareSharedInput(out string refusal)) { reason = "shared input: " + refusal; return false; }
            if (!resources.TryRent(model, model.SharedInput, out VpSkinBakeSnapshot s, out refusal)) { reason = "snapshot: " + refusal; return false; }
            bool completed = false;
            try
            {
                if (!s.TryCaptureFrom(direct)) { reason = "no captured pose for the snapshot"; return false; }
                if (!world.Storage.TryReserveDeferredSkin(model, s.Conservative, out geometry)) { reason = "storage refused the reservation"; return false; }
                s.RunKernel();
                if (!s.TryMeasuredBounds(out Bounds measured, out string why) || !world.Storage.TryCompleteDeferredSkin(geometry, s.Vertices, s.Topology, measured))
                {
                    world.Storage.TryAbandonDeferredSkin(geometry, true);
                    reason = "float bake: " + (why ?? "the storage refused the completion");
                    return false;
                }

                completed = true;
                VpSkinBakeStats.NoteFloatSync();
                return true;
            }
            finally
            {
                resources.Return(s);
                if (!completed) geometry = default;
            }
        }
    }
}
