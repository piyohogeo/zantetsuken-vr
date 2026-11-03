using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The world's skin bake route (DESIGN 4.5.15): how a prepared character's display input is baked when its cut is
    /// accepted -- the product's synchronous CPU bake (the default), the GPU-first route, or the synchronous float
    /// contrast -- chosen at the launch (<see cref="VpSkinBakeSetup"/>) and written in the <c>VP SKIN BAKE:</c> line.
    /// The GPU-first and float routes own a bounded pool of snapshots and (GPU-first) the compute bake; a route that
    /// cannot be made falls back to the CPU bake for the world, with the reason.
    /// </summary>
    public sealed partial class CutWorldRoot
    {
        /// <summary>The GPU-first/float routes' resources, or null (the CPU route, or the route could not be made).</summary>
        public VpSkinBakeResources SkinBake { get; private set; }

        /// <summary>"cpu", "gpu" or "float": the route this world runs its hits down.</summary>
        public string SkinBakeRoute { get; private set; } = "cpu";

        public string SkinBakeReason { get; private set; }

        /// <summary>The GPU-first route's deferred CPU bakes, the world's (DESIGN 4.5.15); null on the other routes.</summary>
        public VpSkinBakePending SkinBakePending { get; private set; }

        // After the dispatcher and the frame exist: the bakes are a participant of the frame (their held works and failed
        // regions are looked at each pump; their collect runs inside the dispatcher's own).
        private void MakeSkinBakePending()
        {
            SkinBakePending = null;
            if (SkinBake == null || SkinBake.Bake == null || Frame == null) return;
            SkinBakePending = new VpSkinBakePending(this, SkinBake);
            Frame.Add(SkinBakePending);
        }

        private void MakeSkinBake()
        {
            SkinBake = null;
            VpSkinBakeStats.Reset();
            VpSkinBakeMode mode = VpSkinBakeSetup.Mode;
            string unrecognised = VpSkinBakeSetup.Unrecognised != null ? " (" + VpSkinBakeSetup.Argument + " " + VpSkinBakeSetup.Unrecognised + " was not recognised)" : "";
            if (mode == VpSkinBakeMode.Cpu)
            {
                SkinBakeRoute = "cpu";
                SkinBakeReason = "the default: the synchronous CPU bake of the 24-byte input" + unrecognised;
                Debug.Log("VP SKIN BAKE: cpu -- " + SkinBakeReason, this);
                return;
            }

            int capacity = profile != null && profile.SkinBakeSnapshotCapacity > 0 ? profile.SkinBakeSnapshotCapacity : VpSkinBakeResources.DefaultCapacity;
            if (Display == null)
            {
                SkinBakeRoute = "cpu";
                SkinBakeReason = VpSkinBakeSetup.Name(mode) + " asked, but the world has no display";
                Debug.LogWarning("VP SKIN BAKE: cpu -- " + SkinBakeReason, this);
                return;
            }

            if (!VpSkinBakeResources.TryCreate(capacity, mode == VpSkinBakeMode.Gpu, out VpSkinBakeResources resources, out string refusal))
            {
                SkinBakeRoute = "cpu";
                SkinBakeReason = VpSkinBakeSetup.Name(mode) + " asked (" + VpSkinBakeSetup.Argument + "), but not available: " + refusal;
                Debug.LogError("VP SKIN BAKE: cpu -- " + SkinBakeReason, this);
                VpSkinBakeSetup.Mode = VpSkinBakeMode.Cpu;
                return;
            }

            SkinBake = resources;
            SkinBakeRoute = VpSkinBakeSetup.Name(mode);
            SkinBakeReason = "asked (" + VpSkinBakeSetup.Argument + " " + SkinBakeRoute + ")" + unrecognised;
            Debug.Log("VP SKIN BAKE: " + SkinBakeRoute + " -- " + (mode == VpSkinBakeMode.Gpu
                ? "a hit's pose is shown by the GPU bake in its frame and the cut is accepted at once; the CPU geometry the cuts below need is made by a worker and the cut DAG waits for it"
                : "a hit's pose is baked synchronously by the float kernel (the GPU route's arithmetic) and uploaded")
                + "; snapshots " + capacity + "; " + SkinBakeReason, this);
        }

        private void ReleaseSkinBake()
        {
            Debug.Log("VP SKIN BAKE (end): " + name + " " + SkinBakeRoute + "; " + VpSkinBakeStats.Describe()
                + (SkinBake != null ? "; " + SkinBake.Describe() : "")
                + (SkinBakePending != null ? "; " + SkinBakePending.Describe() : "")
                + (Geometry != null ? "; dag pending bases registered " + Geometry.PendingBasesRegistered + ", completed " + Geometry.PendingBasesCompleted + ", failed " + Geometry.PendingBasesFailed + ", dropped " + Geometry.PendingBasesDropped + ", now " + Geometry.PendingBasisCount : "")
                + (Display != null && !Display.IsDisposed ? "; gpu regions now " + Display.GpuRegionCount + ", max " + Display.MaxGpuRegions + ", dispatches " + Display.GpuRegionDispatches + ", rebakes " + Display.GpuRegionRebakes + ", lost " + Display.GpuRegionsLost + ", retired " + Display.GpuRegionsRetired : "")
                + (Storage != null ? "; storage deferred reserved " + Storage.DeferredReserved + ", completed " + Storage.DeferredCompleted + ", abandoned " + Storage.DeferredAbandoned + ", pending " + Storage.DeferredPendingCount : ""), this);
            if (SkinBakePending != null) { SkinBakePending.EndAll(); Frame?.Remove(SkinBakePending); SkinBakePending = null; }
            SkinBake?.Dispose();
            SkinBake = null;
        }
    }
}
