using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>The building hull trial's composition (2026-09-30): made from the profile, stepped after the fusion's turn, released before the owners.</summary>
    public sealed partial class CutWorldRoot
    {
        private static readonly Unity.Profiling.ProfilerMarker s_hullMarker = new Unity.Profiling.ProfilerMarker("Zantetsu.BuildingHull.Step");

        /// <summary>The building hull trial, or null when the profile leaves it off.</summary>
        public BuildingHullFusion Hulls { get; private set; }

        private void MakeHulls(CutWorldProfile profile)
        {
            BuildingHullSettings settings = profile.BuildingHull;
            if (!settings.enabled)
            {
                return;
            }

            if (profile.BuildingWorld.enabled)
            {
                UnityEngine.Debug.LogWarning(name + ": the building hull trial needs the building World D6 off; it stays off.", this);
                return;
            }

            if (Rest == null || !Rest.Enabled || Rest.Settings.mode != BuildingRestMode.Kinematic)
            {
                UnityEngine.Debug.LogWarning(name + ": the building hull trial needs the kinematic building rest; it stays off.", this);
                return;
            }

            if (Fusion != null)
            {
                UnityEngine.Debug.LogWarning(name + ": the building hull trial and the building fusion do not share a world (one owns the rest's groups); the hull trial stays off.", this);
                return;
            }

            Hulls = new BuildingHullFusion(settings, Ledger, Owners, Cook, Geometry, Storage, Dispatcher, Rest, profile.SupportEpsilon, profile.AnchorEpsilon, profile.VertexLimit,
                () => CutPhysicsStep.Clock.PhysicsSeconds, () => Time.realtimeSinceStartupAsDouble)
            {
                Cooking = Cook.Cooking,
            };
            // The rest's group notices go to the hull trial: nothing else owns groups in this world.
            Rest.GroupBusy = Hulls.IsGroupBusy;
            Rest.GroupHeld = Hulls.OnGroupHeld;
            Rest.GroupReleasing = Hulls.OnGroupReleasing;
        }

        private void StepHulls()
        {
            if (Hulls == null)
            {
                return;
            }

            using (s_hullMarker.Auto())
            {
                Hulls.Step();
            }
        }

        private void ReleaseHulls()
        {
            if (Hulls == null)
            {
                return;
            }

            if (Rest != null)
            {
                Rest.GroupBusy = null;
                Rest.GroupHeld = null;
                Rest.GroupReleasing = null;
            }

            Hulls.Dispose();
            Hulls = null;
        }

        /// <summary>
        /// A building enters the hull trial: the actor (with a Rigidbody, without colliders of its own) becomes one hull
        /// group whose hull is the convex hull of the shape's convexes, its display geometry a member of the group. The
        /// shape is read here and not kept; the anchors are the actor's frame; the mass is the group's.
        /// </summary>
        public bool TryAddBuildingHull(
            GameObject actor, PhysicsOwnerShape shape, VpStoredGeometry geometry, Matrix4x4 lineageToGeometryLocal, IReadOnlyList<float3> anchors, double mass,
            out LogicalFragmentId fragment, out HullGroup group)
        {
            fragment = default;
            group = null;
            if (!IsReady || Hulls == null || actor == null || shape == null || !(mass > 0.0))
            {
                return false;
            }

            var body = actor.GetComponent<Rigidbody>();
            if (body == null)
            {
                UnityEngine.Debug.LogError(actor.name + ": a hull group needs a Rigidbody.", actor);
                return false;
            }

            fragment = Ledger.AddFragment();   // the anchors are the group's, not the fragment's
            if (!Display.TryShow(fragment, geometry, actor.transform.localToWorldMatrix, lineageToGeometryLocal, Array.Empty<VpClipBoundary>()))
            {
                Ledger.Retire(fragment);
                fragment = default;
                UnityEngine.Debug.LogError(actor.name + ": the display refused to show the building (its geometry's index range, materials, bounds or the display's room).", actor);
                return false;
            }

            group = Hulls.Register(actor, body, shape, fragment, anchors, mass, out string refusal);
            if (group == null)
            {
                Ledger.Retire(fragment);
                fragment = default;
                UnityEngine.Debug.LogError(actor.name + ": the hull trial refused the building: " + refusal, actor);
                return false;
            }

            Owners.Add(fragment, PhysicsFragmentOwner.DisplayOnly(group.Members[0].root, Matrix4x4.identity, BuildingLineage.RegisteredBuilding));
            Geometry.RegisterBaseGeometry(fragment, geometry, lineageToGeometryLocal);
            return true;
        }
    }
}
