using System;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A placed Megacity building or prop of a playable city, registered into the city's one cut world once it is ready
    /// (<see cref="PlacedCuttableRegistration"/>). The scene builder has checked that the instance is the asset the input
    /// was exported from, and hands over the instance's own renderers and colliders.
    /// </summary>
    public sealed class PlayableCityCuttable : MonoBehaviour
    {
        public CutWorldRoot world;
        public TextAsset input;
        public Transform target;
        public Renderer[] instanceRenderers = Array.Empty<Renderer>();
        public Collider[] instanceColliders = Array.Empty<Collider>();
        public float mass = 50f;
        public bool building;

        /// <summary>Diagnosis only: a building registered as an ordinary body, so that its pieces carry no building World D6.</summary>
        public bool withoutBuildingWorld;

        public PlacedCuttableRegistration Registration { get; private set; }
        public bool IsRegistered => Registration != null;

        private void Update()
        {
            if (Registration == null && world != null && world.IsReady && !world.IsEnding)
            {
                // A building goes into the hull trial when the world runs it (2026-09-30), as the building E2E's does.
                bool hull = building && world.Hulls != null;
                Registration = hull
                    ? PlacedCuttableRegistration.RegisterHull(world, JsonUtility.FromJson<PlacedCuttableInput>(input.text), target, instanceRenderers, instanceColliders, mass)
                    : PlacedCuttableRegistration.Register(world, JsonUtility.FromJson<PlacedCuttableInput>(input.text), target,
                        instanceRenderers, instanceColliders, mass, building, withoutBuildingWorld);
                Debug.Log("PLAYABLE CITY: registered " + (building ? "building " : "prop ") + (hull ? "(hull trial) " : "") + Registration.Description);
                enabled = false;
            }
        }

        private void OnDestroy()
        {
            if (Registration != null && (world == null || world.IsReleased))
            {
                Registration.Dispose();
            }
        }
    }
}
