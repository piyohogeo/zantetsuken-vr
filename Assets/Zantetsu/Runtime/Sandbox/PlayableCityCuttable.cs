using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A placed Megacity building or prop of a playable city, registered into the city's one cut world once it is ready
    /// (<see cref="PlacedCuttableRegistration"/>). The scene builder has checked that the instance is the asset the input
    /// was exported from, and hands over the instance's own renderers and colliders.
    /// <para>
    /// The city walk (2026-10-03) places every cuttable of a city this way, many instances of one asset each: the input is
    /// parsed once per asset (<see cref="ParsedInput"/>), registrations are spread over frames (<see cref="PerFrame"/> a
    /// frame), and an instance placed at a uniform scale other than 1 is registered from a copy of the input scaled by it,
    /// at an unscaled stand-in of its pose, so that what the world holds is the shape as placed (the registration itself
    /// takes unscaled instances only). A registration that throws is not retried: <see cref="Failure"/> keeps why.
    /// </para>
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

        /// <summary>At most this many registrations a frame, over every placed cuttable.</summary>
        public const int PerFrame = 8;

        private static readonly Dictionary<TextAsset, PlacedCuttableInput> s_parsed = new Dictionary<TextAsset, PlacedCuttableInput>();
        private static readonly Dictionary<(TextAsset, float), PlacedCuttableInput> s_scaled = new Dictionary<(TextAsset, float), PlacedCuttableInput>();
        private static int s_frame = -1, s_thisFrame;

        /// <summary>
        /// While true no placed cuttable registers (2026-10-03: the city walk holds them until its registration stage, so
        /// that the whole registration is inside that stage -- measured there, behind the preparation -- and a picture of
        /// the city as authored can be taken first). False unless a check sets it.
        /// </summary>
        public static bool Held;

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

        /// <summary>The uniform scale the instance was registered at (1 for an unscaled one).</summary>
        public float RegisteredScale { get; private set; } = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
            s_parsed.Clear();
            s_scaled.Clear();
            s_frame = -1;
            s_thisFrame = 0;
            Held = false;
            s_hit = null;
        }

        /// <summary>The input parsed once per asset.</summary>
        public static PlacedCuttableInput ParsedInput(TextAsset asset)
        {
            if (!s_parsed.TryGetValue(asset, out PlacedCuttableInput data))
            {
                s_parsed[asset] = data = JsonUtility.FromJson<PlacedCuttableInput>(asset.text);
            }

            return data;
        }

        /// <summary>The input scaled uniformly by <paramref name="scale"/> about its own origin: drawn positions, the convex and the anchors.</summary>
        public static PlacedCuttableInput Scaled(PlacedCuttableInput data, float scale)
        {
            return new PlacedCuttableInput
            {
                schemaVersion = data.schemaVersion, topologyCount = data.topologyCount, name = data.name, source = data.source, sha256 = data.sha256,
                isBuilding = data.isBuilding, isCuttable = data.isCuttable, indices = data.indices, topology = data.topology,
                render = data.render.Select(v => new PlacedCuttableInput.Vertex { position = v.position * scale, normal = v.normal, uv = v.uv }).ToArray(),
                hulls = data.hulls.Select(h => new PlacedCuttableInput.Hull { vertices = h.vertices.Select(p => p * scale).ToArray(), faceOffsets = h.faceOffsets, faceIndices = h.faceIndices }).ToArray(),
                anchors = data.anchors.Select(p => p * scale).ToArray(),
            };
        }

        private void Update()
        {
            if (Held || Registration != null || Candidate != null || Failure != null || world == null || !world.IsReady || world.IsEnding)
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

            if (s_frame != Time.frameCount)
            {
                s_frame = Time.frameCount;
                s_thisFrame = 0;
            }

            if (s_thisFrame >= PerFrame)
            {
                return;
            }

            s_thisFrame++;
            GameObject standIn = null;
            try
            {
                // A building goes into the hull trial when the world runs it (2026-09-30), as the building E2E's does.
                bool hull = building && world.Hulls != null;
                PlacedCuttableInput data = ParsedInput(input);
                Transform at = target;
                Vector3 s = target.lossyScale;
                if ((s - Vector3.one).sqrMagnitude > 1e-8f)
                {
                    if (Mathf.Abs(s.x - s.y) > 1e-5f || Mathf.Abs(s.x - s.z) > 1e-5f)
                    {
                        throw new InvalidOperationException(input.name + ": the placed instance is scaled unevenly: " + s.ToString("F4"));
                    }

                    if (!s_scaled.TryGetValue((input, s.x), out PlacedCuttableInput scaled))
                    {
                        s_scaled[(input, s.x)] = scaled = Scaled(data, s.x);
                    }

                    data = scaled;
                    standIn = new GameObject(target.name + " (unscaled stand-in)");
                    standIn.transform.SetPositionAndRotation(target.position, target.rotation);
                    at = standIn.transform;
                    RegisteredScale = s.x;
                }

                if (deferUntilCut)
                {
                    // A cut target only: its convexes (and a prop's, posed and cooked once) at the instance as placed, nothing in the world.
                    Candidate = new PlacedCuttableCandidate(world, data, target, instanceRenderers, instanceColliders, mass);
                    _detector.AddPlaced(Candidate);
                    Debug.Log("PLAYABLE CITY: cut target " + (building ? "building " : "prop ") + (RegisteredScale != 1f ? "(scale " + RegisteredScale.ToString("R") + ") " : "")
                        + "instance " + target.name + " name=" + data.name + " convexes=" + data.hulls.Length + " anchors=" + data.anchors.Length
                        + " (drawn and colliding as the scene placed it until its first cut)");
                }
                else
                {
                    _registration = hull
                        ? PlacedCuttableRegistration.RegisterHull(world, data, at, instanceRenderers, instanceColliders, mass)
                        : PlacedCuttableRegistration.Register(world, data, at, instanceRenderers, instanceColliders, mass, building, withoutBuildingWorld);
                    Debug.Log("PLAYABLE CITY: registered " + (building ? "building " : "prop ") + (hull ? "(hull trial) " : "")
                        + (RegisteredScale != 1f ? "(scale " + RegisteredScale.ToString("R") + ") " : "") + "instance " + target.name + " " + Registration.Description);
                }
            }
            catch (Exception e)
            {
                Failure = e.GetType().Name + ": " + e.Message;
                Debug.Log("PLAYABLE CITY: NOT registered " + (building ? "building " : "prop ") + "instance " + (target != null ? target.name : "(none)") + ": " + Failure);
            }
            finally
            {
                if (standIn != null) Destroy(standIn);
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
