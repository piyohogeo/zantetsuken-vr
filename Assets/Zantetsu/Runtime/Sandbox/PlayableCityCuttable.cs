using System;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A placed Megacity building or prop of a playable city, registered into the city's one cut world once it is ready
    /// (<see cref="PlacedCuttableRegistration"/>). The scene builder has checked that the instance is the asset the input
    /// was exported from, and hands over the instance's own renderers and colliders. A registration that throws is not
    /// retried: <see cref="Failure"/> keeps why.
    /// <para>
    /// With <see cref="deferUntilCut"/> (TL, 2026-10-03; DESIGN 4.5.1: an object is drawn by its own Unity Mesh until it is
    /// cut) nothing of the instance goes into the cut world here: it is made a cut target only
    /// (<see cref="PlacedCuttableCandidate"/>, added to the scene's hit detector), its renderers, materials and colliders
    /// left as the scene has them, and its display geometry and owner are prepared by the hit that cuts it, for it alone.
    /// Without it (the local checks' way, as before) the instance is registered whole here and drawn by the cut world.
    /// </para>
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

        /// <summary>A cut target only until a hit (see the class): the instance stays the scene's own until its first cut.</summary>
        public bool deferUntilCut;

        /// <summary>The scene's katana hit (its detector takes the cut target); found in the scene when not set.</summary>
        public SandboxSlashPropHit hit;

        private PlacedCuttableRegistration _registration;

        /// <summary>
        /// Its registration into the cut world: made here (without <see cref="deferUntilCut"/>), or by the hit that cut a
        /// deferred building (<see cref="PlacedCuttableCandidate.Registration"/>); null otherwise -- a deferred prop's cut
        /// makes an owner of the world's, not a registration.
        /// </summary>
        public PlacedCuttableRegistration Registration => _registration ?? Candidate?.Registration;

        /// <summary>Whether a registration stands (see <see cref="Registration"/>).</summary>
        public bool IsRegistered => Registration != null;

        /// <summary>A deferred instance's cut target, once made; null otherwise.</summary>
        public PlacedCuttableCandidate Candidate { get; private set; }

        /// <summary>Whether it is a cut target: registered, or a deferred one's target made.</summary>
        public bool IsCutTarget => IsRegistered || Candidate != null;

        private static SandboxSlashPropHit s_hit;
        private SlashHitDetector _detector;

        /// <summary>Tests only: the detector a deferred one is added to, in place of the scene's katana hit.</summary>
        internal SlashHitDetector DetectorForTest;

        /// <summary>Why the registration was refused (the exception's message), or null.</summary>
        public string Failure { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
            s_hit = null;
        }

        private void Update()
        {
            if (Registration != null || Candidate != null || Failure != null || world == null || !world.IsReady || world.IsEnding)
            {
                return;
            }

            // A deferred one needs the scene's detector (made at the hit's start): it waits for it without using a turn.
            if (deferUntilCut)
            {
                if (DetectorForTest != null)
                {
                    _detector = DetectorForTest;
                }
                else
                {
                    SandboxSlashPropHit h = hit != null ? hit : (s_hit != null ? s_hit : s_hit = FindAnyObjectByType<SandboxSlashPropHit>());
                    _detector = h != null ? h.Detector : null;
                }

                if (_detector == null) return;
            }

            try
            {
                // A building goes into the hull trial when the world runs it (2026-09-30), as the building E2E's does.
                bool hull = building && world.Hulls != null;
                PlacedCuttableInput data = JsonUtility.FromJson<PlacedCuttableInput>(input.text);
                if (deferUntilCut)
                {
                    // A cut target only: its convexes (and a prop's, posed and cooked once) at the instance as placed, nothing in the world.
                    Candidate = new PlacedCuttableCandidate(world, data, target, instanceRenderers, instanceColliders, mass);
                    _detector.AddPlaced(Candidate);
                    Debug.Log("PLAYABLE CITY: cut target " + (building ? "building " : "prop ") + "instance " + target.name + " name=" + data.name
                        + " convexes=" + data.hulls.Length + " anchors=" + data.anchors.Length + " (drawn and colliding as the scene placed it until its first cut)");
                }
                else
                {
                    _registration = hull
                        ? PlacedCuttableRegistration.RegisterHull(world, data, target, instanceRenderers, instanceColliders, mass)
                        : PlacedCuttableRegistration.Register(world, data, target, instanceRenderers, instanceColliders, mass, building, withoutBuildingWorld);
                    Debug.Log("PLAYABLE CITY: registered " + (building ? "building " : "prop ") + (hull ? "(hull trial) " : "") + "instance " + target.name + " " + Registration.Description);
                }
            }
            catch (Exception e)
            {
                Failure = e.GetType().Name + ": " + e.Message;
                Debug.Log("PLAYABLE CITY: NOT registered " + (building ? "building " : "prop ") + "instance " + (target != null ? target.name : "(none)") + ": " + Failure);
            }

            enabled = false;
        }

        private void OnDestroy()
        {
            if (_registration != null && (world == null || world.IsReleased))
            {
                _registration.Dispose();
            }

            if (Candidate != null)
            {
                _detector?.RemovePlaced(Candidate);
                Candidate.Dispose();   // its hit shape and prepared convexes; a cut's owner or group is the world's
            }
        }
    }
}
