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
            return TryAddBuildingHull(actor, shape, geometry, lineageToGeometryLocal, anchors, mass, default, out fragment, out group);
        }

        /// <param name="issued">
        /// A fragment this world issued already for the building (a hit identified it before its first cut, as a prepared
        /// character's is), which it becomes; unset, one is issued here. A refusal by the display or the trial retires it
        /// either way; a refusal before (the world not ready, no trial, no body) leaves an issued one the caller's.
        /// </param>
        public bool TryAddBuildingHull(
            GameObject actor, PhysicsOwnerShape shape, VpStoredGeometry geometry, Matrix4x4 lineageToGeometryLocal, IReadOnlyList<float3> anchors, double mass,
            LogicalFragmentId issued, out LogicalFragmentId fragment, out HullGroup group)
        {
            bool displayed = false;
            return TryAddBuildingHull(actor, shape, geometry, lineageToGeometryLocal, anchors, mass, issued, ref displayed, out fragment, out group);
        }

        /// <summary>
        /// Tests only: called with "before display" just before the display is asked, "after display" once it took the
        /// geometry, and "after register" once the trial registered the group; on the deferred registration with "deferred
        /// after register", "deferred after owner" and "deferred after base geometry".
        /// </summary>
        internal static Action<string> addBuildingHullHookForTest;

        /// <param name="displayed">
        /// Whether the display took <paramref name="geometry"/> (2026-10-03): set as soon as it did, and readable however the
        /// call ends -- a refusal or an exception. False: the world never showed it, and it is still the caller's to give
        /// back to the storage. True: it is the world's -- a later refusal or exception retires the fragment, and the
        /// world's reclamation takes the geometry back -- and the caller must not give it back.
        /// </param>
        public bool TryAddBuildingHull(
            GameObject actor, PhysicsOwnerShape shape, VpStoredGeometry geometry, Matrix4x4 lineageToGeometryLocal, IReadOnlyList<float3> anchors, double mass,
            LogicalFragmentId issued, ref bool displayed, out LogicalFragmentId fragment, out HullGroup group)
        {
            displayed = false;
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

            if (issued.IsSet && Owners.TryGet(issued, out PhysicsFragmentOwner _))
            {
                // An issued fragment that has an owner already could not take this building's: refused before the display,
                // like the refusals above, rather than met as an exception after it.
                UnityEngine.Debug.LogError(actor.name + ": the fragment issued for the building already has an owner.", actor);
                return false;
            }

            fragment = issued.IsSet ? issued : Ledger.AddFragment();   // the anchors are the group's, not the fragment's
            bool shown;
            try
            {
                addBuildingHullHookForTest?.Invoke("before display");
                shown = Display.TryShow(fragment, geometry, actor.transform.localToWorldMatrix, lineageToGeometryLocal, Array.Empty<VpClipBoundary>());
            }
            catch
            {
                // Not shown: the fragment goes as on the display's refusal, the geometry stays the caller's, and the
                // error is passed on.
                Ledger.Retire(fragment);
                fragment = default;
                throw;
            }

            if (!shown)
            {
                Ledger.Retire(fragment);
                fragment = default;
                UnityEngine.Debug.LogError(actor.name + ": the display refused to show the building (its geometry's index range, materials, bounds or the display's room).", actor);
                return false;
            }

            displayed = true;
            string refusal;
            try
            {
                addBuildingHullHookForTest?.Invoke("after display");
                group = Hulls.Register(actor, body, shape, fragment, anchors, mass, out refusal);
            }
            catch
            {
                // Shown already, so the geometry is the world's: the fragment goes as on the trial's refusal, and the
                // error is passed on.
                Ledger.Retire(fragment);
                fragment = default;
                group = null;
                throw;
            }

            if (group == null)
            {
                Ledger.Retire(fragment);
                fragment = default;
                UnityEngine.Debug.LogError(actor.name + ": the hull trial refused the building: " + refusal, actor);
                return false;
            }

            try
            {
                addBuildingHullHookForTest?.Invoke("after register");
                Owners.Add(fragment, PhysicsFragmentOwner.DisplayOnly(group.Members[0].root, Matrix4x4.identity, BuildingLineage.RegisteredBuilding));
                Geometry.RegisterBaseGeometry(fragment, geometry, lineageToGeometryLocal);
            }
            catch
            {
                // Shown and registered, not completed: the owner (if it was added) and the base geometry go, the group is
                // unregistered, and the fragment goes as on a refusal after the display -- the geometry is the world's,
                // taken back by its reclamation. The error is passed on. The fragment retires before its base geometry is
                // forgotten: the DAG forgets only a retired fragment's.
                if (Owners.TryGet(fragment, out PhysicsFragmentOwner _)) Owners.Retire(fragment);
                if (Ledger.IsCurrentTarget(fragment)) Ledger.Retire(fragment);
                Geometry.Forget(fragment);
                Hulls.Unregister(group, "the world could not complete the building's registration");
                fragment = default;
                group = null;
                throw;
            }

            return true;
        }


        /// <summary>
        /// A building enters the hull trial drawn by its own renderers until its first cut (TL, 2026-10-03; DESIGN 4.5.1):
        /// its group, owner and geometry as <see cref="TryAddBuildingHull"/> makes them, but its display is registered only
        /// at the first cut's publication by the display (the always-kinematic mode only), just before the display cut that
        /// takes the drawing over; <paramref name="handedOver"/> is told right after it (the caller switches its own drawing
        /// and colliders off there). A first cut that ends without a publication takes it back out: its owner retired, its
        /// fragment retired and forgotten, its index range retired -- never shown, so the vertex room goes back by the
        /// world's reclamation -- the group's objects destroyed, and <paramref name="takenBack"/> told why.
        /// <para>
        /// An exception while it is registered (2026-10-03) leaves nothing of the world's: inside the trial's registration
        /// the trial takes its part back and the fragment retires; after it, the owner (if added) is retired, the fragment
        /// retired and its base geometry forgotten, and the group unregistered. Nothing was shown, so -- unlike
        /// <see cref="TryAddBuildingHull"/>, where the display taking the geometry makes it the world's -- the geometry
        /// stays the caller's until its base geometry is registered with the DAG, which roots the geometry's vertex room in
        /// this lineage; from there it is the world's: an exception retires its index range and the world's reclamation
        /// gives the room back, once.
        /// </para>
        /// </summary>
        /// <param name="geometryTaken">
        /// Set when the world took <paramref name="geometry"/> (its base geometry registered), and readable however the call
        /// ends. False: still the caller's to give back to the storage. True: the world's -- the caller must not give it back.
        /// </param>
        public bool TryAddBuildingHullDeferred(
            GameObject actor, PhysicsOwnerShape shape, VpStoredGeometry geometry, Matrix4x4 lineageToGeometryLocal, IReadOnlyList<float3> anchors, double mass,
            LogicalFragmentId issued, Action handedOver, Action<string> takenBack, ref bool geometryTaken, out LogicalFragmentId fragment, out HullGroup group, out string refusal)
        {
            geometryTaken = false;
            fragment = default;
            group = null;
            refusal = null;
            if (!IsReady || Hulls == null || actor == null || shape == null || !(mass > 0.0))
            {
                refusal = "the world is not ready, runs no hull trial, or the building has no actor, shape or mass";
                return false;
            }

            if (!Hulls.Settings.kinematicDisplay)
            {
                refusal = "the hull trial is not in its always-kinematic mode, where the display alone publishes a first cut";
                return false;
            }

            var body = actor.GetComponent<Rigidbody>();
            if (body == null)
            {
                refusal = "a hull group needs a Rigidbody";
                return false;
            }

            fragment = issued.IsSet ? issued : Ledger.AddFragment();
            LogicalFragmentId at = fragment;
            try
            {
                group = Hulls.Register(actor, body, shape, fragment, anchors, mass, out refusal);
            }
            catch
            {
                // The trial took back what it had made; nothing of the world's was made: the fragment goes, the geometry
                // stays the caller's, and the error is passed on.
                if (Ledger.IsCurrentTarget(at)) Ledger.Retire(at);
                fragment = default;
                group = null;
                throw;
            }

            if (group == null)
            {
                Ledger.Retire(fragment);
                fragment = default;
                refusal = "the hull trial refused the building: " + refusal;
                return false;
            }

            try
            {
                addBuildingHullHookForTest?.Invoke("deferred after register");
                Owners.Add(at, PhysicsFragmentOwner.DisplayOnly(group.Members[0].root, Matrix4x4.identity, BuildingLineage.RegisteredBuilding));
                addBuildingHullHookForTest?.Invoke("deferred after owner");
                Geometry.RegisterBaseGeometry(at, geometry, lineageToGeometryLocal);
                geometryTaken = true;
                addBuildingHullHookForTest?.Invoke("deferred after base geometry");
                group.Handover = new HullGroup.FirstCutHandover
                {
                    show = () =>
                    {
                        return actor != null && Display.TryShow(at, geometry, actor.transform.localToWorldMatrix, lineageToGeometryLocal, Array.Empty<VpClipBoundary>());
                    },
                    handedOver = handedOver,
                    takenBack = why =>
                    {
                        if (Owners.TryGet(at, out _)) Owners.Retire(at);
                        if (Ledger.IsCurrentTarget(at)) Ledger.Retire(at);
                        Geometry.Forget(at);
                        Storage.TryRetireIndices(geometry.indexRange);
                        takenBack?.Invoke(why);
                    },
                };
            }
            catch
            {
                // Registered, not completed, nothing shown: the owner (if it was added) goes, the fragment retires and its
                // base geometry is forgotten, and the group is unregistered. A geometry the DAG took is the world's: its
                // index range is retired here and its room goes back by the world's reclamation. The error is passed on.
                if (Owners.TryGet(at, out PhysicsFragmentOwner _)) Owners.Retire(at);
                if (Ledger.IsCurrentTarget(at)) Ledger.Retire(at);
                Geometry.Forget(at);
                Hulls.Unregister(group, "the world could not complete the deferred building's registration");
                if (geometryTaken) Storage.TryRetireIndices(geometry.indexRange);
                fragment = default;
                group = null;
                throw;
            }

            return true;
        }
    }
}
