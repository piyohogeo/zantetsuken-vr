using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The trial fusion of held building pieces (<see cref="BuildingFusion"/>, 2026-09-29) on this world: made after the
    /// rest, stepped after it, released before it. Off by the profile's default; then nothing here exists.
    /// </summary>
    public sealed partial class CutWorldRoot
    {
        /// <summary>The optional trial fusion of building groups (2026-09-29), or null when the profile leaves it off.</summary>
        public BuildingFusion Fusion { get; private set; }

        private static readonly Unity.Profiling.ProfilerMarker s_fusionMarker =
            new Unity.Profiling.ProfilerMarker("Zantetsu.BuildingFusion.Step");

        private void MakeFusion(CutWorldProfile profile)
        {
            BuildingFusionSettings settings = profile.BuildingFusion;
            if (!settings.enabled)
            {
                return;
            }

            if (Rest == null || !Rest.Enabled || Rest.Settings.mode != BuildingRestMode.Kinematic)
            {
                UnityEngine.Debug.LogWarning(name + ": the building fusion needs the kinematic building rest; it stays off.", this);
                return;
            }

            if (profile.BuildingWorld.enabled)
            {
                UnityEngine.Debug.LogWarning(name + ": the building fusion needs the building World D6 off; it stays off.", this);
                return;
            }

            if (profile.BuildingHull.enabled)
            {
                UnityEngine.Debug.LogWarning(name + ": the building fusion and the building hull trial do not share a world (the hull trial owns the rest's groups); the fusion stays off.", this);
                return;
            }

            Fusion = new BuildingFusion(
                settings, Ledger, Owners, Cook, Geometry, Rest, profile.SupportEpsilon, profile.AnchorEpsilon, profile.VertexLimit,
                () => CutPhysicsStep.Clock.PhysicsSeconds, Dispatcher)
            {
                Cooking = Cook.Cooking,
                SeparationStrength = profile.SeparationStrength,
            };
            Driver.Fusion = Fusion;
            if (Placement != null)
            {
                Placement.FusedSide = Fusion.TryGetSidePlacement;
            }

            // A fused member whose group is being cut is under a cut, for the rest's candidates.
            Rest.CutInProgress = fragment => Ledger.TryGetActiveOperation(fragment, out _) || Fusion.IsCutting(fragment);
            Rest.GroupBusy = body => Fusion.IsGroupBusy(body);
            Rest.GroupHeld = Fusion.OnGroupHeld;
            Rest.GroupReleasing = Fusion.OnGroupReleasing;
        }

        private void StepFusion()
        {
            if (Fusion == null)
            {
                return;
            }

            using (s_fusionMarker.Auto())
            {
                Fusion.Step();
            }
        }

        private void ReleaseFusion()
        {
            if (Fusion == null)
            {
                return;
            }

            if (Driver != null)
            {
                Driver.Fusion = null;
            }

            if (Placement != null)
            {
                Placement.FusedSide = null;
            }

            if (Rest != null)
            {
                Rest.GroupHeld = null;
                Rest.GroupReleasing = null;
            }

            Fusion.Dispose();
            Fusion = null;
        }
    }
}
